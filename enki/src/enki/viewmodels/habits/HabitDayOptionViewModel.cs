using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One weekday toggle in <see cref="HabitEditorViewModel"/>'s "días concretos" picker — same
/// "day + selected flag" shape as <see cref="ReminderIconOptionViewModel"/>'s icon picker.</summary>
public sealed partial class HabitDayOptionViewModel : ObservableObject
{
    public DayOfWeek Day { get; }
    public string Label { get; }

    /// <summary>Each day gets its own single-member <c>RadioButton.GroupName</c> in the view — a deliberate
    /// trick to get the segmented-control visual without the mutual-exclusivity a shared group would force,
    /// since several days can be selected at once (see <see cref="HabitEditorViewModel.BuildSelectedDays"/>).</summary>
    public string GroupKey => $"HabitDay_{Day}";

    [ObservableProperty]
    private bool _isSelected;

    public HabitDayOptionViewModel(DayOfWeek day, string label, bool isSelected)
    {
        Day = day;
        Label = label;
        _isSelected = isSelected;
    }
}
