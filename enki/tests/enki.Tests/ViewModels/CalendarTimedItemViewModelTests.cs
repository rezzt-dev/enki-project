using System;
using enki.ViewModels;
using Material.Icons;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="CalendarTimedItemViewModel"/>'s height math — pure pixel math with no
/// service dependency (see its own doc comment), including the franja horaria feature: a Kanban card with
/// a real <see cref="CalendarItemViewModel.DurationMinutes"/> gets its actual height (floored so a very
/// short duration still reads), everything else keeps the old fixed drawing convention.</summary>
public class CalendarTimedItemViewModelTests
{
    private const double HourRowHeight = 60;
    private const double MinimumVisualHeightMinutes = 40;

    private static CalendarItemViewModel MakeItem(
        DateTime momentUtc, int? durationMinutes = null,
        CalendarItemSourceType sourceType = CalendarItemSourceType.Kanban) => new()
    {
        SourceId = "item-1",
        SourceType = sourceType,
        Title = "Reunión",
        MomentUtc = momentUtc,
        IsAllDay = false,
        IsCompleted = false,
        DurationMinutes = durationMinutes,
        LayerColorHex = "#FFFFFF",
        LayerKey = "kanban",
        IconKind = MaterialIconKind.ViewColumnOutline
    };

    [Fact]
    public void NoDuration_UsesFixedMinimumVisualHeight()
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: null);

        var timed = new CalendarTimedItemViewModel(item, HourRowHeight, MinimumVisualHeightMinutes);

        Assert.Equal(MinimumVisualHeightMinutes / 60.0 * HourRowHeight, timed.Height);
        Assert.Equal((int)MinimumVisualHeightMinutes, timed.EffectiveDurationMinutes);
    }

    [Fact]
    public void RealDuration_AboveFloor_UsesRealHeight()
    {
        // 21:30 – 22:15, doc example.
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: 45);

        var timed = new CalendarTimedItemViewModel(item, HourRowHeight, MinimumVisualHeightMinutes);

        Assert.Equal(45.0 / 60.0 * HourRowHeight, timed.Height);
        Assert.Equal(45, timed.EffectiveDurationMinutes);
    }

    [Fact]
    public void RealDuration_BelowFloor_IsFlooredSoItStaysReadable()
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: 5);

        var timed = new CalendarTimedItemViewModel(item, HourRowHeight, MinimumVisualHeightMinutes);

        Assert.Equal(
            CalendarTimedItemViewModel.MinimumRealDurationFloorMinutes / 60.0 * HourRowHeight, timed.Height);
        Assert.Equal((int)CalendarTimedItemViewModel.MinimumRealDurationFloorMinutes, timed.EffectiveDurationMinutes);
    }

    [Fact]
    public void TopOffset_IsIndependentOfDuration()
    {
        var moment = new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc);
        var withDuration = new CalendarTimedItemViewModel(MakeItem(moment, 45), HourRowHeight, MinimumVisualHeightMinutes);
        var withoutDuration = new CalendarTimedItemViewModel(MakeItem(moment, null), HourRowHeight, MinimumVisualHeightMinutes);

        Assert.Equal(withoutDuration.TopOffset, withDuration.TopOffset);
    }

    [Fact]
    public void TimeLabel_WithDuration_ShowsStartEndRange()
    {
        var moment = new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Local);
        var item = MakeItem(moment.ToUniversalTime(), durationMinutes: 45);

        Assert.Equal("21:30–22:15", item.TimeLabel);
    }

    [Fact]
    public void TimeLabel_WithoutDuration_ShowsPlainStartTime()
    {
        var moment = new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Local);
        var item = MakeItem(moment.ToUniversalTime(), durationMinutes: null);

        Assert.Equal("21:30", item.TimeLabel);
    }

    [Theory]
    [InlineData(CalendarItemSourceType.Kanban, true)]
    [InlineData(CalendarItemSourceType.PolarTask, true)]
    [InlineData(CalendarItemSourceType.PolarSubtask, false)]
    [InlineData(CalendarItemSourceType.Reminder, true)]
    public void CanResize_TrueForTimedKanbanPolarAndReminder(CalendarItemSourceType sourceType, bool expected)
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), sourceType: sourceType);

        Assert.Equal(expected, item.CanResize);
    }

    [Fact]
    public void CanResize_FalseForGhostOccurrence()
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc)) with { IsGhostOccurrence = true };

        Assert.False(item.CanResize);
    }

    [Fact]
    public void CanResize_FalseForAllDayItem()
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)) with { IsAllDay = true };

        Assert.False(item.CanResize);
    }

    [Fact]
    public void HasDuration_TrueOnlyWithARealDuration()
    {
        var withDuration = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: 45);
        var withoutDuration = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: null);

        Assert.True(withDuration.HasDuration);
        Assert.False(withoutDuration.HasDuration);
    }

    [Fact]
    public void HasDuration_FalseForAllDayItemEvenWithAStoredDuration()
    {
        // Defensive: an all-day item should never carry a duration in practice (CalendarAggregationService
        // nulls it out), but HasDuration itself doesn't trust that and re-checks IsAllDay.
        var item = MakeItem(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), durationMinutes: 45) with { IsAllDay = true };

        Assert.False(item.HasDuration);
    }

    [Fact]
    public void EndTimeLabel_IsStartPlusDuration()
    {
        var moment = new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Local);
        var item = MakeItem(moment.ToUniversalTime(), durationMinutes: 45);

        Assert.Equal("22:15", item.EndTimeLabel);
    }

    [Fact]
    public void EndTimeLabel_EmptyWithoutDuration()
    {
        var item = MakeItem(new DateTime(2026, 3, 1, 21, 30, 0, DateTimeKind.Utc), durationMinutes: null);

        Assert.Equal(string.Empty, item.EndTimeLabel);
    }
}
