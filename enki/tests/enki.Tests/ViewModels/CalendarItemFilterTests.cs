using System;
using System.Collections.Generic;
using enki.ViewModels;
using Material.Icons;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="CalendarItemFilter"/> — the pure predicate logic behind docs/calendar-feature/
/// 07-filtros-busqueda-y-capas.md's capas/prioridad/etiquetas/completadas/fantasma/búsqueda rules.</summary>
public class CalendarItemFilterTests
{
    private static CalendarItemViewModel MakeItem(
        CalendarItemSourceType sourceType = CalendarItemSourceType.PolarTask,
        string layerKey = "list-1",
        CalendarItemPriority priority = CalendarItemPriority.None,
        IReadOnlyList<string>? tags = null,
        bool isCompleted = false,
        bool isGhostOccurrence = false,
        string title = "Título",
        string description = "") => new()
    {
        SourceId = "item-1",
        SourceType = sourceType,
        Title = title,
        Description = description,
        MomentUtc = DateTime.UtcNow,
        IsAllDay = true,
        IsCompleted = isCompleted,
        LayerColorHex = "#FFFFFF",
        LayerKey = layerKey,
        Priority = priority,
        Tags = tags ?? [],
        IsGhostOccurrence = isGhostOccurrence,
        IconKind = MaterialIconKind.CheckboxMarkedOutline
    };

    [Fact]
    public void MatchesLayer_TrueOnlyWhenLayerKeyIsActive()
    {
        var item = MakeItem(layerKey: "kanban");

        Assert.True(CalendarItemFilter.MatchesLayer(item, ["kanban", "reminder"]));
        Assert.False(CalendarItemFilter.MatchesLayer(item, ["reminder"]));
    }

    [Fact]
    public void MatchesSecondaryFilters_HideGhostOccurrences_HidesOnlyGhosts()
    {
        var ghost = MakeItem(isGhostOccurrence: true);
        var real = MakeItem(isGhostOccurrence: false);

        Assert.False(CalendarItemFilter.MatchesSecondaryFilters(ghost, [], [], hideCompleted: false, hideGhostOccurrences: true));
        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(real, [], [], hideCompleted: false, hideGhostOccurrences: true));
    }

    [Fact]
    public void MatchesSecondaryFilters_HideCompleted_HidesOnlyCompleted()
    {
        var completed = MakeItem(isCompleted: true);
        var pending = MakeItem(isCompleted: false);

        Assert.False(CalendarItemFilter.MatchesSecondaryFilters(completed, [], [], hideCompleted: true, hideGhostOccurrences: false));
        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(pending, [], [], hideCompleted: true, hideGhostOccurrences: false));
    }

    [Fact]
    public void MatchesSecondaryFilters_EmptyPriorityFilter_MatchesEverything()
    {
        var item = MakeItem(priority: CalendarItemPriority.Low);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(item, [], [], hideCompleted: false, hideGhostOccurrences: false));
    }

    [Fact]
    public void MatchesSecondaryFilters_PriorityFilter_RequiresMembership()
    {
        var high = MakeItem(priority: CalendarItemPriority.High);
        var low = MakeItem(priority: CalendarItemPriority.Low);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(high, [CalendarItemPriority.High, CalendarItemPriority.Medium], [], false, false));
        Assert.False(CalendarItemFilter.MatchesSecondaryFilters(low, [CalendarItemPriority.High, CalendarItemPriority.Medium], [], false, false));
    }

    [Fact]
    public void MatchesSecondaryFilters_PriorityFilter_ReminderAlwaysExempt()
    {
        // doc §3: "Reminder no tiene prioridad... queda fuera de este filtro siempre visible".
        var reminder = MakeItem(sourceType: CalendarItemSourceType.Reminder, priority: CalendarItemPriority.None);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(reminder, [CalendarItemPriority.High], [], false, false));
    }

    [Fact]
    public void MatchesSecondaryFilters_TagFilter_RequiresOverlapForPolarItems()
    {
        var tagged = MakeItem(sourceType: CalendarItemSourceType.PolarTask, tags: ["trabajo", "urgente"]);
        var untagged = MakeItem(sourceType: CalendarItemSourceType.PolarTask, tags: ["personal"]);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(tagged, [], ["urgente"], false, false));
        Assert.False(CalendarItemFilter.MatchesSecondaryFilters(untagged, [], ["urgente"], false, false));
    }

    [Fact]
    public void MatchesSecondaryFilters_TagFilter_ExemptsSourcesWithoutTagsConcept()
    {
        // Kanban/Reminder items never carry tags — turning on a tag filter shouldn't hide every one of
        // them (see CalendarItemFilter's own remarks for the reasoning).
        var kanban = MakeItem(sourceType: CalendarItemSourceType.Kanban, tags: []);
        var reminder = MakeItem(sourceType: CalendarItemSourceType.Reminder, tags: []);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(kanban, [], ["urgente"], false, false));
        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(reminder, [], ["urgente"], false, false));
    }

    [Fact]
    public void MatchesSecondaryFilters_CombinesPriorityAndTagWithAnd()
    {
        var matchesBoth = MakeItem(sourceType: CalendarItemSourceType.PolarTask, priority: CalendarItemPriority.High, tags: ["trabajo"]);
        var matchesOnlyPriority = MakeItem(sourceType: CalendarItemSourceType.PolarTask, priority: CalendarItemPriority.High, tags: ["personal"]);

        Assert.True(CalendarItemFilter.MatchesSecondaryFilters(matchesBoth, [CalendarItemPriority.High], ["trabajo"], false, false));
        Assert.False(CalendarItemFilter.MatchesSecondaryFilters(matchesOnlyPriority, [CalendarItemPriority.High], ["trabajo"], false, false));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("TÍTULO", true)]
    [InlineData("título", true)]
    [InlineData("no coincide", false)]
    public void MatchesSearch_MatchesTitleCaseInsensitively(string searchText, bool expected)
    {
        var item = MakeItem(title: "Título de prueba");

        Assert.Equal(expected, CalendarItemFilter.MatchesSearch(item, searchText));
    }

    [Fact]
    public void MatchesSearch_AlsoMatchesDescription()
    {
        var item = MakeItem(title: "Sin coincidencia", description: "contiene la palabra clave");

        Assert.True(CalendarItemFilter.MatchesSearch(item, "palabra clave"));
    }
}
