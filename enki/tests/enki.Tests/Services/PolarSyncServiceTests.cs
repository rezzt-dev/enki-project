using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="PolarSyncService"/>'s highest-risk, network-free logic:
/// <list type="bullet">
/// <item>the DTO&lt;-&gt;local-model mapping applied on every LWW conflict loss and every pull
/// (<c>Apply*Dto</c>/<c>New*FromDto</c>, made <c>internal</c> specifically so these tests can reach them
/// directly — see the <c>InternalsVisibleTo</c> in enki.csproj), including tombstone field round-trip
/// (<see cref="Reminder.IsDeleted"/>/<see cref="Reminder.DeletedAtUtc"/> and their <c>PolarTask</c>/
/// <c>PolarTaskList</c>/<c>PolarSubtask</c> equivalents);</item>
/// <item>the "not linked" guard on <see cref="PolarSyncService.SyncNowAsync"/>, which is what actually runs
/// in any environment without a real Supabase account linked (including this test project's own CI/fresh
/// checkout, where <c>local.properties</c> doesn't exist).</item>
/// </list>
/// Full push/pull HTTP orchestration against a live (or mocked) Supabase endpoint is intentionally not
/// covered here — <see cref="SupabaseRestClient"/> always opens a real <see cref="System.Net.Http.HttpClient"/>
/// with no injectable transport, so exercising it would mean either a live network dependency or a larger
/// refactor than this test-coverage pass justifies; see docs/analisis-proyecto/03-problemas-y-deuda-tecnica.md
/// §8.</summary>
public class PolarSyncServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    // ===================== Guardia "no vinculado" =====================

    [Fact]
    public async System.Threading.Tasks.Task SyncNowAsync_NoOpsWhenNotLinked_LeavesDirtyDataUntouched()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var sync = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);

        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        reminderService.Create("Recordatorio", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        (bool TasksChanged, bool RemindersChanged)? completedArgs = null;
        sync.SyncCompleted += (tasksChanged, remindersChanged) => completedArgs = (tasksChanged, remindersChanged);

        Assert.False(authService.IsLinked);
        await sync.SyncNowAsync();

        Assert.Null(sync.LastError);
        Assert.True(taskService.Store.Lists.Single(l => l.Id == list.Id).Dirty);
        Assert.True(reminderService.Reminders[0].Dirty);
        Assert.Equal(0, settingsService.Settings.PolarLastSyncAtUtcMs);
        // The not-linked guard returns before the try/finally that raises SyncCompleted, so a listener
        // never even hears about a sync attempt that never really started.
        Assert.Null(completedArgs);
    }

    // ===================== Mapeo DTO <-> modelo local (conflictos, tombstones) =====================

    [Fact]
    public void ApplyTaskDto_AppliesTombstoneFieldsAndClearsNothingItShouldnt()
    {
        var task = new PolarTask { Id = "t1", ListId = "l1", Title = "Original", Dirty = true };
        var deletedAt = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        var dto = new TaskDto
        {
            Id = "t1",
            ListId = "l1",
            Title = "Del servidor",
            Description = "desc",
            Completed = true,
            Tags = ["urgente", "casa"],
            DueDate = PolarWire.ToEpochMs(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            OrderIndex = 3,
            Recurrence = "WEEKLY",
            Priority = 2,
            ImagePath = "task-images/x.png",
            TimeEstimate = 45,
            IsDeleted = true,
            DeletedAt = PolarWire.ToEpochMs(deletedAt),
            CreatedAt = PolarWire.ToEpochMs(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            UpdatedAt = PolarWire.ToEpochMs(new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc))
        };

        PolarSyncService.ApplyTaskDto(task, dto);

        Assert.Equal("Del servidor", task.Title);
        Assert.True(task.Completed);
        Assert.Equal(["urgente", "casa"], task.Tags);
        Assert.Equal(PolarRecurrence.Weekly, task.Recurrence);
        Assert.True(task.IsDeleted);
        Assert.Equal(deletedAt, task.DeletedAtUtc);
        // ApplyTaskDto itself doesn't touch Dirty — callers (Push/Pull) decide that explicitly; this locks
        // that division of responsibility down so a future edit can't silently start clearing it here too.
        Assert.True(task.Dirty);
    }

    [Fact]
    public void ApplyTaskDto_UndeletingClearsIsDeletedAndDeletedAt()
    {
        var task = new PolarTask
        {
            Id = "t1", ListId = "l1", Title = "x",
            IsDeleted = true, DeletedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var dto = new TaskDto
        {
            Id = "t1", ListId = "l1", Title = "x", IsDeleted = false, DeletedAt = null,
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow), UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplyTaskDto(task, dto);

        Assert.False(task.IsDeleted);
        Assert.Null(task.DeletedAtUtc);
    }

    [Fact]
    public void NewTaskFromDto_ClearsDirtySoAFreshlyPulledRowIsNotPushedRightBackUp()
    {
        var dto = new TaskDto
        {
            Id = "t1", ListId = "l1", Title = "Pulled",
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow), UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        var task = PolarSyncService.NewTaskFromDto(dto);

        Assert.Equal("t1", task.Id);
        Assert.False(task.Dirty);
    }

    [Fact]
    public void ApplyReminderDto_RoundTripsTombstoneAndLocationFields()
    {
        var reminder = new Reminder { Id = "r1", Title = "Original", Dirty = true };
        var deletedAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var dto = new ReminderDto
        {
            Id = "r1",
            Title = "Del servidor",
            Description = "detalle",
            DateTime = PolarWire.ToEpochMs(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)),
            IsCompleted = true,
            Latitude = 40.4,
            Longitude = -3.7,
            Radius = 100f,
            LocationName = "Casa",
            IsDeleted = true,
            DeletedAt = PolarWire.ToEpochMs(deletedAt),
            CreatedAt = PolarWire.ToEpochMs(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            UpdatedAt = PolarWire.ToEpochMs(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc))
        };

        PolarSyncService.ApplyReminderDto(reminder, dto);

        Assert.Equal("Del servidor", reminder.Title);
        Assert.True(reminder.IsCompleted);
        Assert.Equal(40.4, reminder.Latitude);
        Assert.Equal("Casa", reminder.LocationName);
        Assert.True(reminder.IsDeleted);
        Assert.Equal(deletedAt, reminder.DeletedAtUtc);
    }

    /// <summary>Fase 2 (docs/analisis-proyecto/roadmap.md): si el servidor mueve la fecha del recordatorio
    /// (reprogramación desde Polar/Android), el flag local <see cref="Reminder.IsTriggered"/> — que nunca
    /// viaja por el wire — tiene que rearmarse; si no, el recordatorio se mostraría como "Programado" pero
    /// no volvería a notificar jamás.</summary>
    [Fact]
    public void ApplyReminderDto_WhenServerMovesTheDate_ReArmsIsTriggered()
    {
        var reminder = new Reminder
        {
            Id = "r1",
            Title = "Original",
            NotifyAtUtc = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc),
            IsTriggered = true
        };
        var dto = new ReminderDto
        {
            Id = "r1",
            Title = "Original",
            DateTime = PolarWire.ToEpochMs(new DateTime(2026, 3, 5, 18, 30, 0, DateTimeKind.Utc)),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow),
            UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplyReminderDto(reminder, dto);

        Assert.False(reminder.IsTriggered);
        Assert.Equal(new DateTime(2026, 3, 5, 18, 30, 0, DateTimeKind.Utc), reminder.NotifyAtUtc);
    }

    /// <summary>El rearme solo ocurre cuando la fecha cambia de verdad: una edición remota que no toca la
    /// fecha (título, nota, ubicación...) no debe provocar una segunda notificación del mismo aviso.</summary>
    [Fact]
    public void ApplyReminderDto_WhenServerKeepsTheSameDate_PreservesIsTriggered()
    {
        var notifyAt = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);
        var reminder = new Reminder { Id = "r1", Title = "Original", NotifyAtUtc = notifyAt, IsTriggered = true };
        var dto = new ReminderDto
        {
            Id = "r1",
            Title = "Renombrado en el móvil",
            DateTime = PolarWire.ToEpochMs(notifyAt),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow),
            UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplyReminderDto(reminder, dto);

        Assert.True(reminder.IsTriggered);
    }

    [Fact]
    public void ApplyTaskListDto_RoundTripsTombstoneFields()
    {
        var list = new PolarTaskList { Id = "l1", Title = "Original" };
        var deletedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var dto = new TaskListDto
        {
            Id = "l1", Title = "Del servidor", Icon = "ic_list", Color = "#fff",
            IsDeleted = true, DeletedAt = PolarWire.ToEpochMs(deletedAt),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow), UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplyTaskListDto(list, dto);

        Assert.True(list.IsDeleted);
        Assert.Equal(deletedAt, list.DeletedAtUtc);
    }

    [Fact]
    public void ApplySubtaskDto_RoundTripsTombstoneFields()
    {
        var subtask = new PolarSubtask { Id = "s1", TaskId = "t1", Title = "Original" };
        var deletedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var dto = new SubtaskDto
        {
            Id = "s1", TaskId = "t1", Title = "Del servidor",
            IsDeleted = true, DeletedAt = PolarWire.ToEpochMs(deletedAt),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow), UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplySubtaskDto(subtask, dto);

        Assert.True(subtask.IsDeleted);
        Assert.Equal(deletedAt, subtask.DeletedAtUtc);
    }

    // ===================== PolarWire: conversión de instantes =====================

    [Fact]
    public void PolarWire_EpochMsRoundTrip_PreservesTheInstant()
    {
        var original = new DateTime(2026, 8, 13, 15, 42, 7, DateTimeKind.Utc);
        var ms = PolarWire.ToEpochMs(original);
        var restored = PolarWire.FromEpochMs(ms);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void PolarWire_OrNullVariants_PassThroughNull()
    {
        Assert.Null(PolarWire.ToEpochMsOrNull(null));
        Assert.Null(PolarWire.FromEpochMsOrNull(null));
    }

    [Theory]
    [InlineData(PolarRecurrence.Daily, "DAILY")]
    [InlineData(PolarRecurrence.Weekly, "WEEKLY")]
    [InlineData(PolarRecurrence.Monthly, "MONTHLY")]
    [InlineData(PolarRecurrence.MonWed, "MON_WED")]
    [InlineData(PolarRecurrence.FirstDayMonth, "FIRST_DAY_MONTH")]
    [InlineData(PolarRecurrence.None, "NONE")]
    public void PolarWire_Recurrence_RoundTripsThroughTheWireString(PolarRecurrence recurrence, string wire)
    {
        Assert.Equal(wire, PolarWire.RecurrenceToWire(recurrence));
        Assert.Equal(recurrence, PolarWire.RecurrenceFromWire(wire));
    }

    [Fact]
    public void PolarWire_RecurrenceFromWire_UnknownValueFallsBackToNone()
    {
        Assert.Equal(PolarRecurrence.None, PolarWire.RecurrenceFromWire("algo-desconocido"));
        Assert.Equal(PolarRecurrence.None, PolarWire.RecurrenceFromWire(null));
    }
}
