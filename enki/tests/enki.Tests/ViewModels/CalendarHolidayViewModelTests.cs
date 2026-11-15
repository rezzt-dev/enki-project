using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Services;
using enki.Services.Holidays;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Festivos y días no laborables en las vistas del Calendario (docs/calendar-feature/
/// 15-festivos-y-dias-no-laborables.md): qué casillas se aclaran, qué se rotula, la capa de cada país y la
/// Agenda.</summary>
public sealed class CalendarHolidayViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-holidays-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly CalendarViewModel _vm;

    public CalendarHolidayViewModelTests()
    {
        _settings = new SettingsService(_directory);
        var kanban = new KanbanService(_directory);
        var auth = new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test-key");
        var tasks = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var notifications = new NotificationService(_settings, _directory);
        var colors = new CalendarLayerColorService(kanban, tasks, auth, _settings);
        var aggregation = new CalendarAggregationService(kanban, tasks, reminders, auth, colors);
        _vm = new CalendarViewModel(aggregation, colors, kanban, tasks, reminders, auth, _settings, notifications, _ => { });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private void ShowMonth(int year, int month, params string[] countries)
    {
        _settings.Settings.CalendarHolidayCountries = countries.ToList();
        _vm.ApplyHolidaySettings();
        _vm.SelectMiniDayCommand.Execute(new CalendarMiniDayViewModel(new DateTime(year, month, 15), true, false, false, false));
    }

    private CalendarMonthDayViewModel Cell(int year, int month, int day) =>
        _vm.Days.Single(d => d.Date == new DateTime(year, month, day) && d.IsCurrentMonth);

    [Fact]
    public void WithoutCountries_OnlyWeekendsAreShaded_AndNothingIsLabelled()
    {
        ShowMonth(2026, 12);

        Assert.True(Cell(2026, 12, 5).Holiday.IsNonWorkingDay);   // sábado
        Assert.True(Cell(2026, 12, 6).Holiday.IsNonWorkingDay);   // domingo
        Assert.False(Cell(2026, 12, 8).Holiday.IsNonWorkingDay);  // martes laborable sin países
        Assert.All(_vm.Days, d => Assert.False(d.Holiday.HasHolidays));
        Assert.DoesNotContain(_vm.Layers, l => l.Key.StartsWith(CalendarHolidayViewModel.LayerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void NationalHoliday_IsShadedAndLabelled_ButSpecialDaysOnlyLabelled()
    {
        ShowMonth(2026, 12, "ES");

        var immaculate = Cell(2026, 12, 8);
        Assert.True(immaculate.Holiday.IsNonWorkingDay);
        Assert.True(immaculate.Holiday.IsHoliday);
        Assert.False(string.IsNullOrEmpty(immaculate.Holiday.PrimaryLabel));
        Assert.NotNull(immaculate.Holiday.TooltipOrNull);

        var christmasEve = Cell(2026, 12, 24);
        Assert.True(christmasEve.Holiday.HasHolidays);
        Assert.False(christmasEve.Holiday.IsNonWorkingDay);

        Assert.Null(Cell(2026, 12, 9).Holiday.TooltipOrNull);
        Assert.Contains(_vm.Layers, l => l.Key == "holidays:ES" && l.CanEditColor);
    }

    [Fact]
    public void ShadingSetting_And_SecondarySetting_ApplyLive()
    {
        ShowMonth(2026, 12, "ES");

        _settings.Settings.CalendarShadeNonWorkingDays = false;
        _vm.ApplyHolidaySettings();
        Assert.All(_vm.Days, d => Assert.False(d.Holiday.IsNonWorkingDay));
        Assert.True(Cell(2026, 12, 8).Holiday.HasHolidays);

        _settings.Settings.CalendarShowSecondaryHolidays = false;
        _vm.ApplyHolidaySettings();
        Assert.False(Cell(2026, 12, 24).Holiday.HasHolidays);
    }

    [Fact]
    public void HidingTheCountryLayer_RemovesItsHolidays_ButKeepsTheSelection()
    {
        ShowMonth(2026, 12, "ES");

        _vm.Layers.Single(l => l.Key == "holidays:ES").IsActive = false;

        Assert.False(Cell(2026, 12, 8).Holiday.HasHolidays);
        Assert.False(Cell(2026, 12, 8).Holiday.IsNonWorkingDay);
        Assert.Contains("ES", _settings.Settings.CalendarHolidayCountries);
        Assert.Contains("holidays:ES", _settings.Settings.CalendarHiddenLayerKeys);
    }

    [Fact]
    public void SeveralCountries_PrefixTheCountryCode_AndKeepOneLayerEach()
    {
        ShowMonth(2026, 10, "ES", "US");

        var columbus = Cell(2026, 10, 12);
        Assert.Equal(2, columbus.Holiday.Holidays.Count);
        Assert.All(columbus.Holiday.Holidays, h => Assert.StartsWith(h.CountryCode + " · ", h.Label, StringComparison.Ordinal));
        Assert.True(columbus.Holiday.HasExtraHolidays);
        Assert.Equal(2, _vm.Layers.Count(l => l.Key.StartsWith(CalendarHolidayViewModel.LayerPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void WeekView_ShadesHolidayColumns()
    {
        ShowMonth(2026, 12, "ES");
        _vm.SetViewCommand.Execute(CalendarViewMode.Week);
        _vm.SelectMiniDayCommand.Execute(new CalendarMiniDayViewModel(new DateTime(2026, 12, 8), true, false, false, false));

        var tuesday = _vm.WeekColumns.Single(c => c.Date == new DateTime(2026, 12, 8));
        var wednesday = _vm.WeekColumns.Single(c => c.Date == new DateTime(2026, 12, 9));
        Assert.True(tuesday.Holiday.IsNonWorkingDay);
        Assert.False(wednesday.Holiday.IsNonWorkingDay);
    }

    [Fact]
    public void Agenda_ListsHolidayDays_EvenWithoutItems()
    {
        ShowMonth(2026, 12, "ES");
        _vm.SetViewCommand.Execute(CalendarViewMode.Agenda);
        _vm.SelectMiniDayCommand.Execute(new CalendarMiniDayViewModel(new DateTime(2026, 12, 1), true, false, false, false));

        Assert.False(_vm.IsAgendaEmpty);
        var group = Assert.Single(_vm.AgendaGroups, g => g.Date == new DateTime(2026, 12, 8));
        Assert.Empty(group.Items);
        Assert.True(group.HasHolidays);
    }

    [Fact]
    public void MiniCalendar_UsesTheSameNonWorkingCriterion()
    {
        ShowMonth(2026, 12, "ES");

        var mini = _vm.MiniCalendarDays.Single(d => d.Date == new DateTime(2026, 12, 8) && d.IsCurrentMonth);
        Assert.True(mini.Holiday.IsNonWorkingDay);
    }

    [Fact]
    public void UnsupportedCountryCodes_AreIgnored()
    {
        ShowMonth(2026, 12, "XX", "ES");

        Assert.Single(_vm.Layers, l => l.Key.StartsWith(CalendarHolidayViewModel.LayerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void AgendaGrouping_MergesHolidaysWithItemDays()
    {
        var holiday = new CalendarHolidayViewModel
        {
            Date = new DateTime(2026, 5, 1), CountryCode = "ES", CountryName = "España", Name = "Fiesta del Trabajo",
            LocalName = "Fiesta del Trabajo", Kind = HolidayKind.Public, ColorHex = "#328BA7", ShowCountryCode = false
        };
        var lookup = new Dictionary<DateTime, IReadOnlyList<CalendarHolidayViewModel>> { [holiday.Date] = [holiday] };

        var groups = CalendarAgendaGroupViewModel.Build([], lookup);

        var group = Assert.Single(groups);
        Assert.Equal(holiday.Date, group.Date);
        Assert.Same(holiday, Assert.Single(group.Holidays));
    }
}
