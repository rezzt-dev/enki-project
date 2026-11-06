using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Draft state backing the "Nuevo hábito"/"Editar hábito" popup — same role as
/// <see cref="ReminderEditorViewModel"/> for reminders, minimal by design: a name and a frequency, nothing
/// gamified (docs/propuesta-nuevas-funcionalidades.md §13).</summary>
public sealed partial class HabitEditorViewModel : ObservableObject
{
    public const int NameMaxLength = 80;

    private static readonly DayOfWeek[] WeekOrderMondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public ObservableCollection<HabitDayOptionViewModel> DayOptions { get; } = [];

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private HabitFrequency _frequency = HabitFrequency.Daily;

    public bool CanSave => !string.IsNullOrWhiteSpace(Name)
        && (Frequency != HabitFrequency.SpecificDays || DayOptions.Any(d => d.IsSelected));

    public bool ShowDayOptions => Frequency == HabitFrequency.SpecificDays;

    public HabitEditorViewModel(Habit? editing = null)
    {
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        var selectedDays = editing?.SpecificDays ?? [];
        foreach (var day in WeekOrderMondayFirst)
            DayOptions.Add(new HabitDayOptionViewModel(day, dtf.GetShortestDayName(day).ToUpper(CultureInfo.CurrentCulture), selectedDays.Contains(day)));

        if (editing is not null)
        {
            _name = editing.Name;
            _frequency = editing.Frequency;
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnFrequencyChanged(HabitFrequency value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowDayOptions));
    }

    [RelayCommand]
    private void SelectFrequency(string frequencyName)
    {
        if (Enum.TryParse<HabitFrequency>(frequencyName, out var frequency)) Frequency = frequency;
    }

    [RelayCommand]
    private void ToggleDay(HabitDayOptionViewModel day)
    {
        day.IsSelected = !day.IsSelected;
        OnPropertyChanged(nameof(CanSave));
    }

    public List<DayOfWeek> BuildSelectedDays() => DayOptions.Where(d => d.IsSelected).Select(d => d.Day).ToList();
}
