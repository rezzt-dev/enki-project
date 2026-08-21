using System;
using System.Collections.Generic;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers Fase 3's (docs/calendar-feature/12-plan-de-implementacion-por-fases.md) quick-create
/// popover state — entirely service-free (it only stages a draft; <see cref="CalendarViewModel"/> reads
/// it back to actually create the row), so unlike <see cref="CalendarViewModel"/> itself it can be
/// constructed directly in a test, same reasoning as <see cref="CalendarViewModelTests"/>'s pure
/// calculators.</summary>
public class CalendarQuickCreateViewModelTests
{
    private static KanbanColumn MakeColumn(string title = "Por hacer") => new() { Id = Guid.NewGuid().ToString("N"), Title = title };

    private static PolarTaskList MakeList(string title = "Inbox") => new() { Id = Guid.NewGuid().ToString(), Title = title, Icon = "ic_list", Color = "#7F52FF" };

    [Fact]
    public void CombinedDueDateUtc_WithoutTime_IsLocalMidnight()
    {
        var draft = new CalendarQuickCreateViewModel(new DateTime(2026, 3, 9), null, [MakeColumn()], [], isPolarAvailable: false);

        var localResult = draft.CombinedDueDateUtc.ToLocalTime();

        Assert.Equal(new DateTime(2026, 3, 9), localResult.Date);
        Assert.Equal(TimeSpan.Zero, localResult.TimeOfDay);
    }

    [Fact]
    public void CombinedDueDateUtc_WithInitialTime_PreservesHourAndMinute()
    {
        var draft = new CalendarQuickCreateViewModel(new DateTime(2026, 3, 9), new TimeSpan(14, 30, 0), [MakeColumn()], [], isPolarAvailable: false);

        var localResult = draft.CombinedDueDateUtc.ToLocalTime();

        Assert.True(draft.HasTime);
        Assert.Equal(new TimeSpan(14, 30, 0), localResult.TimeOfDay);
    }

    [Fact]
    public void CanSave_IsFalse_WhenTitleIsBlank()
    {
        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [MakeColumn()], [], isPolarAvailable: false);

        Assert.False(draft.CanSave);

        draft.Title = "Comprar leche";

        Assert.True(draft.CanSave);
    }

    [Fact]
    public void CanSave_KanbanType_RequiresASelectedColumn()
    {
        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [], [], isPolarAvailable: false) { Title = "Algo" };

        // No columns were passed in, so SelectedColumn defaults to null.
        Assert.Null(draft.SelectedColumn);
        Assert.False(draft.CanSave);
    }

    [Fact]
    public void PolarTaskType_WithNoPolarLists_CannotSave_EvenIfAvailable()
    {
        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [MakeColumn()], [], isPolarAvailable: true)
        {
            Title = "Algo",
            SelectedType = CalendarQuickCreateType.PolarTask
        };

        Assert.Null(draft.SelectedPolarList);
        Assert.False(draft.CanSave);
    }

    [Fact]
    public void ReminderType_NeverNeedsAColumnOrList()
    {
        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [], [], isPolarAvailable: false)
        {
            Title = "Llamar al médico",
            SelectedType = CalendarQuickCreateType.Reminder
        };

        Assert.True(draft.CanSave);
    }

    [Fact]
    public void SelectedColumn_DefaultsToFirstColumnPassedIn()
    {
        var first = MakeColumn("Por hacer");
        var second = MakeColumn("En progreso");

        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [first, second], [], isPolarAvailable: false);

        Assert.Same(first, draft.SelectedColumn);
    }

    [Fact]
    public void SelectedPolarList_DefaultsToFirstListPassedIn()
    {
        var list = MakeList();

        var draft = new CalendarQuickCreateViewModel(DateTime.Today, null, [MakeColumn()], [list], isPolarAvailable: true);

        Assert.Same(list, draft.SelectedPolarList);
    }
}
