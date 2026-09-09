using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>"Inicio" shows every list's pending tasks in one feed (see
/// <see cref="PolarTaskManagementViewModel.HomeGroups"/>); "Listas" is the original sidebar-driven,
/// one-list-at-a-time layout.</summary>
public enum PolarTaskManagementViewMode
{
    Home,
    Lists
}

/// <summary>The "Gestión de tareas" view inside Kanban (see <see cref="KanbanViewModel.DisplayMode"/>) —
/// a Polar-compatible lists → tasks → subtasks hierarchy, entirely separate data domain from the local
/// Kanban board. Only reachable once <c>PolarAuthService.IsLinked</c> is true.</summary>
public sealed partial class PolarTaskManagementViewModel : ViewModelBase
{
    private readonly PolarTaskService _taskService;
    private readonly PolarSyncService _syncService;
    private readonly SettingsService? _settingsService;

    [ObservableProperty]
    private PolarTaskManagementViewMode _viewMode = PolarTaskManagementViewMode.Home;

    /// <summary>One section per list with at least one pending task, ordered by
    /// <see cref="PolarTaskList.HomeOrderIndex"/> — the "Inicio" feed (see <see cref="ViewMode"/>).
    /// Rebuilt alongside <see cref="PendingTaskRows"/>/<see cref="CompletedTaskRows"/> by
    /// <see cref="ReloadTasks"/> so any task edit, completion or sync pull keeps both views in sync.</summary>
    public ObservableCollection<PolarHomeGroupViewModel> HomeGroups { get; } = [];

    [ObservableProperty]
    private bool _isHomeEmpty;

    public ObservableCollection<PolarTaskListItemViewModel> Lists { get; } = [];

    /// <summary>Split into pending/completed (instead of one flat, OrderIndex-only list) so the panel
    /// reads like an actual task manager: what's left to do stays front and center, and finished tasks
    /// collapse out of the way behind <see cref="IsCompletedExpanded"/> instead of cluttering the list.
    /// Named *Rows rather than PendingTasks/CompletedTasks to avoid colliding with the int stat property
    /// <see cref="PendingTasks"/> shown in the stats card below.</summary>
    public ObservableCollection<PolarTaskRowViewModel> PendingTaskRows { get; } = [];
    public ObservableCollection<PolarTaskRowViewModel> CompletedTaskRows { get; } = [];

    /// <summary>"COMPLETED ({0})" section header — a computed property instead of a XAML
    /// <c>StringFormat</c> bound to <c>CompletedTaskRows.Count</c> (docs/agent-docs/
    /// internacionalizacion-i18n.md §1.3). Re-raised manually in <see cref="ReloadTasks"/> since
    /// <see cref="ObservableCollection{T}.Count"/> changing doesn't raise this view model's own
    /// PropertyChanged on its own.</summary>
    public string CompletedSectionHeaderLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Kanban_TaskMgmt_CompletedSectionHeader"], CompletedTaskRows.Count);

    [ObservableProperty]
    private PolarTaskListItemViewModel? _selectedList;

    [ObservableProperty]
    private bool _isListsEmpty;

    [ObservableProperty]
    private bool _isTasksEmpty;

    [ObservableProperty]
    private bool _hasCompletedTasks;

    [ObservableProperty]
    private bool _isCompletedExpanded;

    [ObservableProperty]
    private PolarTaskDetailViewModel? _taskDetail;

    [ObservableProperty]
    private PolarListEditorViewModel? _listEditor;

    [ObservableProperty]
    private PolarTaskEditorViewModel? _taskEditor;

    [ObservableProperty]
    private PolarTaskListItemViewModel? _listPendingDelete;

    [ObservableProperty]
    private PolarTaskRowViewModel? _taskPendingDelete;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string? _syncError;

    /// <summary>Composed display text for <see cref="SyncError"/> — kept as a computed property (rather
    /// than a XAML <c>StringFormat</c>) so the "Couldn't sync: {0}" word order can be adapted per locale
    /// instead of being fixed in the view (docs/agent-docs/internacionalizacion-i18n.md §1.3).</summary>
    public string SyncErrorDisplayLabel => SyncError is { } error
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Kanban_TaskMgmt_SyncErrorLabel"], error)
        : string.Empty;

    partial void OnSyncErrorChanged(string? value) => OnPropertyChanged(nameof(SyncErrorDisplayLabel));

    [ObservableProperty]
    private string _lastSyncedLabel = LocalizationService.Instance["Common_Never"];

    /// <summary>Same reasoning as <see cref="SyncErrorDisplayLabel"/> — composes "Last synced: {0}"
    /// outside the view instead of a fixed-word-order XAML <c>StringFormat</c>.</summary>
    public string LastSyncedDisplayLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Kanban_TaskMgmt_LastSyncedLabel"], LastSyncedLabel);

    partial void OnLastSyncedLabelChanged(string value) => OnPropertyChanged(nameof(LastSyncedDisplayLabel));

    /// <summary>Non-null only while <see cref="PolarSyncService.Status"/> is
    /// <see cref="PolarSyncStatus.Offline"/> and a retry is actually scheduled — "Retrying in 2 minutes",
    /// so the user knows the app isn't just stuck (10.2 §8: "mostrar tiempo hasta el próximo reintento").</summary>
    [ObservableProperty]
    private string? _nextRetryLabel;

    /// <summary>True right after this view's own <see cref="SyncNow"/> click was discarded because another
    /// sync (the background timer, the realtime debounce, or another view's own "Sync now") already held
    /// the gate — distinct from a completed cycle that simply changed nothing (Fase 9,
    /// docs/analisis-proyecto/roadmap.md). Cleared the next time any sync cycle actually completes (see
    /// <see cref="HandleSyncCompleted"/>), so the notice never lingers once whatever caused the skip has
    /// resolved.</summary>
    [ObservableProperty]
    private bool _lastSyncNowWasSkipped;

    [ObservableProperty]
    private int _totalLists;

    [ObservableProperty]
    private int _totalTasks;

    [ObservableProperty]
    private int _pendingTasks;

    [ObservableProperty]
    private int _overdueTasks;

    /// <summary>Filtro de etiquetas compartido para la Gestión de tareas de Polar. En vista Listas se
    /// recarga con las etiquetas de la lista seleccionada; en vista Inicio se recarga con las etiquetas
    /// de todas las listas.</summary>
    public TagFilterViewModel TagFilter { get; } = new();

    /// <summary>Ajustes › Cuenta › "Mostrar etiquetas" (<see cref="AppSettings.PolarShowTaskTags"/>).
    /// Set from <c>MainWindowViewModel</c> at construction and live-updated via its settings callback;
    /// defaults to false (hidden) matching <see cref="AppSettings.PolarShowTaskTags"/>'s default.</summary>
    [ObservableProperty]
    private bool _showTagFilter;

    public PolarTaskManagementViewModel(PolarTaskService taskService, PolarSyncService syncService, SettingsService? settingsService = null)
    {
        _taskService = taskService;
        _syncService = syncService;
        _settingsService = settingsService;
        _syncService.SyncCompleted += HandleSyncCompleted;
        TagFilter.Changed += ApplyTagFilter;

        RefreshSyncStatus();
        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(CompletedSectionHeaderLabel));
        OnPropertyChanged(nameof(SyncErrorDisplayLabel));
        OnPropertyChanged(nameof(LastSyncedDisplayLabel));
        RefreshSyncStatus();
        Reload();
        // Reload() rehace listas y filas, pero no los overlays abiertos: se refrescan en el sitio para no
        // descartar lo que el usuario tuviera a medio escribir.
        TaskEditor?.RefreshLocalizedLabels();
        ListEditor?.RefreshLocalizedLabels();
        TaskDetail?.RefreshLocalizedLabels();
    }

    public void Reload()
    {
        var previousSelectedId = SelectedList?.Id;

        Lists.Clear();
        foreach (var list in _taskService.Store.Lists.Where(l => !l.IsDeleted).OrderBy(l => l.OrderIndex))
        {
            Lists.Add(new PolarTaskListItemViewModel(list)
            {
                TaskCount = _taskService.Store.Tasks.Count(t => t.ListId == list.Id && !t.IsDeleted),
                PendingCount = _taskService.Store.Tasks.Count(t => t.ListId == list.Id && !t.IsDeleted && !t.Completed)
            });
        }

        IsListsEmpty = Lists.Count == 0;

        var reselect = previousSelectedId is not null ? Lists.FirstOrDefault(l => l.Id == previousSelectedId) : null;
        SelectList(reselect ?? Lists.FirstOrDefault());
    }

    private void RefreshStats()
    {
        var activeTasks = _taskService.Store.Tasks.Where(t => !t.IsDeleted).ToList();

        TotalLists = Lists.Count;
        TotalTasks = activeTasks.Count;
        PendingTasks = activeTasks.Count(t => !t.Completed);
        OverdueTasks = activeTasks.Count(t => !t.Completed && t.DueDateUtc is { } due && KanbanCard.IsPastDue(due));
    }

    private void HandleSyncCompleted(bool tasksChanged, bool remindersChanged)
    {
        RefreshSyncStatus();
        // A real sync cycle just ran to completion — from anywhere (the background timer, the realtime
        // debounce, this view's own button, or another view's) — so whatever caused an earlier skip has
        // resolved by now.
        LastSyncNowWasSkipped = false;
        if (tasksChanged) Reload();
    }

    private void RefreshSyncStatus()
    {
        SyncError = _syncService.LastError;
        LastSyncedLabel = _syncService.LastSyncedUtc is { } utc
            ? RelativeTimeFormatter.FormatAbsoluteShort(utc)
            : LocalizationService.Instance["Common_Never"];
        NextRetryLabel = _syncService.Status == PolarSyncStatus.Offline
            && _syncService.NextSyncAttemptUtc is { } next
                ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Sync_NextRetryLabel"], RelativeTimeFormatter.FormatUntil(next, DateTime.UtcNow))
                : null;
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        if (!Enum.TryParse<PolarTaskManagementViewMode>(mode, out var parsed)) return;
        ViewMode = parsed;
        ReloadTasks();
    }

    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        var outcome = await _syncService.SyncNowAsync();
        IsSyncing = false;
        // A completed cycle clears this via HandleSyncCompleted (wired to PolarSyncService.SyncCompleted),
        // which also refreshes status and reloads if the sync actually changed something.
        LastSyncNowWasSkipped = outcome == PolarSyncOutcome.SkippedAlreadyRunning;
    }

    // ===================== Selección de lista =====================

    [RelayCommand]
    private void SelectList(PolarTaskListItemViewModel? list)
    {
        foreach (var item in Lists) item.IsSelected = ReferenceEquals(item, list);
        SelectedList = list;
        ReloadTasks();
    }

    private void ReloadTasks()
    {
        PendingTaskRows.Clear();
        CompletedTaskRows.Clear();
        if (SelectedList is { } list)
        {
            foreach (var task in _taskService.Store.Tasks
                         .Where(t => t.ListId == list.Id && !t.IsDeleted)
                         .OrderBy(t => t.OrderIndex))
            {
                var row = new PolarTaskRowViewModel(_taskService, task);
                (task.Completed ? CompletedTaskRows : PendingTaskRows).Add(row);
            }
        }

        IsTasksEmpty = PendingTaskRows.Count == 0 && CompletedTaskRows.Count == 0;
        HasCompletedTasks = CompletedTaskRows.Count > 0;
        if (!HasCompletedTasks) IsCompletedExpanded = false;
        OnPropertyChanged(nameof(CompletedSectionHeaderLabel));
        RefreshStats();

        var allTags = ViewMode == PolarTaskManagementViewMode.Home
            ? _taskService.Store.Tasks
                .Where(t => !t.IsDeleted)
                .SelectMany(t => t.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : SelectedList is { } selected
                ? _taskService.Store.Tasks
                    .Where(t => t.ListId == selected.Id && !t.IsDeleted)
                    .SelectMany(t => t.Tags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();
        TagFilter.Reload(allTags);
        ApplyTagFilter();
        ReloadHome();
    }

    /// <summary>Aplica el filtro de etiquetas actual a todas las filas visibles: tareas pendientes,
    /// completadas y las tareas de los grupos de Inicio. Oculta visualmente sin mutar las colecciones.</summary>
    private void ApplyTagFilter()
    {
        var selectedTags = TagFilter.SelectedTags.ToList();
        foreach (var row in PendingTaskRows) row.IsVisible = row.MatchesTags(selectedTags);
        foreach (var row in CompletedTaskRows) row.IsVisible = row.MatchesTags(selectedTags);
        foreach (var row in HomeGroups.SelectMany(g => g.Tasks)) row.IsVisible = row.MatchesTags(selectedTags);
    }

    /// <summary>Rebuilds <see cref="HomeGroups"/> from every non-deleted list's pending tasks, skipping
    /// lists with none so the feed only ever shows what's actually left to do — matching the "hide fully
    /// done sections" behavior the "Listas" view already gives per-list via its completed-tasks
    /// collapse.</summary>
    private void ReloadHome()
    {
        HomeGroups.Clear();
        foreach (var list in _taskService.Store.Lists
                     .Where(l => !l.IsDeleted)
                     .OrderBy(l => l.HomeOrderIndex)
                     .ThenBy(l => l.OrderIndex))
        {
            var pendingTasks = _taskService.Store.Tasks
                .Where(t => t.ListId == list.Id && !t.IsDeleted && !t.Completed)
                .OrderBy(t => t.OrderIndex)
                .Select(t => new PolarTaskRowViewModel(_taskService, t));

            var group = new PolarHomeGroupViewModel(list, pendingTasks);
            if (group.Tasks.Count > 0) HomeGroups.Add(group);
        }

        if (ViewMode == PolarTaskManagementViewMode.Home)
        {
            var allTags = _taskService.Store.Tasks
                .Where(t => !t.IsDeleted)
                .SelectMany(t => t.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            TagFilter.Reload(allTags);
        }

        ApplyTagFilter();
        IsHomeEmpty = HomeGroups.Count == 0;
    }

    [RelayCommand]
    private void ToggleCompletedSection() => IsCompletedExpanded = !IsCompletedExpanded;

    // ===================== Editor de listas =====================

    [RelayCommand]
    private void OpenCreateList() => ListEditor = PolarListEditorViewModel.ForCreate();

    [RelayCommand]
    private void OpenEditList(PolarTaskListItemViewModel list) => ListEditor = PolarListEditorViewModel.ForEdit(list.List);

    [RelayCommand]
    private void CancelListEditor() => ListEditor = null;

    [RelayCommand]
    private void SaveListEditor()
    {
        if (ListEditor is not { CanSave: true } editor) return;

        if (editor.IsCreateMode)
        {
            var created = _taskService.CreateList(editor.Title.Trim(), editor.SelectedIcon, "#7F52FF", editor.IsDependencyChain);
            ListEditor = null;
            Reload();
            SelectList(Lists.FirstOrDefault(l => l.Id == created.Id));
        }
        else if (editor.SourceList is { } sourceList)
        {
            _taskService.UpdateList(sourceList, editor.Title.Trim(), editor.SelectedIcon, sourceList.Color, editor.IsDependencyChain);
            ListEditor = null;
            Reload();
        }
    }

    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de borrar" salta
    // directo al borrado cuando está desactivado.
    [RelayCommand]
    private void RequestDeleteList(PolarTaskListItemViewModel list)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) ListPendingDelete = list;
        else DeleteList(list);
    }

    [RelayCommand]
    private void CancelDeleteList() => ListPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteList()
    {
        if (ListPendingDelete is not { } list) return;
        DeleteList(list);
        ListPendingDelete = null;
    }

    private void DeleteList(PolarTaskListItemViewModel list)
    {
        _taskService.DeleteList(list.List);
        Reload();
    }

    // ===================== Editor de tareas =====================

    [RelayCommand]
    private void OpenCreateTask()
    {
        if (SelectedList is { } list) TaskEditor = PolarTaskEditorViewModel.ForCreate(list.List);
    }

    [RelayCommand]
    private void OpenEditTask(PolarTaskRowViewModel task)
    {
        if (FindList(task.Task.ListId) is { } list) TaskEditor = PolarTaskEditorViewModel.ForEdit(list, task.Task, _taskService);
    }

    [RelayCommand]
    private void CancelTaskEditor() => TaskEditor = null;

    [RelayCommand]
    private void SaveTaskEditor()
    {
        if (TaskEditor is not { CanSave: true } editor) return;

        if (editor.IsCreateMode)
        {
            _taskService.CreateTask(
                editor.TargetList, editor.Title.Trim(), editor.Description.Trim(), editor.Priority,
                editor.BuildDueDateUtc(), editor.Recurrence, editor.TimeEstimate, editor.BuildTags());
        }
        else if (editor.SourceTask is { } sourceTask)
        {
            _taskService.UpdateTask(
                sourceTask, editor.Title.Trim(), editor.Description.Trim(), editor.Priority,
                editor.BuildDueDateUtc(), editor.Recurrence, editor.TimeEstimate, editor.BuildTags());
        }

        TaskEditor = null;
        ReloadTasks();
    }

    [RelayCommand]
    private void ToggleTaskCompleted(PolarTaskRowViewModel task)
    {
        task.ToggleCompleted();
        ReloadTasks();
    }

    [RelayCommand]
    private void RequestDeleteTask(PolarTaskRowViewModel task)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) TaskPendingDelete = task;
        else DeleteTask(task);
    }

    [RelayCommand]
    private void CancelDeleteTask() => TaskPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteTask()
    {
        if (TaskPendingDelete is not { } task) return;
        DeleteTask(task);
        TaskPendingDelete = null;
    }

    private void DeleteTask(PolarTaskRowViewModel task)
    {
        _taskService.DeleteTask(task.Task);
        Reload();
    }

    // ===================== Detalle de tarea =====================

    [RelayCommand]
    private void OpenTaskDetail(PolarTaskRowViewModel task)
    {
        if (FindList(task.Task.ListId) is { } list) TaskDetail = new PolarTaskDetailViewModel(_taskService, list, task);
    }

    /// <summary>Resolves a task's own parent list rather than assuming <see cref="SelectedList"/>, so
    /// task actions (detail, edit) work the same whether the task row came from "Listas" — where it always
    /// matches the selection anyway — or from the "Inicio" feed, which mixes tasks from every list.</summary>
    private PolarTaskList? FindList(string listId) => _taskService.Store.Lists.FirstOrDefault(l => l.Id == listId);

    /// <summary>Entry point for the calendar's "Editar" action (docs/calendar-feature/
    /// 06-creacion-y-edicion-de-items.md §3) — selects the task's own list (switching "Listas" to it,
    /// same as clicking it in the sidebar would) and opens the full task editor directly. A subtask's
    /// calendar item passes its parent task's id here, opening the parent task rather than a form that
    /// doesn't exist for editing a subtask on its own.</summary>
    public void OpenTaskEditorById(string taskId)
    {
        var task = _taskService.Store.Tasks.FirstOrDefault(t => t.Id == taskId && !t.IsDeleted);
        if (task is null) return;

        ViewMode = PolarTaskManagementViewMode.Lists;
        var listItem = Lists.FirstOrDefault(l => l.Id == task.ListId);
        if (listItem is not null) SelectList(listItem);

        var row = PendingTaskRows.Concat(CompletedTaskRows).FirstOrDefault(r => r.Task.Id == taskId);
        if (row is not null) OpenEditTask(row);
    }

    [RelayCommand]
    private void CloseTaskDetail()
    {
        TaskDetail = null;
        ReloadTasks();
    }

    [RelayCommand]
    private void EditTaskFromDetail()
    {
        if (TaskDetail is not { } detail) return;
        var task = detail.SourceRow;
        TaskDetail = null;
        OpenEditTask(task);
    }

    [RelayCommand]
    private void DeleteTaskFromDetail()
    {
        if (TaskDetail is not { } detail) return;
        var task = detail.SourceRow;
        TaskDetail = null;
        RequestDeleteTask(task);
    }
}
