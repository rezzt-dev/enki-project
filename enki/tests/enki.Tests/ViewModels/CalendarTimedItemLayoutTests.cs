using System;
using enki.ViewModels;
using Material.Icons;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="CalendarTimedItemLayout"/> — the Fase 8 fix for items with overlapping time
/// ranges drawing completely on top of each other in the Semana/Día hour grid (análisis §4.4). Uses
/// <c>HourRowHeight = 60</c> throughout so 1 minute == 1 pixel, keeping the start/end times in each
/// scenario directly readable off the constructor's <c>hour, minute</c> arguments.</summary>
public class CalendarTimedItemLayoutTests
{
    private const double HourRowHeight = 60;

    private static CalendarTimedItemViewModel MakeTimedItem(int hour, int minute, int durationMinutes)
    {
        var moment = new DateTime(2026, 3, 1, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();
        var item = new CalendarItemViewModel
        {
            SourceId = Guid.NewGuid().ToString(),
            SourceType = CalendarItemSourceType.Kanban,
            Title = "Item",
            MomentUtc = moment,
            IsAllDay = false,
            IsCompleted = false,
            DurationMinutes = durationMinutes,
            LayerColorHex = "#FFFFFF",
            LayerKey = "kanban",
            IconKind = MaterialIconKind.ViewColumnOutline
        };
        return new CalendarTimedItemViewModel(item, HourRowHeight, minimumVisualHeightMinutes: 40);
    }

    [Fact]
    public void SingleItem_GetsTheFullWidth()
    {
        var item = MakeTimedItem(9, 0, 60);

        CalendarTimedItemLayout.Apply([item]);

        Assert.Equal(0, item.LeftFraction);
        Assert.Equal(1.0, item.WidthFraction);
    }

    [Fact]
    public void TwoNonOverlappingItems_BothGetTheFullWidth()
    {
        var first = MakeTimedItem(9, 0, 30);
        var second = MakeTimedItem(10, 0, 30);

        CalendarTimedItemLayout.Apply([first, second]);

        Assert.Equal(1.0, first.WidthFraction);
        Assert.Equal(1.0, second.WidthFraction);
    }

    [Fact]
    public void TwoFullyOverlappingItems_SplitTheWidthInHalf()
    {
        var first = MakeTimedItem(9, 0, 60);
        var second = MakeTimedItem(9, 0, 60);

        CalendarTimedItemLayout.Apply([first, second]);

        Assert.Equal(0.5, first.WidthFraction);
        Assert.Equal(0.5, second.WidthFraction);
        Assert.Equal(0, first.LeftFraction);
        Assert.Equal(0.5, second.LeftFraction);
    }

    [Fact]
    public void ThreeMutuallyOverlappingItems_SplitIntoThreeColumns()
    {
        var a = MakeTimedItem(9, 0, 90);
        var b = MakeTimedItem(9, 15, 60);
        var c = MakeTimedItem(9, 30, 30);

        CalendarTimedItemLayout.Apply([a, b, c]);

        Assert.Equal(1.0 / 3, a.WidthFraction, precision: 6);
        Assert.Equal(1.0 / 3, b.WidthFraction, precision: 6);
        Assert.Equal(1.0 / 3, c.WidthFraction, precision: 6);
        Assert.Equal(0, a.LeftFraction);
        Assert.Equal(1.0 / 3, b.LeftFraction, precision: 6);
        Assert.Equal(2.0 / 3, c.LeftFraction, precision: 6);
    }

    [Fact]
    public void ChainedOverlap_ReusesAFreedColumnInsteadOfAlwaysGrowing()
    {
        // A: 9:00–10:00, B: 9:30–10:30 (overlaps A), C: 10:15–11:00 (overlaps B, not A).
        // A and C never overlap in time, so C can reuse A's column once it ends — the whole trio still
        // needs only 2 side-by-side columns, not 3, even though they form one transitive cluster.
        var a = MakeTimedItem(9, 0, 60);
        var b = MakeTimedItem(9, 30, 60);
        var c = MakeTimedItem(10, 15, 45);

        CalendarTimedItemLayout.Apply([a, b, c]);

        Assert.Equal(0.5, a.WidthFraction);
        Assert.Equal(0.5, b.WidthFraction);
        Assert.Equal(0.5, c.WidthFraction);
        Assert.Equal(a.LeftFraction, c.LeftFraction);
        Assert.NotEqual(a.LeftFraction, b.LeftFraction);
    }

    [Fact]
    public void SeparateClusters_AreLaidOutIndependently()
    {
        // A/B overlap in the morning; C/D overlap in the afternoon — an unrelated pair on the same day
        // must not affect either cluster's own column count.
        var a = MakeTimedItem(9, 0, 60);
        var b = MakeTimedItem(9, 30, 30);
        var c = MakeTimedItem(14, 0, 30);
        var d = MakeTimedItem(14, 0, 30);
        var e = MakeTimedItem(14, 0, 30);

        CalendarTimedItemLayout.Apply([a, b, c, d, e]);

        Assert.Equal(0.5, a.WidthFraction);
        Assert.Equal(0.5, b.WidthFraction);
        Assert.Equal(1.0 / 3, c.WidthFraction, precision: 6);
        Assert.Equal(1.0 / 3, d.WidthFraction, precision: 6);
        Assert.Equal(1.0 / 3, e.WidthFraction, precision: 6);
    }

    [Fact]
    public void EmptyList_DoesNotThrow()
    {
        CalendarTimedItemLayout.Apply([]);
    }
}
