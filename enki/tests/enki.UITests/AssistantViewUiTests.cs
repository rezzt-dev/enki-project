using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Controls;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Sección Asistente y Ajustes › Inteligencia artificial sobre el XAML compilado de verdad: cada
/// estado del CLI enseña su panel, una respuesta en Markdown se pinta con sus bloques y la sección de ajustes
/// deja claro cómo se usa Claude. Con <c>ENKI_GRAPH_PREVIEW=&lt;carpeta&gt;</c> guarda además una captura de
/// cada estado, para revisar el diseño sin abrir la app.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class AssistantViewUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _cliDirectory = Path.Combine(Path.GetTempPath(), $"enki-fake-claude-{Guid.NewGuid():N}");

    public AssistantViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        if (Directory.Exists(_cliDirectory)) Directory.Delete(_cliDirectory, true);
    }

    /// <summary>CLI mínimo: solo responde a la comprobación de versión, ayuda y sesión.</summary>
    private string FakeCli(bool loggedIn, bool supportsEffort = true)
    {
        // El CLI vive fuera del almacenamiento privado: SettingsService restringe sus archivos a 0600.
        Directory.CreateDirectory(_cliDirectory);
        var path = Path.Combine(_cliDirectory, "claude");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            case "$1" in
              --version) echo "2.1.280 (Claude Code)";;
              --help) echo "--output-format --include-partial-messages --resume --tools --safe-mode --model --system-prompt[-file] {{(supportsEffort ? "--effort" : "")}}";;
              auth) echo '{"loggedIn":{{(loggedIn ? "true" : "false")}},"authMethod":"claude.ai","email":"ana@example.com","subscriptionType":"pro"}';;
            esac
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private (AssistantView View, AssistantViewModel Vm, Window Window, AiChatService Chats) Create(string cliPath,
        Action<AiChatService, SettingsService>? seed = null)
    {
        var settings = new SettingsService(_directory);
        settings.Settings.Ai.ClaudeCliPath = cliPath;
        settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        settings.Settings.Ai.Enabled = true;
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var chats = new AiChatService(_directory);
        seed?.Invoke(chats, settings);
        var cli = new ClaudeCliService(settings, _directory);
        var vm = new AssistantViewModel(cli, chats, settings, vault, new NotificationService(settings, _directory));
        var view = new AssistantView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 860, Background = new SolidColorBrush(Color.Parse("#121212")) };
        window.Show();
        vm.OnActivated();
        Pump(() => vm.Cli.IsKnown && !vm.Cli.IsChecking);
        if (File.Exists(cliPath)) Assert.True(vm.Cli.IsReady || vm.Cli.IsSignedOut, vm.Cli.Status.ToString());
        Pump(() =>
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            if (vm.Cli.IsReady)
                return Find<TextBox>(view, "ComposerBox").IsEffectivelyVisible
                    && (!vm.Messages.Any(m => m.IsAssistant && m.HasText)
                        || view.GetVisualDescendants().OfType<ChatMarkdownView>().Any());
            return RenderedText(view).Contains(vm.Cli.IsSignedOut ? ClaudeCliService.LoginCommand : vm.Cli.InstallCommand,
                StringComparison.Ordinal);
        });
        return (view, vm, window, chats);
    }

    /// <summary>Identidad con la que el asistente reconoce el vault de prueba (la de su ruta: estos tests no
    /// componen <c>VaultSettingsService</c>).</summary>
    private string VaultId => VaultSettingsService.PathIdentity(Path.Combine(_directory, "vault"));

    /// <summary>Busca un control por nombre en todo el árbol: el hilo, el compositor y la cabecera son controles
    /// propios, cada uno con su ámbito de nombres.</summary>
    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Concat(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>())
            .First(control => control.Name == name);

    private static void Pump(Func<bool> until)
    {
        var watch = Stopwatch.StartNew();
        while (!until() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string RenderedText(Visual root) => string.Join(" | ", root.GetVisualDescendants()
        .Where(v => v is Control { IsEffectivelyVisible: true })
        .Select(v => v switch { SelectableTextBlock s => s.Inlines?.Count > 0 ? string.Concat(s.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : s.Text, TextBlock t => t.Text, _ => null })
        .Where(t => !string.IsNullOrEmpty(t)));

    private static void SavePreview(Control view, string name, int width = 1280, int height = 860)
    {
        if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is not { Length: > 0 } preview) return;
        Directory.CreateDirectory(preview);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(preview, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public void SelectedChatText_UsesContrastingColors_AndUpdatesWithTheme()
    {
        if (OperatingSystem.IsWindows()) return;
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, _) =>
        {
            var session = new AiChatSession { Title = "Selección" };
            session.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Seleccionar texto del chat" });
            session.Messages.Add(new AiChatMessage
            {
                Role = AiChatRole.Assistant,
                Text = "## Texto seleccionado\n\nPárrafo con **negrita**, `código` y [enlace](https://example.com).\n\n1. Elemento de lista\n\n> Una cita\n\n| Columna |\n|---|\n| Celda |\n\n```text\nBloque de código\n```"
            });
            chats.Save(session);
        });
        try
        {
            vm.SelectSessionCommand.Execute(vm.Sessions.Single());
            foreach (var light in new[] { false, true })
            {
                Application.Current!.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
                Dispatcher.UIThread.RunJobs();
                window.GetLayoutManager()!.ExecuteLayoutPass();
                Assert.True(window.TryFindResource("AppBackgroundBrush", out var background));
                window.Background = Assert.IsAssignableFrom<IBrush>(background);
                var blocks = view.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Where(block => block.IsEffectivelyVisible).ToArray();
                Assert.True(blocks.Length >= 7);
                foreach (var block in blocks)
                {
                    block.SelectAll();
                    Assert.True(block.SelectionEnd > block.SelectionStart);
                    Assert.Equal(light ? Colors.White : Colors.Black,
                        Assert.IsAssignableFrom<ISolidColorBrush>(block.SelectionForegroundBrush).Color);
                    Assert.Equal(light ? Colors.Black : Colors.White,
                        Assert.IsAssignableFrom<ISolidColorBrush>(block.SelectionBrush).Color);
                }
                Dispatcher.UIThread.RunJobs();
                window.GetLayoutManager()!.ExecuteLayoutPass();
                SavePreview(view, light ? "assistant-selection-light" : "assistant-selection-dark");
            }
        }
        finally { window.Close(); Application.Current!.RequestedThemeVariant = previousTheme; }
    }

    [Fact]
    public void LoadingRings_RenderAtAllAssistantSizes()
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 20, Margin = new Thickness(20) };
        foreach (var size in new[] { 10, 12, 14, 22 })
            row.Children.Add(new LoadingSpinner { Width = size, Height = size, Foreground = Brushes.White,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        var refresh = new Material.Icons.Avalonia.MaterialIcon { Kind = Material.Icons.MaterialIconKind.Refresh,
            Width = 14, Height = 14, Classes = { "spin" }, Foreground = Brushes.White };
        row.Children.Add(refresh);
        var window = new Window { Width = 220, Height = 70, Background = Brushes.Black, Content = row };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            foreach (var ring in row.Children.OfType<LoadingSpinner>())
            {
                Assert.Equal(ring.Width, ring.Bounds.Width);
                Assert.Equal(ring.Height, ring.Bounds.Height);
            }
            Assert.Equal(new Point(7, 7), refresh.RenderTransformOrigin.ToPixels(new Size(14, 14)));
            SavePreview(window, "assistant-loading-rings", 220, 70);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void HistoryToggle_ReclaimsConversationSpace_AndPreservesDraftAndSearch()
    {
        var (view, vm, window, _) = Create(Path.Combine(_directory, "no-existe"));
        try
        {
            var panel = Find<Grid>(view, "HistoryPanel");
            var rail = Find<Grid>(view, "HistoryRail");
            var collapse = Find<Button>(view, "CollapseHistoryButton");
            var expand = Find<Button>(view, "ExpandHistoryButton");
            vm.DraftText = "Borrador pendiente";
            vm.SessionSearchText = "Mi búsqueda";
            Assert.True(panel.IsEffectivelyVisible);
            Assert.False(rail.IsEffectivelyVisible);
            var initialWidth = panel.Bounds.Width;

            collapse.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.False(panel.IsEffectivelyVisible);
            Assert.True(expand.IsEffectivelyVisible);
            Assert.Equal(52, rail.Bounds.Width);
            Assert.True(rail.Bounds.Width < initialWidth);
            Assert.Equal(rail.Bounds.Width, ((Grid)rail.Parent!).Children.OfType<Grid>()
                .Single(g => Grid.GetColumn(g) == 1).Bounds.X);
            SavePreview(view, "assistant-history-collapsed");

            expand.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.True(panel.IsEffectivelyVisible);
            Assert.False(rail.IsEffectivelyVisible);
            Assert.Equal(initialWidth, panel.Bounds.Width);
            Assert.Equal("Borrador pendiente", vm.DraftText);
            Assert.Equal("Mi búsqueda", vm.SessionSearchText);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void NotInstalled_ShowsSetupWithInstallCommand_AndHidesComposer()
    {
        var (view, vm, window, _) = Create(Path.Combine(_directory, "no-existe"));
        try
        {
            Assert.True(vm.ShowSetupPanel);
            var text = RenderedText(view);
            Assert.Contains("Claude Code", text);
            Assert.Contains(vm.Cli.InstallCommand, text);
            Assert.False(view.GetVisualDescendants().OfType<AssistantComposerView>().Single().IsEffectivelyVisible);
            SavePreview(view, "assistant-not-installed");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void SignedOut_ShowsLoginCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: false));
        try
        {
            Assert.True(vm.Cli.IsSignedOut);
            Assert.Contains(ClaudeCliService.LoginCommand, RenderedText(view));
            SavePreview(view, "assistant-signed-out");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Ready_EmptyChat_ShowsWelcomeAndComposer()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true));
        try
        {
            Assert.True(vm.ShowWelcome);
            Assert.True(Find<TextBox>(view, "ComposerBox").IsEffectivelyVisible);
            Assert.Contains("ana@example.com · Claude Pro", RenderedText(view));
            SavePreview(view, "assistant-welcome");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>En la sección, un chat vacío centra el compositor en la vista, entre la bienvenida y las
    /// sugerencias; con mensajes vuelve bajo el hilo. Es el mismo control en los dos huecos, así que el cuadro
    /// de texto (y con él el borrador y el foco) no se recrea al cambiar.</summary>
    [Fact]
    public void Section_EmptyChat_CentersTheComposer_AndDocksItOnceTheChatHasMessages()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, _) =>
        {
            var chat = new AiChatSession { Title = "Con mensajes", VaultId = VaultId };
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Hola" });
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Text = "¿En qué te ayudo?" });
            chats.Save(chat);
        });
        try
        {
            var composer = view.GetVisualDescendants().OfType<AssistantComposerView>().Single();
            var box = Find<TextBox>(view, "ComposerBox");
            var docked = Find<Decorator>(view, "DockedComposerHost");
            var welcome = Find<AssistantWelcomeView>(view, "Welcome");
            var body = Assert.IsAssignableFrom<Control>(docked.Parent);

            Assert.True(vm.ShowWelcome);
            Assert.Same(welcome.Slot, composer.Parent);
            Assert.Contains("hero", composer.Classes);
            // La bienvenida del hilo compartido no se duplica detrás de la de la sección.
            Assert.False(Find<Panel>(view, "WelcomeHost").IsEffectivelyVisible);
            var surface = Find<Border>(view, "ComposerSurface");
            var origin = surface.TranslatePoint(default, body)!.Value;
            Assert.InRange(origin.X + surface.Bounds.Width / 2 - body.Bounds.Width / 2, -1, 1);
            Assert.InRange(origin.Y + surface.Bounds.Height / 2 - body.Bounds.Height / 2, -1, 1);
            Assert.True(surface.Bounds.Width >= 700, surface.Bounds.Width.ToString());
            SavePreview(view, "assistant-welcome-centered");
            box.Focus();
            vm.DraftText = "Resume la nota abierta";
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            SavePreview(view, "assistant-welcome-centered-focused");

            vm.SelectSessionCommand.Execute(vm.Sessions.Single());
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.False(vm.ShowWelcome);
            Assert.Same(docked, composer.Parent);
            Assert.DoesNotContain("hero", composer.Classes);
            Assert.Same(box, Find<TextBox>(view, "ComposerBox"));
            Assert.True(box.IsEffectivelyVisible);
            Assert.Equal(body.Bounds.Height, docked.Bounds.Bottom, 1);

            vm.NewChatCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.Same(welcome.Slot, composer.Parent);
            Assert.True(box.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Ready_RestoredConversation_RendersMarkdownBlocksAndAttachments()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, settings) =>
        {
            var session = new AiChatSession { Title = "Plan de lanzamiento", SkillName = "Redactor", Model = "sonnet" };
            session.Messages.Add(new AiChatMessage
            {
                Role = AiChatRole.User,
                Text = "Resume la reunión y extrae las tareas.",
                Attachments = [new AiContextAttachment { Title = "Reunión lunes", RelativePath = "Reunión lunes.md", SentChars = 3200, WasTruncated = true }]
            });
            session.Messages.Add(new AiChatMessage
            {
                Role = AiChatRole.Assistant,
                Text = "## Resumen\n\nSe decidió **lanzar el viernes** con el equipo de `beta`.\n\n- [ ] Preparar notas de versión\n- [x] Revisar QA\n\n| Tarea | Responsable |\n|---|---|\n| Notas | Ana |\n\n```bash\ndotnet test\n```"
            });
            session.VaultId = VaultId;
            chats.Save(session);
            settings.Settings.Ai.LastChatByVault[VaultId] = session.Id;
        });
        try
        {
            Assert.Equal("Plan de lanzamiento", vm.ChatTitle);
            Assert.Equal(2, vm.Messages.Count);
            var markdown = view.GetVisualDescendants().OfType<ChatMarkdownView>().Single(markdown => markdown.IsEffectivelyVisible);
            Assert.True(markdown.Children.Count >= 6);
            var text = RenderedText(view);
            Assert.Contains("Resumen", text);
            Assert.Contains("lanzar el viernes", text);
            Assert.Contains("dotnet test", text);
            Assert.Contains("Responsable", text);
            Assert.Contains("Reunión lunes", text);
            Assert.Contains("Redactor", text);
            Assert.Equal("sonnet", vm.SelectedModel!.Alias);
            SavePreview(view, "assistant-conversation");
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EffortSlider_UsesRealCliCapability_AndUpdatesChatEffort(bool supportsEffort)
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true, supportsEffort: supportsEffort));
        try
        {
            vm.ToggleEffortPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var slider = Find<Slider>(view, "EffortSlider");
            Assert.Equal(supportsEffort, slider.IsEnabled);
            Assert.Equal(!supportsEffort, vm.ShowEffortUnavailable);
            if (supportsEffort)
            {
                slider.Value = 2;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("high", vm.SelectedEffort);
            }
            SavePreview(view, supportsEffort ? "assistant-effort" : "assistant-effort-unavailable");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void OpenAi_UsesProviderSelectorAndSharedChatLayout()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_cliDirectory);
        var codex = Path.Combine(_cliDirectory, "codex");
        File.WriteAllText(codex, """
            #!/bin/sh
            case "$1" in
              --version) echo 'codex-cli 1.0.0';;
              exec) echo '--ignore-user-config --ignore-rules --ephemeral --json --sandbox --strict-config';;
              login) echo 'Logged in using ChatGPT' >&2;;
            esac
            """ + "\n");
        File.SetUnixFileMode(codex, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (view, vm, window, _) = Create(Path.Combine(_directory, "missing-claude"), (_, settings) =>
        {
            settings.Settings.Ai.Provider = AiProvider.OpenAi;
            settings.Settings.Ai.CodexCliPath = codex;
        });
        try
        {
            var picker = Find<ComboBox>(view, "ProviderPicker");
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Equal(3, picker.ItemCount);
            Assert.Equal(AiProvider.OpenAi, ((AiProviderOption)picker.SelectedItem!).Provider);
            Assert.Contains("OpenAI", RenderedText(view));
            Assert.True(Find<TextBox>(view, "ComposerBox").IsEffectivelyVisible);
            Assert.False(vm.ShowSetupPanel);
            SavePreview(view, "assistant-openai");
            vm.IsSending = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(picker.IsEnabled);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Settings_AiSection_ExplainsProvidersAndConsent()
    {
        if (OperatingSystem.IsWindows()) return;
        var settings = new SettingsService(_directory);
        settings.Settings.Ai.ClaudeCliPath = FakeCli(loggedIn: true);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var auth = new PolarAuthService(settings);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), new PolarTaskService(_directory),
            new ReminderService(_directory), settings);
        var cli = new ClaudeCliService(settings, _directory);
        var vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(_directory),
            new VaultSettingsService(vault), vault, new VaultBackupService(vault, _directory), auth, sync,
            new NotificationService(settings, _directory),
            (_, _) => Task.FromResult(false), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { },
            _ => { }, _ => { }, _ => { }, _ => { }, () => { },
            aiCliStatus: new ClaudeCliStatusViewModel(cli), aiChatService: new AiChatService(_directory));
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 1100, Background = new SolidColorBrush(Color.Parse("#121212")) };
        window.Show();
        try
        {
            vm.SelectSectionCommand.Execute(SettingsSection.Ai);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var text = RenderedText(view);
            Assert.Contains(Localization.LocalizationService.Instance["Settings_Ai_Usage_SignIn"], text);
            Assert.Contains(Localization.LocalizationService.Instance["Settings_Ai_Usage_Terms"], text);
            Assert.DoesNotContain(Localization.LocalizationService.Instance["Settings_Ai_Cli_Path_Label"], text);

            vm.AiEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsAiConsentDialogOpen);
            SavePreview(view, "settings-ai-consent", 1280, 1100);
            vm.AcceptAiConsentCommand.Execute(null);
            Pump(() => vm.AiCliStatus!.IsKnown && !vm.AiCliStatus.IsChecking);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.Contains(Localization.LocalizationService.Instance["Settings_Ai_Cli_Path_Label"], RenderedText(view));
            SavePreview(view, "settings-ai", 1280, 1100);
            var accessLabel = Localization.LocalizationService.Instance["Settings_Ai_Vault_DefaultAccess_Label"];
            vm.SearchText = accessLabel;
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var accessToggle = view.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(toggle => Avalonia.Automation.AutomationProperties.GetName(toggle) == accessLabel);
            accessToggle.IsChecked = true;
            Assert.True(vm.AiAllowReadAccessByDefault);
            Assert.True(settings.Settings.Ai.AllowReadAccessByDefault);
            SavePreview(view, "settings-ai-default-access", 1280, 1100);
        }
        finally
        {
            window.Close();
        }
    }
    [Fact]
    public void ToolActivityAndApproval_RenderInsideTheResponse()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, settings) =>
        {
            var chat = new AiChatSession { Title = "Organizar" };
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Organiza mis notas" });
            chat.Messages.Add(new AiChatMessage
            {
                Role = AiChatRole.Assistant, Text = "Nota creada.", Activities =
                [new AiToolActivity { Tool = "create_note", Target = "Plan semanal", State = AiToolActivityState.Succeeded }]
            });
            chat.VaultId = VaultId;
            chats.Save(chat);
            settings.Settings.Ai.LastChatByVault[VaultId] = chat.Id;
        });
        try
        {
            var reply = vm.Messages.Last();
            reply.PendingApproval = new AiApprovalViewModel(new AiApprovalRequest("empty_trash",
                enki.Localization.LocalizedTextFormatter.Format("Assistant_Approval_DeleteForever_one", 1), ["Nota antigua"]));
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var text = RenderedText(view);
            Assert.DoesNotContain("Plan semanal", text);
            Assert.Contains("Nota antigua", text);
            var toggle = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "ActivitiesToggle" && ReferenceEquals(button.DataContext, reply));
            var list = view.GetVisualDescendants().OfType<ItemsControl>()
                .Single(control => control.Name == "ActivitiesList" && ReferenceEquals(control.DataContext, reply));
            Assert.False(reply.IsActivitiesExpanded);
            Assert.False(list.IsEffectivelyVisible);
            Assert.Contains(reply.ActivitiesLabel.ToUpperInvariant(), text);
            SavePreview(view, "assistant-tools-collapsed");
            reply.ReportActivity(new AiToolActivity { Tool = "read_note", Target = "Lectura nueva", State = AiToolActivityState.Running });
            Assert.False(reply.IsActivitiesExpanded);
            toggle.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(list.IsEffectivelyVisible);
            Assert.Contains("Plan semanal", RenderedText(view));
            Assert.Contains("Lectura nueva", RenderedText(view));
            reply.ReportActivity(new AiToolActivity { Tool = "list_notes", State = AiToolActivityState.Succeeded });
            Assert.True(reply.IsActivitiesExpanded);
            Assert.False(new AiChatMessageViewModel(reply.Model).IsActivitiesExpanded);
            Assert.DoesNotContain("[[", text);
            Assert.True(reply.HasActivities);
            Assert.True(reply.HasPendingApproval);
            SavePreview(view, "assistant-tools-approval");
            toggle.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(list.IsEffectivelyVisible);
            Assert.Contains("Nota antigua", RenderedText(view));
            reply.PendingApproval.DenyCommand.Execute(null);
            Assert.True(reply.PendingApproval.Decision.IsCompletedSuccessfully);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SidePanel_SharesTheConversationAndDraft_AndOpensHistoryOnDemand()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, settings) =>
        {
            var chat = new AiChatSession { Title = "Plan semanal", VaultId = VaultId };
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "¿Qué toca esta semana?" });
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Text = "Revisar el **lanzamiento**." });
            chats.Save(chat);
            settings.Settings.Ai.LastChatByVault[VaultId] = chat.Id;
        });
        try
        {
            vm.DraftText = "Borrador desde la sección";
            var panel = new AssistantPanelView { DataContext = vm };
            window.Content = panel;
            window.Width = 420;
            Pump(() =>
            {
                window.GetLayoutManager()!.ExecuteLayoutPass();
                return panel.GetVisualDescendants().OfType<ChatMarkdownView>().Any(m => m.IsEffectivelyVisible);
            });

            var text = RenderedText(panel);
            Assert.Contains("Plan semanal", text);
            Assert.Contains("lanzamiento", text);
            Assert.Equal("Borrador desde la sección", Find<TextBox>(panel, "ComposerBox").Text);
            Assert.Contains("compact", panel.GetVisualDescendants().OfType<AssistantComposerView>().Single().Classes);
            SavePreview(panel, "assistant-side-panel", 420, 860);

            Find<Button>(panel, "PanelHistoryToggle").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(vm.IsPanelHistoryOpen);
            Assert.True(panel.GetVisualDescendants().OfType<AssistantHistoryList>().Single().IsEffectivelyVisible);
            SavePreview(panel, "assistant-side-panel-history", 420, 860);

            // Chat vacío en el panel: su propia bienvenida, con el compositor fijo abajo (no centrado).
            Find<Button>(panel, "PanelHistoryToggle").Command!.Execute(null);
            vm.NewChatCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(Find<Panel>(panel, "WelcomeHost").IsEffectivelyVisible);
            Assert.True(panel.GetVisualDescendants().OfType<AssistantWelcomeView>().Single().IsEffectivelyVisible);
            var panelComposer = panel.GetVisualDescendants().OfType<AssistantComposerView>().Single();
            Assert.DoesNotContain("hero", panelComposer.Classes);
            Assert.Equal(panel.Bounds.Height, panelComposer.TranslatePoint(new Point(0, panelComposer.Bounds.Height), panel)!.Value.Y, 1);
            SavePreview(panel, "assistant-side-panel-welcome", 420, 860);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ReadAccessCard_AsksInsideTheReply_NamingScopeAndProvider()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, settings) =>
        {
            var chat = new AiChatSession { Title = "Buscar", VaultId = VaultId };
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Busca mis notas de viaje" });
            chat.Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Text = "Voy a buscar." });
            chats.Save(chat);
            settings.Settings.Ai.LastChatByVault[VaultId] = chat.Id;
        });
        try
        {
            var reply = vm.Messages.Last();
            reply.PendingReadAccess = new AiReadAccessRequestViewModel("Vault", "Claude");
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var text = RenderedText(view);
            Assert.Contains(reply.PendingReadAccess.Question, text);
            Assert.Contains("Claude", reply.PendingReadAccess.Detail);
            Assert.DoesNotContain("[[", text);
            SavePreview(view, "assistant-read-access");
            reply.PendingReadAccess.AllowCommand.Execute(null);
            Assert.True(reply.PendingReadAccess.Decision.IsCompletedSuccessfully);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void UnassignedChat_ShowsTheLinkBanner_AndLocksTheComposer()
    {
        if (OperatingSystem.IsWindows()) return;
        var (view, vm, window, _) = Create(FakeCli(loggedIn: true), (chats, _) =>
        {
            var old = new AiChatSession { Title = "Conversación antigua" };
            old.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Hola" });
            chats.Save(old);
        });
        try
        {
            Assert.Contains(vm.ChatGroups, group => group.IsUnassigned);
            vm.SelectSessionCommand.Execute(vm.Sessions.Single());
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.Contains(enki.Localization.LocalizationService.Instance["Assistant_Unassigned_Banner"], RenderedText(view));
            Assert.False(Find<TextBox>(view, "ComposerBox").IsEffectivelyEnabled);
            SavePreview(view, "assistant-unassigned");

            vm.AssignActiveChatToVaultCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<TextBox>(view, "ComposerBox").IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }
}
