using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly SessionService _sessionService;
    private readonly KanbanService _kanbanService;
    private readonly PolarTaskService _polarTaskService;
    private readonly PolarAuthService _polarAuthService;
    private readonly NotificationService _notificationService;
    private readonly CalendarAggregationService _calendarAggregationService;
    private readonly HabitService _habitService;
    private readonly VaultSettingsService? _vaultSettingsService;
    private readonly Action<NoteFile> _openNote;
    private readonly Action _newNote;
    private readonly Action _navigateToKanban;
    private readonly Action _navigateToSessions;
    private readonly Action _navigateToNotes;
    private readonly Action _navigateToTrash;
    private readonly Action _navigateToCalendar;
    private readonly Action _openReminders;
    private readonly Action _openNotifications;
    private readonly Action _openSettings;
    private readonly Action _navigateToHabits;

    /// <summary>Evita repetir el aviso de tamaño parcial en cada <see cref="Reload"/> (p. ej. al navegar
    /// de un lado a otro del Dashboard) — solo se notifica en la transición de completo a incompleto,
    /// mismo patrón que <c>MainWindowViewModel._polarSyncWasFailing</c>.</summary>
    private bool _vaultSizeWasIncomplete;

    /// <summary>Fixed pixel width of the mini stacked-bar in "TAREAS DEL TABLERO" — a compact indicator
    /// next to the section header, not a full-width progress bar, so it stays a constant regardless of the
    /// card's own width. See <see cref="PendingBarWidth"/>/<see cref="InProgressBarWidth"/>/
    /// <see cref="DoneBarWidth"/>.</summary>
    private const double BoardBarTotalWidth = 220;

    /// <summary>Cap on how far back <see cref="LoadTodayItems"/> looks for overdue backlog — without it, a
    /// vault with months of unaddressed overdue items would fill the whole 5-item "HOY" cupo with ancient
    /// backlog and never show what's actually due today (análisis §4.4, Alta).</summary>
    private static readonly TimeSpan TodayItemsBacklogWindow = TimeSpan.FromDays(30);

    /// <summary>Max entries shown in the "HOY" card.</summary>
    private const int MaxTodayItems = 5;

    /// <summary>Most recently modified note — the large featured tile in "NOTAS RECIENTES". Its own
    /// property rather than an <c>ObservableCollection</c> indexer binding (e.g. "RecentNotes[0]"), since
    /// Avalonia doesn't refresh an indexer binding when the backing collection is cleared and repopulated
    /// on <see cref="Reload"/> — only <see cref="OtherRecentNotes"/>'s own CollectionChanged keeps that
    /// list live.</summary>
    [ObservableProperty]
    private NoteCardViewModel? _featuredNote;

    /// <summary>The rest of the up-to-6 recent notes, excluding <see cref="FeaturedNote"/> — the compact
    /// list beside the featured card.</summary>
    public ObservableCollection<NoteCardViewModel> OtherRecentNotes { get; } = [];

    /// <summary>Configurable sections of the Dashboard, ordered and filtered according to
    /// <see cref="Models.VaultSettings.DashboardSectionOrder"/> and
    /// <see cref="Models.VaultSettings.DashboardHiddenSections"/>.</summary>
    public ObservableCollection<DashboardSectionViewModel> Sections { get; } = [];

    /// <summary>Up to 5 cards from the board's "Por hacer" column, soonest due date first — see
    /// <see cref="LoadTaskColumn"/>. Read-only previews: editing/dragging still happens on the
    /// Kanban page itself.</summary>
    public ObservableCollection<KanbanCardViewModel> PendingTasks { get; } = [];

    /// <summary>Same as <see cref="PendingTasks"/> but for the "En progreso" column.</summary>
    public ObservableCollection<KanbanCardViewModel> InProgressTasks { get; } = [];

    /// <summary>Same as <see cref="PendingTasks"/> but for the "Hecho" column — completes the three-column
    /// board preview so the widget reflects the board's full state, not just the open work.</summary>
    public ObservableCollection<KanbanCardViewModel> DoneTasks { get; } = [];

    /// <summary>Up to 5 items due today or already overdue, aggregated across Kanban, Polar (if linked)
    /// and Reminders — same "hoy + vencido" population the Calendar itself would show, built by
    /// <see cref="CalendarAggregationService"/>. Never edited from here — selecting one is out of scope
    /// for this read-only preview.</summary>
    public ObservableCollection<CalendarItemViewModel> TodayItems { get; } = [];

    /// <summary>Up to 4 most recent entries from <see cref="NotificationService.Notifications"/> (already
    /// newest-first — see <see cref="NotificationService.Push"/>), read or unread, for the "ACTIVIDAD" card.
    /// Reuses <see cref="NotificationItemViewModel"/> as-is, same severity icon/label the bell dropdown
    /// already renders — a read-only feed, opening/marking-as-read still only happens from the bell.</summary>
    public ObservableCollection<NotificationItemViewModel> RecentNotifications { get; } = [];

    /// <summary>Up to 5 non-archived habits with today's done/not-done state, for the compact "Habits"
    /// card — propuesta 13 (docs/propuesta-nuevas-funcionalidades.md §13). Built the same way
    /// <see cref="HabitsViewModel.BuildItem"/> builds each row on the full Hábitos page, so both read
    /// exactly the same consistency-grid logic.</summary>
    public ObservableCollection<HabitItemViewModel> TodayHabits { get; } = [];

    public bool HasTodayHabits => TodayHabits.Count > 0;

    [ObservableProperty]
    private string _vaultPath = string.Empty;

    [ObservableProperty]
    private int _sessionCount;

    [ObservableProperty]
    private string _vaultSizeLabel = "—";

    [ObservableProperty]
    private int _totalNoteCount;

    [ObservableProperty]
    private int _pendingTaskCount;

    [ObservableProperty]
    private int _inProgressTaskCount;

    [ObservableProperty]
    private int _doneTaskCount;

    [ObservableProperty]
    private int _boardTotalCount;

    /// <summary>0–100, <see cref="DoneTaskCount"/> over <see cref="BoardTotalCount"/>, rounded — 0 when the
    /// board is empty rather than an undefined division.</summary>
    [ObservableProperty]
    private int _boardProgressPercent;

    [ObservableProperty]
    private double _pendingBarWidth;

    [ObservableProperty]
    private double _inProgressBarWidth;

    [ObservableProperty]
    private double _doneBarWidth;

    /// <summary>Mirrors <see cref="MainWindowViewModel"/>'s "Calendario" nav entry — the "CALENDARIO" card
    /// in "ACCESOS RÁPIDOS" hides itself the same way when the panel is turned off from Ajustes, instead of
    /// linking to a page the sidebar itself doesn't offer. Kept live via <see cref="SetCalendarEnabled"/>,
    /// not just read once at construction, since the setting can change without the app restarting.</summary>
    [ObservableProperty]
    private bool _isCalendarEnabled;

    /// <summary>Columnas de la rejilla de "ACCESOS RÁPIDOS": 4 con la tarjeta de Calendario visible,
    /// 3 cuando el panel está desactivado — sin esto la UniformGrid reservaría una celda vacía aunque
    /// la tarjeta oculta no ocupe sitio (los hijos invisibles no se miden ni se colocan).</summary>
    public int QuickAccessColumns => IsCalendarEnabled ? 4 : 3;

    partial void OnIsCalendarEnabledChanged(bool value) => OnPropertyChanged(nameof(QuickAccessColumns));

    /// <summary>Columnas de la rejilla en la que se vierten las secciones del Dashboard — enlazada al
    /// <see cref="Controls.DashboardGridPanel"/> de <c>DashboardView.axaml</c>. La fija
    /// <see cref="RebuildSections"/>: 6 para Classic y las plantillas (su geometría está dibujada sobre
    /// esa rejilla), y el número que el usuario haya configurado en el modo Personalizado.</summary>
    [ObservableProperty]
    private int _gridColumns = Services.DashboardLayoutEngine.DefaultColumns;

    /// <summary>Ajustes › General "Mostrar barra de desplazamiento" (Panel principal) — alimenta
    /// <see cref="ScrollBarVisibility"/>, que solo se enlaza al ScrollViewer raíz de DashboardView.
    /// Se mantiene vivo desde <see cref="MainWindowViewModel"/> (mismo patrón de callback explícito que
    /// <see cref="IsCalendarEnabled"/>), no solo leído una vez al arrancar.</summary>
    [ObservableProperty]
    private bool _showScrollbar = true;

    /// <summary>El desplazamiento en sí no cambia — solo se oculta el raíl y el pulgar.</summary>
    public Avalonia.Controls.Primitives.ScrollBarVisibility ScrollBarVisibility =>
        ShowScrollbar ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;

    partial void OnShowScrollbarChanged(bool value) => OnPropertyChanged(nameof(ScrollBarVisibility));

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private int _polarPendingCount;

    [ObservableProperty]
    private int _polarOverdueCount;

    [ObservableProperty]
    private int _unreadNotificationCount;

    [ObservableProperty]
    private int _graphNodeCount;

    [ObservableProperty]
    private int _graphEdgeCount;

    /// <summary>True mientras <see cref="LoadGraphAsync"/> recorre el vault — la tarjeta "SESIONES" muestra
    /// un spinner en vez de "0 notas · 0 enlaces" para que un vault grande no lea como vacío mientras
    /// carga.</summary>
    [ObservableProperty]
    private bool _isGraphLoading = true;

    public bool HasPendingTasks => PendingTasks.Count > 0;
    public bool HasInProgressTasks => InProgressTasks.Count > 0;
    public bool HasDoneTasks => DoneTasks.Count > 0;
    public bool HasBoardCards => BoardTotalCount > 0;
    public bool HasTodayItems => TodayItems.Count > 0;
    public bool HasUnreadNotifications => UnreadNotificationCount > 0;
    public bool HasRecentNotifications => RecentNotifications.Count > 0;
    public bool HasFeaturedNote => FeaturedNote is not null;
    public bool HasOtherRecentNotes => OtherRecentNotes.Count > 0;

    /// <summary>Drives the "ACTIVIDAD" card's empty state — nothing to show when Polar isn't linked (so no
    /// summary row) and there's no notification history either.</summary>
    public bool HasNoActivity => !IsPolarLinked && !HasRecentNotifications;

    // ===================== Computed, localized labels (i18n Fase 2 §1.3) =====================
    // Each of these replaces a fixed-word-order XAML StringFormat/Run composition with a single resource
    // key holding the full sentence template — see docs/agent-docs/internacionalizacion-i18n.md §1.3.

    /// <summary>"HOY" header chip — replaces the "{count} NOTIF. SIN LEER" Run composition.</summary>
    public string TodayUnreadChipLabel =>
        Plural.Get("Dashboard_Today_UnreadNotifChip", UnreadNotificationCount, UnreadNotificationCount);

    /// <summary>"HOY" header chip — replaces the "{pending} PENDIENTES POLAR · {overdue} VENCIDAS" Run
    /// composition. Pending/overdue are each pluralized independently (<see cref="Plural"/>) since a
    /// fixed template can't agree in number with two unrelated counts at once — e.g. Spanish needs
    /// "1 PENDIENTE" but "2 PENDIENTES".</summary>
    public string TodayPolarChipLabel =>
        Plural.Get("Dashboard_Today_PolarChip_Pending", PolarPendingCount, PolarPendingCount) + " · " +
        Plural.Get("Dashboard_Today_PolarChip_Overdue", PolarOverdueCount, PolarOverdueCount);

    /// <summary>"ACTIVIDAD" card summary row — replaces the "{pending} pendientes · {overdue} vencidas en
    /// Polar" Run composition. Same independent-pluralization reasoning as <see cref="TodayPolarChipLabel"/>.</summary>
    public string ActivityPolarSummaryLabel =>
        Plural.Get("Dashboard_Activity_PolarSummary_Pending", PolarPendingCount, PolarPendingCount) + " · " +
        Plural.Get("Dashboard_Activity_PolarSummary_Overdue", PolarOverdueCount, PolarOverdueCount);

    /// <summary>"SESIONES" preview card — replaces the "{nodes} notas · {edges} enlaces" Run composition.
    /// Independently pluralized like <see cref="TodayPolarChipLabel"/> above.</summary>
    public string SessionsGraphSummaryLabel =>
        Plural.Get("Dashboard_Sessions_GraphSummary_Notes", GraphNodeCount, GraphNodeCount) + " · " +
        Plural.Get("Dashboard_Sessions_GraphSummary_Links", GraphEdgeCount, GraphEdgeCount);

    /// <summary>"TAREAS DEL TABLERO" mini progress indicator — replaces the "{percent}% COMPLETADO" Run
    /// composition.</summary>
    public string BoardProgressLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Dashboard_BoardTasks_ProgressLabel"], BoardProgressPercent);

    partial void OnUnreadNotificationCountChanged(int value) => OnPropertyChanged(nameof(TodayUnreadChipLabel));

    partial void OnPolarPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(TodayPolarChipLabel));
        OnPropertyChanged(nameof(ActivityPolarSummaryLabel));
    }

    partial void OnPolarOverdueCountChanged(int value)
    {
        OnPropertyChanged(nameof(TodayPolarChipLabel));
        OnPropertyChanged(nameof(ActivityPolarSummaryLabel));
    }

    partial void OnGraphNodeCountChanged(int value) => OnPropertyChanged(nameof(SessionsGraphSummaryLabel));

    partial void OnGraphEdgeCountChanged(int value) => OnPropertyChanged(nameof(SessionsGraphSummaryLabel));

    partial void OnBoardProgressPercentChanged(int value) => OnPropertyChanged(nameof(BoardProgressLabel));

    private int _graphLoadGeneration;

    /// <summary>Default dashboard section order, used when <see cref="Models.VaultSettings.DashboardSectionOrder"/>
    /// is empty or missing.</summary>
    private static readonly string[] DefaultSectionOrder =
    [
        "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits"
    ];

    /// <summary>Factory for every known section keyed by its persistence key. Each instance resolves its
    /// localized title so the dashboard and settings share the same display name.</summary>
    private static readonly Dictionary<string, Func<DashboardSectionViewModel>> SectionFactories = new()
    {
        ["QuickAccess"] = () => new QuickAccessSectionViewModel
        {
            Key = "QuickAccess",
            Title = LocalizationService.Instance["DashboardSection_QuickAccess"]
        },
        ["Today"] = () => new TodaySectionViewModel
        {
            Key = "Today",
            Title = LocalizationService.Instance["DashboardSection_Today"]
        },
        ["Vault"] = () => new VaultSectionViewModel
        {
            Key = "Vault",
            Title = LocalizationService.Instance["DashboardSection_Vault"]
        },
        ["Activity"] = () => new ActivitySectionViewModel
        {
            Key = "Activity",
            Title = LocalizationService.Instance["DashboardSection_Activity"]
        },
        ["Sessions"] = () => new SessionsSectionViewModel
        {
            Key = "Sessions",
            Title = LocalizationService.Instance["DashboardSection_Sessions"]
        },
        ["BoardTasks"] = () => new BoardTasksSectionViewModel
        {
            Key = "BoardTasks",
            Title = LocalizationService.Instance["DashboardSection_BoardTasks"]
        },
        ["RecentNotes"] = () => new RecentNotesSectionViewModel
        {
            Key = "RecentNotes",
            Title = LocalizationService.Instance["DashboardSection_RecentNotes"]
        },
        ["Habits"] = () => new HabitsSectionViewModel
        {
            Key = "Habits",
            Title = LocalizationService.Instance["DashboardSection_Habits"]
        }
    };

    public DashboardViewModel(
        VaultService vaultService,
        SessionService sessionService,
        KanbanService kanbanService,
        PolarTaskService polarTaskService,
        PolarAuthService polarAuthService,
        NotificationService notificationService,
        CalendarAggregationService calendarAggregationService,
        HabitService habitService,
        Action<NoteFile> openNote,
        Action newNote,
        Action navigateToKanban,
        Action navigateToSessions,
        Action navigateToNotes,
        Action navigateToTrash,
        bool calendarEnabled,
        Action navigateToCalendar,
        Action openReminders,
        Action openNotifications,
        Action openSettings,
        Action navigateToHabits,
        bool showScrollbar,
        VaultSettingsService? vaultSettingsService = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _kanbanService = kanbanService;
        _polarTaskService = polarTaskService;
        _polarAuthService = polarAuthService;
        _notificationService = notificationService;
        _calendarAggregationService = calendarAggregationService;
        _habitService = habitService;
        _vaultSettingsService = vaultSettingsService;
        _openNote = openNote;
        _newNote = newNote;
        _navigateToKanban = navigateToKanban;
        _navigateToSessions = navigateToSessions;
        _navigateToNotes = navigateToNotes;
        _navigateToTrash = navigateToTrash;
        _navigateToCalendar = navigateToCalendar;
        _openReminders = openReminders;
        _openNotifications = openNotifications;
        _openSettings = openSettings;
        _navigateToHabits = navigateToHabits;
        IsCalendarEnabled = calendarEnabled;
        ShowScrollbar = showScrollbar;
        _notificationService.NotificationsChanged += OnNotificationsChanged;

        // VaultSizeChanged fires from a background thread once VaultService's own async recomputation
        // lands (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3) — re-read the now-fresh cached
        // value and marshal the property update onto the UI thread, same convention this codebase already
        // uses for other background-computed values (e.g. FileCardViewModel's thumbnail).
        _vaultService.VaultSizeChanged += () => Dispatcher.UIThread.Post(RefreshVaultSizeLabel);

        Reload();
    }

    private void OnNotificationsChanged() => ReloadNotifications();

    private void RefreshVaultSizeLabel()
    {
        VaultSizeLabel = FileSizeFormatter.Format(_vaultService.GetVaultSizeBytes());
        if (_vaultService.LastVaultSizeCalculationIncomplete && !_vaultSizeWasIncomplete)
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Dashboard_VaultSizeIncomplete_Title"),
                LocalizedTextFormatter.Format("Dashboard_VaultSizeIncomplete_Message"),
                NotificationSeverity.Warning);
        _vaultSizeWasIncomplete = _vaultService.LastVaultSizeCalculationIncomplete;
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(TodayUnreadChipLabel));
        OnPropertyChanged(nameof(TodayPolarChipLabel));
        OnPropertyChanged(nameof(ActivityPolarSummaryLabel));
        OnPropertyChanged(nameof(SessionsGraphSummaryLabel));
        OnPropertyChanged(nameof(BoardProgressLabel));
        Reload();
    }

    /// <summary>Called by <see cref="MainWindowViewModel"/> whenever the "Calendario" panel setting is
    /// flipped from Ajustes, so the "ACCESOS RÁPIDOS" card follows the sidebar nav entry live instead of
    /// only reflecting whatever the setting was at app startup.</summary>
    public void SetCalendarEnabled(bool enabled) => IsCalendarEnabled = enabled;

    public void Reload()
    {
        var notes = _vaultService.GetNotes();
        VaultPath = _vaultService.VaultPath;
        SessionCount = _sessionService.List().Count;
        RefreshVaultSizeLabel();
        TotalNoteCount = notes.Count;

        var recentNotes = notes.Take(6).Select(n => new NoteCardViewModel(n)).ToList();
        FeaturedNote = recentNotes.Count > 0 ? recentNotes[0] : null;
        OtherRecentNotes.Clear();
        foreach (var note in recentNotes.Skip(1)) OtherRecentNotes.Add(note);
        OnPropertyChanged(nameof(HasFeaturedNote));
        OnPropertyChanged(nameof(HasOtherRecentNotes));

        // Matched by explicit CoreColumnRole so renaming or reordering the Kanban columns never empties
        // this widget silently. Non-core columns and legacy columns without a role are ignored.
        var pendingColumn = _kanbanService.Board.Columns.FirstOrDefault(c => c.Role == CoreColumnRole.Backlog);
        var inProgressColumn = _kanbanService.Board.Columns.FirstOrDefault(c => c.Role == CoreColumnRole.InProgress);
        var doneColumn = _kanbanService.Board.Columns.FirstOrDefault(c => c.Role == CoreColumnRole.Done);

        LoadTaskColumn(pendingColumn, PendingTasks);
        LoadTaskColumn(inProgressColumn, InProgressTasks);
        LoadTaskColumn(doneColumn, DoneTasks);

        // Real column totals, not the 5-item preview cap above — otherwise the badge next to "PENDIENTES"
        // would silently read "5" forever on a board with more than five cards, and the completion bar
        // below would be built on truncated numbers.
        PendingTaskCount = VisibleCards(pendingColumn).Count();
        InProgressTaskCount = VisibleCards(inProgressColumn).Count();
        DoneTaskCount = VisibleCards(doneColumn).Count();
        BoardTotalCount = PendingTaskCount + InProgressTaskCount + DoneTaskCount;
        BoardProgressPercent = BoardTotalCount > 0 ? (int)Math.Round(DoneTaskCount * 100.0 / BoardTotalCount) : 0;
        PendingBarWidth = BoardTotalCount > 0 ? BoardBarTotalWidth * PendingTaskCount / BoardTotalCount : 0;
        InProgressBarWidth = BoardTotalCount > 0 ? BoardBarTotalWidth * InProgressTaskCount / BoardTotalCount : 0;
        DoneBarWidth = BoardTotalCount > 0 ? BoardBarTotalWidth * DoneTaskCount / BoardTotalCount : 0;
        OnPropertyChanged(nameof(HasPendingTasks));
        OnPropertyChanged(nameof(HasInProgressTasks));
        OnPropertyChanged(nameof(HasDoneTasks));
        OnPropertyChanged(nameof(HasBoardCards));

        LoadTodayItems();

        // Hidden entirely when not linked (same "cero fricción para quien no usa Polar" principle the
        // Calendar already follows) rather than shown empty/zeroed.
        IsPolarLinked = _polarAuthService.IsLinked;
        if (IsPolarLinked)
        {
            var activeTasks = _polarTaskService.Store.Tasks.Where(t => !t.IsDeleted).ToList();
            PolarPendingCount = activeTasks.Count(t => !t.Completed);
            PolarOverdueCount = activeTasks.Count(t => !t.Completed && t.DueDateUtc is { } due && KanbanCard.IsPastDue(due));
        }

        ReloadNotifications();

        ReloadHabits();

        _ = LoadGraphAsync();

        RebuildSections();
    }

    /// <summary>Populates <see cref="TodayHabits"/> for the compact "Habits" card — same read-projection
    /// logic <see cref="HabitsViewModel.BuildItem"/> uses for the full page, capped to 5 so the card stays
    /// compact regardless of how many habits the vault has.</summary>
    private void ReloadHabits()
    {
        TodayHabits.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var habit in _habitService.Store.Habits.Where(h => !h.Archived).OrderBy(h => h.CreatedUtc).Take(5))
            TodayHabits.Add(HabitsViewModel.BuildItem(habit, today));

        OnPropertyChanged(nameof(HasTodayHabits));
    }

    /// <summary>Builds <see cref="Sections"/> from the per-vault settings, applying the user's order and
    /// hiding disabled sections. Called by <see cref="Reload"/> so layout is refreshed on every dashboard
    /// visit and whenever the active vault changes.</summary>
    private void RebuildSections()
    {
        var settings = _vaultSettingsService?.Settings;
        var order = settings?.DashboardSectionOrder;
        var hidden = settings?.DashboardHiddenSections ?? [];

        if (order is null || order.Count == 0)
            order = DefaultSectionOrder.ToList();

        // Make sure any new/unknown sections introduced by future versions land at the end, and drop
        // stale keys that no longer exist.
        var result = order
            .Concat(DefaultSectionOrder.Except(order))
            .Where(SectionFactories.ContainsKey)
            .Where(key => !hidden.Contains(key))
            .ToList();

        var layoutStyle = settings?.DashboardLayoutStyle ?? DashboardLayoutEngine.Classic;
        var customLayout = settings?.DashboardCustomLayout;

        // Personalizado sin posiciones guardadas ya no cae a Classic: DashboardLayoutEngine.NormalizeCustom
        // auto-coloca las secciones que no traen posición, así que estrenar el modo personalizado da una
        // rejilla compacta lista para reorganizar en vez de una pila de filas a todo lo ancho.
        var gridRows = DashboardLayoutEngine.ClampDimension(settings?.DashboardGridRows ?? DashboardLayoutEngine.DefaultRows);
        var gridColumns = DashboardLayoutEngine.ClampDimension(settings?.DashboardGridColumns ?? DashboardLayoutEngine.DefaultColumns);
        var slots = DashboardLayoutEngine.Compute(result, layoutStyle, customLayout, gridRows, gridColumns);

        // Las plantillas y Classic están dibujadas sobre una rejilla de 6 columnas; solo el modo
        // personalizado usa el número de columnas del vault. El panel de la vista se enlaza a esto.
        GridColumns = layoutStyle == DashboardLayoutEngine.Custom ? gridColumns : DashboardLayoutEngine.DefaultColumns;

        Sections.Clear();
        foreach (var key in result)
        {
            if (!SectionFactories.TryGetValue(key, out var factory)) continue;

            var section = factory();
            if (slots.TryGetValue(key, out var slot))
            {
                section.GridRow = slot.Row;
                section.GridColumn = slot.Column;
                section.GridRowSpan = slot.RowSpan;
                section.GridColumnSpan = slot.ColumnSpan;
            }
            Sections.Add(section);
        }
    }

    /// <summary>Refreshes the notification counter in "ACCESOS RÁPIDOS" and the "ACTIVIDAD" feed every time
    /// <see cref="NotificationService"/> mutates its log, so marking a notification as read anywhere in the
    /// app (bell dropdown, full page, etc.) updates the dashboard badge in real time without waiting for the
    /// next navigation back to Dashboard.</summary>
    private void ReloadNotifications()
    {
        UnreadNotificationCount = _notificationService.Notifications.Count(n => !n.IsRead);
        OnPropertyChanged(nameof(HasUnreadNotifications));

        RecentNotifications.Clear();
        foreach (var notification in _notificationService.Notifications.Take(4))
            RecentNotifications.Add(new NotificationItemViewModel(notification));
        OnPropertyChanged(nameof(HasRecentNotifications));
        OnPropertyChanged(nameof(HasNoActivity));
    }

    /// <summary>"Hoy" + "vencido" across the three domains <see cref="CalendarAggregationService"/> already
    /// aggregates for the Calendar — no lower bound on the scan so an old overdue item is never silently
    /// dropped, same "vencidas" population <see cref="ReminderInboxViewModel"/> already shows for Kanban
    /// alone. Ghost recurrence occurrences are excluded — they're a preview of a future date, not
    /// something due today or overdue right now.</summary>
    private void LoadTodayItems()
    {
        TodayItems.Clear();

        var today = DateTime.Today;
        var rangeEndUtc = today.AddDays(1).ToUniversalTime();
        var rangeStartUtc = (today - TodayItemsBacklogWindow).ToUniversalTime();
        var items = _calendarAggregationService.BuildItems(rangeStartUtc, rangeEndUtc, includeGhostOccurrences: false)
            .Where(i => !i.IsCompleted && (i.IsPastDue || i.MomentLocal.Date == today))
            .ToList();

        // "Vence hoy" always wins the cupo over older backlog (análisis §4.4, Alta) — only the remaining
        // slots, if any, are filled with overdue items, most recent first.
        var dueToday = items.Where(i => i.MomentLocal.Date == today);
        var overdueBacklog = items.Where(i => i.MomentLocal.Date != today).OrderByDescending(i => i.MomentLocal);

        foreach (var item in dueToday.Concat(overdueBacklog).Take(MaxTodayItems))
            TodayItems.Add(item);

        OnPropertyChanged(nameof(HasTodayItems));
    }

    /// <summary>Note/link counts for the "SESIONES" stat card — same global vault graph
    /// <see cref="SessionsViewModel"/> renders (every session's notes plus every vault-root note, wikilinks
    /// resolved across all of it), but read here only for its two counts, never for a live render: an
    /// embedded <c>NativeWebView</c> is a real platform child window (backed by <c>NativeControlHost</c>),
    /// and those don't clip/scroll correctly inside a <c>ScrollViewer</c> (the classic cross-framework
    /// "airspace" problem) — which is exactly why <c>views/sessions/SessionsView.axaml</c> only ever hosts
    /// its own graph WebView in a fixed, non-scrolling panel. The Dashboard is a scrolling page, so it
    /// stays out of the WebView business entirely and just links to the real page
    /// (<see cref="NavigateToSessions"/>) for the actual graph. <see cref="_graphLoadGeneration"/> discards
    /// a stale result in case <see cref="Reload"/> is called again before this finishes, same pattern as
    /// <see cref="SessionsViewModel.LoadGraphCoreAsync"/>.</summary>
    private async System.Threading.Tasks.Task LoadGraphAsync()
    {
        var generation = ++_graphLoadGeneration;
        IsGraphLoading = true;
        var graph = await _sessionService.LoadGlobalGraphAsync();
        if (generation != _graphLoadGeneration) return;

        GraphNodeCount = graph.Nodes.Count;
        GraphEdgeCount = graph.Edges.Count;
        IsGraphLoading = false;
    }

    private static void LoadTaskColumn(KanbanColumn? column, ObservableCollection<KanbanCardViewModel> target)
    {
        target.Clear();
        if (column is null) return;

        foreach (var card in VisibleCards(column)
                     .OrderBy(c => c.DueDateUtc ?? DateTime.MaxValue)
                     .ThenByDescending(c => c.Priority)
                     .Take(5))
            target.Add(new KanbanCardViewModel(card));
    }

    /// <summary>Las tarjetas que el tablero Kanban dibuja de verdad — es decir, sin las archivadas
    /// (docs/agent-docs/kanban-tareas-archivadas.md). Este panel es un espejo del tablero, así que sus
    /// listas y sus contadores tienen que decir lo mismo que él; las Estadísticas, que miran el histórico
    /// y no el estado actual, sí siguen contando las archivadas.</summary>
    private static IEnumerable<KanbanCard> VisibleCards(KanbanColumn? column) =>
        column?.Cards.Where(c => !c.IsArchived) ?? [];

    [RelayCommand]
    private void OpenCard(NoteCardViewModel card) => _openNote(card.Note);

    [RelayCommand]
    private void NewNote() => _newNote();

    [RelayCommand]
    private void NavigateToKanban() => _navigateToKanban();

    [RelayCommand]
    private void NavigateToSessions() => _navigateToSessions();

    [RelayCommand]
    private void NavigateToNotes() => _navigateToNotes();

    [RelayCommand]
    private void NavigateToTrash() => _navigateToTrash();

    [RelayCommand]
    private void NavigateToCalendar() => _navigateToCalendar();

    [RelayCommand]
    private void OpenReminders() => _openReminders();

    [RelayCommand]
    private void OpenNotifications() => _openNotifications();

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void NavigateToHabits() => _navigateToHabits();

    [RelayCommand]
    private void ToggleHabitToday(HabitItemViewModel item)
    {
        _habitService.ToggleCompletion(item.Habit, DateOnly.FromDateTime(DateTime.Today));
        ReloadHabits();
    }
}
