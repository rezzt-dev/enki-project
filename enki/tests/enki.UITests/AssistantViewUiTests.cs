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

    public AssistantViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    /// <summary>CLI mínimo: solo responde a la comprobación de versión, ayuda y sesión.</summary>
    private string FakeCli(bool loggedIn)
    {
        var path = Path.Combine(_directory, "claude");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            case "$1" in
              --version) echo "2.1.280 (Claude Code)";;
              --help) echo "--output-format --include-partial-messages --resume --tools --safe-mode --model --system-prompt[-file]";;
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
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (view, vm, window, chats);
    }

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
        bitmap.Save(Path.Combine(preview, name + ".png"));
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
            Assert.False(view.FindControl<TextBox>("ComposerBox")!.IsEffectivelyVisible);
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
            Assert.True(view.FindControl<TextBox>("ComposerBox")!.IsEffectivelyVisible);
            Assert.Contains("ana@example.com · Claude Pro", RenderedText(view));
            SavePreview(view, "assistant-welcome");
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
            chats.Save(session);
            settings.Settings.Ai.LastChatId = session.Id;
        });
        try
        {
            Assert.Equal("Plan de lanzamiento", vm.ChatTitle);
            Assert.Equal(2, vm.Messages.Count);
            var markdown = view.GetVisualDescendants().OfType<ChatMarkdownView>().Single();
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

    [Fact]
    public void Settings_AiSection_ExplainsHowClaudeIsUsed()
    {
        var settings = new SettingsService(_directory);
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
        }
        finally
        {
            window.Close();
        }
    }
}
