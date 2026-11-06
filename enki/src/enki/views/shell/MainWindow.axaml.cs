using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;

namespace enki;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private QuickCaptureWindow? _quickCaptureWindow;
    private bool _isExplicitExit;

    /// <summary>Si este arranque empieza con la pantalla de arranque puesta (ver <see cref="StartupGate"/>
    /// y <see cref="BuildShell"/>). Se resuelve en el constructor y no cambia: la cortina puede volver a
    /// aparecer más tarde —al elegir idioma, al abrir el primer vault—, pero eso ya lo pide el ViewModel
    /// enlazando <c>IsSectionWarmUpActive</c>, no esta bandera.</summary>
    private readonly bool _startsCovered;

    /// <summary>La shell en sí (sidebar, barra de título, host de secciones), construida por
    /// <see cref="BuildShell"/> y nula hasta entonces.</summary>
    private ShellView? _shell;

    /// <summary>Igual que <see cref="_shell"/>, pero para el ViewModel que la alimenta: casi todo el trabajo
    /// de arranque vive en su constructor, así que no puede existir antes de que haya cortina que lo tape.</summary>
    private MainWindowViewModel? _viewModel;

    /// <summary>Logo ya resuelto para el tema actual. Se guarda porque la cortina puede estar sola durante
    /// los primeros segundos —sin ViewModel y sin shell— y porque la shell que se construya después tiene
    /// que nacer con el logo puesto en vez de con un hueco hasta el próximo cambio de tema.</summary>
    private IImage? _brandLogo;

    private bool _shellBuilt;
    private bool _closed;

    public MainWindow() : this(new SettingsService()) { }

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        StartupTrace.Mark("XAML de la ventana inflado");

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
        if (Application.Current is not null)
            Application.Current.ActualThemeVariantChanged += (_, _) =>
            {
                UpdateWindowIcon();
                UpdateBrandIcon();
            };

        // Resuelve también el logo personalizado antes del primer fotograma, desde los mismos ajustes
        // y archivo que usa la shell. Así no hay un primer launcher con la E y otro con el logo elegido.
        UpdateBrandIcon();

        // Si este arranque abre la shell directamente (idioma elegido, vault configurado, una sola
        // instancia), el primer fotograma ya lleva la cortina opaca. OnOpened inicia solo su animación.
        // Si no —primer
        // arranque, pantalla de vault, segunda instancia— no hay nada que tapar y la shell se construye sin
        // más: no se cubre una pantalla de configuración con una cortina que nadie va a retirar.
        _startsCovered = StartupGate.OpensShellDirectly(settingsService);
        ApplyTitleBarMode(settingsService.Settings.UseCustomTitleBar);
        if (_startsCovered) StartupOverlay.PrepareForOpening();

        // Ctrl+rueda del ratón para el zoom de interfaz (docs/funcionalidad-zoom-interfaz.md §3),
        // igual que Obsidian. Se registra en fase Tunnel (de la ventana hacia el control bajo el
        // cursor) y marca Handled=true solo cuando Ctrl está pulsado, para interceptar el gesto antes
        // de que llegue a un ScrollViewer normal (Kanban, listas…) sin tocar el scroll de siempre.
        AddHandler(PointerWheelChangedEvent, OnCtrlWheelChanged, RoutingStrategies.Tunnel);

        // La precarga de fondo solo trabaja en los huecos en los que el usuario no está tocando nada, y para
        // saberlo necesita esta señal (ver MainWindowViewModel.NoteUserInteraction): construir una sección
        // cuesta cientos de milisegundos de hilo de interfaz, así que no puede empezar justo cuando alguien
        // se pone a escribir. Va en fase Tunnel para que llegue aunque el control que acabe recibiendo el
        // evento lo marque como manejado. Nulo mientras la shell no exista: hasta entonces el usuario no
        // puede tocar nada del programa, pero la ventana ya recibe eventos.
        AddHandler(PointerPressedEvent, (_, _) => _viewModel?.NoteUserInteraction(), RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => _viewModel?.NoteUserInteraction(), RoutingStrategies.Tunnel);

        PropertyChanged += OnWindowPropertyChanged;
        Closing += OnWindowClosing;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_shellBuilt || _closed) return;
        StartupTrace.Mark("ventana visible");

        // La cortina ya estaba preparada antes de Show. Solo ahora se ponen en marcha los relojes:
        // esperar antes de mostrar la ventana no debe consumir los dos segundos ni construir la shell.
        if (_startsCovered)
        {
            StartupOverlay.Entered += BuildShell;
            StartupOverlay.BeginOpening();
            return;
        }

        BuildShell();
    }

    /// <summary>Construye la shell y sus servicios después de entregar el primer fotograma al
    /// compositor. El logo ya está delante y la barra se anima en el hilo de renderizado mientras la
    /// UI construye las vistas. La precarga retira la pantalla cuando la sección inicial está lista y
    /// ha concluido el avance de dos segundos. Un error muestra la ventana de diagnóstico y cierra esta
    /// ventana para no dejar una cortina bloqueada ni una interfaz parcialmente construida.</summary>
    private void BuildShell()
    {
        if (_shellBuilt || _closed) return;
        _shellBuilt = true;
        StartupOverlay.Entered -= BuildShell;

        try
        {
            // 1. La vista de la shell. Su XAML no está en la ventana (ver ShellView) precisamente para
            //    poder construirla aquí.
            var shell = new ShellView();
            shell.SetBrandIcon(_brandLogo);
            ShellHost.Content = shell;
            _shell = shell;
            StartupTrace.Mark("vista de la shell construida");

            // 2. Los servicios y el ViewModel de todas las secciones. Es el otro tramo caro del arranque, y
            //    a partir de aquí ya hay un ViewModel al que enlazar los avisos que se quedaron sin receptor.
            var viewModel = new MainWindowViewModel(_settingsService);
            _viewModel = viewModel;
            DataContext = viewModel;
            StartupTrace.Mark("servicios y ViewModels listos");

            viewModel.QuickCaptureRequested += ShowQuickCapture;
            viewModel.SectionPreloadRequested += page => _shell?.Preload(page);
            viewModel.BrandLogoChanged += UpdateBrandIcon;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Atajos globales del shell (propuesta 29, docs/propuesta-nuevas-funcionalidades.md): ya no son
            // literales en MainWindow.axaml — se construyen aquí a partir de ShortcutRegistry y se reconstruyen
            // cada vez que Ajustes › Atajos remapea o restaura uno, sin necesidad de reiniciar Enki.
            BuildKeyBindings(viewModel);
            ShortcutRegistry.Changed += () => BuildKeyBindings(viewModel);

            ApplyTitleBarMode(viewModel.UseCustomTitleBar);

            // El logo personalizado solo lo conoce el ViewModel; el icono por defecto ya está resuelto desde
            // el constructor y no depende de él.
            if (viewModel.CustomBrandLogoBytes is { Length: > 0 }) UpdateBrandIcon();

            // 3. La primera medida de la shell —sidebar entera, barra de título, host de secciones— se paga
            //    aquí a propósito, con la cortina delante y sin nada animándose. Si se dejara al siguiente
            //    fotograma, competiría con el avance de la barra de progreso, que es la otra mitad de que la
            //    animación de carga no vaya a tirones.
            UpdateLayout();
            StartupTrace.Mark("primer layout de la shell");
        }
        catch (Exception ex)
        {
            // Sin esto, un fallo al levantar los servicios saldría por el dispatcher —dentro de la llamada
            // de vuelta de la cortina, fuera de cualquier try/catch— y dejaría al usuario mirando la
            // pantalla de arranque para siempre.
            LogService.Error("No se pudo completar el arranque de Enki", ex);
            StartupOverlay.IsWarmUpActive = false;
            App.ShowStartupError();
            _isExplicitExit = true;
            Close();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UseCustomTitleBar))
            ApplyTitleBarMode(_viewModel?.UseCustomTitleBar ?? false);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty) UpdateMaximizeRestoreGlyph();
    }

    /// <summary>Cierre de la ventana. El editor de notas guarda con retardo (EditorViewModel.SaveDebounceDelay)
    /// y sin volcar lo pendiente aquí, una pestaña cerrada justo al cerrar la ventana perdería lo último
    /// escrito; lo mismo vale para el retardo propio de Ajustes (SettingsViewModel.SettingsSaveDebounceDelay),
    /// que además vuelca el zoom del vault.
    ///
    /// <para>Si la shell todavía no se ha construido —la ventana se ha cerrado durante el segundo largo que
    /// tarda el arranque— no hay nada que volcar ni que cerrar: se sale por donde se entró.</para></summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_isExplicitExit && _settingsService.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _closed = true;
        if (_viewModel is not { } viewModel) return;

        if (viewModel.Notes is { } notes)
        {
            foreach (var editor in notes.OpenEditors)
            {
                if (editor.TryPrepareClose()) continue;
                e.Cancel = true;
                _closed = false;
                _isExplicitExit = false;
                ShowFromTray();
                return;
            }
        }
        viewModel.Settings?.FlushPendingSave();
        // Fase 1 (docs/analisis-proyecto/roadmap.md): the Skills editor debounces its own autosave the
        // same 600ms as Notes/Ajustes but was never flushed here, so closing the app right after
        // editing a skill silently dropped the last pending change.
        viewModel.Skills?.ActiveEditor?.FlushPendingSave();
        if (viewModel.Skills?.ActiveEditor?.IsDirty == true)
        {
            e.Cancel = true;
            _closed = false;
            _isExplicitExit = false;
            ShowFromTray();
            return;
        }
        // Fase 1 (docs/version-alfa-3/02-roadmap-implementacion.md): locks every unlocked session's DEK
        // and sweeps decrypted temp files, so nothing decrypted survives the process past this point.
        viewModel.Shutdown();
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
        Add(ShortcutIds.QuickCapture, vm.OpenQuickCaptureCommand);
        Add(ShortcutIds.NavigateDashboard, vm.NavigateCommand, AppPage.Dashboard);
        Add(ShortcutIds.NavigateNotes, vm.NavigateCommand, AppPage.Notes);
        Add(ShortcutIds.NavigateKanban, vm.NavigateCommand, AppPage.Kanban);
        Add(ShortcutIds.NavigateCalendar, vm.NavigateCommand, AppPage.Calendar);
        Add(ShortcutIds.NavigateFiles, vm.NavigateCommand, AppPage.Files);
        Add(ShortcutIds.NavigateSessions, vm.NavigateCommand, AppPage.Sessions);
        Add(ShortcutIds.NavigateTrash, vm.NavigateCommand, AppPage.Trash);
        Add(ShortcutIds.NavigateSkills, vm.NavigateCommand, AppPage.Skills);
        Add(ShortcutIds.NavigateAssistant, vm.NavigateCommand, AppPage.Assistant);
        Add(ShortcutIds.ToggleAssistantPanel, vm.ToggleAssistantPanelCommand);
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
        catch (Exception ex)
        {
            // Si el asset no está disponible (diseño, tests), deja el icono que traiga la ventana.
            LogService.Warning($"No se pudo cargar el icono de ventana '{resourceName}'", ex);
        }
    }

    /// <summary>Repinta el logo del brand header del sidebar: la imagen que el usuario haya importado
    /// (Ajustes › Apariencia › Logo) si la tiene puesta, y si no la misma versión adaptativa al tema del
    /// icono de Enki (PNG 1024×1024). El recorte —cuadrado o circular— y el encaje los pone el XAML
    /// enlazando <c>BrandLogoCornerRadius</c>/<c>BrandLogoStretch</c>, así que aquí solo se decide la
    /// fuente. Se llama al arrancar, en cada cambio de tema y cada vez que el viewmodel avisa de que el
    /// logo cambió.
    ///
    /// <para>Ninguna de las dos imágenes llega al <c>Image</c> a su tamaño original: las dos se reducen
    /// antes a <see cref="BrandLogoImage.SidebarPixels"/> con el remuestreo de calidad de Skia. Dejar que
    /// el control encogiera un PNG de 1024 px hasta un cuadro de 32 es lo que dejaba el logo dentado —ver
    /// la nota de <see cref="BrandLogoImage"/>—, y le pasaba igual al icono por defecto.</para></summary>
    private void UpdateBrandIcon()
    {
        var customBytes = DataContext is MainWindowViewModel viewModel
            ? viewModel.CustomBrandLogoBytes
            : _settingsService.Settings.UseCustomBrandLogo
                ? new BrandLogoService(_settingsService).ReadLogoBytes()
                : null;
        // Una imagen ilegible (corrupción en disco) devuelve null y cae al icono de Enki de aquí abajo,
        // en vez de dejar el sidebar sin logo.
        if (BrandLogoImage.Decode(customBytes, BrandLogoImage.SidebarPixels) is { } customLogo)
        {
            ApplyBrandLogo(customLogo);
            return;
        }

        var resourceName = IsDarkTheme() ? "enki-light-1024.png" : "enki-1024.png";
        var uri = new Uri($"avares://enki/assets/icon/{resourceName}");
        try
        {
            using var stream = AssetLoader.Open(uri);
            var logo = Bitmap.DecodeToWidth(stream, BrandLogoImage.SidebarPixels,
                BitmapInterpolationMode.HighQuality);
            ApplyBrandLogo(logo);
        }
        catch (Exception ex)
        {
            // Si el asset no está disponible, el Image queda vacío y el sidebar sigue usable.
            LogService.Warning($"No se pudo cargar el logo integrado '{resourceName}'", ex);
        }
    }

    /// <summary>Deja el mismo mapa de bits en las dos superficies donde aparece la marca: la pantalla de
    /// arranque y el brand header de la shell. 128 px de lado dan de sobra para los 56 px de la cortina, así
    /// que no se decodifica dos veces el PNG de 1024 que va empaquetado.
    ///
    /// <para>Se guarda en <see cref="_brandLogo"/> porque las dos superficies pueden llegar tarde: la
    /// cortina existe desde el primer fotograma, pero la shell no se construye hasta que la cortina está
    /// opaca (ver <see cref="BuildShell"/>) y tiene que nacer con el logo puesto.</para></summary>
    private void ApplyBrandLogo(IImage logo)
    {
        _brandLogo = logo;
        StartupOverlay.SetLogo(logo);
        _shell?.SetBrandIcon(logo);
    }

    private void UpdateMaximizeRestoreGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.IsVisible = !isMaximized;
        RestoreGlyph.IsVisible = isMaximized;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Muestra la captura sin tocar visibilidad ni estado de esta ventana principal. Reutiliza el
    /// mismo viewmodel para conservar el borrador cuando Esc cerró la ventana anterior.</summary>
    public void ShowQuickCapture()
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.QuickCapture.PrepareToShow();
        if (_quickCaptureWindow is null)
        {
            _quickCaptureWindow = new QuickCaptureWindow { DataContext = viewModel.QuickCapture };
            _quickCaptureWindow.Closed += (_, _) => _quickCaptureWindow = null;
            _quickCaptureWindow.Show();
        }
        else
        {
            if (!_quickCaptureWindow.IsVisible) _quickCaptureWindow.Show();
            _quickCaptureWindow.Activate();
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void PrepareForExplicitExit() => _isExplicitExit = true;

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

    /// <summary>Foco directo en el borrador al abrir la bandeja de entrada: el gesto es "apunto esto
    /// antes de que se me olvide", así que teclear tiene que funcionar sin un click extra. No hace
    /// SelectAll como el resto de popups de búsqueda — aquí el texto previo es un borrador que se sigue
    /// escribiendo, no una consulta que se reemplaza.</summary>
    private void InboxPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        var textBox = popup.FindDescendantOfType<TextBox>();
        textBox?.Focus();
        if (textBox is not null) textBox.CaretIndex = textBox.Text?.Length ?? 0;
    }

    private void WebClipPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        var textBox = popup.FindDescendantOfType<TextBox>();
        textBox?.Focus();
        textBox?.SelectAll();
    }

    /// <summary>Enter guarda la página y Escape cierra el popup (o cancela la captura en curso, ver
    /// <c>WebClipViewModel.Cancel</c>) — el web clipper es un solo campo, así que no necesita las flechas
    /// de los otros tres popups del shell.</summary>
    private void WebClipTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (vm.WebClip.SaveCommand.CanExecute(null)) vm.WebClip.SaveCommand.Execute(null);
                break;
            case Key.Escape:
                e.Handled = true;
                vm.WebClip.CancelCommand.Execute(null);
                break;
        }
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
