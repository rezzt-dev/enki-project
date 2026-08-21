using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.ViewModels;

/// <summary>Pure predicate functions backing docs/calendar-feature/07-filtros-busqueda-y-capas.md — kept
/// as stateless static methods (same "pure calculator in its own file" pattern as <c>MonthGridCalculator</c>/
/// <c>WeekGridCalculator</c>) so the combination logic is testable without constructing a
/// <see cref="CalendarViewModel"/> (which needs <c>KanbanService</c>, not test-constructible — see
/// <c>CalendarViewModelTests</c>'s class remarks).</summary>
public static class CalendarItemFilter
{
    /// <summary>Doc §1: a layer toggle hides/shows every item of that origin across all four views at
    /// once.</summary>
    public static bool MatchesLayer(CalendarItemViewModel item, IReadOnlyCollection<string> activeLayerKeys) =>
        activeLayerKeys.Contains(item.LayerKey);

    /// <summary>Doc §3: priority/etiquetas combine with AND between categories and OR within a category;
    /// an empty filter set for a category means "no restriction from that category". <c>Reminder</c> has
    /// no priority of its own and stays visible regardless of the priority filter (doc §3: "queda fuera de
    /// este filtro siempre visible"); items with no tags concept (Kanban/Reminder — ghost occurrences carry
    /// their source's tags already, so no separate carve-out is needed there) get the same treatment for
    /// the tag filter, for the same reason — filtering them out would make turning on any tag filter hide
    /// every non-Polar item, which isn't what "filter by tag" means for a source that was never taggable.
    /// Primitive-typed rather than taking a <see cref="CalendarItemViewModel"/> directly so
    /// <see cref="CalendarUnscheduledItemViewModel"/> can reuse the exact same rule (doc §4: secondary
    /// filters also apply to the "sin programadas" panel).</summary>
    public static bool MatchesSecondaryFilters(
        CalendarItemSourceType sourceType,
        CalendarItemPriority priority,
        IReadOnlyList<string> tags,
        bool isCompleted,
        bool isGhostOccurrence,
        IReadOnlyCollection<CalendarItemPriority> priorityFilters,
        IReadOnlyCollection<string> tagFilters,
        bool hideCompleted,
        bool hideGhostOccurrences)
    {
        if (hideGhostOccurrences && isGhostOccurrence) return false;
        if (hideCompleted && isCompleted) return false;

        if (priorityFilters.Count > 0 && sourceType != CalendarItemSourceType.Reminder
            && !priorityFilters.Contains(priority)) return false;

        if (tagFilters.Count > 0
            && sourceType is CalendarItemSourceType.PolarTask or CalendarItemSourceType.PolarSubtask
            && !tags.Any(tagFilters.Contains)) return false;

        return true;
    }

    public static bool MatchesSecondaryFilters(
        CalendarItemViewModel item,
        IReadOnlyCollection<CalendarItemPriority> priorityFilters,
        IReadOnlyCollection<string> tagFilters,
        bool hideCompleted,
        bool hideGhostOccurrences) =>
        MatchesSecondaryFilters(item.SourceType, item.Priority, item.Tags, item.IsCompleted, item.IsGhostOccurrence,
            priorityFilters, tagFilters, hideCompleted, hideGhostOccurrences);

    /// <summary>Doc §4: same secondary filters apply to the "sin programadas" panel, except "ocultar
    /// ocurrencias fantasma" — which is a no-op here anyway since an unscheduled item is never a ghost
    /// occurrence (no date to project one from).</summary>
    public static bool MatchesSecondaryFilters(
        CalendarUnscheduledItemViewModel item,
        IReadOnlyCollection<CalendarItemPriority> priorityFilters,
        IReadOnlyCollection<string> tagFilters,
        bool hideCompleted) =>
        MatchesSecondaryFilters(item.SourceType, item.Priority, item.Tags, item.IsCompleted, isGhostOccurrence: false,
            priorityFilters, tagFilters, hideCompleted, hideGhostOccurrences: false);

    /// <summary>Doc §2: matches by title (and description) case-insensitively; an empty/blank search
    /// always matches everything so callers don't need to special-case "no active search".</summary>
    public static bool MatchesSearch(CalendarItemViewModel item, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return true;
        return item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
}
