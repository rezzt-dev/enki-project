using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using enki.Models;
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

    private (PolarTaskManagementViewModel Vm, PolarSyncService Sync, PolarTaskService TaskService) CreateViewModel(
        bool confirmBeforeDelete = true)
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.PolarLinked = true;
        settingsService.Settings.PolarUserId = "user-1";
        settingsService.Settings.PolarAccessToken = "test-access-token";
        settingsService.Settings.ConfirmBeforeDelete = confirmBeforeDelete;
        settingsService.Save();

        var authService = new PolarAuthService(settingsService, _handler, BaseUrl, AnonKey);
        var restClient = new SupabaseRestClient(authService, _handler, BaseUrl, AnonKey);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var sync = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var vm = new PolarTaskManagementViewModel(taskService, sync, settingsService);
        return (vm, sync, taskService);
    }

    // ===================== Listas: crear/editar/borrar =====================

    [Fact]
    public void SaveListEditor_InCreateMode_AddsListAndSelectsIt()
    {
        var (vm, _, _) = CreateViewModel();

        vm.OpenCreateListCommand.Execute(null);
        Assert.NotNull(vm.ListEditor);
        vm.ListEditor!.Title = "Compras";
        vm.SaveListEditorCommand.Execute(null);

        Assert.Null(vm.ListEditor);
        var created = Assert.Single(vm.Lists);
        Assert.Equal("Compras", created.Title);
        Assert.Same(created, vm.SelectedList);
    }

    [Fact]
    public void SaveListEditor_WithBlankTitle_DoesNothing()
    {
        var (vm, _, _) = CreateViewModel();

        vm.OpenCreateListCommand.Execute(null);
        vm.ListEditor!.Title = "   ";
        Assert.False(vm.ListEditor.CanSave);
        vm.SaveListEditorCommand.Execute(null);

        Assert.NotNull(vm.ListEditor);
        Assert.Empty(vm.Lists);
    }

    [Fact]
    public void SaveListEditor_InEditMode_RenamesExistingList()
    {
        var (vm, _, taskService) = CreateViewModel();
        taskService.CreateList("Original", "ic_list", "#000", isDependencyChain: false);
        vm.Reload();
        var listItem = vm.Lists.Single();

        vm.OpenEditListCommand.Execute(listItem);
        vm.ListEditor!.Title = "Renombrada";
        vm.SaveListEditorCommand.Execute(null);

        Assert.Equal("Renombrada", vm.Lists.Single().Title);
    }

    [Fact]
    public void RequestDeleteList_WithConfirmBeforeDeleteEnabled_AsksBeforeRemoving()
    {
        var (vm, _, taskService) = CreateViewModel(confirmBeforeDelete: true);
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        vm.Reload();
        var listItem = vm.Lists.Single();

        vm.RequestDeleteListCommand.Execute(listItem);
        Assert.Same(listItem, vm.ListPendingDelete);
        Assert.Single(vm.Lists); // not removed yet

        vm.ConfirmDeleteListCommand.Execute(null);
        Assert.Null(vm.ListPendingDelete);
        Assert.Empty(vm.Lists);
    }

    [Fact]
    public void RequestDeleteList_WithConfirmBeforeDeleteDisabled_RemovesImmediately()
    {
        var (vm, _, taskService) = CreateViewModel(confirmBeforeDelete: false);
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        vm.Reload();
        var listItem = vm.Lists.Single();

        vm.RequestDeleteListCommand.Execute(listItem);

        Assert.Null(vm.ListPendingDelete);
        Assert.Empty(vm.Lists);
    }

    [Fact]
    public void CancelDeleteList_ClearsPendingWithoutRemoving()
    {
        var (vm, _, taskService) = CreateViewModel(confirmBeforeDelete: true);
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        vm.Reload();
        var listItem = vm.Lists.Single();

        vm.RequestDeleteListCommand.Execute(listItem);
        vm.CancelDeleteListCommand.Execute(null);

        Assert.Null(vm.ListPendingDelete);
        Assert.Single(vm.Lists);
    }

    // ===================== Tareas: crear/editar/borrar/completar =====================

    [Fact]
    public void SaveTaskEditor_InCreateMode_AddsTaskToSelectedList()
    {
        var (vm, _, taskService) = CreateViewModel();
        taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        vm.Reload();

        vm.OpenCreateTaskCommand.Execute(null);
        Assert.NotNull(vm.TaskEditor);
        vm.TaskEditor!.Title = "Nueva tarea";
        vm.SaveTaskEditorCommand.Execute(null);

        Assert.Null(vm.TaskEditor);
        var row = Assert.Single(vm.PendingTaskRows);
        Assert.Equal("Nueva tarea", row.Task.Title);
    }

    [Fact]
    public void SaveTaskEditor_InEditMode_UpdatesTaskTitle()
    {
        var (vm, _, taskService) = CreateViewModel();
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Original", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();
        var row = vm.PendingTaskRows.Single();

        vm.OpenEditTaskCommand.Execute(row);
        vm.TaskEditor!.Title = "Actualizada";
        vm.SaveTaskEditorCommand.Execute(null);

        Assert.Equal("Actualizada", vm.PendingTaskRows.Single().Task.Title);
    }

    [Fact]
    public void ToggleTaskCompleted_MovesTaskFromPendingToCompletedRows()
    {
        var (vm, _, taskService) = CreateViewModel();
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();
        var row = vm.PendingTaskRows.Single();

        vm.ToggleTaskCompletedCommand.Execute(row);

        Assert.Empty(vm.PendingTaskRows);
        Assert.Single(vm.CompletedTaskRows);
        Assert.True(vm.HasCompletedTasks);
    }

    [Fact]
    public void RequestDeleteTask_WithConfirmBeforeDeleteEnabled_AsksBeforeRemoving()
    {
        var (vm, _, taskService) = CreateViewModel(confirmBeforeDelete: true);
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();
        var row = vm.PendingTaskRows.Single();

        vm.RequestDeleteTaskCommand.Execute(row);
        Assert.Same(row, vm.TaskPendingDelete);
        Assert.Single(vm.PendingTaskRows);

        vm.ConfirmDeleteTaskCommand.Execute(null);
        Assert.Null(vm.TaskPendingDelete);
        Assert.Empty(vm.PendingTaskRows);
    }

    [Fact]
    public void RequestDeleteTask_WithConfirmBeforeDeleteDisabled_RemovesImmediately()
    {
        var (vm, _, taskService) = CreateViewModel(confirmBeforeDelete: false);
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Tarea", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();
        var row = vm.PendingTaskRows.Single();

        vm.RequestDeleteTaskCommand.Execute(row);

        Assert.Null(vm.TaskPendingDelete);
        Assert.Empty(vm.PendingTaskRows);
    }

    // ===================== Selección de lista =====================

    [Fact]
    public void SelectList_ScopesTaskRowsToThatListOnly()
    {
        var (vm, _, taskService) = CreateViewModel();
        var listA = taskService.CreateList("A", "ic_list", "#000", isDependencyChain: false);
        var listB = taskService.CreateList("B", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(listA, "Tarea A", "", 0, null, PolarRecurrence.None, 0, []);
        taskService.CreateTask(listB, "Tarea B", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();

        var itemB = vm.Lists.Single(l => l.Id == listB.Id);
        vm.SelectListCommand.Execute(itemB);

        Assert.Same(itemB, vm.SelectedList);
        var row = Assert.Single(vm.PendingTaskRows);
        Assert.Equal("Tarea B", row.Task.Title);
    }

    // ===================== Filtro de etiquetas =====================

    [Fact]
    public void TagFilter_SelectingTag_HidesTasksWithoutIt()
    {
        var (vm, _, taskService) = CreateViewModel();
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Con etiqueta", "", 0, null, PolarRecurrence.None, 0, ["urgente"]);
        taskService.CreateTask(list, "Sin etiqueta", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();

        var tagItem = vm.TagFilter.Items.Single(i => i.Tag == "urgente");
        tagItem.ToggleCommand.Execute(null);

        var tagged = vm.PendingTaskRows.Single(r => r.Task.Title == "Con etiqueta");
        var untagged = vm.PendingTaskRows.Single(r => r.Task.Title == "Sin etiqueta");
        Assert.True(tagged.IsVisible);
        Assert.False(untagged.IsVisible);
    }

    [Fact]
    public void TagFilter_NoTagsSelected_ShowsEveryTask()
    {
        var (vm, _, taskService) = CreateViewModel();
        var list = taskService.CreateList("Lista", "ic_list", "#000", isDependencyChain: false);
        taskService.CreateTask(list, "Con etiqueta", "", 0, null, PolarRecurrence.None, 0, ["urgente"]);
        taskService.CreateTask(list, "Sin etiqueta", "", 0, null, PolarRecurrence.None, 0, []);
        vm.Reload();

        Assert.All(vm.PendingTaskRows, r => Assert.True(r.IsVisible));
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
