using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Etiquetas globales y estadísticas (AIA-08). Las etiquetas pasan por <see cref="TagManagerService"/>, el
/// mismo del Gestor de etiquetas, recortado al ámbito del chat: las notas de sus espacios desbloqueados y, solo si el
/// chat llega al vault, las tarjetas de Kanban y las tareas de Polar. Renombrar o fusionar reescribe muchas
/// entidades de una vez, así que pide confirmación con el recuento exacto; las notas guardan su versión anterior en
/// el historial, como hace el Gestor. Las estadísticas son las de la sección Estadísticas
/// (<see cref="StatisticsAggregationService"/>), de solo lectura.</summary>
public sealed partial class AiVaultToolbox
{
    private const int MaxTopTags = 15;

    private void RegisterTagAndStatisticsTools()
    {
        Register("list_tags", AiToolScope.Content, AiToolEffect.Read,
            "Lists every tag in use in this chat's scope with how many notes, Kanban cards and Polar tasks use it, alphabetically, "
            + "so spelling variants appear side by side. Kanban and Polar only count when the chat reaches the vault.",
            Schema(Opt("query", Text("Only tags containing this text."))),
            call =>
            {
                var query = call.GetText("query")?.Trim().TrimStart('#');
                var entries = ScopedTagEntries(call.Context.Scope)
                    .Where(e => string.IsNullOrEmpty(query) || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new
                {
                    Total = entries.Count,
                    Tags = entries.Select(e => new
                    {
                        e.Name,
                        Notes = e.NoteCount,
                        KanbanCards = call.Context.Scope.IncludesVault ? e.KanbanCount : (int?)null,
                        PolarTasks = call.Context.Scope.IncludesVault ? e.PolarCount : (int?)null
                    })
                };
            });

        RegisterAsync("rename_tag", AiToolScope.Content, AiToolEffect.Change,
            "Renames a tag everywhere in this chat's scope: notes and, when the chat reaches the vault, Kanban cards and Polar tasks. "
            + "Renaming to a tag that already exists merges them. The user confirms it in the app with the exact count. "
            + "Tag names are stored in lowercase.",
            Schema(Req("tag", Text("Current tag name, without '#'.")), Req("new_name", Text("New tag name, without '#'."))),
            async call => await ReplaceTagsAsync(call, [call.RequireText("tag")], call.RequireText("new_name")));

        RegisterAsync("merge_tags", AiToolScope.Content, AiToolEffect.Change,
            "Merges several tags into one everywhere in this chat's scope (notes and, when the chat reaches the vault, Kanban cards "
            + "and Polar tasks): every item tagged with any of 'tags' ends up tagged with 'into' instead. The user confirms it in the app.",
            Schema(Req("tags", TextList("Tags to merge, without '#'.")), Req("into", Text("Resulting tag name, without '#'."))),
            async call => await ReplaceTagsAsync(call,
                call.GetTextList("tags") is { Count: > 0 } tags ? tags : throw AiToolCall.Invalid("'tags' must list at least one tag."),
                call.RequireText("into")));

        RegisterAsync("get_statistics", AiToolScope.Vault, AiToolEffect.Read,
            "The user's progress, as in the Statistics section: notes created and edited, words written, Kanban and Polar tasks "
            + "completed, habit completion, active days and streaks for a period, compared with the same stretch of the previous "
            + "period, plus a day-by-day breakdown, task and reminder health and top tags. Use it for questions like "
            + "\"how am I doing this week?\".",
            Schema(Opt("period", Choice("Period to report (default this_week).",
                "this_week", "last_week", "this_month", "last_month", "last_7_days", "last_30_days"))),
            async call =>
            {
                call.Target(AiToolTargetKind.Insight, call.GetText("period") ?? "this_week");
                var (start, end) = StatisticsPeriod(call.GetText("period") ?? "this_week");
                // Lo transcurrido del periodo se compara con lo mismo del anterior: un lunes no se mide contra una semana entera.
                var today = DateOnly.FromDateTime(DateTime.Today);
                var elapsedEnd = end > today.AddDays(1) ? today.AddDays(1) : end;
                var length = end.DayNumber - start.DayNumber;
                var snapshot = await _statistics.ReadVaultSnapshotAsync();
                if (!_statistics.IsSnapshotCurrent(snapshot))
                    throw new AiToolException(AiToolErrorKind.Unavailable, "The vault changed while reading statistics. Try again.");
                var report = _statistics.BuildReport(Utc(start), Utc(elapsedEnd), snapshot);
                var previous = _statistics.BuildReport(Utc(start.AddDays(-length)), Utc(elapsedEnd.AddDays(-length)), snapshot);
                return new
                {
                    From = FormatDay(start),
                    To = FormatDay(end.AddDays(-1)),
                    DaysElapsed = elapsedEnd.DayNumber - start.DayNumber,
                    Current = PeriodSummary(report),
                    PreviousPeriod = new
                    {
                        From = FormatDay(start.AddDays(-length)),
                        To = FormatDay(elapsedEnd.AddDays(-length - 1)),
                        Summary = PeriodSummary(previous)
                    },
                    Daily = report.Daily.Select(d => new
                    {
                        Date = FormatDay(d.Date),
                        d.NotesCreated,
                        d.NotesEdited,
                        d.TasksCompleted,
                        d.HabitsCompleted,
                        d.HabitsApplicable
                    }),
                    ActivityStreak = new { report.ActivityStreak.Current, report.ActivityStreak.Best },
                    HabitStreak = new { report.HabitStreak.Current, report.HabitStreak.Best },
                    Tasks = new
                    {
                        Open = report.Tasks.Total,
                        report.Tasks.Overdue,
                        report.Tasks.DueToday,
                        report.Tasks.DueNext7Days,
                        KanbanPending = report.Flow.Pending,
                        KanbanInProgress = report.Flow.InProgress,
                        KanbanDone = report.Flow.Done
                    },
                    Reminders = new { report.Reminders.Active, report.Reminders.Upcoming, report.Reminders.Overdue },
                    TopTags = report.TopTags.Take(MaxTopTags).Select(t => new { Name = t.Label, Count = (int)t.Value })
                };
            });
    }

    private static object PeriodSummary(StatisticsReport report) => new
    {
        NotesCreated = report.NotesCreatedInRange,
        NotesEdited = report.NotesEditedInRange,
        WordsWritten = report.WordsWrittenInRange,
        TasksCompleted = report.TasksCompletedInRange,
        ActiveDays = report.ActiveDaysInRange,
        HabitCompletionPercent = report.HabitCompletionPercent is { } percent ? Math.Round(percent, 1) : (double?)null
    };

    /// <summary>[inicio, fin) en días locales. La semana empieza el lunes, como en la sección Estadísticas.</summary>
    private static (DateOnly Start, DateOnly End) StatisticsPeriod(string period)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateOnly(today.Year, today.Month, 1);
        return period switch
        {
            "this_week" => (monday, monday.AddDays(7)),
            "last_week" => (monday.AddDays(-7), monday),
            "this_month" => (month, month.AddMonths(1)),
            "last_month" => (month.AddMonths(-1), month),
            "last_7_days" => (today.AddDays(-6), today.AddDays(1)),
            "last_30_days" => (today.AddDays(-29), today.AddDays(1)),
            _ => throw AiToolCall.Invalid("'period' must be this_week, last_week, this_month, last_month, last_7_days or last_30_days.")
        };
    }

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

    /// <summary>Notas de los espacios desbloqueados del ámbito.</summary>
    private List<NoteFile> ScopedNotes(AiAccessScope scope) =>
        Workspaces(scope)
            .Where(w => w.Session is null || _sessions.IsUnlocked(w.Session))
            .SelectMany(w => _vault.GetAllNotesRecursive(w.RootPath))
            .ToList();

    private List<TagManagerEntry> ScopedTagEntries(AiAccessScope scope) =>
        _tagManager.BuildEntries(ScopedNotes(scope), includeTasks: scope.IncludesVault);

    /// <summary>Renombrar (una etiqueta) o fusionar (varias) en el ámbito del chat, tras la confirmación del usuario
    /// con lo que se va a tocar.</summary>
    private async Task<object?> ReplaceTagsAsync(AiToolCall call, IReadOnlyList<string> sources, string target)
    {
        var sourceNames = sources.Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var targetName = target.Trim().TrimStart('#');
        if (!TagManagerService.IsValidTagName(targetName))
            throw AiToolCall.Invalid("The new tag name cannot be empty or contain commas, brackets or line breaks.");
        call.Target(AiToolTargetKind.Tag, string.Join(", ", sourceNames.Select(t => "#" + t)) + " → #" + targetName.ToLowerInvariant());
        // Renombrar a lo mismo (salvo mayúsculas, que se normalizan) no cambia nada.
        if (sourceNames.All(t => string.Equals(t, targetName, StringComparison.Ordinal)))
            throw AiToolCall.Invalid("The new name is the same as the current one.");

        var scope = call.Context.Scope;
        var affected = ScopedTagEntries(scope).Where(e => sourceNames.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var (notes, cards, tasks) = (affected.Sum(e => e.NoteCount), affected.Sum(e => e.KanbanCount), affected.Sum(e => e.PolarCount));
        if (notes + cards + tasks == 0)
            throw new AiToolException(AiToolErrorKind.NotFound, "No note, card or task in this chat's scope uses those tags. Use list_tags.");

        var counts = LocalizedTextFormatter.Resolve(LocalizedTextFormatter.Join(" · ",
            LocalizedTextFormatter.PluralFormat("Assistant_Approval_Tag_Notes", notes, notes),
            LocalizedTextFormatter.PluralFormat("Assistant_Approval_Tag_Cards", cards, cards),
            LocalizedTextFormatter.PluralFormat("Assistant_Approval_Tag_Tasks", tasks, tasks)));
        var approved = await AskApprovalAsync(call,
            LocalizedTextFormatter.Format(sourceNames.Count == 1 ? "Assistant_Approval_RenameTag" : "Assistant_Approval_MergeTags"),
            [call.Activity.Target, counts], LocalizedTextFormatter.Format("Assistant_Approval_Tag_Hint"));
        if (!ChangesAllowed)
            throw new AiToolException(AiToolErrorKind.Denied, "The user disabled vault changes while this action was waiting for approval.");
        if (!approved)
            throw new AiToolException(AiToolErrorKind.Denied,
                "The user did not confirm this action, so nothing was done. Do not retry it unless the user asks again.");

        if (!_host.PrepareBulkNoteChange())
            throw new AiToolException(AiToolErrorKind.Conflict,
                "Some open notes have unsaved changes that could not be saved. Ask the user to save or close them first.");
        var summary = _tagManager.ReplaceTag(sourceNames, targetName, path => WorkspaceOf(scope, path) is not null,
            includeTasks: scope.IncludesVault);
        _host.BulkNotesChanged();
        return new
        {
            Tag = targetName.ToLowerInvariant(),
            Replaced = sourceNames,
            NotesUpdated = summary.Notes,
            KanbanCardsUpdated = summary.Kanban,
            PolarTasksUpdated = summary.Polar
        };
    }
}
