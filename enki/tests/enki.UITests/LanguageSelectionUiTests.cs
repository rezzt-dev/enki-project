using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Media;
using enki.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Comprueba bindings reales del selector y el refresco XAML, sin escribir datos de desarrollo.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class LanguageSelectionUiTests
{
    [Fact]
    public void ColorPicker_InternalLabelsFollowLanguage_AndPaletteUsesHexCodes()
    {
        HeadlessApp.EnsureInitialized();
        var original = LocalizationService.Instance.CurrentCulture.Name;
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        var picker = new LocalizedColorPicker();
        var window = new Window { Content = picker, Width = 800, Height = 600 };
        try
        {
            LocalizationService.Instance.SetLanguage("de-DE");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var button = picker.GetVisualDescendants().OfType<DropDownButton>().First();
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            var alpha = content.GetVisualDescendants().OfType<Control>()
                .Single(c => c.Name == "ColorSpectrumAlphaSlider");
            Assert.Equal(LocalizationService.Instance["ColorPicker_Alpha"], AutomationProperties.GetName(alpha));
            LocalizationService.Instance.SetLanguage("fr-FR");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(LocalizationService.Instance["ColorPicker_Alpha"], AutomationProperties.GetName(alpha));
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var swatches = content.GetVisualDescendants().OfType<Panel>().Where(p => p.DataContext is Color).ToArray();
            Assert.NotEmpty(swatches);
            Assert.All(swatches, p => Assert.Equal(((Color)p.DataContext!).ToString(), ToolTip.GetTip(p)));
            flyout.Hide();
        }
        finally
        {
            window.Close();
            LocalizationService.Instance.SetLanguage(original);
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void SettingsComboBox_PersistsLanguage_AndRefreshesVisibleLabels(string culture)
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-language-ui-" + Guid.NewGuid().ToString("N"));
        var original = LocalizationService.Instance.CurrentCulture.Name;
        Window? window = null;
        SettingsViewModel? vm = null;
        try
        {
            LocalizationService.Instance.SetLanguage("es-ES");
            var settings = new SettingsService(directory);
            settings.Settings.Language = "es-ES";
            var vault = new VaultService(settings);
            var auth = new PolarAuthService(settings);
            var notifications = new NotificationService(settings, directory);
            var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
                new PolarTaskService(directory), new ReminderService(directory), settings);
            vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(directory), new VaultSettingsService(vault),
                vault, new VaultBackupService(vault, directory), auth, sync, notifications,
                () => { }, _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { },
                _ => { }, _ => { }, _ => { }, _ => { }, () => vm!.RefreshLocalizedLabels());
            var view = new SettingsView { DataContext = vm };
            window = new Window { Content = view, Width = 1200, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var combo = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => ReferenceEquals(c.ItemsSource, vm.AvailableLanguages));
            combo.SelectedItem = SupportedLanguages.Find(culture);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(culture, vm.SelectedLanguage?.CultureCode);
            Assert.Equal(culture, new SettingsService(directory).Settings.Language);
            Assert.Equal(SettingsViewModel.DefaultFontLabel, vm.InterfaceFontFamily);
            Assert.Equal(SettingsViewModel.DefaultFontLabel, vm.EditorFontFamily);
            var text = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(LocalizationService.Instance["Settings_General_Language_Description"], text);
        }
        finally
        {
            vm?.FlushPendingSave();
            window?.Close();
            LocalizationService.Instance.SetLanguage(original);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FirstRun_ButtonsExposeNativeNames_AndBindTheCorrectCulture()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), "enki-language-setup-ui-" + Guid.NewGuid().ToString("N"));
        var original = LocalizationService.Instance.CurrentCulture.Name;
        Window? window = null;
        try
        {
            var settings = new SettingsService(directory);
            var completed = false;
            var vm = new LanguageSetupViewModel(settings, () => completed = true);
            var view = new LanguageSetupView { DataContext = vm };
            window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();
            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.CommandParameter is string).ToArray();
            Assert.Equal(6, buttons.Length);
            foreach (var language in SupportedLanguages.All)
            {
                var button = Assert.Single(buttons, b => Equals(b.CommandParameter, language.CultureCode));
                Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == language.NativeName);
                Assert.Same(vm.SelectLanguageCommand, button.Command);
            }
            var french = buttons.Single(b => Equals(b.CommandParameter, "fr-FR"));
            french.Command!.Execute(french.CommandParameter);
            Assert.True(completed);
            Assert.Equal("fr-FR", new SettingsService(directory).Settings.Language);
        }
        finally
        {
            window?.Close();
            LocalizationService.Instance.SetLanguage(original);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
