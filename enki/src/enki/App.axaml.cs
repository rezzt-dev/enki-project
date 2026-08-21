using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using enki.Localization;
using enki.Services;

namespace enki;

public partial class App : Application
{
    public static Window? MainWindowRef { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyStoredLanguage();

            var window = new MainWindow();
            MainWindowRef = window;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the user's saved UI language, if any, before any window/viewmodel is constructed —
    /// docs/agent-docs/internacionalizacion-i18n.md §2.2. When <c>Settings.Language</c> is still null/empty
    /// (first run, or an upgrade from a pre-i18n install), this is a no-op: the UI stays on
    /// <see cref="LocalizationService"/>'s default source language until the first-run language picker
    /// (Fase 4) calls <see cref="LocalizationService.SetLanguage"/> and persists the choice.</summary>
    private static void ApplyStoredLanguage()
    {
        var settingsService = new SettingsService();
        var language = settingsService.Settings.Language;
        if (!string.IsNullOrEmpty(language)) LocalizationService.Instance.SetLanguage(language);
    }
}
