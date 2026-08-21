using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Row in the task list of the "Gestión de tareas" view. Reuses
/// <see cref="KanbanCard.IsPastDue"/>/<see cref="KanbanCard.FormatDueLabel"/> — the same due-date cutoff
/// and formatting already shared by the Kanban board and the reminder inbox — so "vencida"/"hoy" reads
/// identically everywhere in the app, Polar tasks included.</summary>
public sealed partial class PolarTaskRowViewModel : ObservableObject
{
    private readonly PolarTaskService _taskService;

    public PolarTask Task { get; }

    public string Id => Task.Id;
    public string Title => Task.Title;
    public string Description => Task.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Task.Description);
    public bool Completed => Task.Completed;

    public int Priority => Task.Priority;
    public bool HasPriority => Task.Priority > 0;
    public string PriorityLabel => Task.Priority switch
    {
        3 => LocalizationService.Instance["Polar_Priority_High"],
        2 => LocalizationService.Instance["Polar_Priority_Medium"],
        1 => LocalizationService.Instance["Polar_Priority_Low"],
        _ => LocalizationService.Instance["Polar_Priority_None"]
    };

    public bool HasDueDate => Task.DueDateUtc is not null;
    public bool IsOverdue => Task.DueDateUtc is { } due && !Completed && KanbanCard.IsPastDue(due);
    public string DueDateLabel => Task.DueDateUtc is { } due ? KanbanCard.FormatDueLabel(due) : string.Empty;

    public bool HasTags => Task.Tags.Count > 0;
    public string TagsLabel => string.Join(" · ", Task.Tags);

    public bool HasRecurrence => Task.Recurrence != PolarRecurrence.None;

    /// <summary>Delegates to <see cref="PolarRecurrenceFormatter"/> (Fase 10, docs/analisis-proyecto/roadmap.md)
    /// — was a private switch duplicated here, now shared with the Recordatorios editor/list which gained
    /// the same local-only recurrence concept.</summary>
    public string RecurrenceLabel => PolarRecurrenceFormatter.Format(Task.Recurrence);

    public bool HasTimeEstimate => Task.TimeEstimate > 0;
    public string TimeEstimateLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Task_TimeEstimateMinutes"], Task.TimeEstimate);

    [ObservableProperty]
    private bool _isVisible = true;

    public int SubtaskCount => _taskService.Store.Subtasks.Count(s => s.TaskId == Task.Id && !s.IsDeleted);
    public int SubtaskDoneCount => _taskService.Store.Subtasks.Count(s => s.TaskId == Task.Id && !s.IsDeleted && s.Completed);
    public bool HasSubtasks => SubtaskCount > 0;
    public string SubtaskProgressLabel => $"{SubtaskDoneCount}/{SubtaskCount}";

    public bool IsBlocked => _taskService.IsTaskBlocked(Task);

    /// <summary>Full checklist (not just the <see cref="SubtaskProgressLabel"/> summary above), used by
    /// the "Gestión de tareas" home feed to render subtasks inline instead of requiring the task detail
    /// dialog to be opened first — a snapshot taken at construction time, same lifetime as every other
    /// display property here (rows are always rebuilt on reload, see
    /// <see cref="PolarTaskManagementViewModel.ReloadHome"/>).</summary>
    public ObservableCollection<PolarSubtaskRowViewModel> SubtaskRows { get; }

    public PolarTaskRowViewModel(PolarTaskService taskService, PolarTask task)
    {
        _taskService = taskService;
        Task = task;
        SubtaskRows = new ObservableCollection<PolarSubtaskRowViewModel>(
            taskService.Store.Subtasks
                .Where(s => s.TaskId == task.Id && !s.IsDeleted)
                .OrderBy(s => s.OrderIndex)
                .Select(s => new PolarSubtaskRowViewModel(taskService, s)));
    }

    public void ToggleCompleted() => _taskService.SetTaskCompleted(Task, !Task.Completed);

    /// <summary>Every display property above reads straight through to <see cref="Task"/>/the subtask
    /// store, so any mutation done elsewhere (the edit modal, a sync pull) needs an explicit refresh —
    /// same reasoning as <see cref="KanbanCardViewModel.RefreshFromModel"/>.</summary>
    public void RefreshFromModel()
    {
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Devuelve true si no hay etiquetas seleccionadas o si la tarea tiene al menos una
    /// etiqueta en común con <paramref name="selectedTags"/> (comparación insensible a mayúsculas).</summary>
    public bool MatchesTags(IEnumerable<string> selectedTags)
    {
        var selectedList = selectedTags.ToList();
        if (selectedList.Count == 0) return true;

        return Task.Tags.Any(tag =>
            selectedList.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }
}
