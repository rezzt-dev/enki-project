using System.Collections.Generic;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="DashboardLayoutEngine"/> — pure and static, so directly testable without a
/// <see cref="Avalonia.Application"/> or any view-model. The geometric assertions here
/// (<see cref="NonClassicStyle_AllSevenSections_NeverOverlap"/>) are the actual correctness net for the
/// hand-authored slot tables: a mistake there would silently render two Dashboard sections stacked on top
/// of each other, which no unit test framework can "see" the way a screenshot would — asserting no two
/// slots share a cell is the closest available substitute.</summary>
public class DashboardLayoutEngineTests
{
    private static readonly string[] AllSevenKeys =
        ["QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes"];

    public static IEnumerable<object[]> NonClassicStyles =>
        DashboardLayoutEngine.AllStyles.Where(s => s != DashboardLayoutEngine.Classic).Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(NonClassicStyles))]
    public void NonClassicStyle_AllSevenSections_GetAUniqueSlot(string style)
    {
        var slots = DashboardLayoutEngine.Compute(AllSevenKeys, style);

        Assert.Equal(AllSevenKeys.Length, slots.Count);
        Assert.All(AllSevenKeys, key => Assert.True(slots.ContainsKey(key)));
    }

    [Theory]
    [MemberData(nameof(NonClassicStyles))]
    public void NonClassicStyle_AllSevenSections_NeverOverlap(string style)
    {
        var slots = DashboardLayoutEngine.Compute(AllSevenKeys, style).Values.ToList();

        for (var i = 0; i < slots.Count; i++)
        for (var j = i + 1; j < slots.Count; j++)
        {
            var a = slots[i];
            var b = slots[j];

            var rowsOverlap = a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;
            var columnsOverlap = a.Column < b.Column + b.ColumnSpan && b.Column < a.Column + a.ColumnSpan;

            Assert.False(rowsOverlap && columnsOverlap, $"{style}: slots {a} and {b} overlap");
        }
    }

    [Theory]
    [MemberData(nameof(NonClassicStyles))]
    public void NonClassicStyle_EverySlot_StaysWithinSixColumns(string style)
    {
        var slots = DashboardLayoutEngine.Compute(AllSevenKeys, style).Values;

        Assert.All(slots, slot =>
        {
            Assert.True(slot.Column >= 0);
            Assert.True(slot.Column + slot.ColumnSpan <= DashboardLayoutEngine.ColumnCount);
        });
    }

    [Fact]
    public void Classic_PlacesOneFullWidthRowPerSection_InOrder()
    {
        var slots = DashboardLayoutEngine.Compute(AllSevenKeys, DashboardLayoutEngine.Classic);

        for (var i = 0; i < AllSevenKeys.Length; i++)
        {
            var slot = slots[AllSevenKeys[i]];
            Assert.Equal(i, slot.Row);
            Assert.Equal(0, slot.Column);
            Assert.Equal(1, slot.RowSpan);
            Assert.Equal(DashboardLayoutEngine.ColumnCount, slot.ColumnSpan);
        }
    }

    [Fact]
    public void UnrecognizedStyle_FallsBackToClassicBehaviour()
    {
        var slots = DashboardLayoutEngine.Compute(AllSevenKeys, "SomeFutureStyleThatDoesNotExistYet");

        Assert.Equal(AllSevenKeys.Length, slots.Count);
        Assert.Equal(0, slots[AllSevenKeys[0]].Column);
        Assert.Equal(DashboardLayoutEngine.ColumnCount, slots[AllSevenKeys[0]].ColumnSpan);
    }

    [Theory]
    [MemberData(nameof(NonClassicStyles))]
    public void PartialVisibility_StillPlacesEveryVisibleSection_WithoutOverlap(string style)
    {
        var visible = new[] { "BoardTasks", "Vault", "RecentNotes" };
        var slots = DashboardLayoutEngine.Compute(visible, style);

        Assert.Equal(visible.Length, slots.Count);
        Assert.All(visible, key => Assert.True(slots.ContainsKey(key)));
    }

    [Fact]
    public void Compute_EmptyList_ReturnsEmpty()
    {
        Assert.Empty(DashboardLayoutEngine.Compute([], DashboardLayoutEngine.Mosaic));
        Assert.Empty(DashboardLayoutEngine.Compute([], DashboardLayoutEngine.Classic));
    }

    [Theory]
    [InlineData("QuickAccess", true)]
    [InlineData("BoardTasks", true)]
    [InlineData("RecentNotes", true)]
    [InlineData("Today", false)]
    [InlineData("Vault", false)]
    [InlineData("Activity", false)]
    [InlineData("Sessions", false)]
    public void IsWideSection_MatchesEachSectionsContentShape(string key, bool expectedWide) =>
        Assert.Equal(expectedWide, DashboardLayoutEngine.IsWideSection(key));
}
