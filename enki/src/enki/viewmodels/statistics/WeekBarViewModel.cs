using System;
using System.Globalization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>One column of the "Estadísticas" bar chart — precomputes pixel heights from a
/// <see cref="WeeklyStatsPoint"/> the same way <c>DashboardViewModel</c> precomputes
/// <c>PendingBarWidth</c>/<c>InProgressBarWidth</c>/<c>DoneBarWidth</c>, so the view only ever binds
/// already-calculated numbers, never does chart math itself.</summary>
public sealed class WeekBarViewModel(WeeklyStatsPoint point, int maxNotesTotal, int maxTasksCompleted, double maxBarHeight)
{
    public DateOnly WeekStart => point.WeekStart;
    public string WeekLabel => WeekStart.ToString("dd MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);

    public int NotesCreated => point.NotesCreated;
    public int NotesEdited => point.NotesEdited;
    public int NotesTotal => NotesCreated + NotesEdited;
    public int TasksCompleted => point.TasksCompleted;
    public double? HabitCompletionPercent => point.HabitCompletionPercent;

    public double NotesBarHeight => maxNotesTotal == 0 ? 0 : maxBarHeight * NotesTotal / maxNotesTotal;
    public double TasksBarHeight => maxTasksCompleted == 0 ? 0 : maxBarHeight * TasksCompleted / maxTasksCompleted;
    public double HabitBarHeight => maxBarHeight * (HabitCompletionPercent ?? 0) / 100.0;
}
