using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.Views.Editor.Markdown;
using Material.Icons;

namespace enki.ViewModels;

/// <remarks>Es <see cref="IDisposable"/> porque posee un <see cref="VaultChangeWatcher"/>, que envuelve
/// un <see cref="System.IO.FileSystemWatcher"/> y sus temporizadores. Liberarlo es lo mismo que hace
/// <see cref="Shutdown"/> al cerrar la ventana — <see cref="Dispose"/> delega ahí y ambos son
/// idempotentes, así que da igual cuál se llame primero, o las dos veces.</remarks>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private bool _shutdownCompleted;

    private readonly SettingsService _settingsService;
    private readonly CustomThemeService _customThemeService;
    private readonly BrandLogoService _brandLogoService;
    private readonly VaultService _vaultService;
    private readonly NoteSearchService _noteSearchService;
    private readonly VaultBackupService _vaultBackupService;
    private readonly VaultSettingsService _vaultSettingsService;
    private readonly WebClipService _webClipService;
    private readonly GlobalHotkeyService _globalHotkeyService;
    private readonly Action _shortcutChangedHandler;
    private string? _appliedQuickCaptureGesture;

    /// <summary>Vigilancia de los cambios que otro programa hace sobre el vault (Syncthing, Dropbox, otro
    /// editor). Vive aquí, y no dentro de Notas, porque su ciclo de vida es el del vault activo, no el de
    /// una pantalla: se arranca al abrir el vault, se reinicia al cambiarlo y se libera al cerrar la app.
    /// Ver <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.</summary>
    private readonly VaultChangeWatcher _vaultChangeWatcher;

    /// <summary>Plantillas de notas de <c>&lt;vault&gt;/.enki/templates/</c>. Se comparte instancia entre
    /// Documentos (que las aplica) y Ajustes (que elige la de la nota diaria) para que crear una plantilla
    /// desde el selector aparezca en el desplegable de Ajustes sin recargar nada, a través de
    /// <see cref="NoteTemplateService.TemplatesChanged"/>.</summary>
    private readonly NoteTemplateService _noteTemplateService;
    private readonly HabitService _habitService;
    private readonly SessionService _sessionService;
    private readonly KanbanService _kanbanService;
    private readonly NotificationService _notificationService;
    private readonly DismissedReminderService _dismissedReminderService;
    private readonly SnoozedReminderService _snoozedReminderService;

    /// <summary>Aviso de versión nueva (T9). Se construye siempre pero no abre ninguna conexión hasta que
    /// alguien comprueba: sin el ajuste puesto, <c>CheckOnStartupAsync</c> sale antes de tocar la red.</summary>
    private readonly UpdateCheckService _updateCheckService;
    private readonly ReminderService _reminderService;
    private readonly PolarAuthService _polarAuthService;
    private readonly SupabaseRestClient _supabaseRestClient;
    private readonly PolarTaskService _polarTaskService;
    private readonly PolarSyncService _polarSyncService;
    private readonly PolarRealtimeService _polarRealtimeService;
    private readonly DispatcherTimer _reminderTimer;

    /// <summary>Comprueba periódicamente si toca un backup automático (Ajustes › General, Fase 1 de
    /// docs/analisis-proyecto/roadmap.md) — <see cref="VaultBackupService.RunScheduledBackupIfDue"/> es un
    /// no-op mientras el ajuste esté apagado o el intervalo configurado no se haya cumplido, así que este
    /// timer puede vivir siempre encendido, igual que <see cref="_reminderTimer"/>.</summary>
    private readonly DispatcherTimer _autoBackupTimer;

    /// <summary>Background push/pull tick — kept as the offline-first safety net even now that
    /// <see cref="PolarRealtimeService"/> pushes changes over Postgres Realtime, since a websocket can drop
    /// silently (firewalls, sleep/wake, a channel that never joined because the table isn't in the
    /// <c>supabase_realtime</c> publication yet). Only actually does anything once
    /// <see cref="PolarAuthService.IsLinked"/> is true — <c>PolarSyncService.SyncNowAsync</c> is a no-op
    /// otherwise.</summary>
    private readonly DispatcherTimer _polarSyncTimer;

    /// <summary>Tracks whether the last <see cref="PolarSyncService.SyncCompleted"/> tick ended in error, so
    /// the notification pushed in the constructor fires on the éxito→fallo transition, and again every
    /// <see cref="PolarSyncFailureRenotifyInterval"/> while the failure persists (see
    /// <see cref="_lastPolarSyncFailureNotifiedUtc"/>) instead of once and never again.</summary>
    private bool _polarSyncWasFailing;

    /// <summary>UTC instant of the last "Polar sync failed" push while <see cref="_polarSyncWasFailing"/> has
    /// stayed true — null whenever sync isn't currently failing. Fase 9 (docs/analisis-proyecto/roadmap.md):
    /// before this, a sync failure that persisted for hours (an expired session, a server outage) only ever
    /// notified once, on the initial transition — easy to dismiss and forget while the underlying problem,
    /// and the silent loss of every edit made since, kept going unnoticed.</summary>
    private DateTime? _lastPolarSyncFailureNotifiedUtc;

    /// <summary>How often to re-push the "Polar sync failed" notification while the failure persists — long
    /// enough not to spam a user who's aware and dealing with it, short enough that a problem left running
    /// overnight doesn't just have one easily-dismissed notification from hours ago.</summary>
    private static readonly TimeSpan PolarSyncFailureRenotifyInterval = TimeSpan.FromHours(1);

    private static readonly TimeSpan PolarSyncBaseInterval = TimeSpan.FromSeconds(25);

    /// <summary>Cap for the exponential backoff below — an offline device shouldn't keep the sync attempt
    /// rarer than once every 5 minutes forever, so a reconnect is still noticed reasonably soon.</summary>
    private static readonly TimeSpan PolarSyncMaxInterval = TimeSpan.FromMinutes(5);

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];

    /// <summary>The fixed bottom group of the sidebar (max <see cref="SidebarSectionCatalog.MaxBottomSections"/>,
    /// rendered above Ajustes) — same <see cref="NavItemViewModel"/> shape and same <see cref="NavigateCommand"/>
    /// wiring as <see cref="NavItems"/>, just a separate list so <c>MainWindow.axaml</c> can render it as its
    /// own fixed-position group instead of letting it scroll with the main list. Which section lands in
    /// which list is entirely data-driven (Ajustes › General › Secciones de la barra lateral) — see
    /// <see cref="RebuildNavItems"/>.</summary>
    public ObservableCollection<NavItemViewModel> BottomNavItems { get; } = [];

    public DashboardViewModel? Dashboard { get; private set; }
    public CalendarViewModel? Calendar { get; private set; }
    public NotesViewModel? Notes { get; private set; }
    public FilesViewModel? Files { get; private set; }
    public SkillsViewModel? Skills { get; private set; }
    public SessionsViewModel? Sessions { get; private set; }
    public KanbanViewModel? Kanban { get; private set; }
    public TrashViewModel? Trash { get; private set; }
    public TagManagerViewModel? TagManager { get; private set; }
    public SettingsViewModel? Settings { get; private set; }
    public RemindersViewModel? Reminders { get; private set; }
    public NotificationsViewModel? Notifications { get; private set; }
    public HabitsViewModel? Habits { get; private set; }
    public StatisticsViewModel? Statistics { get; private set; }

    [ObservableProperty]
    private VaultSetupViewModel? _vaultSetup;

    /// <summary>Pantalla de selección de idioma en el primer arranque. Se muestra antes que
    /// <see cref="VaultSetup"/> o la shell cuando <see cref="Models.AppSettings.Language"/> aún no ha sido
    /// elegido — docs/agent-docs/internacionalizacion-i18n.md §3.1.</summary>
    [ObservableProperty]
    private LanguageSetupViewModel? _languageSetup;

    /// <summary>Modo enfoque/zen (propuesta 7, docs/propuesta-nuevas-funcionalidades.md) — mirrors
    /// <see cref="NotesViewModel.IsZenMode"/> (itself mirroring whichever note tab is active) so the
    /// sidebar's own Border in MainWindow.axaml can bind its visibility to this without a fragile nested
    /// <c>Notes.ActiveEditor.IsZenMode</c> path. Set via <see cref="OnNotesPropertyChanged"/>, wired once
    /// per <see cref="Notes"/> instance in <c>InitializeShell</c>.</summary>
    [ObservableProperty]
    private bool _isZenMode;

    private void OnNotesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.IsZenMode)) IsZenMode = Notes!.IsZenMode;
    }

    /// <summary>Bell button in the title bar — system event log. See
    /// <see cref="ReminderInbox"/> for the separate, due-date-driven panel behind the inbox button.</summary>
    public NotificationCenterViewModel NotificationCenter { get; }

    /// <summary>Inbox button in the title bar — kanban cards with an approaching or past due date, merged
    /// with the user's own standalone reminders from the Recordatorios page.</summary>
    public ReminderInboxViewModel ReminderInbox { get; }

    /// <summary>Global search box in the title bar — searches notes, kanban cards, Polar tasks
    /// and reminders in parallel.</summary>
    public GlobalSearchViewModel GlobalSearch { get; }

    /// <summary>Buscador rápido de notas al estilo Obsidian: Ctrl+P para saltar a una nota.</summary>
    public QuickSwitcherViewModel QuickSwitcher { get; }

    /// <summary>Paleta de comandos unificada (propuesta 25, docs/propuesta-nuevas-funcionalidades.md):
    /// Ctrl+Shift+K para ejecutar cualquier acción registrada por el shell, no solo navegar. El registro de
    /// comandos vive en <c>MainWindowViewModel.CommandPalette.cs</c> (<c>BuildCommandPaletteItems</c>).</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>Selector "Cambiar de vault" de la cabecera del sidebar (T4,
    /// .docs/project-info/multi-vault-en-caliente.md). Siempre vivo, como <see cref="CommandPalette"/> y
    /// compañía: se construye en el constructor y no en <c>InitializeShell</c>, así que sobrevive a
    /// cualquier número de cambios de vault sin recrearse.</summary>
    public VaultSwitcherViewModel VaultSwitcher { get; }

    /// <summary>Web clipper (T3, .docs/project-info/web-clipper.md): pegar una URL y que salga una nota en
    /// <c>Inbox/</c>. Otro popup del shell, como <see cref="CommandPalette"/>, y por el mismo motivo —
    /// guardar una página no pertenece a ninguna pantalla en concreto y se pide desde la paleta de
    /// comandos, desde Documentos y desde la captura rápida.</summary>
    public WebClipViewModel WebClip { get; }

    /// <summary>Única instancia del borrador y la lógica de la mini-ventana. La ventana visual se puede
    /// recrear, pero este estado sobrevive a Esc hasta que la captura se envía.</summary>
    public QuickCaptureViewModel QuickCapture { get; }

    /// <summary>Petición agnóstica de UI para mostrar la mini-ventana. La atiende <c>MainWindow</c> tanto
    /// para el comando local como para el callback del hotkey nativo.</summary>
    public event Action? QuickCaptureRequested;

    [ObservableProperty]
    private bool _isVaultReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageRevealAllowsTranslation))]
    private ViewModelBase? _currentPage;

    /// <summary>La rejilla del calendario conserva sus coordenadas desde el primer fotograma.</summary>
    public bool PageRevealAllowsTranslation => CurrentPage is not CalendarViewModel;

    [ObservableProperty]
    private bool _isSidebarExpanded;

    [ObservableProperty]
    private double _sidebarWidth;

    /// <summary>True only while Ajustes is the open page — the one entry that never lives in
    /// <see cref="NavItems"/>/<see cref="BottomNavItems"/> (see <see cref="OpenSettings"/>), so unlike every
    /// other nav button its "active" state can't be read off a row's own <c>IsActive</c>.</summary>
    [ObservableProperty]
    private bool _isSettingsActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AreWindowControlsVisible))]
    private bool _useCustomTitleBar;

    /// <summary>Los controles propios solo se muestran cuando ambas preferencias están activadas.</summary>
    public bool AreWindowControlsVisible => UseCustomTitleBar && _settingsService.Settings.ShowWindowControls;

    private void RefreshWindowControls() => OnPropertyChanged(nameof(AreWindowControlsVisible));

    // ===================== Logo del brand header (Ajustes › Apariencia) =====================
    // El cuadro de 32×32 de la esquina superior izquierda del sidebar lleva el icono de Enki adaptativo al
    // tema o la imagen que el usuario haya importado. Lo que decide cuál es esto; quien lo pinta es
    // MainWindow.axaml.cs, que ya resolvía el icono por defecto contra el tema activo y sigue siendo el
    // único sitio que lo hace. Ver docs/agent-docs/logo-personalizado.md.

    /// <summary>Lado del cuadro del logo en el sidebar, en px — el mismo valor que
    /// <c>ShellView.axaml</c>, aquí porque la mitad es el radio que lo vuelve un círculo exacto.
    /// Se redujo de 32 a 24 para hacer el icono personalizado del panel lateral más pequeño y menos
    /// dominante junto al texto del header.</summary>
    private const double BrandLogoSize = 24;

    /// <summary>Bytes de la imagen personalizada, o <c>null</c> para usar el icono de Enki. Se pasan como
    /// bytes y no como ruta para que la vista no mantenga abierto el archivo que Ajustes puede estar a
    /// punto de reemplazar.</summary>
    public byte[]? CustomBrandLogoBytes { get; private set; }

    /// <summary>Avisa a <c>MainWindow</c> de que vuelva a pintar el logo. Un evento y no una propiedad
    /// <c>Bitmap</c> observable porque el icono por defecto solo se sabe resolver en la vista (depende del
    /// tema activo de Avalonia), así que la decisión vive aquí pero la decodificación allí.</summary>
    public event Action? BrandLogoChanged;

    /// <summary>Radio de recorte del cuadro del logo: 0 (cuadrado, como todo lo demás en el sistema de
    /// diseño) o la mitad del lado, el único círculo admitido. Solo puede ser circular con una imagen
    /// personalizada puesta — recortar el icono de Enki, que ya es una insignia cuadrada, le comería las
    /// esquinas.</summary>
    [ObservableProperty]
    private CornerRadius _brandLogoCornerRadius;

    /// <summary>Cómo se encaja la imagen en el cuadro: entera dentro de él si es cuadrada
    /// (<c>Uniform</c>), o recortada hasta llenarlo si el recorte es circular (<c>UniformToFill</c>, el
    /// comportamiento de cualquier avatar — con <c>Uniform</c> una imagen apaisada dejaría el círculo con
    /// huecos arriba y abajo).</summary>
    [ObservableProperty]
    private Stretch _brandLogoStretch = Stretch.Uniform;

    /// <summary>Lo último aplicado, para no releer la imagen del disco ni repintar el logo en cada
    /// <see cref="SettingsService.SettingsChanged"/> — que se dispara al guardar cualquier preferencia de
    /// la app, no solo estas dos.</summary>
    private string _brandLogoSignature = string.Empty;

    /// <summary>Reacción a <see cref="SettingsService.SettingsChanged"/>: solo repinta si cambió algo del
    /// logo.</summary>
    private void RefreshBrandLogo() => ApplyBrandLogo(force: false);

    /// <summary>Reacción a <see cref="BrandLogoService.LogoChanged"/>: la imagen cambió aunque los ajustes
    /// digan lo mismo que antes, así que hay que releerla sí o sí.</summary>
    private void ReloadBrandLogo() => ApplyBrandLogo(force: true);

    private void ApplyBrandLogo(bool force)
    {
        var settings = _settingsService.Settings;
        // Un ajuste encendido cuya imagen ya no está (borrada desde fuera de Enki) cae al icono por
        // defecto en vez de dejar el hueco vacío; Ajustes lo apaga solo la próxima vez que se abre.
        var useCustom = settings.UseCustomBrandLogo && _brandLogoService.HasLogo;
        var isCircular = useCustom && settings.BrandLogoShape == "Circle";

        var signature = $"{useCustom}|{isCircular}";
        if (!force && signature == _brandLogoSignature) return;
        _brandLogoSignature = signature;

        CustomBrandLogoBytes = useCustom ? _brandLogoService.ReadLogoBytes() : null;
        BrandLogoCornerRadius = new CornerRadius(isCircular ? BrandLogoSize / 2 : 0);
        BrandLogoStretch = isCircular ? Stretch.UniformToFill : Stretch.Uniform;
        BrandLogoChanged?.Invoke();
    }

    [ObservableProperty]
    private bool _notificationsPanelEnabled;

    [ObservableProperty]
    private bool _remindersPanelEnabled;

    /// <summary>Shows/hides the inbox (captura rápida) button in the title bar.</summary>
    [ObservableProperty]
    private bool _inboxPanelEnabled;

    /// <summary>Shows/hides the "Cambiar de vault" row between the sidebar's brand header and its
    /// navigation. Con el selector oculto, "Cambiar de vault" de la paleta de comandos sigue abriendo el
    /// mismo desplegable — ver el ancla del Popup en MainWindow.axaml.</summary>
    [ObservableProperty]
    private bool _vaultSwitcherEnabled;

    /// <summary>Shows/hides the "Recordatorios" entry in the sidebar's fixed bottom group.</summary>
    [ObservableProperty]
    private bool _remindersSidebarEnabled;

    /// <summary>Shows/hides the "Notificaciones" entry in the sidebar's fixed bottom group.</summary>
    [ObservableProperty]
    private bool _notificationsSidebarEnabled;

    [ObservableProperty]
    private bool _calendarPanelEnabled;

    [ObservableProperty]
    private bool _skillsPanelEnabled;

    [ObservableProperty]
    private bool _dashboardPanelEnabled;

    [ObservableProperty]
    private bool _kanbanPanelEnabled;

    [ObservableProperty]
    private bool _notesPanelEnabled;

    [ObservableProperty]
    private bool _filesPanelEnabled;

    [ObservableProperty]
    private bool _sessionsPanelEnabled;

    [ObservableProperty]
    private bool _trashPanelEnabled;

    [ObservableProperty]
    private bool _tagManagerPanelEnabled;

    [ObservableProperty]
    private bool _habitsPanelEnabled;

    [ObservableProperty]
    private bool _statisticsPanelEnabled;

    /// <summary>Zoom de interfaz (docs/funcionalidad-zoom-interfaz.md) — factor aplicado al
    /// <c>ScaleTransform</c> que envuelve el <c>Grid</c> raíz de <c>MainWindow.axaml</c>. Derivado de
    /// <c>VaultSettings.UiZoomPercent</c> (por-vault, no global), refrescado en <see cref="InitializeShell"/>
    /// y <see cref="OnVaultChanged"/> — los mismos dos puntos donde ya se recarga el resto del estado
    /// que depende del vault activo.</summary>
    [ObservableProperty]
    private double _uiZoomScale;

    /// <summary>Paso de cada pulsación de zoom (atajos de teclado y cada "click" de Ctrl+rueda) — mismo
    /// valor que el paso del slider de Ajustes, ver docs/funcionalidad-zoom-interfaz.md §3.</summary>
    public const int UiZoomStepPercent = 10;

    private const int UiZoomMinPercent = 50;
    private const int UiZoomMaxPercent = 200;

    public MaterialIconKind SidebarToggleIcon =>
        IsSidebarExpanded ? MaterialIconKind.ChevronDoubleLeft : MaterialIconKind.ChevronDoubleRight;

    partial void OnIsSidebarExpandedChanged(bool value) => OnPropertyChanged(nameof(SidebarToggleIcon));

    /// <summary>Solicita una entrada visual; la vista gestiona los fotogramas y su cancelación.</summary>
    [ObservableProperty]
    private int _pageRevealRevision;

    private void AnimatePageReveal() => PageRevealRevision++;

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        foreach (var item in NavItems) item.IsActive = item.Page == page;
        foreach (var item in BottomNavItems) item.IsActive = item.Page == page;
        IsSettingsActive = false;

        ViewModelBase? targetPage = page switch
        {
            AppPage.Dashboard => Dashboard,
            AppPage.Calendar => Calendar,
            AppPage.Notes => Notes,
            AppPage.Files => Files,
            AppPage.Skills => Skills,
            AppPage.Sessions => Sessions,
            AppPage.Kanban => Kanban,
            AppPage.Trash => Trash,
            AppPage.TagManager => TagManager,
            AppPage.Settings => Settings,
            AppPage.Habits => Habits,
            AppPage.Statistics => Statistics,
            AppPage.Reminders => Reminders,
            AppPage.Notifications => Notifications,
            _ => Dashboard
        };
        if (ReferenceEquals(CurrentPage, targetPage)) return;

        CurrentPage = targetPage;
        AnimatePageReveal();
        // Si no hay vigilancia nativa, releer los módulos de disco conserva los cambios externos.
        if (!_settingsService.Settings.VaultWatchExternalChanges || _vaultChangeWatcher.IsPolling)
            _dirtyPages.UnionWith([AppPage.Dashboard, AppPage.Files, AppPage.Skills, AppPage.Sessions,
                AppPage.Trash, AppPage.TagManager, AppPage.Statistics]);
        QueueCurrentPageRefresh();
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 248 : 76;
        _settingsService.Settings.SidebarExpanded = IsSidebarExpanded;
        _settingsService.Save();
    }

    /// <summary>Ctrl+/Ctrl+numpad+ y Ctrl+rueda hacia arriba (MainWindow.axaml.cs) — sube el zoom un
    /// escalón. Ver docs/funcionalidad-zoom-interfaz.md §3.</summary>
    [RelayCommand]
    private void ZoomIn() => AdjustUiZoom(UiZoomStepPercent);

    /// <summary>Ctrl-/Ctrl+numpad- y Ctrl+rueda hacia abajo — baja el zoom un escalón.</summary>
    [RelayCommand]
    private void ZoomOut() => AdjustUiZoom(-UiZoomStepPercent);

    /// <summary>Ctrl+0 — vuelve al 100% (el tamaño con el que ya arrancaba la app antes de esta
    /// funcionalidad).</summary>
    [RelayCommand]
    private void ZoomReset()
    {
        if (Settings is null) return;
        Settings.UiZoomPercent = 100;
    }

    /// <summary>Punto de entrada compartido por los tres atajos de arriba y por el Ctrl+rueda del
    /// ratón (<c>MainWindow.axaml.cs</c>, que no puede expresarse como un <c>KeyBinding</c> porque
    /// necesita interceptar el scroll antes de que llegue a un <c>ScrollViewer</c> normal). Escribe en
    /// <see cref="SettingsViewModel.UiZoomPercent"/> en vez de en <c>_vaultSettingsService.Settings</c>
    /// directamente para reutilizar su mismo debounce/guardado/callback — ver
    /// docs/funcionalidad-zoom-interfaz.md §2/§9 punto 5.</summary>
    public void AdjustUiZoom(int deltaPercent)
    {
        if (Settings is null) return;
        Settings.UiZoomPercent = Math.Clamp(Settings.UiZoomPercent + deltaPercent, UiZoomMinPercent, UiZoomMaxPercent);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (ReferenceEquals(CurrentPage, Settings)) return;
        foreach (var item in NavItems) item.IsActive = false;
        foreach (var item in BottomNavItems) item.IsActive = false;
        IsSettingsActive = true;
        CurrentPage = Settings;
        AnimatePageReveal();
    }

    /// <summary>Kept as its own command (rather than inlining <c>Navigate(AppPage.Reminders)</c> at every
    /// call site) since callers outside the sidebar — the command palette, the Dashboard's own "ver
    /// recordatorios" action — reach it as a plain parameterless delegate.</summary>
    [RelayCommand]
    private void OpenReminders() => Navigate(AppPage.Reminders);

    /// <summary>See <see cref="OpenReminders"/>.</summary>
    [RelayCommand]
    private void OpenNotifications() => Navigate(AppPage.Notifications);

    /// <summary>Pushes the trash's combined item count (documents + tasks + files + reminders) into the
    /// "Papelera" nav entry's badge (Fase 2 §9) — called after every explicit <see cref="TrashViewModel.Reload"/>
    /// site, matching this app's existing "reload on navigate/vault-change" refresh philosophy rather than
    /// a fully live subscription.</summary>
    private void UpdateTrashBadge()
    {
        var item = NavItems.FirstOrDefault(n => n.Page == AppPage.Trash);
        if (item is not null && Trash is not null) item.Count = Trash.TotalCount;
    }

    /// <summary>Pushes <see cref="ReminderInbox"/>'s overdue count (same "vencidas" population as the
    /// title-bar inbox badge) into the "Calendario" nav entry's badge (Fase 2 §9) — kept live via
    /// <see cref="ReminderInboxViewModel"/>'s own <see cref="KanbanService.BoardChanged"/> subscription
    /// instead of duplicating that wiring here.</summary>
    private void UpdateCalendarBadge()
    {
        var item = NavItems.FirstOrDefault(n => n.Page == AppPage.Calendar);
        if (item is not null) item.Count = ReminderInbox.OverdueCount;
    }

    /// <summary>Pushes <see cref="Reminders"/>'s unread count into whichever list (main or bottom group)
    /// currently holds the "Recordatorios" nav entry — live via the <see cref="PropertyChanged"/>
    /// subscription wired in <c>InitializeShell</c>, same "push on change" approach as
    /// <see cref="UpdateCalendarBadge"/> rather than a binding straight to <c>Reminders.UnreadCount</c>
    /// (that only worked before because the entry was a hand-written XAML button, not a data-bound row).</summary>
    private void UpdateRemindersBadge()
    {
        var item = NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Page == AppPage.Reminders);
        if (item is not null && Reminders is not null) item.Count = Reminders.UnreadCount;
    }

    /// <summary>See <see cref="UpdateRemindersBadge"/>.</summary>
    private void UpdateNotificationsBadge()
    {
        var item = NavItems.Concat(BottomNavItems).FirstOrDefault(n => n.Page == AppPage.Notifications);
        if (item is not null && Notifications is not null) item.Count = Notifications.UnreadCount;
    }

    /// <summary>Ctrl+Shift+N — jumps to Notas
    /// and starts a new note from wherever the user currently is, same navigate-then-act pattern as
    /// <see cref="OpenKanbanCard"/>.</summary>
    [RelayCommand]
    private void QuickNewNote()
    {
        Navigate(AppPage.Notes);
        Notes?.NewNoteCommand.Execute(null);
    }

    [RelayCommand]
    public void OpenGlobalSearch() => GlobalSearch.Open();

    [RelayCommand]
    public void CloseGlobalSearch() => GlobalSearch.Close();

    [RelayCommand]
    private void GlobalSearchResultSelected(GlobalSearchResultViewModel result)
    {
        GlobalSearch.OpenResult(result);
        GlobalSearch.Close();
    }

    [RelayCommand]
    public void OpenQuickSwitcher() => QuickSwitcher.IsOpen = true;

    [RelayCommand]
    public void OpenCommandPalette() => CommandPalette.IsOpen = true;

    [RelayCommand]
    public void OpenQuickCapture() => QuickCaptureRequested?.Invoke();

    /// <summary>Called from <c>MainWindow.axaml.cs</c>'s <c>Closing</c> handler, alongside the editor/settings
    /// flush calls already there. Before this, <see cref="SessionEncryptionService.LockAll"/> was never
    /// actually invoked from anywhere despite its own doc comment claiming otherwise, and no decrypted temp
    /// file was ever swept on shutdown — both meant a password-protected session's key and any decrypted
    /// PDF/attachment copy could outlive the process indefinitely (docs/version-alfa-3/01-analisis-detallado.md
    /// §4.1, Alta).</summary>
    public void Shutdown()
    {
        if (_shutdownCompleted) return;
        _shutdownCompleted = true;

        StopPageRefresh();
        _reminderTimer.Stop();
        _autoBackupTimer.Stop();
        _polarSyncTimer.Stop();

        MarkdownImageCache.Clear();
        ShortcutRegistry.Changed -= _shortcutChangedHandler;
        _globalHotkeyService.Dispose();
        _vaultChangeWatcher.Dispose();
        _webClipService.Dispose();
        _updateCheckService.Dispose();
        _vaultService.EncryptionService.LockAll();
        VaultService.CleanupStaleDecryptedTempFiles();
    }

    /// <summary>Ver el comentario de la clase: mismo trabajo que <see cref="Shutdown"/>.</summary>
    public void Dispose() => Shutdown();
}
