using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.ViewModels;
using Material.Icons;

namespace enki.Services;

/// <summary>The only genuinely new service of the calendar feature (docs/calendar-feature/
/// 11-arquitectura-tecnica.md §3) — builds <see cref="CalendarItemViewModel"/>s from the three existing
/// data domains for a given date range. Purely a function of the state already loaded in memory by the
/// three source services: persists nothing, owns nothing, never mutates any of them.</summary>
public sealed class CalendarAggregationService(
    KanbanService kanbanService,
    PolarTaskService polarTaskService,
    ReminderService reminderService,
    PolarAuthService polarAuthService,
    CalendarLayerColorService? layerColorService = null)
{
    /// <summary>Hard safety cap on chained <c>CalculateNextDueDate</c> calls per recurring task when
    /// expanding ghost occurrences (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §4) — every
    /// existing <c>PolarRecurrence</c> value always advances the date, so this only guards against a
    /// future recurrence kind that might not.</summary>
    private const int MaxGhostOccurrenceIterations = 366;

    /// <summary><see cref="CalendarItemViewModel.LayerKey"/>/<see cref="CalendarUnscheduledItemViewModel.LayerKey"/>
    /// for the two fixed layers (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1) — every Polar
    /// list's own key is simply its <c>PolarTaskList.Id</c>. Public so <c>CalendarViewModel</c> builds its
    /// <c>Layers</c> panel with the exact same keys these items are tagged with.</summary>
    public const string KanbanLayerKey = "kanban";
    public const string ReminderLayerKey = "reminder";

    /// <summary>Every <see cref="CalendarItemViewModel"/> whose positioning moment falls in
    /// [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>), ordered the same way Google
    /// Calendar orders a day's events: all-day items first, then by time (docs/calendar-feature/
    /// 04-vistas-y-navegacion.md §2). Layers/secondary filters (docs/calendar-feature/
    /// 07-filtros-busqueda-y-capas.md) are deliberately not applied here — this returns the full unfiltered
    /// set for the range and <see cref="ViewModels.CalendarViewModel"/> filters it, so the mini-calendar's
    /// "has items" dot and every view stay in sync with a single filtering pass instead of two.</summary>
    /// <param name="includeGhostOccurrences">Doc §4's "ocultar ocurrencias fantasma" toggle — when false,
    /// only each recurring task's real occurrence is returned, matching a plain non-recurring calendar.</param>
    public List<CalendarItemViewModel> BuildItems(DateTime rangeStartUtc, DateTime rangeEndUtc, bool includeGhostOccurrences = true)
    {
        var items = new List<CalendarItemViewModel>();
        // Un solo mapa por reconstrucción (ver CalendarLayerColorService) — resolver el color item a item
        // recorrería columnas y listas una vez por tarjeta.
        var colors = layerColorService?.BuildMap();

        foreach (var column in kanbanService.Board.Columns)
        foreach (var card in column.Cards)
        {
            if (card.DueDateUtc is not { } due || due < rangeStartUtc || due >= rangeEndUtc) continue;

            var isAllDay = IsAllDay(due);
            items.Add(new CalendarItemViewModel
            {
                SourceId = card.Id,
                SourceType = CalendarItemSourceType.Kanban,
                Title = card.Title,
                Description = card.Description,
                MomentUtc = due,
                IsAllDay = isAllDay,
                IsCompleted = card.CompletedAtUtc is not null,
                // A duration only ever means something alongside a real time of day (doc §5) — an all-day
                // card ignores whatever DurationMinutes happens to be stored.
                DurationMinutes = isAllDay ? null : card.DurationMinutes,
                LayerColorHex = CalendarItemViewModel.KanbanLayerColorHex,
                CalendarColorHex = ResolveKanbanColumnColor(colors, column.Id),
                LayerKey = KanbanLayerKey,
                Priority = MapPriority(card.Priority),
                IconKind = MaterialIconKind.ViewColumnOutline,
                HasSubtasksOrChildren = card.Subtasks.Count > 0,
                Subtasks = card.Subtasks
                    .Select(s => new CalendarSubtaskViewModel { Id = s.Id, Title = s.Title, IsDone = s.IsDone, ParentSourceType = CalendarItemSourceType.Kanban })
                    .ToList()
            });
        }

        // Same gate the "Gestión de tareas" view itself uses before showing any Polar data — see
        // docs/calendar-feature/11-arquitectura-tecnica.md §3.
        if (polarAuthService.IsLinked)
        {
            var lists = polarTaskService.Store.Lists;

            foreach (var task in polarTaskService.Store.Tasks.Where(t => !t.IsDeleted))
            {
                var list = lists.FirstOrDefault(l => l.Id == task.ListId);

                if (task.DueDateUtc is { } due && due >= rangeStartUtc && due < rangeEndUtc)
                {
                    var taskSubtasks = polarTaskService.Store.Subtasks.Where(s => s.TaskId == task.Id && !s.IsDeleted).ToList();
                    var isAllDay = IsAllDay(due);
                    items.Add(new CalendarItemViewModel
                    {
                        SourceId = task.Id,
                        SourceType = CalendarItemSourceType.PolarTask,
                        Title = task.Title,
                        Description = task.Description,
                        MomentUtc = due,
                        IsAllDay = isAllDay,
                        IsCompleted = task.Completed,
                        DurationMinutes = isAllDay ? null : task.DurationMinutes,
                        LayerColorHex = list?.Color ?? CalendarItemViewModel.KanbanLayerColorHex,
                        CalendarColorHex = ResolvePolarListColor(colors, task.ListId),
                        LayerKey = task.ListId,
                        Priority = MapPriority(task.Priority),
                        Tags = task.Tags,
                        IconKind = MaterialIconKind.CheckboxMarkedOutline,
                        HasSubtasksOrChildren = taskSubtasks.Count > 0,
                        ParentContextLabel = list?.Title ?? string.Empty,
                        Subtasks = taskSubtasks
                            .Select(s => new CalendarSubtaskViewModel { Id = s.Id, Title = s.Title, IsDone = s.Completed, ParentSourceType = CalendarItemSourceType.PolarTask })
                            .ToList()
                    });
                }

                // Ghost occurrences (doc §4) are previewed regardless of whether the task's *real*
                // DueDateUtc itself falls in this range — a weekly task due last month still needs to
                // preview this week's upcoming occurrence.
                if (includeGhostOccurrences)
                    items.AddRange(BuildGhostOccurrences(task, list, rangeStartUtc, rangeEndUtc, ResolvePolarListColor(colors, task.ListId)));
            }

            foreach (var subtask in polarTaskService.Store.Subtasks.Where(s => !s.IsDeleted))
            {
                if (subtask.DueDateUtc is not { } due || due < rangeStartUtc || due >= rangeEndUtc) continue;

                var parentTask = polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == subtask.TaskId);
                var list = parentTask is null ? null : lists.FirstOrDefault(l => l.Id == parentTask.ListId);
                items.Add(new CalendarItemViewModel
                {
                    SourceId = subtask.Id,
                    SourceType = CalendarItemSourceType.PolarSubtask,
                    Title = subtask.Title,
                    MomentUtc = due,
                    IsAllDay = IsAllDay(due),
                    IsCompleted = subtask.Completed,
                    LayerColorHex = list?.Color ?? CalendarItemViewModel.KanbanLayerColorHex,
                    CalendarColorHex = ResolvePolarListColor(colors, parentTask?.ListId),
                    LayerKey = parentTask?.ListId ?? string.Empty,
                    IconKind = MaterialIconKind.SubdirectoryArrowRight,
                    ParentContextLabel = parentTask?.Title ?? string.Empty,
                    ParentSourceId = parentTask?.Id ?? string.Empty
                });
            }
        }

        foreach (var reminder in reminderService.ActiveReminders)
        {
            if (reminder.NotifyAtUtc >= rangeStartUtc && reminder.NotifyAtUtc < rangeEndUtc)
            {
                items.Add(new CalendarItemViewModel
                {
                    SourceId = reminder.Id,
                    SourceType = CalendarItemSourceType.Reminder,
                    Title = reminder.Title,
                    Description = reminder.Description,
                    MomentUtc = reminder.NotifyAtUtc,
                    IsAllDay = IsAllDay(reminder.NotifyAtUtc),
                    IsCompleted = reminder.IsCompleted,
                    DurationMinutes = IsAllDay(reminder.NotifyAtUtc) ? null : reminder.DurationMinutes,
                    LayerColorHex = CalendarItemViewModel.ReminderLayerColorHex,
                    CalendarColorHex = Resolve(colors, ReminderLayerKey),
                    LayerKey = ReminderLayerKey,
                    IconKind = Enum.TryParse<MaterialIconKind>(reminder.Icon, out var kind) ? kind : MaterialIconKind.BellRingOutline
                });
            }

            // Same parity gap this preview closes for Polar tasks above: previewed regardless of whether
            // the reminder's *real* NotifyAtUtc itself falls in this range (análisis alfa-3 §4.4, Media).
            if (includeGhostOccurrences)
                items.AddRange(BuildGhostOccurrences(reminder, rangeStartUtc, rangeEndUtc, Resolve(colors, ReminderLayerKey)));
        }

        return items
            .OrderBy(i => i.IsAllDay ? 0 : 1)
            .ThenBy(i => i.MomentLocal)
            .ToList();
    }

    /// <summary>Every <c>KanbanCard</c>/<c>PolarTask</c>/<c>PolarSubtask</c> with <c>DueDateUtc == null</c>
    /// — feeds the "sin programadas" sidebar panel (docs/calendar-feature/04-vistas-y-navegacion.md §8).
    /// <c>Reminder</c> never appears here (<c>NotifyAtUtc</c> is never null, docs/calendar-feature/
    /// 03-glosario-y-modelo-de-datos.md §6).</summary>
    public List<CalendarUnscheduledItemViewModel> BuildUnscheduledItems()
    {
        var items = new List<CalendarUnscheduledItemViewModel>();
        var colors = layerColorService?.BuildMap();

        foreach (var column in kanbanService.Board.Columns)
        foreach (var card in column.Cards)
        {
            if (card.DueDateUtc is not null) continue;
            items.Add(new CalendarUnscheduledItemViewModel
            {
                SourceId = card.Id,
                SourceType = CalendarItemSourceType.Kanban,
                Title = card.Title,
                LayerColorHex = CalendarItemViewModel.KanbanLayerColorHex,
                CalendarColorHex = ResolveKanbanColumnColor(colors, column.Id),
                LayerKey = KanbanLayerKey,
                Priority = MapPriority(card.Priority),
                IconKind = MaterialIconKind.ViewColumnOutline,
                ParentContextLabel = column.Title,
                IsCompleted = card.CompletedAtUtc is not null
            });
        }

        if (polarAuthService.IsLinked)
        {
            var lists = polarTaskService.Store.Lists;

            foreach (var task in polarTaskService.Store.Tasks.Where(t => !t.IsDeleted && t.DueDateUtc is null))
            {
                var list = lists.FirstOrDefault(l => l.Id == task.ListId);
                items.Add(new CalendarUnscheduledItemViewModel
                {
                    SourceId = task.Id,
                    SourceType = CalendarItemSourceType.PolarTask,
                    Title = task.Title,
                    LayerColorHex = list?.Color ?? CalendarItemViewModel.KanbanLayerColorHex,
                    CalendarColorHex = ResolvePolarListColor(colors, task.ListId),
                    LayerKey = task.ListId,
                    Priority = MapPriority(task.Priority),
                    Tags = task.Tags,
                    IsCompleted = task.Completed,
                    IconKind = MaterialIconKind.CheckboxMarkedOutline,
                    ParentContextLabel = list?.Title ?? string.Empty
                });
            }

            foreach (var subtask in polarTaskService.Store.Subtasks.Where(s => !s.IsDeleted && s.DueDateUtc is null))
            {
                var parentTask = polarTaskService.Store.Tasks.FirstOrDefault(t => t.Id == subtask.TaskId);
                var list = parentTask is null ? null : lists.FirstOrDefault(l => l.Id == parentTask.ListId);
                items.Add(new CalendarUnscheduledItemViewModel
                {
                    SourceId = subtask.Id,
                    SourceType = CalendarItemSourceType.PolarSubtask,
                    Title = subtask.Title,
                    LayerColorHex = list?.Color ?? CalendarItemViewModel.KanbanLayerColorHex,
                    CalendarColorHex = ResolvePolarListColor(colors, parentTask?.ListId),
                    LayerKey = parentTask?.ListId ?? string.Empty,
                    IsCompleted = subtask.Completed,
                    IconKind = MaterialIconKind.SubdirectoryArrowRight,
                    ParentContextLabel = parentTask?.Title ?? string.Empty
                });
            }
        }

        return items;
    }

    /// <summary>Chains <see cref="PolarTaskService.CalculateNextDueDate"/> forward from
    /// <paramref name="task"/>'s real <c>DueDateUtc</c> to preview future occurrences of a recurring task
    /// within [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>) — docs/calendar-feature/
    /// 03-glosario-y-modelo-de-datos.md §4. Pure and static (no service dependency) so it's directly
    /// testable without a live <see cref="PolarTaskService"/>, unlike the rest of this class — see
    /// <c>CalendarAggregationServiceTests</c>.</summary>
    /// <param name="calendarColorHex">Color de calendario de la lista (ver
    /// <see cref="CalendarLayerColorService"/>) — <c>null</c> deja la ocurrencia con el color de origen,
    /// que es lo que ven los tests que llaman a este método directamente.</param>
    public static List<CalendarItemViewModel> BuildGhostOccurrences(
        PolarTask task, PolarTaskList? list, DateTime rangeStartUtc, DateTime rangeEndUtc, string? calendarColorHex = null)
    {
        var results = new List<CalendarItemViewModel>();
        if (task.IsDeleted || task.Recurrence == PolarRecurrence.None || task.DueDateUtc is not { } current) return results;

        for (var i = 0; i < MaxGhostOccurrenceIterations; i++)
        {
            current = PolarTaskService.CalculateNextDueDate(current, task.Recurrence);
            if (current >= rangeEndUtc) break;
            if (current < rangeStartUtc) continue;

            results.Add(new CalendarItemViewModel
            {
                SourceId = task.Id,
                SourceType = CalendarItemSourceType.PolarTask,
                Title = task.Title,
                Description = task.Description,
                MomentUtc = current,
                IsAllDay = IsAllDay(current),
                IsCompleted = false,
                IsGhostOccurrence = true,
                LayerColorHex = list?.Color ?? CalendarItemViewModel.KanbanLayerColorHex,
                CalendarColorHex = calendarColorHex,
                LayerKey = task.ListId,
                Priority = MapPriority(task.Priority),
                Tags = task.Tags,
                IconKind = MaterialIconKind.CheckboxMarkedOutline,
                ParentContextLabel = list?.Title ?? string.Empty
            });
        }

        return results;
    }

    /// <summary>Same idea as the <see cref="PolarTask"/> overload above, for recurring reminders (análisis
    /// alfa-3 §4.4, Media — the ghost-occurrence preview only ever covered Polar tasks, leaving the other
    /// recurring item type the app natively supports without this parity). Reminders reuse
    /// <see cref="PolarRecurrence"/> and <see cref="PolarTaskService.CalculateNextDueDate"/> directly (see
    /// <see cref="Reminder.Recurrence"/>), so the chaining logic is identical.</summary>
    public static List<CalendarItemViewModel> BuildGhostOccurrences(
        Reminder reminder, DateTime rangeStartUtc, DateTime rangeEndUtc, string? calendarColorHex = null)
    {
        var results = new List<CalendarItemViewModel>();
        if (reminder.IsDeleted || reminder.Recurrence == PolarRecurrence.None) return results;

        var current = reminder.NotifyAtUtc;
        for (var i = 0; i < MaxGhostOccurrenceIterations; i++)
        {
            current = PolarTaskService.CalculateNextDueDate(current, reminder.Recurrence);
            if (current >= rangeEndUtc) break;
            if (current < rangeStartUtc) continue;

            var isAllDay = IsAllDay(current);
            results.Add(new CalendarItemViewModel
            {
                SourceId = reminder.Id,
                SourceType = CalendarItemSourceType.Reminder,
                Title = reminder.Title,
                Description = reminder.Description,
                MomentUtc = current,
                IsAllDay = isAllDay,
                IsCompleted = false,
                IsGhostOccurrence = true,
                DurationMinutes = isAllDay ? null : reminder.DurationMinutes,
                LayerColorHex = CalendarItemViewModel.ReminderLayerColorHex,
                CalendarColorHex = calendarColorHex,
                LayerKey = ReminderLayerKey,
                IconKind = Enum.TryParse<MaterialIconKind>(reminder.Icon, out var kind) ? kind : MaterialIconKind.BellRingOutline
            });
        }

        return results;
    }

    /// <summary>Los tres helpers de color de abajo devuelven <c>null</c> cuando no hay
    /// <see cref="CalendarLayerColorService"/> (construcción sin él, p. ej. en tests), y entonces cada
    /// item cae en su <c>LayerColorHex</c> de siempre.</summary>
    private static string? Resolve(Dictionary<string, string>? colors, string colorKey) =>
        colors is null ? null : CalendarLayerColorService.Resolve(colors, colorKey);

    private static string? ResolveKanbanColumnColor(Dictionary<string, string>? colors, string columnId) =>
        Resolve(colors, CalendarLayerColorService.KanbanColumnColorKey(columnId));

    private static string? ResolvePolarListColor(Dictionary<string, string>? colors, string? listId) =>
        string.IsNullOrEmpty(listId) ? null : Resolve(colors, listId);

    private static CalendarItemPriority MapPriority(KanbanPriority priority) => priority switch
    {
        KanbanPriority.High => CalendarItemPriority.High,
        KanbanPriority.Medium => CalendarItemPriority.Medium,
        KanbanPriority.Low => CalendarItemPriority.Low,
        _ => CalendarItemPriority.None
    };

    /// <summary>Same 0–3 scale <c>PolarTaskRowViewModel.PriorityLabel</c> already reads (docs/
    /// calendar-feature/03-glosario-y-modelo-de-datos.md §2.2: 0 = sin prioridad).</summary>
    private static CalendarItemPriority MapPriority(int polarPriority) => polarPriority switch
    {
        3 => CalendarItemPriority.High,
        2 => CalendarItemPriority.Medium,
        1 => CalendarItemPriority.Low,
        _ => CalendarItemPriority.None
    };

    private static bool IsAllDay(DateTime momentUtc) => momentUtc.ToLocalTime().TimeOfDay == TimeSpan.Zero;
}
