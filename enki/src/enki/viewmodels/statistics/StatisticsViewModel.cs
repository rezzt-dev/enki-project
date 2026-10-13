using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Qué ventana de historial cubre el informe.</summary>
public enum StatisticsRange
{
    Week,
    Month,
    AllTime
}

/// <summary>Página de Estadísticas. Toma el informe completo de
/// <see cref="StatisticsAggregationService.BuildReport"/> y lo reparte en las propiedades que consume la
/// vista, incluidas las series ya listas para los gráficos de <c>controls/charts/</c>.
///
/// Igual que <c>WeekBarViewModel</c> precalculaba alturas en píxeles para que el XAML no hiciera
/// aritmética, aquí el ViewModel es también quien rotula: el servicio devuelve cifras y etiquetas que son
/// dato (nombres de etiqueta, carpetas, fechas), y todo lo que es vocabulario de interfaz — prioridades,
/// estados del flujo, tramos de recordatorios — se traduce aquí con <see cref="LocalizationService"/>,
/// que es lo que permite que <see cref="RefreshLocalizedLabels"/> lo rehaga entero al cambiar de idioma.
///
/// La página se divide en seis <see cref="StatisticsSectionViewModel"/> plegables cuyo estado se persiste
/// en <see cref="Models.AppSettings.StatisticsCollapsedSections"/>.</summary>
public sealed partial class StatisticsViewModel : ViewModelBase
{
    private const int AllTimeWeeks = 52;

    private readonly StatisticsAggregationService _aggregationService;
    private readonly PolarAuthService _polarAuthService;
    private readonly bool _loadInBackground;
    private readonly NotificationService? _notificationService;
    private int _loadRevision;
    private StatisticsAggregationService.VaultStatisticsSnapshot? _vaultSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isLoading;

    public bool ShowEmptyState => !IsLoading && !HasEnoughData;
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Opcional a propósito: los tests construyen el ViewModel sin ajustes y entonces el estado
    /// plegado/desplegado simplemente vive lo que dure la instancia, sin tocar disco.</summary>
    private readonly SettingsService? _settingsService;

    public StatisticsViewModel(StatisticsAggregationService aggregationService, PolarAuthService polarAuthService,
        SettingsService? settingsService = null, bool loadOnCreate = true, bool loadInBackground = false,
        NotificationService? notificationService = null)
    {
        _aggregationService = aggregationService;
        _polarAuthService = polarAuthService;
        _settingsService = settingsService;
        _loadInBackground = loadInBackground;
        _notificationService = notificationService;

        var collapsed = settingsService?.Settings.StatisticsCollapsedSections ?? [];
        PulseSection = BuildSection(SectionKeys.Pulse, "Statistics_Section_Pulse", collapsed);
        RhythmSection = BuildSection(SectionKeys.Rhythm, "Statistics_Section_Rhythm", collapsed);
        TasksSection = BuildSection(SectionKeys.Tasks, "Statistics_Section_Tasks", collapsed);
        KnowledgeSection = BuildSection(SectionKeys.Knowledge, "Statistics_Section_Knowledge", collapsed);
        HabitsSection = BuildSection(SectionKeys.Habits, "Statistics_Section_Habits", collapsed);
        StorageSection = BuildSection(SectionKeys.Storage, "Statistics_Section_Storage", collapsed);

        if (loadOnCreate) Reload();
    }

    // ===================== Sub-secciones plegables =====================

    private static class SectionKeys
    {
        public const string Pulse = "pulse";
        public const string Rhythm = "rhythm";
        public const string Tasks = "tasks";
        public const string Knowledge = "knowledge";
        public const string Habits = "habits";
        public const string Storage = "storage";
    }

    public StatisticsSectionViewModel PulseSection { get; }
    public StatisticsSectionViewModel RhythmSection { get; }
    public StatisticsSectionViewModel TasksSection { get; }
    public StatisticsSectionViewModel KnowledgeSection { get; }
    public StatisticsSectionViewModel HabitsSection { get; }
    public StatisticsSectionViewModel StorageSection { get; }

    /// <summary>Las seis en orden de aparición — lo que recorre <see cref="RefreshLocalizedLabels"/> y lo
    /// que hace fácil añadir una séptima sin repetir la lista en cinco sitios.</summary>
    public IReadOnlyList<StatisticsSectionViewModel> Sections =>
        [PulseSection, RhythmSection, TasksSection, KnowledgeSection, HabitsSection, StorageSection];

    private StatisticsSectionViewModel BuildSection(string key, string titleResourceKey, List<string> collapsed) =>
        new(key, LocalizationService.Instance[titleResourceKey], !collapsed.Contains(key), PersistSectionState);

    /// <summary>Reescribe la lista completa de plegadas en cada cambio: son seis claves, y recalcularla
    /// entera evita tener que razonar sobre añadir/quitar una a una si el estado llegara desincronizado.</summary>
    private void PersistSectionState(StatisticsSectionViewModel _)
    {
        if (_settingsService is null) return;
        _settingsService.Settings.StatisticsCollapsedSections =
            [.. Sections.Where(section => !section.IsExpanded).Select(section => section.Key)];
        _settingsService.Save();
    }

    // ===================== Rango =====================

    [ObservableProperty]
    private StatisticsRange _range = StatisticsRange.Month;

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private bool _hasEnoughData;

    /// <summary>Rótulo del rango activo ("últimos 28 días"), para que cada tarjeta no tenga que repetirlo.</summary>
    [ObservableProperty]
    private string _rangeLabel = string.Empty;

    // ===================== Serie semanal (compatibilidad + gráfico de barras) =====================

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _weeklyNotesSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _weeklyTasksSeries = [];

    [ObservableProperty]
    private int _notesThisWeek;

    [ObservableProperty]
    private int _tasksCompletedThisWeek;

    // ===================== KPIs del rango =====================

    [ObservableProperty]
    private int _notesCreatedInRange;

    [ObservableProperty]
    private int _notesEditedInRange;

    [ObservableProperty]
    private int _tasksCompletedInRange;

    [ObservableProperty]
    private int _wordsWrittenInRange;

    [ObservableProperty]
    private int _activeDaysInRange;

    [ObservableProperty]
    private int _rangeDays;

    [ObservableProperty]
    private int _currentStreak;

    [ObservableProperty]
    private int _bestStreak;

    /// <summary>"18 / 28" — los días con actividad sobre los del rango. Como fracción se lee de un
    /// vistazo; como porcentaje suelto no diría de cuántos días estamos hablando.</summary>
    public string ActiveDaysLabel => $"{ActiveDaysInRange} / {RangeDays}";

    public double ActiveDaysPercent => RangeDays == 0 ? 0 : ActiveDaysInRange * 100.0 / RangeDays;

    public double AverageNotesPerActiveDay =>
        ActiveDaysInRange == 0 ? 0 : (double)(NotesCreatedInRange + NotesEditedInRange) / ActiveDaysInRange;

    /// <summary>"1,4 · POR DÍA ACTIVO". Una cifra suelta bajo otra cifra no se entiende: la unidad va
    /// pegada al número, no en una leyenda aparte.</summary>
    public string PerActiveDayLabel =>
        $"{AverageNotesPerActiveDay.ToString("0.#", CultureInfo.CurrentCulture)} · {LocalizationService.Instance["Statistics_Tile_PerActiveDay"]}";

    // ===================== Hábitos =====================

    [ObservableProperty]
    private double? _habitCompletionPercent;

    [ObservableProperty]
    private int _habitStreakCurrent;

    [ObservableProperty]
    private int _habitStreakBest;

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _habitConsistency = [];

    [ObservableProperty]
    private IReadOnlyList<HeatCell> _habitHeatmapCells = [];

    public bool HasHabitData => HabitCompletionPercent is not null;

    /// <summary>La misma cifra sin nullable, para el medidor circular: <c>RadialGauge.Value</c> es un
    /// <c>double</c> y un enlace nulo no tiene conversión válida, aunque la tarjeta esté oculta.</summary>
    public double HabitCompletionValue => HabitCompletionPercent ?? 0;
    public bool HasHabitRanking => HabitConsistency.Count > 0;
    public bool HasHabitHeatmap => HabitHeatmapCells.Count > 0;

    // ===================== Vault =====================

    [ObservableProperty]
    private int _vaultTotalNotes;

    [ObservableProperty]
    private int _vaultTotalSessions;

    [ObservableProperty]
    private string _vaultSizeLabel = string.Empty;

    [ObservableProperty]
    private int _vaultFolders;

    [ObservableProperty]
    private int _vaultFiles;

    [ObservableProperty]
    private int _vaultDistinctTags;

    [ObservableProperty]
    private int _vaultTotalWords;

    [ObservableProperty]
    private double _vaultAverageWords;

    [ObservableProperty]
    private string _longestNoteTitle = string.Empty;

    [ObservableProperty]
    private int _longestNoteWords;

    [ObservableProperty]
    private string _notesSizeLabel = string.Empty;

    [ObservableProperty]
    private string _filesSizeLabel = string.Empty;

    /// <summary>"ADJUNTOS · 112,3 KB" — desglosa el tamaño total en la parte que no son notas.</summary>
    public string FilesSizeSummary =>
        $"{LocalizationService.Instance["Statistics_Tile_FilesSize"]} · {FilesSizeLabel}";

    // ===================== Tareas =====================

    [ObservableProperty]
    private int _kanbanPending;

    [ObservableProperty]
    private int _kanbanInProgress;

    [ObservableProperty]
    private int _kanbanDone;

    [ObservableProperty]
    private int _tasksOverdue;

    [ObservableProperty]
    private int _tasksDueToday;

    [ObservableProperty]
    private int _tasksDueNext7Days;

    [ObservableProperty]
    private int _tasksWithoutDueDate;

    [ObservableProperty]
    private int _subtasksTotal;

    [ObservableProperty]
    private int _subtasksDone;

    [ObservableProperty]
    private double _subtaskPercent;

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _subtaskSegments = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _flowSegments = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _priorityDistribution = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _columnDistribution = [];

    public int TotalKanbanCards => KanbanPending + KanbanInProgress + KanbanDone;
    public bool HasKanbanCards => TotalKanbanCards > 0;
    public bool HasPriorityData => PriorityDistribution.Any(p => p.Value > 0);
    public bool HasColumnCards => ColumnDistribution.Any(c => c.Value > 0);
    public bool HasSubtasks => SubtasksTotal > 0;

    /// <summary>"34/51 · 67%" — el avance de subtareas dicho de las dos formas en las que se pregunta.</summary>
    public string SubtasksSummary =>
        $"{SubtasksDone}/{SubtasksTotal} · {SubtaskPercent.ToString("0", CultureInfo.CurrentCulture)}%";

    /// <summary>Porcentaje de tarjetas del tablero que ya están en la columna final.</summary>
    public double BoardCompletionPercent => TotalKanbanCards == 0 ? 0 : KanbanDone * 100.0 / TotalKanbanCards;

    // ===================== Recordatorios =====================

    [ObservableProperty]
    private int _remindersActive;

    [ObservableProperty]
    private int _remindersOverdue;

    [ObservableProperty]
    private int _remindersRecurring;

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _reminderSegments = [];

    public bool HasReminders => RemindersActive > 0;
    public bool HasRecurringReminders => RemindersRecurring > 0;

    public string RemindersRecurringLabel =>
        $"{RemindersRecurring} · {LocalizationService.Instance["Statistics_Tile_RemindersRecurring"]}";

    // ===================== Series de los gráficos =====================

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _dailyNotesSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _dailyTasksSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _growthSeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _weekdaySeries = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _noteLengthBuckets = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _topTags = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _topFolders = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _longestNotes = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _fileTypes = [];

    [ObservableProperty]
    private IReadOnlyList<ChartDatum> _sessionNotes = [];

    [ObservableProperty]
    private IReadOnlyList<HeatCell> _activityHeatmapCells = [];

    [ObservableProperty]
    private IReadOnlyList<double> _punchCardValues = [];

    [ObservableProperty]
    private IReadOnlyList<double> _notesSparkline = [];

    [ObservableProperty]
    private IReadOnlyList<double> _tasksSparkline = [];

    [ObservableProperty]
    private IReadOnlyList<double> _habitsSparkline = [];

    public bool HasDailySeries => DailyNotesSeries.Count > 1;
    public bool HasTags => TopTags.Count > 0;
    public bool HasFolders => TopFolders.Count > 0;
    public bool HasLongestNotes => LongestNotes.Count > 0;
    public bool HasFiles => FileTypes.Count > 0;
    public bool HasSessionNotes => SessionNotes.Any(s => s.Value > 0);
    public bool HasActivityHeatmap => ActivityHeatmapCells.Any(c => c.Count > 0);
    public bool HasPunchCard => PunchCardValues.Any(v => v > 0);
    public bool HasNoteLengths => NoteLengthBuckets.Any(b => b.Value > 0);

    // ── Etiquetas de día de la semana: lunes→domingo, localizadas ──
    public string[] DayOfWeekHeaders { get; private set; } = BuildDayHeaders();

    // ===================== Recarga =====================

    public void Reload()
    {
        var revision = ++_loadRevision;
        if (_loadInBackground)
        {
            IsLoading = true;
            PendingLoad = ReloadAsync(revision, PendingLoad);
            return;
        }
        IsPolarLinked = _polarAuthService.IsLinked;

        var (startUtc, endUtc) = BuildRangeUtc(Range);
        var report = _aggregationService.BuildReport(startUtc, endUtc);

        ApplyReport(report);
    }

    /// <summary>Serializa los recorridos del disco y descarta solicitudes sustituidas antes de
    /// empezar otra lectura. Solo las lecturas usan el pool; agregar los almacenes mutables y
    /// publicar propiedades siempre ocurre en el dispatcher.</summary>
    private async Task ReloadAsync(int revision, Task previousLoad)
    {
        try
        {
            await previousLoad.ConfigureAwait(false);
            if (revision != Volatile.Read(ref _loadRevision)) return;
            var snapshot = await _aggregationService.ReadVaultSnapshotAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _loadRevision || !_aggregationService.IsSnapshotCurrent(snapshot)) return;
                _vaultSnapshot = snapshot;
                ApplySnapshot(snapshot);
            });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Warning("No se pudieron actualizar las estadísticas", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision != _loadRevision) return;
                _notificationService?.PushLocalized(LocalizedTextFormatter.Format("Statistics_Title"),
                    VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Warning);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision == _loadRevision) IsLoading = false;
            });
        }
    }

    private void ApplySnapshot(StatisticsAggregationService.VaultStatisticsSnapshot snapshot)
    {
        IsPolarLinked = _polarAuthService.IsLinked;
        var (startUtc, endUtc) = BuildRangeUtc(Range);
        ApplyReport(_aggregationService.BuildReport(startUtc, endUtc, snapshot));
    }

    private void ApplyReport(StatisticsReport report)
    {

        ApplyWeeklySeries(report);
        ApplyDailySeries(report);
        ApplyVault(report);
        ApplyTasks(report);
        ApplyHabits(report);
        ApplyReminders(report);
        ApplyTops(report);

        RangeLabel = BuildRangeLabel(report.RangeDays);

        NotesCreatedInRange = report.NotesCreatedInRange;
        NotesEditedInRange = report.NotesEditedInRange;
        TasksCompletedInRange = report.TasksCompletedInRange;
        WordsWrittenInRange = report.WordsWrittenInRange;
        ActiveDaysInRange = report.ActiveDaysInRange;
        RangeDays = report.RangeDays;
        CurrentStreak = report.ActivityStreak.Current;
        BestStreak = report.ActivityStreak.Best;

        // Un tablero recién creado ya trae tarjetas de ejemplo (KanbanService.CreateSeedBoard), así que
        // contarlas aquí haría que un vault virgen nunca mostrara el estado vacío. La página se abre con
        // actividad real, con hábitos en marcha o con notas ya escritas — no con el contenido semilla.
        HasEnoughData = report.Daily.Any(d => d.Total > 0)
            || report.HabitCompletionPercent is not null
            || report.Vault.Notes > 0;

        UpdateSectionSummaries();
        RaiseDerived();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>El dato que cada cabecera sigue mostrando con su sección plegada.</summary>
    private void UpdateSectionSummaries()
    {
        var loc = LocalizationService.Instance;
        PulseSection.Summary = RangeLabel;
        RhythmSection.Summary = $"{ActiveDaysLabel} · {loc["Statistics_Tile_ActiveDays"]}";
        TasksSection.Summary = $"{TotalKanbanCards} · {loc["Statistics_Unit_Cards"]}";
        KnowledgeSection.Summary = $"{VaultTotalNotes} · {loc["Statistics_Unit_Notes"]}";
        HabitsSection.Summary = HabitCompletionPercent is { } percent
            ? percent.ToString("0", CultureInfo.CurrentCulture) + "%"
            : loc["Statistics_Tile_NoHabits"];
        StorageSection.Summary = VaultSizeLabel;
    }

    private void ApplyWeeklySeries(StatisticsReport report)
    {
        WeeklyNotesSeries = [.. report.Weekly.Select(w => new ChartDatum(
            w.WeekStart.ToString("dd MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture),
            w.NotesCreated + w.NotesEdited)
        {
            ShortLabel = w.WeekStart.ToString("d/M", CultureInfo.CurrentCulture)
        })];

        WeeklyTasksSeries = [.. report.Weekly.Select(w => new ChartDatum(
            w.WeekStart.ToString("dd MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture),
            w.TasksCompleted))];

        var lastWeek = report.Weekly.Count == 0 ? null : report.Weekly[^1];
        NotesThisWeek = lastWeek is null ? 0 : lastWeek.NotesCreated + lastWeek.NotesEdited;
        TasksCompletedThisWeek = lastWeek?.TasksCompleted ?? 0;
    }

    private void ApplyDailySeries(StatisticsReport report)
    {
        DailyNotesSeries = [.. report.Daily.Select(d => new ChartDatum(
            d.Date.ToString("d", CultureInfo.CurrentCulture),
            d.NotesCreated + d.NotesEdited)
        {
            ShortLabel = d.Date.ToString("d MMM", CultureInfo.CurrentCulture)
        })];

        DailyTasksSeries = [.. report.Daily.Select(d => new ChartDatum(
            d.Date.ToString("d", CultureInfo.CurrentCulture), d.TasksCompleted))];

        GrowthSeries = report.Growth;
        WeekdaySeries = report.WeekdayActivity;
        ActivityHeatmapCells = report.ActivityHeatmap;
        PunchCardValues = report.PunchCard;

        // Las sparklines de las fichas de KPI solo necesitan la forma reciente: con el rango "Todo" (364
        // puntos) dentro de 60px de ancho no se distinguiría nada, así que se recortan a los últimos 30 días.
        NotesSparkline = [.. report.Daily.TakeLast(30).Select(d => (double)(d.NotesCreated + d.NotesEdited))];
        TasksSparkline = [.. report.Daily.TakeLast(30).Select(d => (double)d.TasksCompleted)];
        HabitsSparkline = [.. report.Daily.TakeLast(30)
            .Select(d => d.HabitsApplicable == 0 ? 0 : d.HabitsCompleted * 100.0 / d.HabitsApplicable)];
    }

    private void ApplyVault(StatisticsReport report)
    {
        var vault = report.Vault;
        VaultTotalNotes = vault.Notes;
        VaultTotalSessions = vault.Sessions;
        VaultFolders = vault.Folders;
        VaultFiles = vault.Files;
        VaultDistinctTags = vault.DistinctTags;
        VaultTotalWords = vault.TotalWords;
        VaultAverageWords = vault.AverageWords;
        LongestNoteTitle = vault.LongestNoteTitle;
        LongestNoteWords = vault.LongestNoteWords;
        VaultSizeLabel = StatisticsAggregationService.FormatBytes(vault.TotalBytes);
        NotesSizeLabel = StatisticsAggregationService.FormatBytes(vault.NotesBytes);
        FilesSizeLabel = StatisticsAggregationService.FormatBytes(vault.FilesBytes);
    }

    private void ApplyTasks(StatisticsReport report)
    {
        KanbanPending = report.Flow.Pending;
        KanbanInProgress = report.Flow.InProgress;
        KanbanDone = report.Flow.Done;

        TasksOverdue = report.Tasks.Overdue;
        TasksDueToday = report.Tasks.DueToday;
        TasksDueNext7Days = report.Tasks.DueNext7Days;
        TasksWithoutDueDate = report.Tasks.NoDueDate;
        SubtasksTotal = report.Tasks.Subtasks;
        SubtasksDone = report.Tasks.SubtasksDone;
        SubtaskPercent = report.Tasks.SubtaskCompletionPercent;

        var doneLabel = LocalizationService.Instance["Statistics_Flow_Done"];
        var pendingLabel = LocalizationService.Instance["Statistics_Flow_Pending"];
        SubtaskSegments = ChartDatum.WithShades(
        [
            new ChartDatum(doneLabel, report.Tasks.SubtasksDone),
            new ChartDatum(pendingLabel, report.Tasks.Subtasks - report.Tasks.SubtasksDone)
        ]);

        var loc = LocalizationService.Instance;
        FlowSegments = ChartDatum.WithShades(
        [
            new ChartDatum(loc["Statistics_Flow_Pending"], report.Flow.Pending),
            new ChartDatum(loc["Statistics_Flow_InProgress"], report.Flow.InProgress),
            new ChartDatum(loc["Statistics_Flow_Done"], report.Flow.Done)
        ]);

        PriorityDistribution = ChartDatum.WithShades(
        [
            new ChartDatum(loc["Kanban_Priority_High"], report.PriorityCounts[0]),
            new ChartDatum(loc["Kanban_Priority_Medium"], report.PriorityCounts[1]),
            new ChartDatum(loc["Kanban_Priority_Low"], report.PriorityCounts[2])
        ]);

        ColumnDistribution = report.ColumnDistribution;
    }

    private void ApplyHabits(StatisticsReport report)
    {
        HabitCompletionPercent = report.HabitCompletionPercent;
        HabitStreakCurrent = report.HabitStreak.Current;
        HabitStreakBest = report.HabitStreak.Best;
        HabitConsistency = report.HabitConsistency;
        HabitHeatmapCells = report.HabitHeatmap;
    }

    private void ApplyReminders(StatisticsReport report)
    {
        RemindersActive = report.Reminders.Active;
        RemindersOverdue = report.Reminders.Overdue;
        RemindersRecurring = report.Reminders.Recurring;

        var loc = LocalizationService.Instance;
        ReminderSegments = ChartDatum.WithShades(
        [
            new ChartDatum(loc["Reminders_Status_Scheduled"], report.Reminders.Upcoming),
            new ChartDatum(loc["Reminders_Status_Overdue"], report.Reminders.Overdue),
            new ChartDatum(loc["Statistics_Flow_Done"], report.Reminders.Completed)
        ]);
    }

    private void ApplyTops(StatisticsReport report)
    {
        TopTags = report.TopTags;
        TopFolders = report.TopFolders;
        LongestNotes = report.LongestNotes;
        NoteLengthBuckets = report.NoteLengthBuckets;
        FileTypes = ChartDatum.WithShades(report.FileTypes);
        SessionNotes = report.SessionNotes;
    }

    /// <summary>Las propiedades calculadas (las que no llevan <c>[ObservableProperty]</c>) no notifican
    /// solas; se avisa de todas juntas al final de cada recarga.</summary>
    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasHabitData));
        OnPropertyChanged(nameof(HabitCompletionValue));
        OnPropertyChanged(nameof(HasHabitRanking));
        OnPropertyChanged(nameof(HasHabitHeatmap));
        OnPropertyChanged(nameof(HasKanbanCards));
        OnPropertyChanged(nameof(TotalKanbanCards));
        OnPropertyChanged(nameof(HasPriorityData));
        OnPropertyChanged(nameof(HasColumnCards));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtasksSummary));
        OnPropertyChanged(nameof(BoardCompletionPercent));
        OnPropertyChanged(nameof(HasReminders));
        OnPropertyChanged(nameof(HasDailySeries));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(HasLongestNotes));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasSessionNotes));
        OnPropertyChanged(nameof(HasActivityHeatmap));
        OnPropertyChanged(nameof(HasPunchCard));
        OnPropertyChanged(nameof(HasNoteLengths));
        OnPropertyChanged(nameof(ActiveDaysLabel));
        OnPropertyChanged(nameof(ActiveDaysPercent));
        OnPropertyChanged(nameof(AverageNotesPerActiveDay));
        OnPropertyChanged(nameof(PerActiveDayLabel));
        OnPropertyChanged(nameof(FilesSizeSummary));
        OnPropertyChanged(nameof(HasRecurringReminders));
        OnPropertyChanged(nameof(RemindersRecurringLabel));
    }

    partial void OnRangeChanged(StatisticsRange value)
    {
        // Cambiar la ventana del gráfico no cambia el vault: reutilizar la lectura evita otra E/S.
        if (_loadInBackground && !IsLoading && _vaultSnapshot is { } snapshot
            && _aggregationService.IsSnapshotCurrent(snapshot)) ApplySnapshot(snapshot);
        else Reload();
    }

    [RelayCommand]
    private void SelectRange(string rangeName)
    {
        if (Enum.TryParse<StatisticsRange>(rangeName, out var range)) Range = range;
    }

    private static (DateTime StartUtc, DateTime EndUtc) BuildRangeUtc(StatisticsRange range)
    {
        var nowUtc = DateTime.UtcNow;
        var endUtc = DateTime.UtcNow.Date.AddDays(1).ToUniversalTime();
        var startUtc = range switch
        {
            StatisticsRange.Week => nowUtc.AddDays(-7),
            StatisticsRange.AllTime => nowUtc.AddDays(-7 * AllTimeWeeks),
            _ => nowUtc.AddDays(-28)
        };
        return (startUtc, endUtc);
    }

    private static string BuildRangeLabel(int days) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Statistics_RangeLabel"], days);

    private static string[] BuildDayHeaders() =>
        [.. Enumerable.Range(0, 7).Select(d =>
            CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((d + 1) % 7))
                .ToUpper(CultureInfo.CurrentCulture))];

    /// <summary>Mismo contrato que el resto de páginas en un cambio de idioma en caliente (i18n Fase 4):
    /// las etiquetas de días, los rótulos de prioridad/flujo y las fechas de las series se formatean con
    /// la cultura actual, así que rehacer el informe entero es lo correcto y lo más simple.</summary>
    public void RefreshLocalizedLabels()
    {
        DayOfWeekHeaders = BuildDayHeaders();
        OnPropertyChanged(nameof(DayOfWeekHeaders));

        var loc = LocalizationService.Instance;
        PulseSection.ApplyLocalizedTitle(loc["Statistics_Section_Pulse"]);
        RhythmSection.ApplyLocalizedTitle(loc["Statistics_Section_Rhythm"]);
        TasksSection.ApplyLocalizedTitle(loc["Statistics_Section_Tasks"]);
        KnowledgeSection.ApplyLocalizedTitle(loc["Statistics_Section_Knowledge"]);
        HabitsSection.ApplyLocalizedTitle(loc["Statistics_Section_Habits"]);
        StorageSection.ApplyLocalizedTitle(loc["Statistics_Section_Storage"]);

        Reload();
    }
}
