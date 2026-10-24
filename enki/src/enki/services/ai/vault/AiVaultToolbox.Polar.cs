using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Listas, tareas y subtareas de Polar (la "Gestión de tareas" sincronizada con la app móvil), solo
/// con una cuenta vinculada. Todo pasa por <see cref="PolarTaskService"/>, que marca cada cambio para que
/// <see cref="PolarSyncService"/> lo suba en la siguiente sincronización.</summary>
public sealed partial class AiVaultToolbox
{
    private static readonly string[] PolarPriorityNames = ["none", "low", "medium", "high"];

    private void RegisterPolarTools()
    {
        Register("list_polar", AiToolScope.Polar, AiToolEffect.Read,
            "Lists Polar task lists with their tasks (priority, due date, recurrence, tags, estimate, subtasks). "
            + "Polar is the task manager synced with the user's Polar mobile app.",
            Schema(Opt("list", Text("List title or id (omit for every list).")),
                Opt("include_completed", Flag("Include completed tasks (default true)."))),
            call =>
            {
                List<PolarTaskList> lists = call.Has("list")
                    ? [PolarList(call, "list")]
                    : _polar.Store.Lists.Where(l => !l.IsDeleted).OrderBy(l => l.OrderIndex).ToList();
                var completed = call.GetFlag("include_completed") ?? true;
                return new
                {
                    Lists = lists.Select(list => new
                    {
                        list.Id,
                        list.Title,
                        list.Color,
                        DependencyChain = list.IsDependencyChain,
                        Tasks = _polar.Store.Tasks
                            .Where(t => t.ListId == list.Id && !t.IsDeleted && (completed || !t.Completed))
                            .OrderBy(t => t.OrderIndex)
                            .Select(PolarTaskSummary)
                            .ToList()
                    }).ToList()
                };
            });

        Register("create_polar_list", AiToolScope.Polar, AiToolEffect.Change,
            "Creates a Polar task list. In a dependency chain only the first unfinished task is unlocked.",
            Schema(Req("title", Text("List title.")), Opt("color", Text("Hex color like #7F52FF.")),
                Opt("icon", Choice("Icon.", ViewModels.PolarIconMap.Keys)), Opt("dependency_chain", Flag("Default false."))),
            call =>
            {
                var title = call.RequireText("title").Trim();
                call.Target(AiToolTargetKind.PolarList, title);
                var list = _polar.CreateList(title, PolarIcon(call) ?? "ic_list", PolarColor(call) ?? "#7F52FF", call.GetFlag("dependency_chain") ?? false);
                call.Target(AiToolTargetKind.PolarList, list.Title, list.Id);
                return new { list.Id, list.Title, list.Color };
            });

        Register("update_polar_list", AiToolScope.Polar, AiToolEffect.Change,
            "Changes a Polar list's title, color, icon or dependency-chain mode.",
            Schema(Req("list_id", Text("List id or title.")), Opt("title", Text("New title.")), Opt("color", Text("Hex color.")),
                Opt("icon", Choice("Icon.", ViewModels.PolarIconMap.Keys)), Opt("dependency_chain", Flag("Dependency chain mode."))),
            call =>
            {
                var list = PolarList(call, "list_id");
                _polar.UpdateList(list, call.GetText("title")?.Trim() is { Length: > 0 } title ? title : list.Title,
                    PolarIcon(call) ?? list.Icon, PolarColor(call) ?? list.Color, call.GetFlag("dependency_chain") ?? list.IsDependencyChain);
                call.Target(AiToolTargetKind.PolarList, list.Title, list.Id);
                return new { list.Id, list.Title, list.Color, DependencyChain = list.IsDependencyChain };
            });

        Register("delete_polar_list", AiToolScope.Polar, AiToolEffect.Change,
            "Moves a Polar list and all its tasks to the trash.",
            Schema(Req("list_id", Text("List id or title."))),
            call =>
            {
                var list = PolarList(call, "list_id");
                _polar.DeleteList(list);
                return new { Trashed = true, list.Title };
            });

        Register("create_polar_task", AiToolScope.Polar, AiToolEffect.Change,
            "Creates a task in a Polar list. A recurring task needs a due date; once completed it moves to its next occurrence.",
            Schema(Req("list_id", Text("List id or title.")), Req("title", Text("Task title.")), Opt("description", Text("Description.")),
                Opt("priority", Choice("Priority (default none).", PolarPriorityNames)), Opt("due", Text("Due date. " + DateDescription)),
                Opt("recurrence", Choice("Recurrence (default none).", RecurrenceNames)),
                Opt("time_estimate_minutes", Number("Estimated minutes.")), Opt("duration_minutes", Number("Calendar time slot length.")),
                Opt("tags", TextList("Tags.")), Opt("subtasks", TextList("Subtask titles."))),
            call =>
            {
                var list = PolarList(call, "list_id");
                var title = call.RequireText("title").Trim();
                call.Target(AiToolTargetKind.PolarTask, title);
                var due = Date(call, "due");
                var recurrence = Recurrence(call) ?? PolarRecurrence.None;
                if (recurrence != PolarRecurrence.None && due is null) throw AiToolCall.Invalid("A recurring task needs a 'due' date.");
                var task = _polar.CreateTask(list, title, call.GetText("description") ?? string.Empty, PolarPriority(call) ?? 0, due,
                    recurrence, call.GetInteger("time_estimate_minutes", 0, 100_000) ?? 0, NormalizedTags(call.GetTextList("tags")).ToArray(),
                    call.GetInteger("duration_minutes", 0, 24 * 60));
                foreach (var subtask in call.GetTextList("subtasks") ?? []) _polar.AddSubtask(task, subtask);
                call.Target(AiToolTargetKind.PolarTask, task.Title, task.Id);
                return PolarTaskSummary(task);
            });

        Register("update_polar_task", AiToolScope.Polar, AiToolEffect.Change,
            "Edits a Polar task. Only the fields you pass change; pass null to clear due or duration_minutes. "
            + "completed=true completes it (and its subtasks); a completed recurring task moves to its next date at the next sync.",
            Schema(Req("task_id", Text("Task id.")), Opt("title", Text("New title.")), Opt("description", Text("New description.")),
                Opt("priority", Choice("Priority.", PolarPriorityNames)), Opt("due", NullableText(DateDescription)),
                Opt("recurrence", Choice("Recurrence.", RecurrenceNames)), Opt("time_estimate_minutes", Number("Estimated minutes.")),
                Opt("duration_minutes", new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("integer", "null") }),
                Opt("tags", TextList("Full list of tags.")), Opt("completed", Flag("Completed state."))),
            call =>
            {
                var task = PolarTask(call);
                var due = call.Has("due") ? Date(call, "due") : task.DueDateUtc;
                var recurrence = Recurrence(call) ?? task.Recurrence;
                if (recurrence != PolarRecurrence.None && due is null) throw AiToolCall.Invalid("A recurring task needs a 'due' date.");
                _polar.UpdateTask(task,
                    call.GetText("title")?.Trim() is { Length: > 0 } title ? title : task.Title,
                    call.Has("description") ? call.GetText("description") ?? string.Empty : task.Description,
                    PolarPriority(call) ?? task.Priority, due, recurrence,
                    call.GetInteger("time_estimate_minutes", 0, 100_000) ?? task.TimeEstimate,
                    call.Has("tags") ? NormalizedTags(call.GetTextList("tags")).ToArray() : task.Tags.ToArray(),
                    call.Has("duration_minutes") ? call.GetInteger("duration_minutes", 0, 24 * 60) : task.DurationMinutes);
                // Una recurrente completada no se adelanta aquí: lo hace PolarSyncService después de subir el
                // completado, para que Polar llegue a verlo (ver PolarTaskService.RollRecurringTasks).
                if (call.GetFlag("completed") is { } completed && completed != task.Completed) _polar.SetTaskCompleted(task, completed);
                call.Target(AiToolTargetKind.PolarTask, task.Title, task.Id);
                return PolarTaskSummary(task);
            });

        Register("delete_polar_task", AiToolScope.Polar, AiToolEffect.Change,
            "Moves a Polar task (with its subtasks) to the trash.",
            Schema(Req("task_id", Text("Task id."))),
            call =>
            {
                var task = PolarTask(call);
                _polar.DeleteTask(task);
                return new { Trashed = true, task.Title };
            });

        Register("add_polar_subtask", AiToolScope.Polar, AiToolEffect.Change,
            "Adds a subtask to a Polar task.",
            Schema(Req("task_id", Text("Task id.")), Req("title", Text("Subtask title.")), Opt("due", Text("Optional due date. " + DateDescription))),
            call =>
            {
                var task = PolarTask(call);
                var subtask = _polar.AddSubtask(task, call.RequireText("title").Trim());
                if (Date(call, "due") is { } due) _polar.UpdateSubtaskDueDate(subtask, due);
                return new { subtask.Id, subtask.Title, Task = task.Title };
            });

        Register("update_polar_subtask", AiToolScope.Polar, AiToolEffect.Change,
            "Completes or reopens a Polar subtask, changes its due date (null clears it), or deletes it (delete=true).",
            Schema(Req("subtask_id", Text("Subtask id.")), Opt("completed", Flag("Completed state.")),
                Opt("due", NullableText(DateDescription)), Opt("delete", Flag("Delete the subtask."))),
            call =>
            {
                var id = call.RequireText("subtask_id").Trim();
                var subtask = _polar.Store.Subtasks.FirstOrDefault(s => s.Id == id && !s.IsDeleted)
                    ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no Polar subtask with id \"{id}\".");
                call.Target(AiToolTargetKind.PolarTask, subtask.Title, subtask.TaskId);
                if (call.GetFlag("delete") == true)
                {
                    _polar.DeleteSubtask(subtask);
                    return new { Deleted = true, subtask.Title };
                }
                if (call.GetFlag("completed") is { } completed && completed != subtask.Completed) _polar.SetSubtaskCompleted(subtask, completed);
                if (call.Has("due")) _polar.UpdateSubtaskDueDate(subtask, Date(call, "due"));
                return new { subtask.Id, subtask.Title, subtask.Completed, Due = FormatDate(subtask.DueDateUtc) };
            });
    }

    private PolarTaskList PolarList(AiToolCall call, string argument)
    {
        var requested = call.RequireText(argument).Trim();
        var list = _polar.Store.Lists.FirstOrDefault(l => !l.IsDeleted && l.Id == requested)
            ?? _polar.Store.Lists.FirstOrDefault(l => !l.IsDeleted && string.Equals(l.Title, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no Polar list \"{requested}\". Use list_polar.");
        call.Target(AiToolTargetKind.PolarList, list.Title, list.Id);
        return list;
    }

    private PolarTask PolarTask(AiToolCall call)
    {
        var id = call.RequireText("task_id").Trim();
        var task = _polar.Store.Tasks.FirstOrDefault(t => t.Id == id && !t.IsDeleted)
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no Polar task with id \"{id}\". Use list_polar.");
        call.Target(AiToolTargetKind.PolarTask, task.Title, task.Id);
        return task;
    }

    private static int? PolarPriority(AiToolCall call) => call.GetText("priority")?.Trim().ToLowerInvariant() is { } value
        ? Array.IndexOf(PolarPriorityNames, value) is var index and >= 0 ? index
            : throw AiToolCall.Invalid($"Unknown priority \"{value}\": use none, low, medium or high.")
        : null;

    private static string? PolarColor(AiToolCall call) => call.GetText("color") is { Length: > 0 } color
        ? HexColorUtility.NormalizeHex(color) ?? throw AiToolCall.Invalid($"\"{color}\" is not a hex color like #7F52FF.")
        : null;

    private static string? PolarIcon(AiToolCall call) => call.GetText("icon") is { Length: > 0 } icon
        ? ViewModels.PolarIconMap.Keys.Contains(icon) ? icon : throw AiToolCall.Invalid($"Unknown icon \"{icon}\".")
        : null;

    private static readonly string[] RecurrenceNames = ["none", "daily", "weekly", "monthly", "mon_wed", "first_day_month"];

    private static PolarRecurrence? Recurrence(AiToolCall call) => call.GetText("recurrence")?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "none" => PolarRecurrence.None,
        "daily" => PolarRecurrence.Daily,
        "weekly" => PolarRecurrence.Weekly,
        "monthly" => PolarRecurrence.Monthly,
        "mon_wed" => PolarRecurrence.MonWed,
        "first_day_month" => PolarRecurrence.FirstDayMonth,
        var other => throw AiToolCall.Invalid($"Unknown recurrence \"{other}\": use " + string.Join(", ", RecurrenceNames) + ".")
    };

    private static string RecurrenceName(PolarRecurrence recurrence) => recurrence switch
    {
        PolarRecurrence.Daily => "daily",
        PolarRecurrence.Weekly => "weekly",
        PolarRecurrence.Monthly => "monthly",
        PolarRecurrence.MonWed => "mon_wed",
        PolarRecurrence.FirstDayMonth => "first_day_month",
        _ => "none"
    };

    private object PolarTaskSummary(PolarTask task) => new
    {
        task.Id,
        task.Title,
        Description = task.Description.Length > 0 ? Truncate(task.Description, 2_000) : null,
        task.Completed,
        Priority = PolarPriorityNames[Math.Clamp(task.Priority, 0, 3)],
        Due = FormatDate(task.DueDateUtc),
        Recurrence = task.Recurrence == PolarRecurrence.None ? null : RecurrenceName(task.Recurrence),
        Tags = task.Tags.Count > 0 ? task.Tags : null,
        TimeEstimateMinutes = task.TimeEstimate > 0 ? task.TimeEstimate : (int?)null,
        task.DurationMinutes,
        Blocked = _polar.IsTaskBlocked(task) ? true : (bool?)null,
        Subtasks = _polar.Store.Subtasks.Where(s => s.TaskId == task.Id && !s.IsDeleted).OrderBy(s => s.OrderIndex)
            .Select(s => new { s.Id, s.Title, s.Completed, Due = FormatDate(s.DueDateUtc) }).ToList() is { Count: > 0 } subtasks ? subtasks : null
    };
}
