using System.Collections.ObjectModel;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Read projection of a persisted <see cref="Habit"/> for the Hábitos list and the Dashboard's
/// compact card — same "wrap the model, precompute display strings" shape as
/// <see cref="ReminderListItemViewModel"/>. <see cref="IsDoneToday"/> and <see cref="ConsistencyDays"/> are
/// snapshots built once by <c>HabitsViewModel.Reload()</c>/<c>DashboardViewModel.Reload()</c> from
/// <see cref="Services.HabitService"/>, not live-recomputed — rebuilt on the next <c>Reload()</c> like
/// every other read projection in the app.</summary>
public sealed class HabitItemViewModel(Habit habit)
{
    public Habit Habit { get; } = habit;

    public string Id => Habit.Id;
    public string Name => Habit.Name;
    public HabitFrequency Frequency => Habit.Frequency;

    public string FrequencyLabel => Frequency switch
    {
        HabitFrequency.Weekly => LocalizationService.Instance["Habits_Frequency_Weekly"],
        HabitFrequency.SpecificDays => LocalizationService.Instance["Habits_Frequency_SpecificDays"],
        _ => LocalizationService.Instance["Habits_Frequency_Daily"]
    };

    public required bool IsDoneToday { get; init; }

    /// <summary>Same "checked circle vs. outline" toggle icon convention as
    /// <see cref="ReminderListItemViewModel.CompletedToggleIcon"/>.</summary>
    public MaterialIconKind ToggleIconKind => IsDoneToday ? MaterialIconKind.CheckCircle : MaterialIconKind.CheckCircleOutline;

    /// <summary>Oldest-first window of the consistency grid — see <see cref="ConsistencyDayViewModel"/>.</summary>
    public ObservableCollection<ConsistencyDayViewModel> ConsistencyDays { get; init; } = [];
}
