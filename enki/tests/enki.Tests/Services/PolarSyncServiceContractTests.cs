using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>End-to-end contract tests for <see cref="PolarSyncService.SyncNowAsync"/> against a scripted
/// fake Supabase transport — the push/pull HTTP orchestration <c>PolarSyncServiceTests</c> explicitly left
/// uncovered because <see cref="SupabaseRestClient"/> had no injectable transport (10.2 §6/§7). Also covers
/// <see cref="PolarSyncService.Status"/>, the derived state the "sincronizado/sincronizando/error/offline"
/// UI indicator reads (10.2 §8).</summary>
public class PolarSyncServiceContractTests : IDisposable
{
    private const string BaseUrl = "https://fake.supabase.test";
    private const string AnonKey = "anon-key-123";

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SettingsService _settingsService;
    private readonly PolarTaskService _taskService;
    private readonly ReminderService _reminderService;
    private readonly PolarSyncService _sync;

    public PolarSyncServiceContractTests()
    {
        _settingsService = new SettingsService(_storageDirectory);
        _settingsService.Settings.PolarLinked = true;
        _settingsService.Settings.PolarUserId = "user-1";
        _settingsService.Settings.PolarAccessToken = "test-access-token";
        _settingsService.Save();

        var authService = new PolarAuthService(_settingsService, _handler, BaseUrl, AnonKey);
        var restClient = new SupabaseRestClient(authService, _handler, BaseUrl, AnonKey);
        _taskService = new PolarTaskService(_storageDirectory);
        _reminderService = new ReminderService(_storageDirectory);
        _sync = new PolarSyncService(authService, restClient, _taskService, _reminderService, _settingsService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public async Task SyncNowAsync_PushesDirtyListAndPullsNothingNew_ClearsDirtyAndReportsSynced()
    {
        var list = _taskService.CreateList("Trabajo", "ic_list", "#000", isDependencyChain: false);
        var echoedUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc);

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] // push task_lists (echoes back, no LWW conflict)
        {
            new TaskListDto { Id = list.Id, Title = list.Title, UpdatedAt = echoedUpdatedAt, CreatedAt = echoedUpdatedAt }
        }));
        EnqueueEmptyPulls();

        (bool TasksChanged, bool RemindersChanged)? completed = null;
        _sync.SyncCompleted += (tasksChanged, remindersChanged) => completed = (tasksChanged, remindersChanged);

        var outcome = await _sync.SyncNowAsync();

        Assert.Equal(PolarSyncOutcome.Completed, outcome);
        Assert.Null(_sync.LastError);
        Assert.False(_taskService.Store.Lists.Single(l => l.Id == list.Id).Dirty);
        Assert.NotNull(_sync.LastSyncedUtc);
        Assert.Equal(PolarSyncStatus.Synced, _sync.Status);
        Assert.Equal((true, false), completed);

        // Push before pull, and lists → tasks → subtasks → reminders order for both halves.
        Assert.Equal(5, _handler.Requests.Count);
        Assert.Equal($"{BaseUrl}/rest/v1/task_lists?on_conflict=id", _handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("/rest/v1/task_lists?", _handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("/rest/v1/tasks?", _handler.Requests[2].RequestUri!.ToString());
        Assert.Contains("/rest/v1/subtasks?", _handler.Requests[3].RequestUri!.ToString());
        Assert.Contains("/rest/v1/reminders?", _handler.Requests[4].RequestUri!.ToString());
    }

    [Fact]
    public async Task SyncNowAsync_PushLosesLwwConflict_AdoptsServerVersionButStillClearsDirty()
    {
        var list = _taskService.CreateList("Título local", "ic_list", "#000", isDependencyChain: false);
        var newerUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc) + 60_000; // someone else's write, strictly newer

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = list.Id, Title = "Título remoto", UpdatedAt = newerUpdatedAt, CreatedAt = newerUpdatedAt }
        }));
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        var local = _taskService.Store.Lists.Single(l => l.Id == list.Id);
        Assert.Equal("Título remoto", local.Title);
        Assert.False(local.Dirty);
    }

    /// <summary>Fase 9 (docs/analisis-proyecto/roadmap.md): before this, the branch above discarded the
    /// user's own edit with no visible trace at all — the title just silently reverted. Now every LWW loss
    /// in a sync cycle is collected and reported through one <see cref="PolarSyncService.LocalEditsOverwritten"/>
    /// call, carrying whatever the user had actually typed (captured before it's overwritten by the server's
    /// version).</summary>
    [Fact]
    public async Task SyncNowAsync_PushLosesLwwConflict_RaisesLocalEditsOverwrittenWithTheDiscardedTitle()
    {
        var list = _taskService.CreateList("Título local", "ic_list", "#000", isDependencyChain: false);
        var newerUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc) + 60_000;

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = list.Id, Title = "Título remoto", UpdatedAt = newerUpdatedAt, CreatedAt = newerUpdatedAt }
        }));
        EnqueueEmptyPulls();

        IReadOnlyList<string>? overwritten = null;
        _sync.LocalEditsOverwritten += titles => overwritten = titles;

        await _sync.SyncNowAsync();

        Assert.NotNull(overwritten);
        Assert.Equal(new[] { "Título local" }, overwritten);
    }

    /// <summary>Multiple rows losing their conflict in the same cycle are batched into a single event call
    /// (one per <see cref="PolarSyncService.SyncNowAsync"/> attempt, not one per row) — matching how
    /// <c>KanbanViewModel.DeleteColumn</c> already reports a card count instead of firing once per card.</summary>
    [Fact]
    public async Task SyncNowAsync_MultipleListsLoseLwwConflict_BatchesAllTitlesIntoOneEventCall()
    {
        var listA = _taskService.CreateList("Lista A local", "ic_list", "#000", isDependencyChain: false);
        var listB = _taskService.CreateList("Lista B local", "ic_list", "#000", isDependencyChain: false);
        var newerA = PolarWire.ToEpochMs(listA.UpdatedAtUtc) + 60_000;
        var newerB = PolarWire.ToEpochMs(listB.UpdatedAtUtc) + 60_000;

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = listA.Id, Title = "Lista A remota", UpdatedAt = newerA, CreatedAt = newerA },
            new TaskListDto { Id = listB.Id, Title = "Lista B remota", UpdatedAt = newerB, CreatedAt = newerB }
        }));
        EnqueueEmptyPulls();

        var callCount = 0;
        IReadOnlyList<string>? overwritten = null;
        _sync.LocalEditsOverwritten += titles => { callCount++; overwritten = titles; };

        await _sync.SyncNowAsync();

        Assert.Equal(1, callCount);
        Assert.NotNull(overwritten);
        Assert.Equal(2, overwritten!.Count);
        Assert.Contains("Lista A local", overwritten);
        Assert.Contains("Lista B local", overwritten);
    }

    /// <summary>Fase 9: a manual "Sync now" click that lands while another attempt already holds the gate
    /// must say so — before this, both a real completed cycle and a discarded one looked identical to the
    /// caller (a quick, silent flip of the local <c>IsSyncing</c> flag).</summary>
    [Fact]
    public async Task SyncNowAsync_CalledWhileAnotherAttemptIsInFlight_ReturnsSkippedAlreadyRunning()
    {
        _taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _handler.Enqueue(_ => gate.Task.GetAwaiter().GetResult());
        EnqueueEmptyPulls();

        // Task.Run, not a bare call: SyncNowAsync runs synchronously up to its first genuine suspension
        // point, and the gate's blocking GetResult() below sits before that point (FakeHttpMessageHandler
        // dequeues it synchronously) — calling it directly on this thread would deadlock the test itself,
        // never reaching the "second attempt" call below at all.
        var first = Task.Run(async () => await _sync.SyncNowAsync());
        while (_handler.Requests.Count == 0) await Task.Delay(5); // wait until the first attempt is actually in flight

        var secondOutcome = await _sync.SyncNowAsync();
        Assert.Equal(PolarSyncOutcome.SkippedAlreadyRunning, secondOutcome);

        gate.SetResult(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));
        var firstOutcome = await first;
        Assert.Equal(PolarSyncOutcome.Completed, firstOutcome);
    }

    [Fact]
    public async Task SyncNowAsync_NotLinked_ReturnsNotLinkedOutcome()
    {
        _settingsService.Settings.PolarLinked = false;
        _settingsService.Save();

        var outcome = await _sync.SyncNowAsync();

        Assert.Equal(PolarSyncOutcome.NotLinked, outcome);
    }

    [Fact]
    public async Task SyncNowAsync_LocalEditArrivesWhileListPushInFlight_KeepsDirtySoTheNewerEditSyncsNextCycle()
    {
        var list = _taskService.CreateList("Título v1", "ic_list", "#000", isDependencyChain: false);
        var sentUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc);

        _handler.Enqueue(_ =>
        {
            // Simulate the user editing the list again while this push is still in flight — the response
            // below echoes back exactly what was sent (as if there was no LWW conflict at all), but by the
            // time it arrives locally the row has already moved on, so it must not be treated as synced.
            list.Title = "Título v2";
            list.UpdatedAtUtc = list.UpdatedAtUtc.AddSeconds(5);
            list.Dirty = true;
            return JsonResponse(HttpStatusCode.OK, new[]
            {
                new TaskListDto { Id = list.Id, Title = "Título v1", UpdatedAt = sentUpdatedAt, CreatedAt = sentUpdatedAt }
            });
        });
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        var local = _taskService.Store.Lists.Single(l => l.Id == list.Id);
        Assert.Equal("Título v2", local.Title);
        Assert.True(local.Dirty);
    }

    [Fact]
    public async Task SyncNowAsync_CompletingAnOverdueRecurringTask_PushesCompletedBeforeRollingItForwardLocally()
    {
        // A daily task due yesterday, marked done today — the normal case for a recurring task, since by
        // the time someone checks it off it's usually already at or past its due date.
        var list = _taskService.CreateList("Rutina", "ic_list", "#000", isDependencyChain: false);
        var pastDue = DateTime.UtcNow.AddDays(-1);
        var task = _taskService.CreateTask(list, "Diaria", "", 0, pastDue, PolarRecurrence.Daily, 0, []);
        _taskService.SetTaskCompleted(task, true);

        var echoedListUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc);
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] // push task_lists
        {
            new TaskListDto { Id = list.Id, Title = list.Title, UpdatedAt = echoedListUpdatedAt, CreatedAt = echoedListUpdatedAt }
        }));

        TaskDto? pushedTaskDto = null;
        _handler.Enqueue(request => // push tasks — capture exactly what was sent to Supabase
        {
            var body = request.Content!.ReadAsStringAsync().Result;
            var dtos = JsonSerializer.Deserialize<List<TaskDto>>(body)!;
            pushedTaskDto = dtos.Single(d => d.Id == task.Id);
            return JsonResponse(HttpStatusCode.OK, dtos);
        });
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        Assert.NotNull(pushedTaskDto);
        Assert.True(pushedTaskDto!.Completed);

        // Locally, the task has since rolled forward to its next (incomplete) occurrence — but that must
        // only happen after the "completed=true" push above already reached Supabase.
        var rolled = _taskService.Store.Tasks.Single(t => t.Id == task.Id);
        Assert.False(rolled.Completed);
        Assert.True(rolled.DueDateUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task SyncNowAsync_ServerRejectsWithUnauthorized_SetsLocalizedErrorAndLeavesDataDirty()
    {
        var list = _taskService.CreateList("Trabajo", "ic_list", "#000", isDependencyChain: false);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await _sync.SyncNowAsync();

        Assert.Equal(LocalizationService.Instance["Polar_Sync_Error_Unauthorized"], _sync.LastError);
        Assert.False(_sync.LastErrorWasNetwork);
        Assert.Equal(PolarSyncStatus.Error, _sync.Status);
        Assert.True(_taskService.Store.Lists.Single(l => l.Id == list.Id).Dirty);
    }

    [Fact]
    public async Task SyncNowAsync_NoConnection_ReportsOfflineStatusForBackoff()
    {
        _taskService.CreateList("Trabajo", "ic_list", "#000", isDependencyChain: false);
        _handler.Enqueue(_ => throw new HttpRequestException("No route to host"));

        await _sync.SyncNowAsync();

        Assert.True(_sync.LastErrorWasNetwork);
        Assert.Equal(PolarSyncStatus.Offline, _sync.Status);
        Assert.Equal(LocalizationService.Instance["Polar_Sync_Error_NoConnection"], _sync.LastError);
    }

    [Fact]
    public void Status_NotLinked_ReportsNotLinkedWithoutAttemptingASync()
    {
        _settingsService.Settings.PolarLinked = false;
        _settingsService.Save();

        Assert.Equal(PolarSyncStatus.NotLinked, _sync.Status);
    }

    // ===================== Aislamiento de cuentas — UserId (Fase 4) =====================
    // docs/analisis-proyecto/roadmap.md, Fase 4: filas Dirty no sincronizadas de una cuenta Polar no deben
    // empujarse jamás bajo la sesión de una cuenta distinta vinculada después en el mismo dispositivo.

    /// <summary>Reproduce el escenario completo del hallazgo #5 del análisis: datos creados/editados
    /// offline bajo la cuenta A, nunca sincronizados, luego desvinculados y resincronizados bajo una cuenta
    /// B distinta en el mismo dispositivo. Antes de esta fase, <c>PushTaskListsAsync</c> enviaba toda fila
    /// <c>Dirty</c> con <c>UserId = cuenta actualmente vinculada</c>, sin importar bajo qué sesión se creó
    /// — fuga real de datos entre cuentas Supabase distintas.</summary>
    [Fact]
    public async Task SyncNowAsync_DirtyDataOwnedByAPreviousAccount_NeverPushesUnderTheNewlyLinkedAccount()
    {
        var list = _taskService.CreateList("Lista de la cuenta A", "ic_list", "#000", isDependencyChain: false);
        list.UserId = "user-a"; // estampado mientras la cuenta A estaba vinculada
        _taskService.Save();

        // Se desvincula la cuenta A y se vincula una cuenta B distinta en el mismo dispositivo — el
        // constructor de esta clase ya deja la sesión vinculada como "user-1"; la reemplazamos por "user-b".
        _settingsService.Settings.PolarUserId = "user-b";
        _settingsService.Save();

        // Solo deberían dispararse los 4 pulls — ningún push, porque no hay ninguna fila Dirty que
        // pertenezca a "user-b" (la única fila Dirty pertenece a "user-a").
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        Assert.Null(_sync.LastError);
        var stillLocal = _taskService.Store.Lists.Single(l => l.Id == list.Id);
        Assert.True(stillLocal.Dirty);
        Assert.Equal("user-a", stillLocal.UserId);
        Assert.Equal(4, _handler.Requests.Count);
        Assert.DoesNotContain(_handler.Requests, r => r.RequestUri!.ToString().Contains("on_conflict"));
        Assert.DoesNotContain(_handler.RequestBodies, body => body is not null && body.Contains(list.Id));
    }

    [Fact]
    public async Task SyncNowAsync_DirtyDataOwnedByTheSameLinkedAccount_PushesNormally()
    {
        var list = _taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        list.UserId = "user-1"; // misma cuenta que ya vincula el constructor de esta clase
        _taskService.Save();
        var echoedUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc);

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = list.Id, Title = list.Title, UpdatedAt = echoedUpdatedAt, CreatedAt = echoedUpdatedAt }
        }));
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        Assert.False(_taskService.Store.Lists.Single(l => l.Id == list.Id).Dirty);
    }

    /// <summary>Una fila sin propietario (creada antes de vincular ninguna cuenta, o que predata el campo
    /// <c>UserId</c>) es libre de reclamarse para la primera cuenta que la sincroniza — a diferencia de una
    /// fila ya marcada con una cuenta distinta, aquí no hay ninguna cuenta a la que proteger.</summary>
    [Fact]
    public async Task SyncNowAsync_UnownedDirtyData_GetsClaimedForTheLinkedAccountAndPushed()
    {
        var list = _taskService.CreateList("Lista creada antes de vincular", "ic_list", "#000", isDependencyChain: false);
        Assert.Null(list.UserId);
        var echoedUpdatedAt = PolarWire.ToEpochMs(list.UpdatedAtUtc);

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = list.Id, Title = list.Title, UpdatedAt = echoedUpdatedAt, CreatedAt = echoedUpdatedAt }
        }));
        EnqueueEmptyPulls();

        await _sync.SyncNowAsync();

        var local = _taskService.Store.Lists.Single(l => l.Id == list.Id);
        Assert.False(local.Dirty);
        Assert.Equal("user-1", local.UserId);
    }

    /// <summary>Una fila recibida por pull pertenece, por definición, a la cuenta vinculada (RLS de
    /// Supabase solo deja ver las propias) — estamparla también sana en el momento cualquier fila local
    /// heredada de antes de este campo.</summary>
    [Fact]
    public async Task SyncNowAsync_PullingANewRemoteList_StampsItWithTheLinkedAccount()
    {
        var remoteId = Guid.NewGuid().ToString();
        var now = PolarWire.ToEpochMs(DateTime.UtcNow);
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[]
        {
            new TaskListDto { Id = remoteId, Title = "Remota", CreatedAt = now, UpdatedAt = now }
        }));
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskDto>()));
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<SubtaskDto>()));
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<ReminderDto>()));

        await _sync.SyncNowAsync();

        Assert.Equal("user-1", _taskService.Store.Lists.Single(l => l.Id == remoteId).UserId);
    }

    private void EnqueueEmptyPulls()
    {
        for (var i = 0; i < 4; i++) _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };
}
