using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    private readonly VaultService _vaultService;
    private readonly VaultBackupService _vaultBackupService;
    private readonly VaultSettingsService _vaultSettingsService;
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

    /// <summary>Inbox button in the title bar — kanban cards with an approaching or past due date.</summary>
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

    [ObservableProperty]
    private bool _isSettingsActive;

    [ObservableProperty]
    private bool _isRemindersActive;

    [ObservableProperty]
    private bool _isNotificationsActive;

    [ObservableProperty]
    private bool _isStatisticsActive;

    [ObservableProperty]
    private bool _useCustomTitleBar;

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
        IsSettingsActive = false;
        IsRemindersActive = false;
        IsNotificationsActive = false;
        IsStatisticsActive = page == AppPage.Statistics;

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
            _ => Dashboard
        };
        AnimatePageReveal();

        if (page == AppPage.Dashboard) Dashboard!.Reload();
        if (page == AppPage.Files) Files!.Reload();
        if (page == AppPage.Skills) Skills!.Reload();
        if (page == AppPage.Sessions) Sessions!.Reload();
        if (page == AppPage.Kanban) Kanban!.Reload();
        if (page == AppPage.Habits) Habits!.Reload();
        if (page == AppPage.Statistics) Statistics!.Reload();
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
        IsSettingsActive = true;
        IsRemindersActive = false;
        IsNotificationsActive = false;
        IsStatisticsActive = false;
        CurrentPage = Settings;
        AnimatePageReveal();
    }

    [RelayCommand]
    private void OpenReminders()
    {
        foreach (var item in NavItems) item.IsActive = false;
        IsSettingsActive = false;
        IsNotificationsActive = false;
        IsRemindersActive = true;
        IsStatisticsActive = false;
        CurrentPage = Reminders;
        AnimatePageReveal();
        Reminders?.Reload();
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        foreach (var item in NavItems) item.IsActive = false;
        IsSettingsActive = false;
        IsRemindersActive = false;
        IsNotificationsActive = true;
        IsStatisticsActive = false;
        CurrentPage = Notifications;
        AnimatePageReveal();
        Notifications?.Reload();
    }

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
}
