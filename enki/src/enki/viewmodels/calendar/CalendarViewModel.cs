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
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Fases 1–3 (docs/calendar-feature/12-plan-de-implementacion-por-fases.md): the four views
/// (Mes/Semana/Día/Agenda) plus the sidebar mini-calendar, all sharing one <see cref="ReferenceDate"/> so
/// switching views never loses "where you were" (docs/calendar-feature/04-vistas-y-navegacion.md §9), plus
/// Fase 3's interactions — quick-create, drag &amp; drop reschedule, complete/eliminar and navigation to
/// the three real editors (docs/calendar-feature/05-interacciones-y-microinteracciones.md,
/// 06-creacion-y-edicion-de-items.md). Every mutation here goes through the same three source services'
/// real write paths (mutate the referenced model, then that service's own <c>Save()</c>/update method) —
/// the calendar itself never persists anything of its own (doc 05 §1).</summary>
public sealed partial class CalendarViewModel : ViewModelBase
{
    /// <summary>Monday-first order, matching <see cref="MonthGridCalculator"/>/<see cref="WeekGridCalculator"/>'s
    /// default — <see cref="WeekdayHeaders"/>/<see cref="MiniWeekdayHeaders"/> rotate this when
    /// <see cref="WeekStartsOnMonday"/> is off (docs/calendar-feature/09-configuracion-y-ajustes.md §1).
    /// Replaces what used to be three separate hardcoded Spanish arrays (month names, short month names,
    /// full weekday names) plus two more short/mini weekday-label arrays — six 6-language translations
    /// avoided by sourcing everything from <see cref="CultureInfo"/> instead, per docs/agent-docs/
    /// internacionalizacion-i18n.md §1.7.</summary>
    private static readonly DayOfWeek[] WeekOrderMondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    /// <summary>Agenda has no infinite scroll in this phase — ◀/▶ move by "una página de agenda"
    /// (docs/calendar-feature/04-vistas-y-navegacion.md §9), a fixed window of this many days.</summary>
    private const int AgendaWindowDays = 30;

    private readonly CalendarAggregationService _aggregationService;
    private readonly ExternalCalendarService? _externalCalendarService;
    private readonly CalendarLayerColorService _layerColorService;
    private readonly KanbanService _kanbanService;
    private readonly PolarTaskService _polarTaskService;
    private readonly ReminderService _reminderService;
    private readonly PolarAuthService _polarAuthService;
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly Action<CalendarItemViewModel> _openItemEditor;
    private readonly DispatcherTimer _nowTimer;

    /// <summary>Debounces <see cref="Rebuild"/> against every keystroke in the search box — it re-fetches
    /// and re-filters the whole visible range from <see cref="CalendarAggregationService"/>, which on a
    /// vault with many items showed up as input lag typing into the search field (análisis §4.4, Fase 8).
    /// Same delay/restart-on-Tick pattern as the editor's debounce timers (<see cref="EditorDebounce"/>).</summary>
    private readonly DispatcherTimer _searchDebounceTimer;

    /// <summary>42 fixed cells (6 weeks) for the month containing <see cref="ReferenceDate"/> — see
    /// <see cref="RebuildMonth"/>.</summary>
    public ObservableCollection<CalendarMonthDayViewModel> Days { get; } = [];

    /// <summary>7 columns for Semana, or 1 column for Día — both views share this type, see
    /// <see cref="CalendarWeekColumnViewModel"/>.</summary>
    public ObservableCollection<CalendarWeekColumnViewModel> WeekColumns { get; } = [];

    public ObservableCollection<CalendarAgendaGroupViewModel> AgendaGroups { get; } = [];

    /// <summary>Drives the Agenda view's empty-state message — days without items simply don't appear
    /// as groups (docs/calendar-feature/04-vistas-y-navegacion.md §6), so an empty window needs its own
    /// explicit "nothing here" text instead of just rendering a blank list.</summary>
    [ObservableProperty]
    private bool _isAgendaEmpty;

    /// <summary>Sidebar mini-calendar (docs/calendar-feature/04-vistas-y-navegacion.md §7) — browsable
    /// independently of <see cref="ReferenceDate"/> via <see cref="MiniCalendarPreviousMonthCommand"/>/
    /// <see cref="MiniCalendarNextMonthCommand"/>, but resynced to it every time the main view navigates.</summary>
    public ObservableCollection<CalendarMiniDayViewModel> MiniCalendarDays { get; } = [];

    /// <summary>24 fixed rows (00:00–23:00) for the Semana/Día hour grid — rebuilt only when the working-
    /// hours band or 24h/12h format changes (<see cref="RebuildHourRows"/>), never on a plain data refresh.</summary>
    public IReadOnlyList<CalendarHourRowViewModel> HourRows { get; private set; } = [];

    // ===================== Fase 4: capas, filtros, búsqueda, "sin programadas" =====================
    // docs/calendar-feature/07-filtros-busqueda-y-capas.md.

    /// <summary>Kanban/Recordatorios/one-per-Polar-list toggles (doc §1) — rebuilt (not just re-flagged)
    /// whenever the set of Polar lists could have changed, see <see cref="RebuildLayers"/>.</summary>
    public ObservableCollection<CalendarLayerViewModel> Layers { get; } = [];

    /// <summary>One row per distinct <c>PolarTask.Tags</c> value currently in use — empty/hidden entirely
    /// when Polar isn't linked (doc §5).</summary>
    public ObservableCollection<CalendarTagFilterViewModel> TagFilters { get; } = [];

    /// <summary>"Sin programadas" sidebar panel (docs/calendar-feature/04-vistas-y-navegacion.md §8) —
    /// respects the same active layers/secondary filters as the grid (doc §4), rebuilt alongside every
    /// other Fase 4 collection in <see cref="Rebuild"/>.</summary>
    public ObservableCollection<CalendarUnscheduledItemViewModel> UnscheduledItems { get; } = [];

    /// <summary>Drives the panel's auto-collapse (doc §"Panel de no programadas": "vacío... se colapsa/
    /// oculta automáticamente") — raised manually in <see cref="RebuildUnscheduled"/> since it's a plain
    /// derived property over an <see cref="ObservableCollection{T}"/>, not an <c>[ObservableProperty]</c>
    /// of its own.</summary>
    public bool HasUnscheduledItems => UnscheduledItems.Count > 0;

    /// <summary>Popup del botón "Hoy": todo lo que cae hoy, pendiente arriba y completado abajo, cada
    /// grupo en orden alfabético. Ignora a propósito capas, filtros secundarios y "ocultar completadas"
    /// — es un resumen del día completo, no otra vista de la rejilla filtrada. Se reconstruye en
    /// <see cref="Rebuild"/>, así que marcar un item como hecho desde el propio popup lo mueve de grupo.</summary>
    public ObservableCollection<CalendarItemViewModel> TodayPendingItems { get; } = [];

    /// <summary>Ver <see cref="TodayPendingItems"/>.</summary>
    public ObservableCollection<CalendarItemViewModel> TodayCompletedItems { get; } = [];

    public bool HasTodayPendingItems => TodayPendingItems.Count > 0;
    public bool HasTodayCompletedItems => TodayCompletedItems.Count > 0;
    public bool HasNoTodayItems => !HasTodayPendingItems && !HasTodayCompletedItems;
    public int TodayPendingCount => TodayPendingItems.Count;
    public int TodayCompletedCount => TodayCompletedItems.Count;

    /// <summary>Cabecera del popup de "Hoy" — fecha larga en la cultura activa.</summary>
    [ObservableProperty]
    private string _todayPopoverHeaderLabel = string.Empty;

    [ObservableProperty]
    private bool _isTodayPopoverOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _priorityFilterHigh;

    [ObservableProperty]
    private bool _priorityFilterMedium;

    [ObservableProperty]
    private bool _priorityFilterLow;

    [ObservableProperty]
    private bool _priorityFilterNone;

    [ObservableProperty]
    private bool _hideCompletedItems;

    [ObservableProperty]
    private bool _hideGhostOccurrences;

    [ObservableProperty]
    private CalendarViewMode _activeView = CalendarViewMode.Month;

    /// <summary>The single date every view is "centered" on — a month for Mes, a week for Semana, the day
    /// itself for Día, the start of the window for Agenda. Never reset by switching views
    /// (docs/calendar-feature/04-vistas-y-navegacion.md §9).</summary>
    [ObservableProperty]
    private DateTime _referenceDate = DateTime.Today;

    /// <summary>Month currently shown by the mini-calendar — starts in sync with <see cref="ReferenceDate"/>
    /// but can drift while the user browses ahead with its own arrows, see <see cref="MiniCalendarDays"/>.</summary>
    [ObservableProperty]
    private DateTime _miniCalendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    private string _periodLabel = string.Empty;

    [ObservableProperty]
    private string _miniCalendarLabel = string.Empty;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    /// <summary>Solicita una entrada visual; la vista gestiona los fotogramas y su cancelación.</summary>
    [ObservableProperty]
    private int _revealRevision;

    private void AnimateReveal() => RevealRevision++;

    /// <summary>Ajustes › Calendario "Mostrar barra de desplazamiento del sidebar" —
    /// drives <see cref="SidebarScrollBarVisibility"/> below, bound only on the sidebar's ScrollViewer in
    /// CalendarView.axaml (mini-calendario, filtros, sin programar).</summary>
    [ObservableProperty]
    private bool _showSidebarScrollbar;

    /// <summary>Scrolling itself is unaffected — only the track/thumb are hidden.</summary>
    public Avalonia.Controls.Primitives.ScrollBarVisibility SidebarScrollBarVisibility =>
        ShowSidebarScrollbar ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;

    partial void OnShowSidebarScrollbarChanged(bool value) => OnPropertyChanged(nameof(SidebarScrollBarVisibility));

    // ===================== Fase 5: ajustes de Calendario (docs/calendar-feature/
    // 09-configuracion-y-ajustes.md §1) — every one of these is set live by MainWindowViewModel whenever
    // the user changes it in Ajustes while the calendar is already open (same "explicit callback" pattern
    // ShowSidebarScrollbar already established above), never persisted from here directly.

    [ObservableProperty]
    private bool _weekStartsOnMonday = true;

    [ObservableProperty]
    private bool _showWeekends = true;

    [ObservableProperty]
    private int _workingHoursStart = 9;

    [ObservableProperty]
    private int _workingHoursEnd = 18;

    [ObservableProperty]
    private bool _timeFormat24h = true;

    /// <summary>5 columns (Mon–Fri) when <see cref="ShowWeekends"/> is off, 7 otherwise — the Mes grid's
    /// weekday header and day cells both bind their <c>UniformGrid.Columns</c> to this so they can never
    /// disagree; Semana/Día simply bind to <see cref="WeekColumns"/>.Count instead, since that collection
    /// is already filtered the same way (see <see cref="RebuildWeek"/>).</summary>
    public int MonthGridColumns => ShowWeekends ? 7 : 5;

    /// <summary>3-letter (or culture-equivalent) weekday labels for the Mes grid header, in
    /// <see cref="WeekStartsOnMonday"/> order and filtered to match <see cref="MonthGridColumns"/>.</summary>
    public IReadOnlyList<string> WeekdayHeaders
    {
        get
        {
            var order = RotateToWeekStart(WeekOrderMondayFirst);
            if (!ShowWeekends) order = order.Where(d => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            return order.Select(d => dtf.GetAbbreviatedDayName(d).ToUpper(CultureInfo.CurrentCulture)).ToList();
        }
    }

    /// <summary>Single-letter (or culture-equivalent shortest) weekday labels for the sidebar
    /// mini-calendario header — always all 7 (doc §1: <see cref="CalendarShowWeekends"><c>CalendarShowWeekends</c></see>
    /// only applies to Mes/Semana), just reordered by <see cref="WeekStartsOnMonday"/>.</summary>
    public IReadOnlyList<string> MiniWeekdayHeaders
    {
        get
        {
            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            return RotateToWeekStart(WeekOrderMondayFirst).Select(d => dtf.GetShortestDayName(d).ToUpper(CultureInfo.CurrentCulture)).ToList();
        }
    }

    // Sunday-first is Monday-first rotated left by one (Sunday moves from the end to the front).
    private List<DayOfWeek> RotateToWeekStart(DayOfWeek[] mondayFirst) =>
        WeekStartsOnMonday ? mondayFirst.ToList() : [mondayFirst[^1], .. mondayFirst[..^1]];

    partial void OnWeekStartsOnMondayChanged(bool value)
    {
        OnPropertyChanged(nameof(WeekdayHeaders));
        OnPropertyChanged(nameof(MiniWeekdayHeaders));
        Rebuild();
    }

    partial void OnShowWeekendsChanged(bool value)
    {
        OnPropertyChanged(nameof(MonthGridColumns));
        OnPropertyChanged(nameof(WeekdayHeaders));
        Rebuild();
    }

    partial void OnWorkingHoursStartChanged(int value) => RebuildHourRows();
    partial void OnWorkingHoursEndChanged(int value) => RebuildHourRows();
    partial void OnTimeFormat24hChanged(bool value) => RebuildHourRows();

    /// <summary>Non-null while the quick-create popover (docs/calendar-feature/
    /// 06-creacion-y-edicion-de-items.md §1) is open — opened by <see cref="OpenQuickCreate"/>.</summary>
    [ObservableProperty]
    private CalendarQuickCreateViewModel? _quickCreate;

    /// <summary>Non-null while the delete-confirmation dialog for a <see cref="CalendarItemViewModel"/> is
    /// open — same confirm-before-soft-delete pattern <c>KanbanViewModel.CardPendingDelete</c> already
    /// uses (docs/calendar-feature/05-interacciones-y-microinteracciones.md §6).</summary>
    [ObservableProperty]
    private CalendarItemViewModel? _itemPendingDelete;

    public bool IsMonthView => ActiveView == CalendarViewMode.Month;
    public bool IsWeekView => ActiveView == CalendarViewMode.Week;
    public bool IsDayView => ActiveView == CalendarViewMode.Day;
    public bool IsAgendaView => ActiveView == CalendarViewMode.Agenda;
    /// <summary>Semana and Día both render through <see cref="WeekColumns"/> (Día is just a 1-column
    /// week) — this is the single flag their shared hour-grid section binds its visibility to.</summary>
    public bool IsHourGridView => IsWeekView || IsDayView;

    /// <summary>Whether the "Tarea Polar" tab/context-menu shortcut should be offered at all (doc 06 §1:
    /// "si no está vinculado, ese botón directamente no se renderiza"). Not an <c>[ObservableProperty]</c>
    /// since it's a plain proxy over <see cref="PolarAuthService.IsLinked"/> — <see cref="HandlePolarLinkChanged"/>
    /// raises the change notification for it directly instead.</summary>
    public bool IsPolarLinked => _polarAuthService.IsLinked;

    partial void OnActiveViewChanged(CalendarViewMode value)
    {
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsWeekView));
        OnPropertyChanged(nameof(IsDayView));
        OnPropertyChanged(nameof(IsAgendaView));
        OnPropertyChanged(nameof(IsHourGridView));
    }

    // Search/priority/tag filters aren't persisted (doc §1 only requires persisting the layer
    // combination) — session-only, reset each time Enki opens.
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }
    partial void OnPriorityFilterHighChanged(bool value) => Rebuild();
    partial void OnPriorityFilterMediumChanged(bool value) => Rebuild();
    partial void OnPriorityFilterLowChanged(bool value) => Rebuild();
    partial void OnPriorityFilterNoneChanged(bool value) => Rebuild();

    [ObservableProperty] private bool _isMiniCalendarExpanded;

    partial void OnIsMiniCalendarExpandedChanged(bool value)
    {
        _settingsService.Settings.CalendarMiniCalendarExpanded = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _isLayersExpanded;

    partial void OnIsLayersExpandedChanged(bool value)
    {
        _settingsService.Settings.CalendarLayersExpanded = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _isSearchExpanded;

    partial void OnIsSearchExpandedChanged(bool value)
    {
        _settingsService.Settings.CalendarSearchExpanded = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _isFiltersExpanded;

    partial void OnIsFiltersExpandedChanged(bool value)
    {
        _settingsService.Settings.CalendarFiltersExpanded = value;
        _settingsService.Save();
    }

    [ObservableProperty] private bool _isUnscheduledExpanded;

    partial void OnIsUnscheduledExpandedChanged(bool value)
    {
        _settingsService.Settings.CalendarUnscheduledExpanded = value;
        _settingsService.Save();
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _settingsService.Settings.CalendarSidebarCollapsed = value;
        _settingsService.Save();
    }

    partial void OnHideCompletedItemsChanged(bool value)
    {
        _settingsService.Settings.CalendarHideCompletedItems = value;
        _settingsService.Save();
        Rebuild();
    }

    partial void OnHideGhostOccurrencesChanged(bool value)
    {
        _settingsService.Settings.CalendarHideGhostOccurrences = value;
        _settingsService.Save();
        Rebuild();
    }

    public CalendarViewModel(
        CalendarAggregationService aggregationService,
        CalendarLayerColorService layerColorService,
        KanbanService kanbanService,
        PolarTaskService polarTaskService,
        ReminderService reminderService,
        PolarAuthService polarAuthService,
        SettingsService settingsService,
        NotificationService notificationService,
        Action<CalendarItemViewModel> openItemEditor,
        ExternalCalendarService? externalCalendarService = null)
    {
        _aggregationService = aggregationService;
        _externalCalendarService = externalCalendarService;
        _layerColorService = layerColorService;
        _kanbanService = kanbanService;
        _polarTaskService = polarTaskService;
        _reminderService = reminderService;
        _polarAuthService = polarAuthService;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _openItemEditor = openItemEditor;

        // Read before subscribing to the On*Changed handlers below, so restoring these persisted
        // preferences at startup doesn't immediately re-save the same value back to disk or re-trigger a
        // rebuild that's about to happen anyway at the end of the constructor.
        _isMiniCalendarExpanded = _settingsService.Settings.CalendarMiniCalendarExpanded;
        _isLayersExpanded = _settingsService.Settings.CalendarLayersExpanded;
        _isSearchExpanded = _settingsService.Settings.CalendarSearchExpanded;
        _isFiltersExpanded = _settingsService.Settings.CalendarFiltersExpanded;
        _isUnscheduledExpanded = _settingsService.Settings.CalendarUnscheduledExpanded;
        _isSidebarCollapsed = _settingsService.Settings.CalendarSidebarCollapsed;
        _hideCompletedItems = _settingsService.Settings.CalendarHideCompletedItems;
        _hideGhostOccurrences = _settingsService.Settings.CalendarHideGhostOccurrences;
        _showSidebarScrollbar = _settingsService.Settings.CalendarShowSidebarScrollbar;
        _weekStartsOnMonday = _settingsService.Settings.CalendarWeekStartsOnMonday;
        _showWeekends = _settingsService.Settings.CalendarShowWeekends;
        _workingHoursStart = _settingsService.Settings.CalendarWorkingHoursStart;
        _workingHoursEnd = _settingsService.Settings.CalendarWorkingHoursEnd;
        _timeFormat24h = _settingsService.Settings.CalendarTimeFormat24h;
        _activeView = ParseViewMode(_settingsService.Settings.CalendarDefaultView);

        RebuildHourRows();

        // Same live-refresh pattern as ReminderInboxViewModel (docs/notificaciones-y-recordatorios.md):
        // whichever of the three sources changes, the calendar just rebuilds itself — no manual "Reload"
        // call needed from MainWindowViewModel.Navigate (docs/calendar-feature/11-arquitectura-tecnica.md
        // §6). Also reacts to linking/unlinking Polar, since that flips which items are even eligible.
        _kanbanService.BoardChanged += Rebuild;
        _layerColorService.ColorsChanged += Rebuild;
        _polarTaskService.DataChanged += Rebuild;
        _reminderService.RemindersChanged += Rebuild;
        _polarAuthService.LinkStateChanged += HandlePolarLinkChanged;
        if (_externalCalendarService is not null) _externalCalendarService.DataChanged += Rebuild;

        // The "now" line (docs/calendar-feature/10-diseno-visual-y-componentes.md §3) needs to creep
        // forward even when nothing else changes — refreshed on its own light timer instead of piggy-
        // backing on a full Rebuild, so it never re-flows the rest of the hour grid just to move a line.
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _nowTimer.Tick += async (_, _) =>
        {
            RefreshNowIndicator();
            if (_externalCalendarService is not null) await _externalCalendarService.RefreshAllAsync();
        };
        _nowTimer.Start();

        _searchDebounceTimer = new DispatcherTimer { Interval = EditorDebounce.Delay };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            Rebuild();
        };

        Rebuild();
        if (_externalCalendarService is not null) _ = _externalCalendarService.RefreshAllAsync();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    /// <summary>Recarga las preferencias del calendario al cambiar de vault.</summary>
    public void ReloadPreferences()
    {
        var settings = _settingsService.Settings;
        IsMiniCalendarExpanded = settings.CalendarMiniCalendarExpanded;
        IsLayersExpanded = settings.CalendarLayersExpanded;
        IsSearchExpanded = settings.CalendarSearchExpanded;
        IsFiltersExpanded = settings.CalendarFiltersExpanded;
        IsUnscheduledExpanded = settings.CalendarUnscheduledExpanded;
        IsSidebarCollapsed = settings.CalendarSidebarCollapsed;
        HideCompletedItems = settings.CalendarHideCompletedItems;
        HideGhostOccurrences = settings.CalendarHideGhostOccurrences;
        ShowSidebarScrollbar = settings.CalendarShowSidebarScrollbar;
        WeekStartsOnMonday = settings.CalendarWeekStartsOnMonday;
        ShowWeekends = settings.CalendarShowWeekends;
        WorkingHoursStart = settings.CalendarWorkingHoursStart;
        WorkingHoursEnd = settings.CalendarWorkingHoursEnd;
        TimeFormat24h = settings.CalendarTimeFormat24h;
        ActiveView = ParseViewMode(settings.CalendarDefaultView);
        Rebuild();
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(MiniCalendarLabel));
        OnPropertyChanged(nameof(WeekdayHeaders));
        OnPropertyChanged(nameof(MiniWeekdayHeaders));
        QuickCreate?.RefreshLocalizedLabels();
        Rebuild();
    }

    [RelayCommand]
    private void SetView(CalendarViewMode mode)
    {
        if (ActiveView == mode) return;
        ActiveView = mode;
        AnimateReveal();
        Rebuild();
    }

    [RelayCommand]
    private void PreviousPeriod()
    {
        ReferenceDate = ActiveView switch
        {
            CalendarViewMode.Month => ReferenceDate.AddMonths(-1),
            CalendarViewMode.Week => ReferenceDate.AddDays(-7),
            CalendarViewMode.Day => ReferenceDate.AddDays(-1),
            CalendarViewMode.Agenda => ReferenceDate.AddDays(-AgendaWindowDays),
            _ => ReferenceDate
        };
        AnimateReveal();
        Rebuild();
    }

    [RelayCommand]
    private void NextPeriod()
    {
        ReferenceDate = ActiveView switch
        {
            CalendarViewMode.Month => ReferenceDate.AddMonths(1),
            CalendarViewMode.Week => ReferenceDate.AddDays(7),
            CalendarViewMode.Day => ReferenceDate.AddDays(1),
            CalendarViewMode.Agenda => ReferenceDate.AddDays(AgendaWindowDays),
            _ => ReferenceDate
        };
        AnimateReveal();
        Rebuild();
    }

    [RelayCommand]
    private void GoToToday()
    {
        ReferenceDate = DateTime.Today;
        AnimateReveal();
        Rebuild();
    }

    /// <summary>Botón "Hoy" de la barra: abre el resumen del día (<see cref="TodayPendingItems"/>/
    /// <see cref="TodayCompletedItems"/>) como diálogo modal. No navega ni reconstruye la rejilla — solo
    /// refresca las dos listas del día, así abrirlo es instantáneo y la vista de detrás no parpadea.</summary>
    [RelayCommand]
    private void OpenTodayPopover()
    {
        RebuildToday();
        IsTodayPopoverOpen = true;
    }

    [RelayCommand]
    private void CloseTodayPopover() => IsTodayPopoverOpen = false;

    /// <summary>"Ver en el calendario" del pie del resumen: cierra el diálogo y lleva la vista activa a hoy.</summary>
    [RelayCommand]
    private void ShowTodayInCalendar()
    {
        IsTodayPopoverOpen = false;
        GoToToday();
    }

    /// <summary>Pulsar un item del resumen abre su editor real — el diálogo se cierra antes, porque el
    /// editor vive en otra pantalla y el overlay modal taparía la ventana entera.</summary>
    [RelayCommand]
    private void OpenTodayItem(CalendarItemViewModel item)
    {
        IsTodayPopoverOpen = false;
        if (item.CanEdit) _openItemEditor(item);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void MiniCalendarPreviousMonth()
    {
        MiniCalendarMonth = MiniCalendarMonth.AddMonths(-1);
        RebuildMiniCalendar();
    }

    [RelayCommand]
    private void MiniCalendarNextMonth()
    {
        MiniCalendarMonth = MiniCalendarMonth.AddMonths(1);
        RebuildMiniCalendar();
    }

    /// <summary>Clicking a mini-calendar day navigates the main view to it — or, if Mes is already
    /// showing that month, simply moves the highlight (docs/calendar-feature/04-vistas-y-navegacion.md
    /// §7). Both cases are the same code path: <see cref="ReferenceDate"/> drives every view's grid, so
    /// setting it and rebuilding covers "navigate" and "just highlight" alike.</summary>
    [RelayCommand]
    private void SelectMiniDay(CalendarMiniDayViewModel day)
    {
        ReferenceDate = day.Date;
        AnimateReveal();
        Rebuild();
    }

    private void Rebuild()
    {
        // Layers/tag filters/"sin programadas" are shared across every view (doc §1: "es un estado del
        // CalendarViewModel, no una preferencia por vista"), so they rebuild once here rather than inside
        // each RebuildX below.
        RebuildLayers();
        RebuildTagFilters();
        RebuildUnscheduled();
        RebuildToday();

        switch (ActiveView)
        {
            case CalendarViewMode.Month: RebuildMonth(); break;
            case CalendarViewMode.Week: RebuildWeek(); break;
            case CalendarViewMode.Day: RebuildDay(); break;
            case CalendarViewMode.Agenda: RebuildAgenda(); break;
        }

        // Main navigation always pulls the mini-calendar back in sync with the reference date; its own
        // Previous/NextMonth commands rebuild directly (bypassing this method) so independent browsing
        // isn't stomped on the next data-change event.
        MiniCalendarMonth = new DateTime(ReferenceDate.Year, ReferenceDate.Month, 1);
        RebuildMiniCalendar();
    }

    /// <summary>Fetches the unfiltered range from <see cref="CalendarAggregationService"/> (honoring
    /// <see cref="HideGhostOccurrences"/> up front, since a hidden ghost occurrence should never even be
    /// computed) and applies capas/secondary filters/search highlighting (docs/calendar-feature/
    /// 07-filtros-busqueda-y-capas.md) — the single filtering pass every RebuildX method below funnels
    /// through, so the grid, the mini-calendar dot and (for capas/hide-completed/hide-ghost — see doc §4)
    /// the "sin programadas" panel can never disagree about what's currently visible.</summary>
    private List<CalendarItemViewModel> BuildFilteredItems(DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var activeLayers = ActiveLayerKeys;
        var priorityFilters = ActivePriorityFilters;
        var tagFilters = ActiveTagFilters;

        return _aggregationService.BuildItems(rangeStartUtc, rangeEndUtc, includeGhostOccurrences: !HideGhostOccurrences)
            .Where(i => CalendarItemFilter.MatchesLayer(i, activeLayers))
            .Where(i => CalendarItemFilter.MatchesSecondaryFilters(i, priorityFilters, tagFilters, HideCompletedItems, HideGhostOccurrences))
            .Select(i => i with { IsSearchMatch = CalendarItemFilter.MatchesSearch(i, SearchText) && !string.IsNullOrWhiteSpace(SearchText) })
            .ToList();
    }

    private void RebuildToday()
    {
        var today = DateTime.Today;
        var items = _aggregationService.BuildItems(today.ToUniversalTime(), today.AddDays(1).ToUniversalTime(),
                includeGhostOccurrences: !HideGhostOccurrences)
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        TodayPendingItems.Clear();
        TodayCompletedItems.Clear();
        foreach (var item in items)
            (item.IsCompleted ? TodayCompletedItems : TodayPendingItems).Add(item);

        OnPropertyChanged(nameof(HasTodayPendingItems));
        OnPropertyChanged(nameof(HasTodayCompletedItems));
        OnPropertyChanged(nameof(HasNoTodayItems));
        var longDate = today.ToString("D", CultureInfo.CurrentCulture);
        TodayPopoverHeaderLabel = longDate.Length > 0
            ? char.ToUpper(longDate[0], CultureInfo.CurrentCulture) + longDate[1..]
            : longDate;
        OnPropertyChanged(nameof(TodayPendingCount));
        OnPropertyChanged(nameof(TodayCompletedCount));
    }

    private void RebuildMonth()
    {
        var visibleMonth = new DateTime(ReferenceDate.Year, ReferenceDate.Month, 1);
        PeriodLabel = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Calendar_Header_MonthYear"],
            FullMonth(visibleMonth.Month), visibleMonth.Year);

        // Doc §1: "todas las capas activas" grid math always covers the full 7-day week (weekend filtering
        // happens after, not by asking MonthGridCalculator for a different shape) — that way the "6 weeks"
        // invariant every date-range query below relies on never changes, only which cells actually render.
        var dates = MonthGridCalculator.BuildDates(visibleMonth, WeekStartsOnMonday);
        if (!ShowWeekends) dates = dates.Where(IsWeekday).ToArray();

        // Local calendar days converted to UTC for the aggregation query, same "work in local time,
        // persist/compare in UTC" rule DueDateUtc/NotifyAtUtc already follow everywhere else in Enki.
        var rangeStartUtc = dates[0].ToUniversalTime();
        var rangeEndUtc = dates[^1].AddDays(1).ToUniversalTime();

        var items = BuildFilteredItems(rangeStartUtc, rangeEndUtc);
        var today = DateTime.Today;
        var columns = MonthGridColumns;

        Days.Clear();
        foreach (var date in dates)
        {
            var isCurrentMonth = date.Month == visibleMonth.Month && date.Year == visibleMonth.Year;
            // Days outside the visible month never show chips, even if something technically falls on
            // that date — docs/calendar-feature/10-diseno-visual-y-componentes.md §2: "no hay items
            // fantasma de mes, un día fuera de mes simplemente no muestra nada".
            var dayItems = isCurrentMonth
                ? items.Where(i => i.MomentLocal.Date == date.Date).ToList()
                : new List<CalendarItemViewModel>();
            Days.Add(new CalendarMonthDayViewModel(date, isCurrentMonth, date == today, date == ReferenceDate.Date, dayItems, Days.Count, columns));
        }
    }

    private void RebuildWeek()
    {
        var monday = WeekGridCalculator.StartOfWeek(ReferenceDate, WeekStartsOnMonday);
        var sunday = monday.AddDays(6);
        PeriodLabel = FormatWeekLabel(monday, sunday);

        var rangeStartUtc = monday.ToUniversalTime();
        var rangeEndUtc = sunday.AddDays(1).ToUniversalTime();
        var items = BuildFilteredItems(rangeStartUtc, rangeEndUtc);
        var nowLocal = DateTime.Now;

        WeekColumns.Clear();
        for (var i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            if (!ShowWeekends && !IsWeekday(date)) continue;
            var dayItems = items.Where(it => it.MomentLocal.Date == date.Date).ToList();
            WeekColumns.Add(new CalendarWeekColumnViewModel(date, dayItems, nowLocal));
        }
    }

    private static bool IsWeekday(DateTime date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private void RebuildDay()
    {
        var date = ReferenceDate.Date;
        PeriodLabel = FormatDayLabel(date);

        var rangeStartUtc = date.ToUniversalTime();
        var rangeEndUtc = date.AddDays(1).ToUniversalTime();
        var items = BuildFilteredItems(rangeStartUtc, rangeEndUtc);

        WeekColumns.Clear();
        WeekColumns.Add(new CalendarWeekColumnViewModel(date, items, DateTime.Now));
    }

    private void RebuildAgenda()
    {
        var start = ReferenceDate.Date;
        var end = start.AddDays(AgendaWindowDays);
        PeriodLabel = FormatAgendaRangeLabel(start, end);

        var items = BuildFilteredItems(start.ToUniversalTime(), end.ToUniversalTime());

        AgendaGroups.Clear();
        foreach (var group in CalendarAgendaGroupViewModel.Build(items)) AgendaGroups.Add(group);
        IsAgendaEmpty = AgendaGroups.Count == 0;
    }

    private void RebuildMiniCalendar()
    {
        MiniCalendarLabel = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Calendar_Header_MonthYear"],
            AbbrMonth(MiniCalendarMonth.Month), MiniCalendarMonth.Year);

        var dates = MonthGridCalculator.BuildDates(MiniCalendarMonth, WeekStartsOnMonday);
        var rangeStartUtc = dates[0].ToUniversalTime();
        var rangeEndUtc = dates[^1].AddDays(1).ToUniversalTime();
        var itemDates = BuildFilteredItems(rangeStartUtc, rangeEndUtc)
            .Select(i => i.MomentLocal.Date)
            .ToHashSet();

        var today = DateTime.Today;
        MiniCalendarDays.Clear();
        foreach (var date in dates)
        {
            var isCurrentMonth = date.Month == MiniCalendarMonth.Month && date.Year == MiniCalendarMonth.Year;
            MiniCalendarDays.Add(new CalendarMiniDayViewModel(date, isCurrentMonth, date == today, date == ReferenceDate.Date, itemDates.Contains(date.Date)));
        }
    }

    // ===================== Fase 4: capas, filtros, "sin programadas" (doc 07) =====================

    private HashSet<string> ActiveLayerKeys =>
        Layers.Where(l => l.IsToggleable && l.IsActive).Select(l => l.Key).ToHashSet();

    private HashSet<CalendarItemPriority> ActivePriorityFilters
    {
        get
        {
            var set = new HashSet<CalendarItemPriority>();
            if (PriorityFilterHigh) set.Add(CalendarItemPriority.High);
            if (PriorityFilterMedium) set.Add(CalendarItemPriority.Medium);
            if (PriorityFilterLow) set.Add(CalendarItemPriority.Low);
            if (PriorityFilterNone) set.Add(CalendarItemPriority.None);
            return set;
        }
    }

    private HashSet<string> ActiveTagFilters => TagFilters.Where(t => t.IsActive).Select(t => t.Tag).ToHashSet();

    /// <summary>Una fila del panel "capas" antes de existir como <see cref="CalendarLayerViewModel"/> —
    /// ver <see cref="RebuildLayers"/>, que la usa para decidir si basta con refrescar las filas que ya
    /// hay o hay que rehacer la colección entera.</summary>
    private readonly record struct LayerRow(
        string Key, string ColorKey, string Label, string ColorHex,
        bool CanEditColor, bool HasCustomColor, bool IsChildRow);

    /// <summary>Rebuilds the Kanban/Recordatorios/per-list toggles — but only replaces the collection
    /// (and thus every <see cref="CalendarLayerViewModel"/> instance in it, dropping any open popup bound
    /// to one) when the *set of rows* actually changed, e.g. a Polar list or a Kanban column was
    /// created/deleted, or Polar just got linked. A plain data change (a card's title edited) calls this
    /// just as often as everything else in <see cref="Rebuild"/>, so this guard is what keeps the sidebar
    /// from flickering on every unrelated edit. Cuando la estructura no cambia pero sí el texto o el
    /// color (renombrar una columna, elegir un color), las filas se actualizan en sitio: así el emergente
    /// del color no se cierra solo en cuanto se pulsa un color.
    ///
    /// Bajo KANBAN aparecen tres estados compartidos y las columnas personalizadas (doc §1b). Son solo color: la
    /// visibilidad sigue siendo de todo Kanban a la vez, que es lo que esta sección hacía hasta ahora.</summary>
    private void RebuildLayers()
    {
        var hidden = _settingsService.Settings.CalendarHiddenLayerKeys;
        var colors = _layerColorService.BuildMap();
        var desired = new List<LayerRow>
        {
            new(CalendarAggregationService.KanbanLayerKey, string.Empty,
                UpperLayerLabel("Calendar_Sidebar_LayerKanban"),
                CalendarItemViewModel.KanbanLayerColorHex, false, false, false)
        };

        // Los tres estados base aparecen una sola vez; las columnas personalizadas conservan
        // su identidad y el nombre del tablero para distinguir títulos repetidos.
        foreach (var (role, labelKey) in new[]
                 {
                     (CoreColumnRole.Backlog, "Kanban_Seed_ColumnTodo"),
                     (CoreColumnRole.InProgress, "Kanban_Seed_ColumnInProgress"),
                     (CoreColumnRole.Done, "Kanban_Seed_ColumnDone")
                 })
        {
            AddKanbanColorRow(CalendarLayerColorService.KanbanRoleColorKey(role),
                LocalizationService.Instance[labelKey]);
        }
        foreach (var column in _kanbanService.AllColumns.Where(c => c.Role == CoreColumnRole.None))
            AddKanbanColorRow(CalendarLayerColorService.KanbanColumnColorKey(column.Id),
                _kanbanService.ColumnLabel(column));

        void AddKanbanColorRow(string colorKey, string label) => desired.Add(new LayerRow(
            CalendarAggregationService.KanbanLayerKey, colorKey, label,
            CalendarLayerColorService.Resolve(colors, colorKey),
            CanEditColor: true, _layerColorService.HasCustomColor(colorKey), IsChildRow: true));

        desired.Add(new LayerRow(
            CalendarAggregationService.ReminderLayerKey, CalendarAggregationService.ReminderLayerKey,
            UpperLayerLabel("Calendar_Sidebar_LayerReminders"),
            CalendarLayerColorService.Resolve(colors, CalendarAggregationService.ReminderLayerKey),
            CanEditColor: true, _layerColorService.HasCustomColor(CalendarAggregationService.ReminderLayerKey), IsChildRow: false));

        if (_polarAuthService.IsLinked)
        {
            desired.AddRange(_polarTaskService.Store.Lists.Where(l => !l.IsDeleted).OrderBy(l => l.OrderIndex)
                .Select(l => new LayerRow(
                    l.Id, l.Id, l.Title, CalendarLayerColorService.Resolve(colors, l.Id),
                    CanEditColor: true, _layerColorService.HasCustomColor(l.Id), IsChildRow: false)));
        }

        if (_externalCalendarService is not null)
        {
            var index = 0;
            foreach (var feed in _externalCalendarService.Subscriptions)
            {
                desired.Add(new LayerRow(CalendarAggregationService.ExternalLayerPrefix + feed.Id,
                    string.Empty, feed.Name, CalendarLayerPalette.PickerSwatches[index++ % CalendarLayerPalette.PickerSwatches.Count],
                    CanEditColor: false, HasCustomColor: false, IsChildRow: false));
            }
        }

        // La identidad de una fila es su clave de color (única por fila, a diferencia de Key, que las
        // sub-filas de Kanban comparten con su fila madre) más si es sub-fila o no.
        if (Layers.Count == desired.Count &&
            Layers.Select(l => (l.ColorKey, l.IsChildRow)).SequenceEqual(desired.Select(d => (d.ColorKey, d.IsChildRow))))
        {
            for (var i = 0; i < desired.Count; i++)
            {
                var row = desired[i];
                Layers[i].Label = row.Label;
                Layers[i].ColorHex = row.ColorHex;
                Layers[i].CanEditColor = row.CanEditColor;
                Layers[i].HasCustomColor = row.HasCustomColor;
                if (Layers[i].IsToggleable) Layers[i].IsActive = !hidden.Contains(row.Key);
            }
            return;
        }

        Layers.Clear();
        foreach (var row in desired)
        {
            Layers.Add(new CalendarLayerViewModel(
                row.Key, row.Label, row.ColorHex, !hidden.Contains(row.Key),
                row.IsChildRow ? null : HandleLayerToggled,
                row.ColorKey, HandleLayerColorChanged, row.CanEditColor, row.HasCustomColor, row.IsChildRow));
        }
    }

    /// <summary>Las dos capas fijas van en mayúsculas (como el resto de encabezados de la barra
    /// lateral); los títulos de columnas y listas se muestran tal cual los escribió el usuario.</summary>
    private static string UpperLayerLabel(string key) =>
        LocalizationService.Instance[key].ToUpper(CultureInfo.CurrentCulture);

    private void HandleLayerToggled(CalendarLayerViewModel layer)
    {
        var hidden = _settingsService.Settings.CalendarHiddenLayerKeys;
        if (layer.IsActive) hidden.Remove(layer.Key);
        else if (!hidden.Contains(layer.Key)) hidden.Add(layer.Key);
        _settingsService.Save();
        Rebuild();
    }

    /// <summary>Guarda (o quita, con <paramref name="hex"/> a <c>null</c>) el color de calendario de una
    /// capa. No reconstruye nada aquí: el servicio dispara <c>ColorsChanged</c> y de ahí sale el
    /// <see cref="Rebuild"/>, igual que con cualquier otro cambio de datos del calendario.</summary>
    private void HandleLayerColorChanged(CalendarLayerViewModel layer, string? hex)
    {
        if (string.IsNullOrEmpty(layer.ColorKey)) return;

        if (hex is null) _layerColorService.ResetColor(layer.ColorKey);
        else _layerColorService.SetColor(layer.ColorKey, hex);
    }

    /// <summary>Same "only rebuild on an actual key-set change" guard as <see cref="RebuildLayers"/> —
    /// selection isn't persisted (doc §1 only requires persisting layers), so there's no "hidden set" to
    /// consult here, new tags just start active.</summary>
    private void RebuildTagFilters()
    {
        var desired = _polarAuthService.IsLinked
            ? _polarTaskService.Store.Tasks.Where(t => !t.IsDeleted).SelectMany(t => t.Tags).Distinct().OrderBy(t => t).ToList()
            : [];

        if (TagFilters.Select(t => t.Tag).SequenceEqual(desired)) return;

        var previouslyActive = TagFilters.Where(t => t.IsActive).Select(t => t.Tag).ToHashSet();
        TagFilters.Clear();
        foreach (var tag in desired)
            TagFilters.Add(new CalendarTagFilterViewModel(tag, previouslyActive.Contains(tag), HandleTagFilterToggled));
    }

    private void HandleTagFilterToggled(CalendarTagFilterViewModel tag) => Rebuild();

    private void RebuildUnscheduled()
    {
        var activeLayers = ActiveLayerKeys;
        var priorityFilters = ActivePriorityFilters;
        var tagFilters = ActiveTagFilters;

        UnscheduledItems.Clear();
        // Doc §4: the panel respects the same active capas as the grid, and every secondary filter except
        // "ocultar ocurrencias fantasma" (which never applies here — see
        // CalendarItemFilter.MatchesSecondaryFilters's CalendarUnscheduledItemViewModel overload).
        foreach (var item in _aggregationService.BuildUnscheduledItems()
                     .Where(i => activeLayers.Contains(i.LayerKey))
                     .Where(i => CalendarItemFilter.MatchesSecondaryFilters(i, priorityFilters, tagFilters, HideCompletedItems)))
            UnscheduledItems.Add(item);
        OnPropertyChanged(nameof(HasUnscheduledItems));
    }

    /// <summary>Moves the "now" line without touching anything else — see the timer set up in the
    /// constructor and <see cref="CalendarWeekColumnViewModel.RefreshNow"/>. A no-op outside Semana/Día.</summary>
    private void RefreshNowIndicator()
    {
        if (!IsHourGridView) return;
        var nowLocal = DateTime.Now;
        foreach (var column in WeekColumns) column.RefreshNow(nowLocal);
    }

    private void HandlePolarLinkChanged()
    {
        OnPropertyChanged(nameof(IsPolarLinked));
        Rebuild();
    }

    // ===================== Fase 3: creación rápida (doc 06 §1) =====================

    /// <summary>Opens the quick-create popover pre-filled from wherever the user clicked — a Mes day cell
    /// or a Semana/Día empty slot (<paramref name="localTime"/> non-null only for the latter). Public
    /// (not a <c>[RelayCommand]</c>) so the view's code-behind can call it directly from a pointer-event
    /// handler on the hour grid, where the date+hour come from two different nested DataContexts that a
    /// single XAML-bound CommandParameter can't combine (see CalendarView.axaml.cs).</summary>
    public void OpenQuickCreate(DateTime localDate, TimeSpan? localTime = null, CalendarQuickCreateType? preferredType = null)
    {
        QuickCreate = new CalendarQuickCreateViewModel(
            localDate, localTime,
            _kanbanService.AllColumns.ToList(),
            _polarTaskService.Store.Lists.Where(l => !l.IsDeleted).ToList(),
            _polarAuthService.IsLinked);

        QuickCreate.SetBoards(_kanbanService.Boards, _kanbanService.DefaultBoard);

        if (preferredType is { } type && (type != CalendarQuickCreateType.PolarTask || _polarAuthService.IsLinked))
            QuickCreate.SelectedType = type;
    }

    /// <summary>Mes day cell click (doc 05 §3) — bindable directly from XAML since only the cell's own
    /// date is needed.</summary>
    [RelayCommand]
    private void OpenQuickCreateForDay(DateTime date) => OpenQuickCreate(date);

    /// <summary>The three direct-shortcut context-menu entries over an empty cell/slot (doc 05 §7:
    /// "Crear tarjeta Kanban aquí"/"Crear tarea Polar aquí"/"Crear recordatorio aquí") — same popover as a
    /// plain click, just pre-selecting the type instead of leaving it on the default.</summary>
    [RelayCommand]
    private void OpenQuickCreateForDayAsKanban(DateTime date) => OpenQuickCreate(date, null, CalendarQuickCreateType.Kanban);

    [RelayCommand]
    private void OpenQuickCreateForDayAsPolarTask(DateTime date) => OpenQuickCreate(date, null, CalendarQuickCreateType.PolarTask);

    [RelayCommand]
    private void OpenQuickCreateForDayAsReminder(DateTime date) => OpenQuickCreate(date, null, CalendarQuickCreateType.Reminder);

    /// <summary>"+" button in the top bar (doc 06 §1) — anchors on whatever date the user is currently
    /// looking at rather than always "hoy", so paging to a future month before creating doesn't silently
    /// create the item back in the present.</summary>
    [RelayCommand]
    private void OpenQuickCreateForReferenceDate() => OpenQuickCreate(ReferenceDate);

    [RelayCommand]
    private void CancelQuickCreate() => QuickCreate = null;

    [RelayCommand]
    private void SaveQuickCreate()
    {
        if (QuickCreate is not { CanSave: true } draft) return;
        if (CreateFromQuickCreate(draft) is not null) QuickCreate = null;
    }

    /// <summary>"Más opciones" (doc 06 §1, point 6) — saves with whatever minimal data is already filled
    /// in, then immediately opens the full editor for the rest, so the quick popover is never a ceiling.</summary>
    [RelayCommand]
    private void SaveQuickCreateAndEditMore()
    {
        if (QuickCreate is not { CanSave: true } draft) return;
        var created = CreateFromQuickCreate(draft);
        if (created is not null) { QuickCreate = null; _openItemEditor(created); }
    }

    /// <summary>Color de calendario de una sola capa — solo lo usa
    /// <see cref="CreateFromQuickCreate"/>, que construye un item suelto fuera del recorrido normal de
    /// <see cref="CalendarAggregationService.BuildItems"/> (donde el mapa se construye una única vez).</summary>
    private string ResolveLayerColor(string colorKey) =>
        CalendarLayerColorService.Resolve(_layerColorService.BuildMap(), colorKey);

    private CalendarItemViewModel? CreateFromQuickCreate(CalendarQuickCreateViewModel draft)
    {
        var title = draft.Title.Trim();
        var dueUtc = draft.CombinedDueDateUtc;
        var isAllDay = !draft.HasTime;

        switch (draft.SelectedType)
        {
            case CalendarQuickCreateType.Kanban when draft.SelectedColumn is { } column:
            {
                var durationMinutes = draft.CanShowDuration && draft.HasDuration ? draft.DurationMinutes : (int?)null;
                var card = new KanbanCard
                {
                    Id = Guid.NewGuid().ToString("N"), Title = title, DueDateUtc = dueUtc,
                    DurationMinutes = durationMinutes
                };
                if (!_kanbanService.Boards.Any(b => b.Columns.Contains(column)))
                {
                    _notificationService.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
                        LocalizationService.Instance["Kanban_Multi_MissingDestination"], NotificationSeverity.Warning);
                    return null;
                }
                column.Cards.Add(card);
                try { _kanbanService.Save(); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
                {
                    column.Cards.Remove(card);
                    _notificationService.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
                        LocalizationService.Instance["Kanban_Multi_SaveFailed"], NotificationSeverity.Error);
                    return null;
                }
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Title"),
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Message", title, LocalizedTextFormatter.Format("Calendar_Label_Kanban")),
                    NotificationSeverity.Success);
                return new CalendarItemViewModel
                {
                    SourceId = card.Id, SourceType = CalendarItemSourceType.Kanban, Title = title,
                    MomentUtc = dueUtc, IsAllDay = isAllDay, IsCompleted = false,
                    DurationMinutes = isAllDay ? null : durationMinutes,
                    LayerColorHex = CalendarItemViewModel.KanbanLayerColorHex,
                    CalendarColorHex = ResolveLayerColor(CalendarLayerColorService.KanbanColumnColorKey(column.Id)),
                    LayerKey = CalendarAggregationService.KanbanLayerKey, IconKind = MaterialIconKind.ViewColumnOutline
                };
            }
            case CalendarQuickCreateType.PolarTask when draft.SelectedPolarList is { } list:
            {
                var durationMinutes = draft.CanShowDuration && draft.HasDuration ? draft.DurationMinutes : (int?)null;
                var task = _polarTaskService.CreateTask(list, title, string.Empty, 0, dueUtc, PolarRecurrence.None, 0, [], durationMinutes);
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Title"),
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Message", title, LocalizedTextFormatter.Format("Calendar_Source_PolarTask")),
                    NotificationSeverity.Success);
                return new CalendarItemViewModel
                {
                    SourceId = task.Id, SourceType = CalendarItemSourceType.PolarTask, Title = title,
                    MomentUtc = dueUtc, IsAllDay = isAllDay, IsCompleted = false,
                    DurationMinutes = isAllDay ? null : task.DurationMinutes,
                    LayerColorHex = list.Color, CalendarColorHex = ResolveLayerColor(list.Id), LayerKey = list.Id,
                    IconKind = MaterialIconKind.CheckboxMarkedOutline, ParentContextLabel = list.Title
                };
            }
            case CalendarQuickCreateType.Reminder:
            {
                var durationMinutes = draft.CanShowDuration && draft.HasDuration ? draft.DurationMinutes : (int?)null;
                var reminder = _reminderService.Create(title, string.Empty, dueUtc, MaterialIconKind.BellRingOutline.ToString(), durationMinutes);
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Title"),
                    LocalizedTextFormatter.Format("Calendar_Notify_ItemCreated_Message", title, LocalizedTextFormatter.Format("Calendar_Label_Reminder")),
                    NotificationSeverity.Success);
                return new CalendarItemViewModel
                {
                    SourceId = reminder.Id, SourceType = CalendarItemSourceType.Reminder, Title = title,
                    MomentUtc = dueUtc, IsAllDay = isAllDay, IsCompleted = false,
                    DurationMinutes = isAllDay ? null : reminder.DurationMinutes,
                    LayerColorHex = CalendarItemViewModel.ReminderLayerColorHex,
                    CalendarColorHex = ResolveLayerColor(CalendarAggregationService.ReminderLayerKey),
                    LayerKey = CalendarAggregationService.ReminderLayerKey, IconKind = MaterialIconKind.BellRingOutline
                };
            }
            default:
                return null;
        }
    }

    // ===================== Fase 3: editar / completar / eliminar (docs 05 §4–6, 06 §3) =====================

    /// <summary>"Editar" from the detail popover, and "Ir a Kanban/Gestión de tareas" from the context
    /// menu (doc 05 §7) — both land the user on the real editor for this item's source rather than a
    /// read-only detail, since from the calendar there's no reason to make "ver" and "editar" two separate
    /// hops when the destination screen already gates further changes behind its own save button.</summary>
    [RelayCommand]
    private void RequestEditItem(CalendarItemViewModel item)
    {
        if (item.CanEdit) _openItemEditor(item);
    }

    [RelayCommand]
    private void ToggleCompleteItem(CalendarItemViewModel item)
    {
        // Defensive: the popover/context menu already gate this behind CanToggleComplete, which is false
        // for a ghost occurrence (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §4).
        if (item.IsGhostOccurrence) return;

        switch (item.SourceType)
        {
            case CalendarItemSourceType.PolarTask:
                var task = _polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == item.SourceId && !t.IsDeleted);
                if (task is not null) _polarTaskService.SetTaskCompleted(task, !task.Completed);
                break;
            case CalendarItemSourceType.PolarSubtask:
                var subtask = _polarTaskService.Store.Subtasks.FirstOrDefault(s => s.Id == item.SourceId && !s.IsDeleted);
                if (subtask is not null) _polarTaskService.SetSubtaskCompleted(subtask, !subtask.Completed);
                break;
            case CalendarItemSourceType.Reminder:
                _reminderService.ToggleCompleted(item.SourceId);
                break;
            // Kanban cards have no "completado" field of their own (doc 03 §3) — CalendarItemViewModel.
            // CanToggleComplete already keeps the checkbox out of the popover for this source, so this
            // branch is unreachable from the UI; left absent rather than a dead default case.
        }
    }

    /// <summary>Toggles a subtask/child listed read-only in the detail popover (doc 06 §5) — routes to
    /// whichever real owner (Kanban card or Polar task) it belongs to via
    /// <see cref="CalendarSubtaskViewModel.ParentSourceType"/>, since the subtask itself never carries a
    /// direct reference back to its parent's live model.</summary>
    [RelayCommand]
    private void ToggleSubtask(CalendarSubtaskViewModel subtask)
    {
        if (subtask.ParentSourceType == CalendarItemSourceType.Kanban)
        {
            var model = _kanbanService.AllColumns.SelectMany(c => c.Cards)
                .SelectMany(c => c.Subtasks).FirstOrDefault(s => s.Id == subtask.Id);
            if (model is null) return;
            model.IsDone = !model.IsDone;
            _kanbanService.Save();
        }
        else if (subtask.ParentSourceType == CalendarItemSourceType.PolarTask)
        {
            var model = _polarTaskService.Store.Subtasks.FirstOrDefault(s => s.Id == subtask.Id && !s.IsDeleted);
            if (model is not null) _polarTaskService.SetSubtaskCompleted(model, !model.Completed);
        }
    }

    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de borrar" salta
    // directo al borrado cuando está desactivado.
    [RelayCommand]
    private void RequestDeleteItem(CalendarItemViewModel item)
    {
        // Defensive: same CanDelete gate already keeps the button/menu entry out of the UI for a ghost
        // occurrence (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §4).
        if (item.IsGhostOccurrence) return;

        if (_settingsService.Settings.ConfirmBeforeDelete) ItemPendingDelete = item;
        else DeleteItem(item);
    }

    [RelayCommand]
    private void CancelDeleteItem() => ItemPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteItem()
    {
        if (ItemPendingDelete is not { } item) return;
        DeleteItem(item);
        ItemPendingDelete = null;
    }

    private void DeleteItem(CalendarItemViewModel item)
    {
        switch (item.SourceType)
        {
            case CalendarItemSourceType.Kanban:
                DeleteKanbanCard(item.SourceId);
                break;
            case CalendarItemSourceType.PolarTask:
                var task = _polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == item.SourceId && !t.IsDeleted);
                if (task is not null) _polarTaskService.DeleteTask(task);
                break;
            case CalendarItemSourceType.PolarSubtask:
                var subtask = _polarTaskService.Store.Subtasks.FirstOrDefault(s => s.Id == item.SourceId && !s.IsDeleted);
                if (subtask is not null) _polarTaskService.DeleteSubtask(subtask);
                break;
            case CalendarItemSourceType.Reminder:
                _reminderService.Delete(item.SourceId);
                break;
        }
    }

    /// <summary>Same soft-delete-to-trash path as <c>KanbanViewModel.ConfirmDeleteCard</c> (doc 05 §6) —
    /// duplicated rather than called through <c>KanbanViewModel</c> because the calendar only ever holds a
    /// reference to <see cref="KanbanService"/>, never to the Kanban page's own viewmodel.</summary>
    private void DeleteKanbanCard(string cardId)
    {
        foreach (var column in _kanbanService.AllColumns)
        {
            var card = column.Cards.FirstOrDefault(c => c.Id == cardId);
            if (card is null) continue;

            var index = column.Cards.IndexOf(card);
            column.Cards.Remove(card);
            _kanbanService.Boards.First(b => b.Columns.Contains(column)).TrashedCards.Add(new KanbanTrashedCard
            {
                Card = card,
                ColumnId = column.Id,
                ColumnTitle = column.Title,
                CardIndex = index
            });
            _kanbanService.Save();
            return;
        }
    }

    // ===================== Fase 3: arrastrar y soltar para reprogramar (doc 05 §2) =====================

    /// <summary>Reprograma la entidad real identificada por <paramref name="sourceType"/> y
    /// <paramref name="sourceId"/>. El controlador de arrastre comparte este camino entre chips y filas
    /// sin programar. Con <paramref name="newLocalTime"/> nulo (Mes, todo el día o Agenda) conserva la
    /// hora original; al soltar en una franja horaria actualiza tanto la fecha como la hora.</summary>
    public void MoveItem(CalendarItemSourceType sourceType, string sourceId, DateTime newLocalDate, TimeSpan? newLocalTime, bool isOpeningMilestone = false)
    {
        switch (sourceType)
        {
            case CalendarItemSourceType.Kanban:
            {
                var card = _kanbanService.AllColumns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == sourceId);
                if (card is null) return;
                var newDate = isOpeningMilestone
                    ? CombineUtc(newLocalDate, TimeSpan.Zero, card.StartDateUtc)
                    : CombineUtc(newLocalDate, newLocalTime, card.DueDateUtc);
                var start = isOpeningMilestone ? newDate : card.StartDateUtc;
                var end = isOpeningMilestone ? card.DueDateUtc : newDate;
                if (start is { } opening && end is { } closing && opening > closing)
                {
                    _notificationService.Push(LocalizationService.Instance["Calendar_Label_Kanban"],
                        LocalizationService.Instance["Kanban_DateRange_Invalid"], NotificationSeverity.Warning);
                    return;
                }
                if (isOpeningMilestone) card.StartDateUtc = newDate;
                else card.DueDateUtc = newDate;
                _kanbanService.Save();
                break;
            }
            case CalendarItemSourceType.PolarTask:
            {
                var task = _polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == sourceId && !t.IsDeleted);
                if (task is null) return;
                var newDue = CombineUtc(newLocalDate, newLocalTime, task.DueDateUtc);
                _polarTaskService.UpdateTask(task, task.Title, task.Description, task.Priority, newDue, task.Recurrence, task.TimeEstimate, task.Tags.ToArray());
                break;
            }
            case CalendarItemSourceType.PolarSubtask:
            {
                var subtask = _polarTaskService.Store.Subtasks.FirstOrDefault(s => s.Id == sourceId && !s.IsDeleted);
                if (subtask is null) return;
                _polarTaskService.UpdateSubtaskDueDate(subtask, CombineUtc(newLocalDate, newLocalTime, subtask.DueDateUtc));
                break;
            }
            case CalendarItemSourceType.Reminder:
            {
                var reminder = _reminderService.ActiveReminders.FirstOrDefault(r => r.Id == sourceId);
                if (reminder is null) return;
                _reminderService.Reschedule(sourceId, CombineUtc(newLocalDate, newLocalTime, reminder.NotifyAtUtc));
                break;
            }
        }
    }

    // ===================== Franja horaria: redimensionar arrastrando el borde inferior (doc 05 §2b) =====================

    /// <summary>Smallest resizable duration — matches the 5-minute grain
    /// <see cref="RoundToFiveMinutes"/> snaps to, so a card can never be dragged down to something the
    /// snap would immediately round back up past zero.</summary>
    public const int MinimumDurationMinutes = 5;

    /// <summary>Writes back a dragged resize of a timed item's bottom edge in the Semana/Día grid (docs/
    /// calendar-feature/05-interacciones-y-microinteracciones.md §2b) — Kanban, PolarTask and Reminder.
    /// Stored as minutes rather than an absolute end instant so a later <see cref="MoveItem"/> that changes
    /// the start time keeps the same length automatically. PolarSubtask and all-day/ghost items are filtered
    /// out by <see cref="CalendarItemViewModel.CanResize"/>; this method is the defensive floor for the write
    /// path.</summary>
    public void ResizeItem(CalendarItemSourceType sourceType, string sourceId, int durationMinutes)
    {
        var clamped = Math.Max(durationMinutes, MinimumDurationMinutes);
        switch (sourceType)
        {
            case CalendarItemSourceType.Kanban:
            {
                var card = _kanbanService.AllColumns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == sourceId);
                if (card is null) return;
                card.DurationMinutes = clamped;
                _kanbanService.Save();
                break;
            }
            case CalendarItemSourceType.PolarTask:
            {
                var task = _polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == sourceId && !t.IsDeleted);
                if (task is null) return;
                _polarTaskService.UpdateTaskDuration(task, clamped);
                break;
            }
            case CalendarItemSourceType.Reminder:
            {
                _reminderService.RescheduleDuration(sourceId, clamped);
                break;
            }
        }
    }

    /// <summary>Rounds a raw pixel-derived minute count to the nearest 5-minute grain — the same
    /// granularity the Kanban card editor's duration stepper already uses for
    /// <c>KanbanCardEditorViewModel.IncrementDueMinute</c>/<c>PolarTask.TimeEstimate</c>'s
    /// <c>NumericUpDown</c>, so the resize gesture never lands on a value the editor couldn't also
    /// produce by hand.</summary>
    public static int RoundToFiveMinutes(double rawMinutes) => (int)(Math.Round(rawMinutes / 5.0) * 5);

    /// <summary>Longest duration a card starting at <paramref name="item"/>'s time of day can have while
    /// still ending within the same calendar day — resizing (like the rest of Fase 3's date handling,
    /// docs/calendar-feature/04-vistas-y-navegacion.md "todo el trabajo de fecha… en hora local") never
    /// spans across midnight, avoiding the cross-day edge cases a real end-of-day rollover would need.</summary>
    public static int MaxDurationMinutesSameDay(CalendarItemViewModel item) =>
        Math.Max(MinimumDurationMinutes, (int)(24 * 60 - item.MomentLocal.TimeOfDay.TotalMinutes));

    private static DateTime CombineUtc(DateTime newLocalDate, TimeSpan? newLocalTime, DateTime? previousUtc)
    {
        var timeOfDay = newLocalTime ?? previousUtc?.ToLocalTime().TimeOfDay ?? TimeSpan.Zero;
        var local = newLocalDate.Date.Add(timeOfDay);
        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>Culture-derived, all-caps abbreviated month name — replaces the old hardcoded
    /// <c>SpanishMonthsShort</c> array (docs/agent-docs/internacionalizacion-i18n.md §1.7).</summary>
    private static string AbbrMonth(int month) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(month).ToUpper(CultureInfo.CurrentCulture);

    /// <summary>Culture-derived, all-caps full month name — replaces the old hardcoded
    /// <c>SpanishMonths</c> array (docs/agent-docs/internacionalizacion-i18n.md §1.7).</summary>
    private static string FullMonth(int month) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month).ToUpper(CultureInfo.CurrentCulture);

    private static string FormatWeekLabel(DateTime start, DateTime end)
    {
        if (start.Month == end.Month)
            return $"{start.Day}–{end.Day} {AbbrMonth(start.Month)} {end.Year}";
        if (start.Year == end.Year)
            return $"{start.Day} {AbbrMonth(start.Month)} – {end.Day} {AbbrMonth(end.Month)} {end.Year}";
        return $"{start.Day} {AbbrMonth(start.Month)} {start.Year} – {end.Day} {AbbrMonth(end.Month)} {end.Year}";
    }

    private static string FormatDayLabel(DateTime date)
    {
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        var weekday = dtf.GetDayName(date.DayOfWeek).ToUpper(CultureInfo.CurrentCulture);
        return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Header_FullDate"], weekday, date.Day, FullMonth(date.Month));
    }

    private static string FormatAgendaRangeLabel(DateTime start, DateTime end)
    {
        var lastDay = end.AddDays(-1);
        return $"{start.Day} {AbbrMonth(start.Month)} – {lastDay.Day} {AbbrMonth(lastDay.Month)}";
    }

    /// <summary>Rebuilds the 24 fixed hour rows — called once from the constructor and again whenever
    /// <see cref="WorkingHoursStart"/>/<see cref="WorkingHoursEnd"/>/<see cref="TimeFormat24h"/> changes
    /// live from Ajustes (docs/calendar-feature/09-configuracion-y-ajustes.md §1). A plain property
    /// re-raise rather than an <see cref="ObservableCollection{T}"/> — the 24 rows never change one at a
    /// time, only ever as a whole batch.</summary>
    private void RebuildHourRows()
    {
        HourRows = Enumerable.Range(0, 24)
            .Select(hour => new CalendarHourRowViewModel(hour, hour >= WorkingHoursStart && hour < WorkingHoursEnd, TimeFormat24h))
            .ToList();
        OnPropertyChanged(nameof(HourRows));
    }

    private static CalendarViewMode ParseViewMode(string value) =>
        Enum.TryParse<CalendarViewMode>(value, out var mode) ? mode : CalendarViewMode.Month;
}
