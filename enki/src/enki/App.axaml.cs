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
        MotionService.Apply(false);
        // Las dos marcas de arriba y esta separan las tres partes de "casi dos segundos antes de poder
        // pintar nada": inicializar la plataforma gráfica, cargar los estilos de la aplicación, y el
        // trabajo propio de Enki (ver StartupTrace). Sin ese corte, «el arranque tarda» no distingue lo que
        // se puede recortar de lo que cuesta el toolkit.
        StartupTrace.Mark("estilos de la aplicación cargados");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTrace.Mark("plataforma gráfica lista");
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
                MotionService.Apply(settingsService.Settings.UseSubtleAnimations);
                StartupTrace.Mark("ajustes, idioma y tema");

                var window = new MainWindow(settingsService);
                StartupWindowPlacement.Apply(window);
                MainWindowRef = window;
                desktop.MainWindow = window;
                StartupTrace.Mark("ventana entregada al ciclo de vida");
            }
            catch (Exception ex)
            {
                LogService.Error("No se pudo completar el arranque de Enki", ex);
                ShowStartupError();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Enseña la ventana de error de arranque y la deja como ventana principal. Público dentro del
    /// ensamblado porque el arranque ya no cabe entero en el constructor de <c>MainWindow</c>: la shell se
    /// construye después, cuando la pantalla de arranque ya está delante (ver <c>MainWindow.BuildShell</c>),
    /// y un fallo en ese tramo tiene que acabar en la misma ventana que un fallo en este bloque — si no,
    /// saldría por el dispatcher y el usuario se quedaría mirando la cortina para siempre.</summary>
    internal static void ShowStartupError()
    {
        var errorWindow = BuildStartupErrorWindow();
        StartupWindowPlacement.Apply(errorWindow);
        MainWindowRef = errorWindow;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = errorWindow;
        errorWindow.Show();
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
