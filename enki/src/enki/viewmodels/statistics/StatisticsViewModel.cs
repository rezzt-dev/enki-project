using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Which window of weekly history <see cref="StatisticsViewModel.Weeks"/> covers — propuesta 15's
/// "rango configurable (última semana, último mes, todo el histórico)".</summary>
public enum StatisticsRange
{
    Week,
    Month,
    AllTime
}

/// <summary>Full "Estadísticas" page — propuesta 15 (docs/propuesta-nuevas-funcionalidades.md §15): weekly
/// trends built purely from <see cref="StatisticsAggregationService"/>, with simple monochrome bars instead
/// of any charting library (no such dependency exists anywhere else in the app).</summary>
public sealed partial class StatisticsViewModel : ViewModelBase
{
    /// <summary>"Todo el histórico" is capped at a year of weeks rather than truly unbounded — a chart with
    /// hundreds of columns stops being readable long before it stops being technically correct.</summary>
    private const int AllTimeWeeks = 52;

    private const double ChartMaxBarHeight = 120;

    private readonly StatisticsAggregationService _aggregationService;
    private readonly PolarAuthService _polarAuthService;

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

    /// <summary>Drives the empty state ("vuelve cuando tengas más actividad") for a vault too new/quiet to
    /// chart anything meaningful yet.</summary>
    [ObservableProperty]
    private bool _hasEnoughData;

    public bool HasHabitData => HabitCompletionPercent is not null;

    public StatisticsViewModel(StatisticsAggregationService aggregationService, PolarAuthService polarAuthService)
    {
        _aggregationService = aggregationService;
        _polarAuthService = polarAuthService;
        Reload();
    }

    public void Reload()
    {
        IsPolarLinked = _polarAuthService.IsLinked;

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

        // Includes HabitCompletionPercent so a user who only tracks Hábitos (no notes/tasks yet) still
        // sees their consistency chart instead of the "vuelve cuando tengas más actividad" empty state
        // (análisis §4.4, Media).
        HasEnoughData = series.Any(w => w.NotesCreated > 0 || w.NotesEdited > 0 || w.TasksCompleted > 0)
            || HabitCompletionPercent is not null;
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
        // Half-open, end exclusive — same convention as CalendarAggregationService.BuildItems.
        var endUtc = DateTime.UtcNow.Date.AddDays(1).ToUniversalTime();
        var startUtc = range switch
        {
            StatisticsRange.Week => nowUtc.AddDays(-7),
            StatisticsRange.AllTime => nowUtc.AddDays(-7 * AllTimeWeeks),
            _ => nowUtc.AddDays(-28)
        };
        return (startUtc, endUtc);
    }

    /// <summary>Mismo contrato que el resto de páginas cuando <see cref="MainWindowViewModel"/> coordina un
    /// cambio de idioma en tiempo real (i18n Fase 4) — <see cref="WeekBarViewModel.WeekLabel"/> se formatea
    /// con la cultura actual, así que basta con reconstruir la serie.</summary>
    public void RefreshLocalizedLabels() => Reload();
}
