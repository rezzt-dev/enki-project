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
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly CustomThemeService _customThemeService;
    private readonly BrandLogoService _brandLogoService;
    private readonly VaultService _vaultService;
    private readonly NoteSearchService _noteSearchService;
    private readonly VaultBackupService _vaultBackupService;
    private readonly VaultSettingsService _vaultSettingsService;

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

    [ObservableProperty]
    private bool _isVaultReady;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

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
    private bool _useCustomTitleBar;

    // ===================== Logo del brand header (Ajustes › Apariencia) =====================
    // El cuadro de 32×32 de la esquina superior izquierda del sidebar lleva el icono de Enki adaptativo al
    // tema o la imagen que el usuario haya importado. Lo que decide cuál es esto; quien lo pinta es
    // MainWindow.axaml.cs, que ya resolvía el icono por defecto contra el tema activo y sigue siendo el
    // único sitio que lo hace. Ver docs/agent-docs/logo-personalizado.md.

    /// <summary>Lado del cuadro del logo en el sidebar, en px — el mismo valor que
    /// <c>MainWindow.axaml</c>, aquí porque la mitad es el radio que lo vuelve un círculo exacto.</summary>
    private const double BrandLogoSize = 32;

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

    /// <summary>Page-entry reveal (Fase 2 §7, same Opacity 0→1 + TranslateY 8px/200-300ms CubicEaseOut
    /// technique as <see cref="CalendarViewModel"/>'s own reveal) applied once at the shell level around
    /// <see cref="CurrentPage"/> instead of duplicated per page, so every <see cref="Navigate"/>/
    /// <see cref="OpenSettings"/>/<see cref="OpenReminders"/>/<see cref="OpenNotifications"/> switch gets it
    /// for free.</summary>
    [ObservableProperty]
    private double _pageRevealOpacity = 1;

    [ObservableProperty]
    private double _pageRevealOffsetY;

    private const double PageRevealTravelPixels = 8;

    private void AnimatePageReveal()
    {
        PageRevealOpacity = 0;
        PageRevealOffsetY = PageRevealTravelPixels;
        Dispatcher.UIThread.Post(() =>
        {
            PageRevealOpacity = 1;
            PageRevealOffsetY = 0;
        }, DispatcherPriority.Loaded);
    }

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        foreach (var item in NavItems) item.IsActive = item.Page == page;
        foreach (var item in BottomNavItems) item.IsActive = item.Page == page;
        IsSettingsActive = false;

        CurrentPage = page switch
        {
            AppPage.Dashboard => Dashboard,
            AppPage.Calendar => Calendar,
            AppPage.Notes => Notes,
            AppPage.Files => Files,
            AppPage.Skills => Skills,
            AppPage.Sessions => Sessions,
            AppPage.Kanban => Kanban,
            AppPage.Trash => Trash,
            AppPage.Settings => Settings,
            AppPage.Habits => Habits,
            AppPage.Statistics => Statistics,
            AppPage.Reminders => Reminders,
            AppPage.Notifications => Notifications,
            _ => Dashboard
        };
        AnimatePageReveal();

        if (page == AppPage.Dashboard) Dashboard!.Reload();
        if (page == AppPage.Files) Files!.Refresh();
        if (page == AppPage.Skills) Skills!.Reload();
        if (page == AppPage.Sessions) Sessions!.Reload();
        if (page == AppPage.Kanban) Kanban!.Reload();
        if (page == AppPage.Habits) Habits!.Reload();
        if (page == AppPage.Statistics) Statistics!.Reload();
        if (page == AppPage.Reminders) Reminders!.Reload();
        if (page == AppPage.Notifications) Notifications!.Reload();
        if (page == AppPage.Trash)
        {
            Trash!.Reload();
            UpdateTrashBadge();
        }
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

    /// <summary>Called from <c>MainWindow.axaml.cs</c>'s <c>Closing</c> handler, alongside the editor/settings
    /// flush calls already there. Before this, <see cref="SessionEncryptionService.LockAll"/> was never
    /// actually invoked from anywhere despite its own doc comment claiming otherwise, and no decrypted temp
    /// file was ever swept on shutdown — both meant a password-protected session's key and any decrypted
    /// PDF/attachment copy could outlive the process indefinitely (docs/version-alfa-3/01-analisis-detallado.md
    /// §4.1, Alta).</summary>
    public void Shutdown()
    {
        _vaultService.EncryptionService.LockAll();
        VaultService.CleanupStaleDecryptedTempFiles();
    }
}
