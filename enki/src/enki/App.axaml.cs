using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using enki.Localization;
using enki.Services;
using System;
using System.Globalization;
using Avalonia.Interactivity;

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
            try
            {
                // Una sola instancia desde la primera lectura de idioma/tema hasta toda la composición
                // de la shell. El constructor no es una lectura inocua: puede migrar el vault, guardar y
                // acceder al llavero, por lo que repetirlo aquí triplicaba esos efectos en cada arranque.
                var settingsService = new SettingsService();
                ApplyStoredLanguage(settingsService);
                ApplyStoredThemeVariant(settingsService);

                var window = new MainWindow(settingsService);
                MainWindowRef = window;
                desktop.MainWindow = window;
            }
            catch (Exception ex)
            {
                LogService.Error("No se pudo completar el arranque de Enki", ex);
                var errorWindow = BuildStartupErrorWindow();
                MainWindowRef = errorWindow;
                desktop.MainWindow = errorWindow;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the user's saved UI language, if any, before any window/viewmodel is constructed —
    /// docs/agent-docs/internacionalizacion-i18n.md §2.2. When <c>Settings.Language</c> is still null/empty
    /// (first run, or an upgrade from a pre-i18n install), this is a no-op: the UI stays on
    /// <see cref="LocalizationService"/>'s default source language until the first-run language picker
    /// (Fase 4) calls <see cref="LocalizationService.SetLanguage"/> and persists the choice.</summary>
    private static void ApplyStoredLanguage(SettingsService settingsService)
    {
        var language = settingsService.Settings.Language;
        if (!string.IsNullOrEmpty(language)) LocalizationService.Instance.SetLanguage(language);
    }

    /// <summary>Sets <see cref="RequestedThemeVariant"/> from the saved light/dark preference before the
    /// main window is shown, avoiding a dark flash for users who saved the light theme. Custom themes are
    /// re-applied later by <see cref="MainWindowViewModel"/>, so this only affects the built-in variant.</summary>
    private static void ApplyStoredThemeVariant(SettingsService settingsService)
    {
        if (Current is not null)
        {
            Current.RequestedThemeVariant = settingsService.Settings.Theme == "Light"
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    private static Window BuildStartupErrorWindow()
    {
        var loc = LocalizationService.Instance;
        var logPath = LogService.LogDirectory ?? loc["Startup_Error_LogUnavailable"];
        var window = new Window
        {
            Title = $"Enki — {loc["Startup_Error_Title"]}",
            Width = 560,
            Height = 260,
            MinWidth = 420,
            MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = loc["Startup_Error_Title"], FontSize = 20 },
                new TextBlock
                {
                    Text = string.Format(CultureInfo.CurrentCulture, loc["Startup_Error_Message"], logPath),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new Button { Content = loc["Common_Close"], HorizontalAlignment = HorizontalAlignment.Right }
            }
        };
        ((Button)((StackPanel)window.Content).Children[2]).Click += (_, _) => window.Close();
        return window;
    }

    private void TrayQuickCapture_Click(object? sender, EventArgs e)
    {
        if (MainWindowRef is MainWindow window) window.ShowQuickCapture();
    }

    private void TrayOpenEnki_Click(object? sender, EventArgs e)
    {
        if (MainWindowRef is MainWindow window) window.ShowFromTray();
    }

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        if (MainWindowRef is MainWindow window) window.PrepareForExplicitExit();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.TryShutdown();
    }
}
