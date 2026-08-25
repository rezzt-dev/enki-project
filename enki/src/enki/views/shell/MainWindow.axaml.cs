using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;

namespace enki;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tamaño inicial vía ClientSize en vez de Width/Height en el XAML: Avalonia deja Width/Height
        // como un valor de layout persistente que en X11 compite con los ConfigureNotify nativos del
        // redimensionado por arrastre (grips propios, BeginResizeDrag más abajo), así que el marco de la
        // ventana crece pero el contenido se queda arreglado al tamaño fijo, dejando franjas negras sin
        // pintar alrededor (bug conocido de Avalonia en X11 — AvaloniaUI/Avalonia#2329). ClientSize solo
        // fija el tamaño de arranque sin dejar esa restricción colgada.
        ClientSize = new Size(1320, 820);

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

        // Atajos globales del shell (propuesta 29, docs/propuesta-nuevas-funcionalidades.md): ya no son
        // literales en MainWindow.axaml — se construyen aquí a partir de ShortcutRegistry y se reconstruyen
        // cada vez que Ajustes › Atajos remapea o restaura uno, sin necesidad de reiniciar Enki.
        BuildKeyBindings(viewModel);
        ShortcutRegistry.Changed += () => BuildKeyBindings(viewModel);

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
            // Fase 1 (docs/version-alfa-3/02-roadmap-implementacion.md): locks every unlocked session's DEK
            // and sweeps decrypted temp files, so nothing decrypted survives the process past this point.
            viewModel.Shutdown();
        };
    }

    /// <summary>Rebuilds every remappable shell-level <c>KeyBinding</c> from the current effective gestures
    /// in <see cref="ShortcutRegistry"/> — called once at startup and again whenever Ajustes › Atajos
    /// remaps or resets a shortcut (see <see cref="ShortcutRegistry.Changed"/>). The numeric-keypad zoom
    /// aliases (<c>Ctrl+Add</c>/<c>Ctrl+Subtract</c>/<c>Ctrl+NumPad0</c>) are intentionally not remappable —
    /// they stay fixed alongside whatever gesture <see cref="ShortcutIds.ZoomIn"/>/<see cref="ShortcutIds.ZoomOut"/>/
    /// <see cref="ShortcutIds.ZoomReset"/> currently resolve to, so numpad users keep zooming either way.</summary>
    private void BuildKeyBindings(MainWindowViewModel vm)
    {
        KeyBindings.Clear();

        void Add(string shortcutId, ICommand command, object? parameter = null)
        {
            var binding = new KeyBinding { Gesture = ShortcutRegistry.GetGesture(shortcutId), Command = command };
            if (parameter is not null) binding.CommandParameter = parameter;
            KeyBindings.Add(binding);
        }

        Add(ShortcutIds.GlobalSearch, vm.OpenGlobalSearchCommand);
        Add(ShortcutIds.QuickSwitcher, vm.OpenQuickSwitcherCommand);
        Add(ShortcutIds.CommandPalette, vm.OpenCommandPaletteCommand);
        Add(ShortcutIds.QuickNewNote, vm.QuickNewNoteCommand);
        Add(ShortcutIds.NavigateDashboard, vm.NavigateCommand, AppPage.Dashboard);
        Add(ShortcutIds.NavigateNotes, vm.NavigateCommand, AppPage.Notes);
        Add(ShortcutIds.NavigateKanban, vm.NavigateCommand, AppPage.Kanban);
        Add(ShortcutIds.NavigateCalendar, vm.NavigateCommand, AppPage.Calendar);
        Add(ShortcutIds.NavigateFiles, vm.NavigateCommand, AppPage.Files);
        Add(ShortcutIds.NavigateSessions, vm.NavigateCommand, AppPage.Sessions);
        Add(ShortcutIds.NavigateTrash, vm.NavigateCommand, AppPage.Trash);
        Add(ShortcutIds.NavigateSkills, vm.NavigateCommand, AppPage.Skills);
        Add(ShortcutIds.NavigateStatistics, vm.NavigateCommand, AppPage.Statistics);
        Add(ShortcutIds.OpenSettings, vm.OpenSettingsCommand);
        Add(ShortcutIds.ZoomIn, vm.ZoomInCommand);
        Add(ShortcutIds.ZoomOut, vm.ZoomOutCommand);
        Add(ShortcutIds.ZoomReset, vm.ZoomResetCommand);

        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Add, KeyModifiers.Control), Command = vm.ZoomInCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Subtract, KeyModifiers.Control), Command = vm.ZoomOutCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.NumPad0, KeyModifiers.Control), Command = vm.ZoomResetCommand });
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

    private void CommandPalettePopup_Opened(object? sender, EventArgs e)
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

    /// <summary>Mismos atajos que <see cref="QuickSwitcherTextBox_KeyDown"/> para la paleta de comandos
    /// (propuesta 25, docs/propuesta-nuevas-funcionalidades.md): flechas mueven la selección, Enter ejecuta
    /// el comando seleccionado, Escape cierra el popup sin ejecutar nada.</summary>
    private void CommandPaletteTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                vm.CommandPalette.MoveSelectionDownCommand.Execute(null);
                break;
            case Key.Up:
                e.Handled = true;
                vm.CommandPalette.MoveSelectionUpCommand.Execute(null);
                break;
            case Key.Enter:
                e.Handled = true;
                vm.CommandPalette.OpenSelectedCommand.Execute(null);
                break;
            case Key.Escape:
                e.Handled = true;
                vm.CommandPalette.IsOpen = false;
                break;
        }
    }
}
