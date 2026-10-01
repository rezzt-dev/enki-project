using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="CalendarAggregationService.BuildGhostOccurrences"/> — the Fase 4 recurrence
/// preview (docs/calendar-feature/03-glosario-y-modelo-de-datos.md §4). Pure and static, so unlike the rest
/// of <see cref="CalendarAggregationService"/> (which needs a live <c>KanbanService</c> with no
/// storage-directory override, see <c>CalendarViewModelTests</c>'s class remarks) it's directly
/// constructible in a test.</summary>
public class CalendarAggregationServiceTests
{
    [Fact]
    public void KanbanRange_PersistsAndProjectsBothMilestones_WithoutMarkingOpeningOverdue()
    {
        using var _ = new TempDirScope(out var directory);
        var kanban = new KanbanService(directory);
        var start = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var end = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
        kanban.DefaultBoard.Columns[0].Cards.Add(new KanbanCard
        {
            Id = "range", Title = "Entrega", StartDateUtc = start, DueDateUtc = end
        });
        kanban.Save();
        kanban = new KanbanService(directory);
        var card = kanban.AllColumns.SelectMany(c => c.Cards).Single(c => c.Id == "range");
        Assert.Equal(start, card.StartDateUtc);
        Assert.Equal(end, card.DueDateUtc);
        var svc = new CalendarAggregationService(kanban, new PolarTaskService(directory),
            new ReminderService(directory), new PolarAuthService(new SettingsService(directory)));
        var items = svc.BuildItems(start, end.AddDays(1)).Where(x => x.SourceId == "range").ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(start, items[0].MomentUtc);
        Assert.Equal(end, items[1].MomentUtc);
        Assert.True(items[0].IsOpeningMilestone);
        Assert.False(items[0].IsPastDue);
        Assert.False(items[1].IsOpeningMilestone);
        Assert.All(items, item => Assert.True(item.IsAllDay));
        Assert.Single(svc.BuildItems(start, end), x => x.SourceId == "range");
        Assert.DoesNotContain(svc.BuildUnscheduledItems(), x => x.SourceId == "range");
        card.DueDateUtc = null;
        Assert.Single(svc.BuildItems(start, end), x => x.SourceId == "range");
        Assert.DoesNotContain(svc.BuildUnscheduledItems(), x => x.SourceId == "range");
        card.StartDateUtc = null;
        Assert.Contains(svc.BuildUnscheduledItems(), x => x.SourceId == "range");
    }

    private static PolarTask MakeTask(
        DateTime? dueUtc, PolarRecurrence recurrence, bool isDeleted = false, int priority = 0, string[]? tags = null) => new()
    {
        Id = "task-1",
        ListId = "list-1",
        Title = "Regar las plantas",
        DueDateUtc = dueUtc,
        Recurrence = recurrence,
        IsDeleted = isDeleted,
        Priority = priority,
        Tags = (tags ?? []).ToList()
    };

    [Fact]
    public void BuildGhostOccurrences_NoRecurrence_ReturnsEmpty()
    {
        var task = MakeTask(new DateTime(2026, 3, 1), PolarRecurrence.None);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list: null, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_NoDueDate_ReturnsEmpty()
    {
        var task = MakeTask(dueUtc: null, PolarRecurrence.Weekly);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list: null, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_Deleted_ReturnsEmpty()
    {
        var task = MakeTask(new DateTime(2026, 3, 1), PolarRecurrence.Weekly, isDeleted: true);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list: null, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_Weekly_ProjectsEachFutureOccurrenceWithinRange()
    {
        // Real due date is a Sunday; a weekly task previewed across the following four weeks should
        // surface the next three Sundays (the real occurrence itself is never a ghost — doc §4).
        var realDue = new DateTime(2026, 3, 1);
        var task = MakeTask(realDue, PolarRecurrence.Weekly);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list: null, rangeStartUtc: realDue, rangeEndUtc: realDue.AddDays(28));

        Assert.Equal(3, result.Count);
        Assert.Equal([realDue.AddDays(7), realDue.AddDays(14), realDue.AddDays(21)], result.Select(r => r.MomentUtc));
        Assert.All(result, r => Assert.True(r.IsGhostOccurrence));
        Assert.All(result, r => Assert.False(r.IsCompleted));
    }

    [Fact]
    public void BuildGhostOccurrences_ExcludesOccurrencesBeforeRangeStart()
    {
        // The real occurrence is well before the visible range, but its future weekly projections that
        // land inside the range must still surface (docs/calendar-feature/11-arquitectura-tecnica.md §3:
        // "previsualizados... independientemente de si la fecha real cae en el rango").
        var realDue = new DateTime(2026, 1, 1);
        var task = MakeTask(realDue, PolarRecurrence.Weekly);
        var rangeStart = new DateTime(2026, 3, 1);
        var rangeEnd = new DateTime(2026, 3, 8);

        var result = CalendarAggregationService.BuildGhostOccurrences(task, list: null, rangeStart, rangeEnd);

        Assert.Single(result);
        Assert.True(result[0].MomentUtc >= rangeStart && result[0].MomentUtc < rangeEnd);
    }

    [Fact]
    public void BuildGhostOccurrences_UsesListColorAndCopiesTagsAndPriority()
    {
        var task = MakeTask(new DateTime(2026, 3, 1), PolarRecurrence.Daily, priority: 3, tags: ["urgente", "casa"]);
        var list = new PolarTaskList { Id = "list-1", Title = "Hogar", Color = "#FF00FF" };

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list, new DateTime(2026, 3, 1), new DateTime(2026, 3, 3));

        var occurrence = Assert.Single(result);
        Assert.Equal("#FF00FF", occurrence.LayerColorHex);
        Assert.Equal("list-1", occurrence.LayerKey);
        Assert.Equal(CalendarItemPriority.High, occurrence.Priority);
        Assert.Equal(["urgente", "casa"], occurrence.Tags);
        Assert.Equal("Hogar", occurrence.ParentContextLabel);
    }

    [Fact]
    public void BuildGhostOccurrences_StopsOnceOccurrencesReachRangeEnd()
    {
        var task = MakeTask(new DateTime(2026, 1, 1), PolarRecurrence.Daily);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            task, list: null, new DateTime(2026, 1, 1), new DateTime(2026, 1, 5));

        // Daily from Jan 1: Jan 2, 3, 4 fall in [Jan 1, Jan 5) — Jan 5 itself is excluded (half-open range).
        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.True(r.MomentUtc < new DateTime(2026, 1, 5)));
    }

    [Fact]
    public void BuildGhostOccurrences_VeryOldDailyTaskLeftUnfinished_AgesOutOfTheVisibleRange()
    {
        // análisis §4.4, Dev Media: el bucle de proyección tiene un tope fijo de 366 iteraciones
        // (MaxGhostOccurrenceIterations) porque RollRecurringTasks solo avanza la fecha real al
        // completarse una ocurrencia. Una tarea diaria dejada sin marcar durante más de un año agota ese
        // tope antes de alcanzar el rango visible del Calendario y desaparece de él en silencio, aunque
        // sigue siendo localizable en la lista de tareas Polar — este test documenta ese comportamiento
        // actual (conocido, no corregido en esta fase) para que un cambio futuro al límite o al algoritmo
        // de avance lo note explícitamente en vez de dejarlo regresar sin aviso.
        var realDue = new DateTime(2020, 1, 1); // more than 366 days before the visible range below
        var task = MakeTask(realDue, PolarRecurrence.Daily);
        var rangeStart = new DateTime(2026, 3, 1);
        var rangeEnd = new DateTime(2026, 3, 8);

        var result = CalendarAggregationService.BuildGhostOccurrences(task, list: null, rangeStart, rangeEnd);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_DailyTaskWithin366Days_StillReachesTheVisibleRange()
    {
        // Same setup as the "ages out" test above but just inside the 366-iteration budget, so the loop
        // reaches the visible range before exhausting itself — pins the boundary the previous test sits
        // just past, so both fail loudly if MaxGhostOccurrenceIterations or the roll algorithm changes.
        var rangeStart = new DateTime(2026, 3, 1);
        var rangeEnd = new DateTime(2026, 3, 8);
        var realDue = rangeStart.AddDays(-360);
        var task = MakeTask(realDue, PolarRecurrence.Daily);

        var result = CalendarAggregationService.BuildGhostOccurrences(task, list: null, rangeStart, rangeEnd);

        Assert.NotEmpty(result);
    }

    // ===================== Fase 6: paridad de ocurrencias fantasma para Recordatorios (análisis §4.4, Media) =====================

    private static Reminder MakeReminder(DateTime notifyAtUtc, PolarRecurrence recurrence, bool isDeleted = false, int? durationMinutes = null) => new()
    {
        Id = "reminder-1",
        Title = "Regar las plantas",
        NotifyAtUtc = notifyAtUtc,
        Recurrence = recurrence,
        IsDeleted = isDeleted,
        DurationMinutes = durationMinutes
    };

    [Fact]
    public void BuildGhostOccurrences_Reminder_NoRecurrence_ReturnsEmpty()
    {
        var reminder = MakeReminder(new DateTime(2026, 3, 1), PolarRecurrence.None);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            reminder, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_Reminder_Deleted_ReturnsEmpty()
    {
        var reminder = MakeReminder(new DateTime(2026, 3, 1), PolarRecurrence.Weekly, isDeleted: true);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            reminder, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1));

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGhostOccurrences_Reminder_Weekly_ProjectsEachFutureOccurrenceWithinRange()
    {
        var realNotifyAt = new DateTime(2026, 3, 1);
        var reminder = MakeReminder(realNotifyAt, PolarRecurrence.Weekly);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            reminder, rangeStartUtc: realNotifyAt, rangeEndUtc: realNotifyAt.AddDays(28));

        Assert.Equal(3, result.Count);
        Assert.Equal([realNotifyAt.AddDays(7), realNotifyAt.AddDays(14), realNotifyAt.AddDays(21)], result.Select(r => r.MomentUtc));
        Assert.All(result, r => Assert.True(r.IsGhostOccurrence));
        Assert.All(result, r => Assert.False(r.IsCompleted));
        Assert.All(result, r => Assert.Equal(CalendarItemSourceType.Reminder, r.SourceType));
    }

    [Fact]
    public void BuildGhostOccurrences_Reminder_CarriesDurationMinutes_ForTimedOccurrences()
    {
        var realNotifyAt = new DateTime(2026, 3, 1, 9, 0, 0);
        var reminder = MakeReminder(realNotifyAt, PolarRecurrence.Daily, durationMinutes: 30);

        var result = CalendarAggregationService.BuildGhostOccurrences(
            reminder, realNotifyAt, realNotifyAt.AddDays(2));

        var occurrence = Assert.Single(result);
        Assert.Equal(30, occurrence.DurationMinutes);
    }

    // ===================== Fase 3: duración en Polar y Recordatorios =====================

    private static (CalendarAggregationService Service, PolarTaskService Tasks, ReminderService Reminders)
        CreateServiceWithStores(out string storageDirectory)
    {
        storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        var settingsService = new SettingsService(storageDirectory);
        var kanbanService = new KanbanService(storageDirectory);
        var authService = new PolarAuthService(settingsService, baseUrl: "https://test.example", anonKey: "test-key");
        settingsService.Settings.PolarLinked = true;
        settingsService.Settings.PolarUserId = "user-1";
        var taskService = new PolarTaskService(storageDirectory);
        var reminderService = new ReminderService(storageDirectory);
        var service = new CalendarAggregationService(kanbanService, taskService, reminderService, authService);
        return (service, taskService, reminderService);
    }

    [Fact]
    public void BuildItems_MapsPolarTaskDurationMinutes()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var (service, taskService, _) = CreateServiceWithStores(out storageDirectory);

        var list = taskService.CreateList("Lista", "ic_list", "#7F52FF", false);
        var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        taskService.CreateTask(list, "Tarea", "", 0, due, PolarRecurrence.None, 0, [], durationMinutes: 45);

        var items = service.BuildItems(due.AddHours(-1), due.AddHours(2));
        var item = Assert.Single(items);

        Assert.Equal(45, item.DurationMinutes);
    }

    [Fact]
    public void BuildItems_MapsReminderDurationMinutes()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var (service, _, reminderService) = CreateServiceWithStores(out storageDirectory);

        var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        reminderService.Create("Recordatorio", "", due, "BellRingOutline", durationMinutes: 30);

        var items = service.BuildItems(due.AddHours(-1), due.AddHours(2));
        var item = Assert.Single(items);

        Assert.Equal(30, item.DurationMinutes);
    }

    [Fact]
    public void BuildItems_ProjectsReminderGhostOccurrences_WhenIncluded()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var (service, _, reminderService) = CreateServiceWithStores(out storageDirectory);

        var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        reminderService.Create("Recordatorio semanal", "", due, "BellRingOutline", recurrence: PolarRecurrence.Weekly);

        var itemsWithGhosts = service.BuildItems(due.AddDays(6), due.AddDays(8), includeGhostOccurrences: true);
        var itemsWithoutGhosts = service.BuildItems(due.AddDays(6), due.AddDays(8), includeGhostOccurrences: false);

        var ghost = Assert.Single(itemsWithGhosts);
        Assert.True(ghost.IsGhostOccurrence);
        Assert.Equal(due.AddDays(7), ghost.MomentUtc);
        Assert.Empty(itemsWithoutGhosts);
    }

    [Fact]
    public void BuildItems_IgnoresDurationMinutes_ForAllDayItems()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var (service, _, reminderService) = CreateServiceWithStores(out storageDirectory);

        // All-day means local midnight — use Today (local) converted to UTC instead of hardcoding UTC midnight.
        var due = DateTime.Today.ToUniversalTime();
        reminderService.Create("Todo el día", "", due, "BellRingOutline", durationMinutes: 30);

        var items = service.BuildItems(due.AddHours(-1), due.AddDays(1));
        var item = Assert.Single(items);

        Assert.Null(item.DurationMinutes);
        Assert.True(item.IsAllDay);
    }

    // ===================== Fase 3: IsCompleted de tarjetas Kanban (análisis §4.4, Alta) =====================

    [Fact]
    public void BuildItems_KanbanCard_IsCompletedTrue_WhenCompletedAtUtcSet()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var kanbanService = new KanbanService(storageDirectory);
        var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var card = new KanbanCard { Id = "done-1", Title = "Hecha", DueDateUtc = due, CompletedAtUtc = due };
        kanbanService.DefaultBoard.Columns[0].Cards.Add(card);
        var svc = new CalendarAggregationService(kanbanService, new PolarTaskService(storageDirectory), new ReminderService(storageDirectory), new PolarAuthService(new SettingsService(storageDirectory)));

        var items = svc.BuildItems(due.AddHours(-1), due.AddHours(1));

        var item = Assert.Single(items);
        Assert.True(item.IsCompleted);
    }

    [Fact]
    public void BuildItems_KanbanCard_IsCompletedFalse_WhenCompletedAtUtcNull()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var kanbanService = new KanbanService(storageDirectory);
        var due = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var card = new KanbanCard { Id = "pending-1", Title = "Pendiente", DueDateUtc = due };
        kanbanService.DefaultBoard.Columns[0].Cards.Add(card);
        var svc = new CalendarAggregationService(kanbanService, new PolarTaskService(storageDirectory), new ReminderService(storageDirectory), new PolarAuthService(new SettingsService(storageDirectory)));

        var items = svc.BuildItems(due.AddHours(-1), due.AddHours(1));

        var item = Assert.Single(items);
        Assert.False(item.IsCompleted);
    }

    [Fact]
    public void BuildUnscheduledItems_KanbanCard_IsCompletedTrue_WhenCompletedAtUtcSet()
    {
        using var _ = new TempDirScope(out var storageDirectory);
        var kanbanService = new KanbanService(storageDirectory);
        var card = new KanbanCard { Id = "done-2", Title = "Hecha sin fecha", CompletedAtUtc = DateTime.UtcNow };
        kanbanService.DefaultBoard.Columns[0].Cards.Add(card);
        var svc = new CalendarAggregationService(kanbanService, new PolarTaskService(storageDirectory), new ReminderService(storageDirectory), new PolarAuthService(new SettingsService(storageDirectory)));

        var items = svc.BuildUnscheduledItems();

        var item = Assert.Single(items, i => i.SourceId == card.Id);
        Assert.True(item.IsCompleted);
    }

    private sealed class TempDirScope : IDisposable
    {
        public string TempPath { get; }

        public TempDirScope(out string directory)
        {
            TempPath = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
            directory = TempPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(TempPath)) Directory.Delete(TempPath, recursive: true);
        }
    }
}
