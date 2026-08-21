using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using enki.Services;
using enki.Tests.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the "Sync now" button in <see cref="PolarTaskManagementViewModel"/> against a scripted
/// fake Supabase transport — same infrastructure as <c>PolarSyncServiceContractTests</c>. Fase 9
/// (docs/analisis-proyecto/roadmap.md): a manual sync click that lands while another attempt (the
/// background timer, the realtime debounce, or another view's own "Sync now") already holds the gate must
/// tell the user so, instead of silently doing nothing and leaving the button's own <c>IsSyncing</c> flag as
/// the only — misleadingly normal-looking — evidence anything happened at all.</summary>
public class PolarTaskManagementViewModelTests : IDisposable
{
    private const string BaseUrl = "https://fake.supabase.test";
    private const string AnonKey = "anon-key-123";

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (PolarTaskManagementViewModel Vm, PolarSyncService Sync, PolarTaskService TaskService) CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.PolarLinked = true;
        settingsService.Settings.PolarUserId = "user-1";
        settingsService.Settings.PolarAccessToken = "test-access-token";
        settingsService.Save();

        var authService = new PolarAuthService(settingsService, _handler, BaseUrl, AnonKey);
        var restClient = new SupabaseRestClient(authService, _handler, BaseUrl, AnonKey);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var sync = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var vm = new PolarTaskManagementViewModel(taskService, sync, settingsService);
        return (vm, sync, taskService);
    }

    [Fact]
    public async Task SyncNowCommand_WhileAnotherAttemptIsInFlight_SetsLastSyncNowWasSkipped()
    {
        var (vm, sync, taskService) = CreateViewModel();
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);

        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _handler.Enqueue(_ => gate.Task.GetAwaiter().GetResult());
        for (var i = 0; i < 4; i++) _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));

        // Task.Run, not a bare call: SyncNowAsync runs synchronously up to its first genuine suspension
        // point, and the gate's blocking GetResult() below sits before that point — calling it directly on
        // this thread would deadlock the test itself before ever reaching the assertions below.
        var first = Task.Run(async () => await sync.SyncNowAsync()); // stands in for the background timer/realtime debounce holding the gate
        while (_handler.Requests.Count == 0) await Task.Delay(5);

        await vm.SyncNowCommand.ExecuteAsync(null);

        Assert.True(vm.LastSyncNowWasSkipped);
        Assert.False(vm.IsSyncing);

        gate.SetResult(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));
        await first;
    }

    [Fact]
    public async Task SyncNowCommand_WhenItCompletes_ClearsLastSyncNowWasSkipped()
    {
        var (vm, sync, taskService) = CreateViewModel();
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);

        // First: force a skip, same as the test above.
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        _handler.Enqueue(_ => gate.Task.GetAwaiter().GetResult());
        for (var i = 0; i < 4; i++) _handler.Enqueue(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));

        var first = Task.Run(async () => await sync.SyncNowAsync());
        while (_handler.Requests.Count == 0) await Task.Delay(5);
        await vm.SyncNowCommand.ExecuteAsync(null);
        Assert.True(vm.LastSyncNowWasSkipped);

        gate.SetResult(JsonResponse(HttpStatusCode.OK, Array.Empty<TaskListDto>()));
        await first; // this completed cycle's SyncCompleted event should clear the notice

        Assert.False(vm.LastSyncNowWasSkipped);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T payload) => new(status)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
    };
}
