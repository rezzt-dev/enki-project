using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Iconos adaptativos al tema: fondo oscuro con E clara en tema claro, fondo
        // claro con E oscura en tema oscuro, para mantener contraste en la barra de
        // título/taskbar del SO y en el brand del sidebar (docs/project-icons/).
        UpdateWindowIcon();
        UpdateBrandIcon();
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += (_, _) =>
            {
                UpdateWindowIcon();
                UpdateBrandIcon();
            };

        var viewModel = new MainWindowViewModel();
        DataContext = viewModel;

        ApplyTitleBarMode(viewModel.UseCustomTitleBar);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.UseCustomTitleBar))
                ApplyTitleBarMode(viewModel.UseCustomTitleBar);
        };

        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateMaximizeRestoreGlyph();
        };

        // Ctrl+rueda del ratón para el zoom de interfaz (docs/funcionalidad-zoom-interfaz.md §3),
        // igual que Obsidian. Se registra en fase Tunnel (de la ventana hacia el control bajo el
        // cursor) y marca Handled=true solo cuando Ctrl está pulsado, para interceptar el gesto antes
        // de que llegue a un ScrollViewer normal (Kanban, listas…) sin tocar el scroll de siempre.
        AddHandler(PointerWheelChangedEvent, OnCtrlWheelChanged, RoutingStrategies.Tunnel);

        // The note editor's autosave is debounced (EditorViewModel.SaveDebounceDelay) — without this, a
        // tab closed right as the window closes could lose whatever was typed in that last window. Same
        // reasoning for Ajustes' own slider debounce (SettingsViewModel.SettingsSaveDebounceDelay), which
        // now also flushes the vault-scoped zoom level.
        Closing += (_, _) =>
        {
            viewModel.Notes?.FlushAllEditors();
            viewModel.Settings?.FlushPendingSave();
            // Fase 1 (docs/analisis-proyecto/roadmap.md): the Skills editor debounces its own autosave the
            // same 600ms as Notes/Ajustes but was never flushed here, so closing the app right after
            // editing a skill silently dropped the last pending change.
            viewModel.Skills?.ActiveEditor?.FlushPendingSave();
        };
    }

    private void OnCtrlWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (DataContext is not MainWindowViewModel viewModel) return;

        viewModel.AdjustUiZoom(e.Delta.Y > 0 ? MainWindowViewModel.UiZoomStepPercent : -MainWindowViewModel.UiZoomStepPercent);
        e.Handled = true;
    }

    /// <summary>Switches the window between Enki's own title bar (drag region + min/max/close) and the
    /// OS-drawn one. Toggled live from the "Usar barra de título propia" setting — see
    /// <see cref="SettingsViewModel.OnUseCustomTitleBarChanged"/>. <see cref="WindowDecorations.BorderOnly"/>
    /// would keep a resizable border, but Avalonia draws that border itself from its own (light,
    /// rounded) decorations theme — it ignores Enki's dark palette entirely and shows up as a mismatched
    /// strip above the app. <see cref="WindowDecorations.None"/> drops that border too, so resizing is
    /// replaced by the grip regions at the bottom of this file (ResizeGrip_PointerPressed).</summary>
    private void ApplyTitleBarMode(bool useCustomTitleBar)
    {
        WindowDecorations = useCustomTitleBar ? WindowDecorations.None : WindowDecorations.Full;
        ExtendClientAreaToDecorationsHint = useCustomTitleBar;
    }

    /// <summary>Resuelve si la app está actualmente en tema oscuro, teniendo en cuenta
    /// el tema real (<see cref="Application.ActualThemeVariant"/>) y, si es <c>Default</c>,
    /// el tema solicitado (<see cref="Application.RequestedThemeVariant"/>).</summary>
    private static bool IsDarkTheme()
    {
        if (Application.Current is null) return true;

        var actual = Application.Current.ActualThemeVariant;
        if (actual == ThemeVariant.Dark) return true;
        if (actual == ThemeVariant.Light) return false;
        return Application.Current.RequestedThemeVariant != ThemeVariant.Light;
    }

    /// <summary>Actualiza el icono de la ventana según el tema actual: versión clara
    /// (<c>enki-light.ico</c>) sobre fondos oscuros de la app/ SO, versión oscura
    /// (<c>enki.ico</c>) sobre fondos claros. Mantiene la E legible en ambos modos.</summary>
    private void UpdateWindowIcon()
    {
        var resourceName = IsDarkTheme() ? "enki-light.ico" : "enki.ico";
        var uri = new Uri($"avares://enki/assets/icon/{resourceName}");
        try
        {
            using var stream = AssetLoader.Open(uri);
            Icon = new WindowIcon(stream);
        }
        catch (Exception)
        {
            // Si el asset no está disponible (diseño, tests), deja el icono que traiga la ventana.
        }
    }

    /// <summary>Actualiza el logo del brand header del sidebar con la misma versión
    /// adaptativa del icono (PNG 1024×1024 escalado a 32×32).</summary>
    private void UpdateBrandIcon()
    {
        var resourceName = IsDarkTheme() ? "enki-light-1024.png" : "enki-1024.png";
        var uri = new Uri($"avares://enki/assets/icon/{resourceName}");
        try
        {
            using var stream = AssetLoader.Open(uri);
            BrandIcon.Source = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Si el asset no está disponible, el Image queda vacío y el sidebar sigue usable.
        }
    }

    private void UpdateMaximizeRestoreGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !isMaximized;
        RestoreGlyph.IsVisible = isMaximized;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximizeRestore() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Edge/corner grips (see MainWindow.axaml) stand in for the OS resize border that
    /// <see cref="WindowDecorations.None"/> removes. Each grip carries its <see cref="WindowEdge"/> as
    /// a string Tag so all eight share this one handler.</summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } || !Enum.TryParse<WindowEdge>(tag, out var edge)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        BeginResizeDrag(edge, e);
    }

    /// <summary>Focuses the search box as soon as the global search popup opens, so the user can start
    /// typing immediately without an extra click. The popup is already in the visual tree at this point,
    /// so we search by type rather than relying on a window-level name registration.</summary>
    private void GlobalSearchPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        var textBox = popup.FindDescendantOfType<TextBox>();
        textBox?.Focus();
        textBox?.SelectAll();
    }

    private void QuickSwitcherPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        var textBox = popup.FindDescendantOfType<TextBox>();
        textBox?.Focus();
        textBox?.SelectAll();
    }

    /// <summary>Atajos de teclado dentro del buscador rápido: flechas para mover la selección,
    /// Enter para abrir la nota seleccionada, Escape la cierra.</summary>
    private void QuickSwitcherTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                vm.QuickSwitcher.MoveSelectionDownCommand.Execute(null);
                break;
            case Key.Up:
                e.Handled = true;
                vm.QuickSwitcher.MoveSelectionUpCommand.Execute(null);
                break;
            case Key.Enter:
                e.Handled = true;
                vm.QuickSwitcher.OpenSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                e.Handled = true;
                vm.QuickSwitcher.IsOpen = false;
                break;
        }
    }
}
