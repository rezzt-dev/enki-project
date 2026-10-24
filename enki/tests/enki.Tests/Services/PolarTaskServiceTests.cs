using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="PolarTaskService"/>'s riskiest local logic — none of it needs a network call,
/// but it's exactly the surface <see cref="PolarSyncService"/> depends on being correct before anything is
/// pushed to Supabase: cascading tombstones (03-problemas-y-deuda-tecnica.md §8), dependency-chain
/// gating, and the recurrence-rollover fallback engine. Each test points storage at its own throwaway temp
/// directory, same pattern as <see cref="ReminderServiceTests"/>.</summary>
public class PolarTaskServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private PolarTaskService CreateService() => new(_storageDirectory);

    // ===================== Tombstones en cascada =====================

    [Fact]
    public void DeleteList_CascadesSoftDeleteToItsTasksButNotOtherLists()
    {
        var service = CreateService();
        var list = service.CreateList("Lista A", "ic_list", "#000", isDependencyChain: false);
        var otherList = service.CreateList("Lista B", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var otherTask = service.CreateTask(otherList, "Otra tarea", "", 0, null, PolarRecurrence.None, 0, []);

        service.DeleteList(list);

        Assert.True(service.Store.Lists.Single(l => l.Id == list.Id).IsDeleted);
        Assert.True(service.Store.Tasks.Single(t => t.Id == task.Id).IsDeleted);
        Assert.NotNull(service.Store.Tasks.Single(t => t.Id == task.Id).DeletedAtUtc);
        Assert.True(service.Store.Tasks.Single(t => t.Id == task.Id).Dirty);
        Assert.False(service.Store.Lists.Single(l => l.Id == otherList.Id).IsDeleted);
        Assert.False(service.Store.Tasks.Single(t => t.Id == otherTask.Id).IsDeleted);
    }

    [Fact]
    public void DeleteTask_CascadesSoftDeleteToItsSubtasksButNotOtherTasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var otherTask = service.CreateTask(list, "Otra tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        var otherSubtask = service.AddSubtask(otherTask, "Otra subtarea");

        service.DeleteTask(task);

        Assert.True(service.Store.Tasks.Single(t => t.Id == task.Id).IsDeleted);
        Assert.True(service.Store.Subtasks.Single(s => s.Id == subtask.Id).IsDeleted);
        Assert.NotNull(service.Store.Subtasks.Single(s => s.Id == subtask.Id).DeletedAtUtc);
        Assert.False(service.Store.Tasks.Single(t => t.Id == otherTask.Id).IsDeleted);
        Assert.False(service.Store.Subtasks.Single(s => s.Id == otherSubtask.Id).IsDeleted);
    }

    [Fact]
    public void DeleteTask_DoesNotResurrectAlreadyDeletedSubtasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        service.DeleteSubtask(subtask);
        var deletedAtBeforeTaskDelete = service.Store.Subtasks.Single(s => s.Id == subtask.Id).DeletedAtUtc;

        service.DeleteTask(task);

        Assert.Equal(deletedAtBeforeTaskDelete, service.Store.Subtasks.Single(s => s.Id == subtask.Id).DeletedAtUtc);
    }

    // ===================== Restaurar desde la Papelera (análisis alfa-3 §4.3) =====================

    [Fact]
    public void RestoreList_UndeletesListButNotItsPreviouslyCascadedTasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista A", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        service.DeleteList(list);

        service.RestoreList(list);

        Assert.False(service.Store.Lists.Single(l => l.Id == list.Id).IsDeleted);
        Assert.Null(service.Store.Lists.Single(l => l.Id == list.Id).DeletedAtUtc);
        Assert.True(service.Store.Lists.Single(l => l.Id == list.Id).Dirty);
        Assert.True(service.Store.Tasks.Single(t => t.Id == task.Id).IsDeleted);
    }

    [Fact]
    public void RestoreTask_UndeletesTaskAndCascadesToItsSubtasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var otherTask = service.CreateTask(list, "Otra tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        var otherSubtask = service.AddSubtask(otherTask, "Otra subtarea");
        service.DeleteTask(task);
        service.DeleteTask(otherTask);

        service.RestoreTask(task);

        Assert.False(service.Store.Tasks.Single(t => t.Id == task.Id).IsDeleted);
        Assert.Null(service.Store.Tasks.Single(t => t.Id == task.Id).DeletedAtUtc);
        Assert.True(service.Store.Tasks.Single(t => t.Id == task.Id).Dirty);
        Assert.False(service.Store.Subtasks.Single(s => s.Id == subtask.Id).IsDeleted);
        Assert.True(service.Store.Tasks.Single(t => t.Id == otherTask.Id).IsDeleted);
        Assert.True(service.Store.Subtasks.Single(s => s.Id == otherSubtask.Id).IsDeleted);
    }

    // ===================== Purga de tombstones antiguos (análisis alfa-3, Fase 4) =====================

    // ===================== Borrado permanente desde la Papelera =====================

    [Fact]
    public void PurgeTask_PhysicallyRemovesTaskAndItsSubtasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        service.DeleteTask(task);

        service.PurgeTask(task);

        Assert.DoesNotContain(service.Store.Tasks, t => t.Id == task.Id);
        Assert.DoesNotContain(service.Store.Subtasks, s => s.Id == subtask.Id);
    }

    [Fact]
    public void PurgeList_PhysicallyRemovesListAndItsTasksAndSubtasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        service.DeleteList(list);

        service.PurgeList(list);

        Assert.DoesNotContain(service.Store.Lists, l => l.Id == list.Id);
        Assert.DoesNotContain(service.Store.Tasks, t => t.Id == task.Id);
        Assert.DoesNotContain(service.Store.Subtasks, s => s.Id == subtask.Id);
    }

    [Fact]
    public void PurgeList_DoesNotRemoveTasksFromOtherLists()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var otherList = service.CreateList("Otra", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(otherList, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        service.DeleteList(list);

        service.PurgeList(list);

        Assert.Single(service.Store.Lists, l => l.Id == otherList.Id);
        Assert.Single(service.Store.Tasks, t => t.Id == task.Id);
    }

    [Fact]
    public void PurgeAllDeleted_PhysicallyRemovesEveryDeletedListTaskAndSubtask()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        service.DeleteList(list);
        var keptList = service.CreateList("Mantener", "ic_list", "#000", isDependencyChain: false);
        var keptTask = service.CreateTask(keptList, "Mantener", "", 0, null, PolarRecurrence.None, 0, []);

        service.PurgeAllDeleted();

        Assert.DoesNotContain(service.Store.Lists, l => l.Id == list.Id);
        Assert.DoesNotContain(service.Store.Tasks, t => t.Id == task.Id);
        Assert.DoesNotContain(service.Store.Subtasks, s => s.Id == subtask.Id);
        Assert.Contains(service.Store.Lists, l => l.Id == keptList.Id);
        Assert.Contains(service.Store.Tasks, t => t.Id == keptTask.Id);
    }

    /// <summary>Mismo criterio de retención de 30 días que ya usan <c>KanbanService</c> y
    /// <c>ReminderService.PurgeOldTombstones</c> — una lista borrada hace más de 30 días y ya sincronizada
    /// (<c>!Dirty</c>) se purga físicamente al arrancar; una reciente, o una todavía pendiente de subir, se
    /// conserva.</summary>
    [Fact]
    public void Constructor_PurgesOldSyncedTombstones_ButKeepsRecentOrStillDirtyOnes()
    {
        var first = CreateService();
        var oldSynced = first.CreateList("Vieja y sincronizada", "ic_list", "#000", isDependencyChain: false);
        first.DeleteList(oldSynced);
        oldSynced.Dirty = false;
        oldSynced.DeletedAtUtc = DateTime.UtcNow.AddDays(-31);

        var oldButDirty = first.CreateList("Vieja pero sin sincronizar", "ic_list", "#000", isDependencyChain: false);
        first.DeleteList(oldButDirty);
        oldButDirty.DeletedAtUtc = DateTime.UtcNow.AddDays(-31);

        var recent = first.CreateList("Reciente", "ic_list", "#000", isDependencyChain: false);
        first.DeleteList(recent);
        recent.Dirty = false;
        recent.DeletedAtUtc = DateTime.UtcNow.AddDays(-2);

        first.Save();

        var second = CreateService();

        Assert.DoesNotContain(second.Store.Lists, l => l.Id == oldSynced.Id);
        Assert.Contains(second.Store.Lists, l => l.Id == oldButDirty.Id);
        Assert.Contains(second.Store.Lists, l => l.Id == recent.Id);
    }

    // ===================== Completar en cascada =====================

    [Fact]
    public void SetTaskCompleted_CascadesToActiveSubtasksOnly()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        var deletedSubtask = service.AddSubtask(task, "Subtarea borrada");
        service.DeleteSubtask(deletedSubtask);

        service.SetTaskCompleted(task, true);

        Assert.True(service.Store.Subtasks.Single(s => s.Id == subtask.Id).Completed);
        Assert.False(service.Store.Subtasks.Single(s => s.Id == deletedSubtask.Id).Completed);
    }

    // ===================== Cadena de dependencias =====================

    [Fact]
    public void IsTaskBlocked_TrueOnlyWhenAnEarlierIncompleteTaskPrecedesItInADependencyChain()
    {
        var service = CreateService();
        var chain = service.CreateList("Cadena", "ic_list", "#000", isDependencyChain: true);
        var first = service.CreateTask(chain, "Primero", "", 0, null, PolarRecurrence.None, 0, []);
        var second = service.CreateTask(chain, "Segundo", "", 0, null, PolarRecurrence.None, 0, []);

        Assert.False(service.IsTaskBlocked(first));
        Assert.True(service.IsTaskBlocked(second));

        service.SetTaskCompleted(first, true);
        Assert.False(service.IsTaskBlocked(second));
    }

    [Fact]
    public void IsTaskBlocked_FalseWhenListIsNotADependencyChain()
    {
        var service = CreateService();
        var list = service.CreateList("Lista normal", "ic_list", "#000", isDependencyChain: false);
        var first = service.CreateTask(list, "Primero", "", 0, null, PolarRecurrence.None, 0, []);
        var second = service.CreateTask(list, "Segundo", "", 0, null, PolarRecurrence.None, 0, []);

        Assert.False(service.IsTaskBlocked(second));
        Assert.False(service.IsTaskBlocked(first));
    }

    [Fact]
    public void IsTaskBlocked_FalseForAnAlreadyCompletedTask()
    {
        var service = CreateService();
        var chain = service.CreateList("Cadena", "ic_list", "#000", isDependencyChain: true);
        var first = service.CreateTask(chain, "Primero", "", 0, null, PolarRecurrence.None, 0, []);
        var second = service.CreateTask(chain, "Segundo", "", 0, null, PolarRecurrence.None, 0, []);
        service.SetTaskCompleted(second, true);

        Assert.False(service.IsTaskBlocked(second));
    }

    // ===================== Motor de recurrencia =====================

    [Theory]
    [InlineData(PolarRecurrence.Daily, 2024, 3, 10, 2024, 3, 11)]
    [InlineData(PolarRecurrence.Weekly, 2024, 3, 10, 2024, 3, 17)]
    [InlineData(PolarRecurrence.Monthly, 2024, 3, 10, 2024, 4, 10)]
    [InlineData(PolarRecurrence.FirstDayMonth, 2024, 3, 10, 2024, 4, 1)]
    public void CalculateNextDueDate_AdvancesRelativeToThePreviousDueDate(
        PolarRecurrence recurrence, int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        // Zona UTC explícita: sin transiciones DST, la aritmética de pared equivale a la UTC pura y el
        // test es determinista en cualquier máquina (la preservación de hora local con DST se cubre en la
        // sección dedicada más abajo — Fase 2 del roadmap de deficiencias).
        var from = new DateTime(fromYear, fromMonth, fromDay, 9, 30, 0, DateTimeKind.Utc);
        var next = PolarTaskService.CalculateNextDueDate(from, recurrence, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(toYear, toMonth, toDay, 9, 30, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Tuesday, DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Wednesday, DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Thursday, DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Friday, DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Saturday, DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Sunday, DayOfWeek.Monday)]
    public void CalculateNextDueDate_MonWed_AlwaysLandsOnTheNextMondayOrWednesday(DayOfWeek from, DayOfWeek expected)
    {
        // Find the next date on or after 2024-01-01 that falls on `from`.
        var date = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        while (date.DayOfWeek != from) date = date.AddDays(1);

        var next = PolarTaskService.CalculateNextDueDate(date, PolarRecurrence.MonWed, TimeZoneInfo.Utc);

        Assert.Equal(expected, next.DayOfWeek);
        Assert.True(next > date);
    }

    [Fact]
    public void CalculateNextDueDate_None_ReturnsTheSameDate()
    {
        var from = new DateTime(2024, 3, 10, 9, 30, 0, DateTimeKind.Utc);
        Assert.Equal(from, PolarTaskService.CalculateNextDueDate(from, PolarRecurrence.None, TimeZoneInfo.Utc));
    }

    // ===================== DST / cambio de horario (Fase 2, docs/analisis-proyecto/roadmap.md) =====================

    /// <summary>Zona con DST real (salto de primavera el 31/03/2024 y de otoño el 27/10/2024), fijada
    /// explícitamente para que estos tests sean deterministas en cualquier máquina, independientemente de
    /// su zona horaria local.</summary>
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static DateTime UtcFromMadridWall(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Unspecified), Madrid);

    /// <summary>El caso del análisis §6: la hora de pared elegida por el usuario (09:00) se preserva a
    /// través de una transición DST para cualquier recurrencia — la representación UTC sí se desplaza una
    /// hora (eso es lo correcto), la hora local no.</summary>
    [Theory]
    // Salto de primavera 2024 (CET → CEST la noche del 30 al 31 de marzo).
    [InlineData(PolarRecurrence.Daily, 2024, 3, 30, 2024, 3, 31)]
    [InlineData(PolarRecurrence.Weekly, 2024, 3, 24, 2024, 3, 31)]
    [InlineData(PolarRecurrence.Monthly, 2024, 3, 15, 2024, 4, 15)]
    [InlineData(PolarRecurrence.FirstDayMonth, 2024, 3, 1, 2024, 4, 1)]
    // Salto de otoño 2024 (CEST → CET la noche del 26 al 27 de octubre).
    [InlineData(PolarRecurrence.Daily, 2024, 10, 26, 2024, 10, 27)]
    [InlineData(PolarRecurrence.Weekly, 2024, 10, 20, 2024, 10, 27)]
    [InlineData(PolarRecurrence.Monthly, 2024, 10, 15, 2024, 11, 15)]
    [InlineData(PolarRecurrence.FirstDayMonth, 2024, 10, 1, 2024, 11, 1)]
    public void CalculateNextDueDate_AcrossDstTransition_PreservesLocalWallTime(
        PolarRecurrence recurrence, int fromYear, int fromMonth, int fromDay, int toYear, int toMonth, int toDay)
    {
        var fromUtc = UtcFromMadridWall(fromYear, fromMonth, fromDay, 9, 0);

        var next = PolarTaskService.CalculateNextDueDate(fromUtc, recurrence, Madrid);

        Assert.Equal(UtcFromMadridWall(toYear, toMonth, toDay, 9, 0), next);
    }

    [Fact]
    public void CalculateNextDueDate_AcrossSpringForward_UtcShiftsAnHour_WallTimeDoesNotMove()
    {
        var fromUtc = UtcFromMadridWall(2024, 3, 30, 9, 0); // 08:00 UTC (aún CET)

        var next = PolarTaskService.CalculateNextDueDate(fromUtc, PolarRecurrence.Daily, Madrid);

        Assert.Equal(new DateTime(2024, 3, 31, 7, 0, 0, DateTimeKind.Utc), next); // 07:00 UTC (ya CEST)
        Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(next, Madrid).Hour);      // 09:00 de pared
    }

    [Fact]
    public void CalculateNextDueDate_AcrossFallBack_UtcShiftsAnHour_WallTimeDoesNotMove()
    {
        var fromUtc = UtcFromMadridWall(2024, 10, 26, 9, 0); // 07:00 UTC (aún CEST)

        var next = PolarTaskService.CalculateNextDueDate(fromUtc, PolarRecurrence.Daily, Madrid);

        Assert.Equal(new DateTime(2024, 10, 27, 8, 0, 0, DateTimeKind.Utc), next); // 08:00 UTC (ya CET)
        Assert.Equal(9, TimeZoneInfo.ConvertTimeFromUtc(next, Madrid).Hour);       // 09:00 de pared
    }

    [Fact]
    public void RollRecurringTasks_RollsAPastDueCompletedRecurringTaskForwardAndUncompletesItsSubtasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var dueDate = DateTime.UtcNow.AddDays(-10);
        var task = service.CreateTask(list, "Tarea diaria", "", 0, dueDate, PolarRecurrence.Daily, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");
        service.SetTaskCompleted(task, true);
        Assert.True(service.Store.Subtasks.Single(s => s.Id == subtask.Id).Completed);

        service.RollRecurringTasks();

        var rolled = service.Store.Tasks.Single(t => t.Id == task.Id);
        Assert.False(rolled.Completed);
        Assert.True(rolled.DueDateUtc > dueDate);
        Assert.True(rolled.Dirty);
        Assert.False(service.Store.Subtasks.Single(s => s.Id == subtask.Id).Completed);
    }

    [Fact]
    public void RollRecurringTasks_MissedSeveralPeriods_RollsAllTheWayToTheNextFutureOccurrence()
    {
        // The app wasn't opened for 10 days while a daily task sat completed — a single +1 day step
        // would leave it 9 days overdue instead of reflecting the actual next occurrence.
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var dueDate = DateTime.UtcNow.AddDays(-10);
        var task = service.CreateTask(list, "Tarea diaria", "", 0, dueDate, PolarRecurrence.Daily, 0, []);
        service.SetTaskCompleted(task, true);

        service.RollRecurringTasks();

        var rolled = service.Store.Tasks.Single(t => t.Id == task.Id);
        Assert.False(rolled.Completed);
        Assert.True(rolled.DueDateUtc > DateTime.UtcNow);
    }

    [Fact]
    public void RollRecurringTasks_LeavesAFutureDueRecurringTaskUntouched()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var futureDueDate = DateTime.UtcNow.AddDays(10);
        var task = service.CreateTask(list, "Tarea diaria", "", 0, futureDueDate, PolarRecurrence.Daily, 0, []);
        service.SetTaskCompleted(task, true);

        service.RollRecurringTasks();

        var unchanged = service.Store.Tasks.Single(t => t.Id == task.Id);
        Assert.True(unchanged.Completed);
        Assert.Equal(futureDueDate, unchanged.DueDateUtc);
    }

    [Fact]
    public void RollRecurringTasks_IgnoresIncompleteOrNonRecurringTasks()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var pastDue = DateTime.UtcNow.AddDays(-10);
        var incompleteRecurring = service.CreateTask(list, "Incompleta", "", 0, pastDue, PolarRecurrence.Daily, 0, []);
        var completedNonRecurring = service.CreateTask(list, "No recurrente", "", 0, pastDue, PolarRecurrence.None, 0, []);
        service.SetTaskCompleted(completedNonRecurring, true);

        service.RollRecurringTasks();

        Assert.Equal(pastDue, service.Store.Tasks.Single(t => t.Id == incompleteRecurring.Id).DueDateUtc);
        Assert.Equal(pastDue, service.Store.Tasks.Single(t => t.Id == completedNonRecurring.Id).DueDateUtc);
        Assert.True(service.Store.Tasks.Single(t => t.Id == completedNonRecurring.Id).Completed);
    }

    // ===================== Aislamiento de cuentas — UserId (Fase 4) =====================

    [Fact]
    public void CreateList_StampsUserId_WithTheCurrentlyLinkedAccount()
    {
        var service = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => "user-a");

        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);

        Assert.Equal("user-a", list.UserId);
    }

    [Fact]
    public void CreateList_WithNoAccountLinked_LeavesUserIdNull()
    {
        var service = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => null);

        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);

        Assert.Null(list.UserId);
    }

    /// <summary>Editing a row created under account A while account B is linked must not silently hand it
    /// over to B — it stays owned by A (and therefore stays <c>Dirty</c> forever under B's session, never
    /// pushed) until A relinks. This is the same rule <see cref="PolarSyncService"/>'s push filter enforces
    /// (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    [Fact]
    public void UpdateList_WhileADifferentAccountIsLinked_DoesNotReassignOwnership()
    {
        var currentUser = "user-a";
        var service = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => currentUser);
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        Assert.Equal("user-a", list.UserId);

        currentUser = "user-b";
        service.UpdateList(list, "Lista renombrada", "ic_list", "#000", isDependencyChain: false);

        Assert.Equal("user-a", list.UserId);
        Assert.True(list.Dirty);
    }

    /// <summary>A row that predates the <c>UserId</c> field (loaded from disk as null) gets claimed the
    /// first time it's edited under some linked account, instead of staying unowned forever.</summary>
    [Fact]
    public void UpdateTask_WithNoPriorOwner_ClaimsItForWhoeverIsCurrentlyLinked()
    {
        var service = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => null);
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        Assert.Null(task.UserId);

        var linked = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => "user-a");
        var reloadedTask = linked.Store.Tasks.Single(t => t.Id == task.Id);
        linked.UpdateTask(reloadedTask, "Editada", "", 0, null, PolarRecurrence.None, 0, []);

        Assert.Equal("user-a", reloadedTask.UserId);
    }

    [Fact]
    public void DeleteTask_CascadesOwnershipStampToItsSubtasks()
    {
        var service = new PolarTaskService(_storageDirectory, currentUserIdProvider: () => "user-a");
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        var subtask = service.AddSubtask(task, "Subtarea");

        service.DeleteTask(task);

        Assert.Equal("user-a", service.Store.Subtasks.Single(s => s.Id == subtask.Id).UserId);
    }

    [Fact]
    public void ClearAll_EmptiesAndPersistsTheStore()
    {
        var service = CreateService();
        var list = service.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        service.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);

        service.ClearAll();

        Assert.Empty(service.Store.Lists);
        Assert.Empty(service.Store.Tasks);
        Assert.Empty(service.Store.Subtasks);

        var reloaded = CreateService();
        Assert.Empty(reloaded.Store.Lists);
        Assert.Empty(reloaded.Store.Tasks);
    }
}
