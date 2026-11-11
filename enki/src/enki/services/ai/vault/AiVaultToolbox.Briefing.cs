using System;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Resumen del día y de la semana bajo demanda (AIA-12): en una sola lectura, lo que el asistente
/// necesita para preparar el briefing —agenda del periodo, tareas y recordatorios vencidos, hábitos pendientes de
/// hoy, la nota diaria y las notas editadas hace poco—. Es solo lectura y no guarda nada: la base para unas rutinas
/// futuras (AIA-21), que podrán pedirlo sin presencia del usuario. Los módulos que cuelgan del vault solo aparecen
/// en un chat que llega al vault, igual que en <c>get_workspace_overview</c>.</summary>
public sealed partial class AiVaultToolbox
{
    private const int MaxBriefingItems = 40;
    private const int MaxBriefingNotes = 15;

    private void RegisterBriefingTools()
    {
        Register("get_briefing", AiToolScope.Content, AiToolEffect.Read,
            "Everything needed for a daily or weekly briefing in one call: the agenda of the period, overdue Kanban cards, Polar "
            + "tasks and reminders, habits still pending today, whether today's daily note exists, and the notes edited recently. "
            + "Use it when the user asks for a summary of their day or week, what is pending or what to focus on. "
            + "Then write a short, prioritized briefing: what is late first, then what is due, then what changed.",
            Schema(Opt("period", Choice("\"day\" (today, default) or \"week\" (today and the next six days).", "day", "week"))),
            call =>
            {
                var week = call.GetText("period") == "week";
                call.Target(AiToolTargetKind.Insight, week ? "week" : "day");
                var scope = call.Context.Scope;
                var vaultModules = scope.IncludesVault;
                var now = DateTime.UtcNow;
                var today = DailyNoteService.Today;
                var from = Utc(today);
                var to = Utc(today.AddDays(week ? 7 : 1));
                // "Recientes": desde ayer para el día y la última semana para la semana.
                var recentSince = Utc(today.AddDays(week ? -7 : -1));

                var agenda = vaultModules && _calendar is not null
                    ? _calendar.BuildItems(from, to).Where(i => !i.IsCompleted).OrderBy(i => i.MomentUtc).ToList()
                    : null;
                var overdueCards = vaultModules
                    ? _kanban.AllColumns.SelectMany(c => c.Cards)
                        .Where(c => !c.IsCompleted && c.DueDateUtc is { } due && KanbanCard.IsPastDue(due))
                        .OrderBy(c => c.DueDateUtc).ToList()
                    : null;
                var overdueTasks = vaultModules && _polarAuth.IsLinked
                    ? _polar.Store.Tasks.Where(t => !t.IsDeleted && !t.Completed && t.DueDateUtc is { } due && KanbanCard.IsPastDue(due))
                        .OrderBy(t => t.DueDateUtc).ToList()
                    : null;
                var overdueReminders = vaultModules
                    ? _reminders.ActiveReminders.Where(r => !r.IsCompleted && r.NotifyAtUtc < now).OrderBy(r => r.NotifyAtUtc).ToList()
                    : null;
                var habits = vaultModules
                    ? _habits.Store.Habits.Where(h => !h.Archived && HabitService.IsApplicable(h, today)).ToList()
                    : null;
                var dailyNote = vaultModules ? _dailyNotes.Find(today, _dailyNotes.ResolveTemplate()) : null;
                var recent = Workspaces(scope)
                    .Where(w => w.Session is null || _sessions.IsUnlocked(w.Session))
                    .SelectMany(w => _vault.GetAllNotesRecursive(w.RootPath).Select(n => (Workspace: w, Note: n)))
                    .Where(t => t.Note.ModifiedUtc >= recentSince)
                    .OrderByDescending(t => t.Note.ModifiedUtc)
                    .ToList();

                return new
                {
                    Period = week ? "week" : "day",
                    Now = FormatInstant(now),
                    From = FormatDay(today),
                    To = FormatDay(today.AddDays(week ? 6 : 0)),
                    Modules = vaultModules ? null : "This chat only reaches its session: agenda, tasks, reminders and habits are not included.",
                    Agenda = agenda is null ? null : new
                    {
                        Total = agenda.Count,
                        Items = agenda.Take(MaxBriefingItems).Select(AgendaItem)
                    },
                    Overdue = !vaultModules ? null : new
                    {
                        KanbanCards = overdueCards!.Take(MaxBriefingItems).Select(c => new
                        {
                            c.Id,
                            c.Title,
                            Due = FormatDate(c.DueDateUtc),
                            Priority = c.Priority.ToString().ToLowerInvariant()
                        }),
                        PolarTasks = overdueTasks?.Take(MaxBriefingItems).Select(t => new { t.Id, t.Title, Due = FormatDate(t.DueDateUtc) }),
                        Reminders = overdueReminders!.Take(MaxBriefingItems).Select(r => new { r.Id, r.Title, When = FormatInstant(r.NotifyAtUtc) })
                    },
                    HabitsToday = habits?.Select(h => new { h.Id, h.Name, Done = HabitService.IsCompleted(h, today) }),
                    DailyNote = !vaultModules ? null : new { Exists = dailyNote is not null, Title = dailyNote?.Title },
                    RecentNotes = new
                    {
                        Since = FormatDay(today.AddDays(week ? -7 : -1)),
                        Total = recent.Count,
                        Notes = recent.Take(MaxBriefingNotes).Select(t => new
                        {
                            Workspace = WorkspaceKey(t.Workspace),
                            Path = RelativePath(t.Workspace, t.Note.FullPath),
                            t.Note.Title,
                            Modified = FormatInstant(t.Note.ModifiedUtc)
                        })
                    }
                };
            });
    }
}
