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
