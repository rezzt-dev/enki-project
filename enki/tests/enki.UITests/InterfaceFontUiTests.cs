using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.UITests;

/// <summary>La fuente de la interfaz tiene que llegar a todo el texto de la UI —incluidas las micro-labels
/// que por defecto van en JetBrains Mono y los controles de FluentTheme como el placeholder de un TextBox—
/// y volver a "por defecto" tiene que restaurar las fuentes empaquetadas, dejando siempre en mono el texto
/// que lo necesita.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class InterfaceFontUiTests
{
    private const string SpaceGrotesk = "Space Grotesk";
    private const string JetBrainsMono = "JetBrainsMono Nerd Font Mono";

    [Fact]
    public void InterfaceFont_ReachesLabelsAndFluentControls_AndDefaultRestoresBundledFonts()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-interface-font-ui-" + Guid.NewGuid().ToString("N"));
        Window? window = null;
        SettingsViewModel? vm = null;
        try
        {
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            var auth = new PolarAuthService(settings);
            var notifications = new NotificationService(settings, directory);
            var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
                new PolarTaskService(directory), new ReminderService(directory), settings);
            vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(directory), new VaultSettingsService(vault),
                vault, new VaultBackupService(vault, directory), auth, sync, notifications,
                () => { }, _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { },
                _ => { }, _ => { }, _ => { }, _ => { }, () => { });

            var body = new TextBlock { Text = "Dashboard" };
            var faint = new TextBlock { Text = "hace 5 días", Classes = { "faint" } };
            var eyebrow = new TextBlock { Text = "NOTAS", Classes = { "eyebrow" } };
            var search = new TextBox { PlaceholderText = "Buscar en todo…" };
            var code = new TextBlock { Text = "var x = 1;" };
            code.Bind(TextBlock.FontFamilyProperty, code.GetResourceObservable("MonoFontFamily"));
            window = new Window
            {
                Width = 800, Height = 600,
                Content = new StackPanel { Children = { body, faint, eyebrow, search, code } }
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.InterfaceFontFamily = "Poppins";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Poppins", body.FontFamily.Name);
            Assert.Equal("Poppins", faint.FontFamily.Name);
            Assert.Equal("Poppins", eyebrow.FontFamily.Name);
            Assert.Equal("Poppins", search.FontFamily.Name);
            Assert.All(search.GetVisualDescendants().OfType<TextBlock>(), t => Assert.Equal("Poppins", t.FontFamily.Name));
            Assert.Equal(JetBrainsMono, code.FontFamily.Name);

            vm.InterfaceFontFamily = SettingsViewModel.DefaultFontLabel;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(SpaceGrotesk, body.FontFamily.Name);
            Assert.Equal(JetBrainsMono, faint.FontFamily.Name);
            Assert.Equal(JetBrainsMono, eyebrow.FontFamily.Name);
            Assert.Equal(SpaceGrotesk, search.FontFamily.Name);
            Assert.Equal(JetBrainsMono, code.FontFamily.Name);
        }
        finally
        {
            // Los recursos de la app son de todo el proceso: nunca dejar la fuente cambiada para otros tests.
            if (vm is not null) vm.InterfaceFontFamily = SettingsViewModel.DefaultFontLabel;
            vm?.FlushPendingSave();
            window?.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
