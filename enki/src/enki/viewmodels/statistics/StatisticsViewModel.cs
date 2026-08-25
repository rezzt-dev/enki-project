using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Which window of weekly history <see cref="StatisticsViewModel.Weeks"/> covers.</summary>
public enum StatisticsRange
{
    Week,
    Month,
    AllTime
}

/// <summary>Redesigned "Estadísticas" page — hero stat grid, kanban flow meter, weekly bar chart,
/// and a habit consistency heatmap. All rendered with the app's native controls (Border/Grid/StackPanel)
/// without any charting library.</summary>
public sealed partial class StatisticsViewModel : ViewModelBase
{
    private const int AllTimeWeeks = 52;
    private const double ChartMaxBarHeight = 120;

    private readonly StatisticsAggregationService _aggregationService;
    private readonly PolarAuthService _polarAuthService;

    // ── Weekly series (existing) ──
    public ObservableCollection<WeekBarViewModel> Weeks { get; } = [];

    [ObservableProperty]
    private StatisticsRange _range = StatisticsRange.Month;

    [ObservableProperty]
    private int _notesThisWeek;

    [ObservableProperty]
    private int _tasksCompletedThisWeek;

    [ObservableProperty]
    private double? _habitCompletionPercent;

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private bool _hasEnoughData;

    public bool HasHabitData => HabitCompletionPercent is not null;

    // ── Vault snapshot ──
    [ObservableProperty]
    private int _vaultTotalNotes;

    [ObservableProperty]
    private int _vaultTotalSessions;

    [ObservableProperty]
    private string _vaultSizeLabel = string.Empty;

    // ── Kanban flow meter ──
    [ObservableProperty]
    private int _kanbanPending;

    [ObservableProperty]
    private int _kanbanInProgress;

    [ObservableProperty]
    private int _kanbanDone;

    /// <summary>Drives the three segments of the flow meter — each is a fraction of the total so the
    /// XAML can bind to proportional widths without the View doing its own math.</summary>
    public double KanbanPendingFraction => TotalKanbanCards == 0 ? 0 : Math.Max(0.02, (double)KanbanPending / TotalKanbanCards);
    public double KanbanInProgressFraction => TotalKanbanCards == 0 ? 0 : Math.Max(0.02, (double)KanbanInProgress / TotalKanbanCards);
    public double KanbanDoneFraction => TotalKanbanCards == 0 ? 0 : Math.Max(0.02, (double)KanbanDone / TotalKanbanCards);
    public int TotalKanbanCards => KanbanPending + KanbanInProgress + KanbanDone;
    public bool HasKanbanCards => TotalKanbanCards > 0;

    // ── Habit heatmap ──
    /// <summary>12 weeks (rows) × 7 days (columns) — each cell is a HeatmapCellViewModel with State 0/1/2.
    /// Oldest week first (bottom row).</summary>
    public ObservableCollection<ObservableCollection<HeatmapCellViewModel>> HabitHeatmapWeeks { get; } = [];

    public bool HasHeatmapData => HabitHeatmapWeeks.Count > 0;

    // ── Day-of-week labels: Mon through Sun, localized (non-static so XAML ItemsSource binds cleanly) ──
    public string[] DayOfWeekHeaders { get; } =
        [.. Enumerable.Range(0, 7).Select(d =>
            CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((d + 1) % 7)))
        ];

    public StatisticsViewModel(StatisticsAggregationService aggregationService, PolarAuthService polarAuthService)
    {
        _aggregationService = aggregationService;
        _polarAuthService = polarAuthService;
        Reload();
    }

    public void Reload()
    {
        IsPolarLinked = _polarAuthService.IsLinked;

        // ── Weekly series ──
        var (startUtc, endUtc) = BuildRangeUtc(Range);
        var series = _aggregationService.BuildWeeklySeries(startUtc, endUtc);

        var maxNotesTotal = series.Count == 0 ? 0 : series.Max(w => w.NotesCreated + w.NotesEdited);
        var maxTasksCompleted = series.Count == 0 ? 0 : series.Max(w => w.TasksCompleted);

        Weeks.Clear();
        foreach (var week in series)
            Weeks.Add(new WeekBarViewModel(week, maxNotesTotal, maxTasksCompleted, ChartMaxBarHeight));

        var lastWeek = series.LastOrDefault();
        NotesThisWeek = lastWeek is null ? 0 : lastWeek.NotesCreated + lastWeek.NotesEdited;
        TasksCompletedThisWeek = lastWeek?.TasksCompleted ?? 0;
        HabitCompletionPercent = lastWeek?.HabitCompletionPercent;
        OnPropertyChanged(nameof(HasHabitData));

        HasEnoughData = series.Any(w => w.NotesCreated > 0 || w.NotesEdited > 0 || w.TasksCompleted > 0)
            || HabitCompletionPercent is not null;

        // ── Vault snapshot ──
        var vaultSnap = _aggregationService.BuildVaultSnapshot();
        VaultTotalNotes = vaultSnap.TotalNotes;
        VaultTotalSessions = vaultSnap.TotalSessions;
        VaultSizeLabel = FormatSize(vaultSnap.VaultSizeBytes);

        // ── Kanban flow ──
        var flow = _aggregationService.BuildKanbanFlow();
        KanbanPending = flow.Pending;
        KanbanInProgress = flow.InProgress;
        KanbanDone = flow.Done;
        OnPropertyChanged(nameof(HasKanbanCards));
        OnPropertyChanged(nameof(KanbanPendingFraction));
        OnPropertyChanged(nameof(KanbanInProgressFraction));
        OnPropertyChanged(nameof(KanbanDoneFraction));
        OnPropertyChanged(nameof(TotalKanbanCards));

        // ── Habit heatmap ──
        HabitHeatmapWeeks.Clear();
        var rawHeatmap = _aggregationService.BuildHabitHeatmap();
        foreach (var week in rawHeatmap)
        {
            var weekRow = new ObservableCollection<HeatmapCellViewModel>();
            foreach (var cell in week)
                weekRow.Add(new HeatmapCellViewModel(cell.Date, cell.State, cell.Date.ToString("ddd d", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture)));
            HabitHeatmapWeeks.Add(weekRow);
        }
        OnPropertyChanged(nameof(HasHeatmapData));
    }

    partial void OnRangeChanged(StatisticsRange value) => Reload();

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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    /// <summary>Same contract as every other page when <see cref="MainWindowViewModel"/> coordinates a
    /// live language switch (i18n Fase 4) — the weekly label is formatted with the current culture, so
    /// rebuilding the series is enough.</summary>
    public void RefreshLocalizedLabels() => Reload();
}
