using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Detail view of a Polar task shown when its row is clicked in "Gestión de tareas" — mirrors
/// <see cref="KanbanCardDetailViewModel"/>'s role for the local Kanban board, adding the extra metadata
/// Polar tasks carry (recurrence, tags, time estimate, source list, dependency-chain lock). Every display
/// field proxies <see cref="SourceRow"/>; completing the task or toggling a subtask writes straight through
/// to <see cref="PolarTaskService"/> and refreshes <see cref="SourceRow"/>, so the list behind the dialog
/// is already correct once it closes.</summary>
public sealed partial class PolarTaskDetailViewModel : ObservableObject
{
    public PolarTaskRowViewModel SourceRow { get; }
    public PolarTaskList List { get; }

    public string Title => SourceRow.Title;
    public string Description => SourceRow.Description;
    public bool HasDescription => SourceRow.HasDescription;

    public bool Completed => SourceRow.Completed;
    public MaterialIconKind CompletedIcon => Completed ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline;
    public string CompletedLabel => Completed
        ? LocalizationService.Instance["Polar_TaskDetail_Completed"]
        : LocalizationService.Instance["Polar_TaskDetail_MarkCompleted"];

    public bool HasPriority => SourceRow.HasPriority;
    public int Priority => SourceRow.Priority;
    public string PriorityLabel => SourceRow.PriorityLabel;

    public bool HasDueDate => SourceRow.HasDueDate;
    public bool IsOverdue => SourceRow.IsOverdue;
    public string DueDateLabel => SourceRow.DueDateLabel;

    public bool HasRecurrence => SourceRow.HasRecurrence;
    public string RecurrenceLabel => SourceRow.RecurrenceLabel;

    public bool HasTimeEstimate => SourceRow.HasTimeEstimate;
    public string TimeEstimateLabel => SourceRow.TimeEstimateLabel;

    public bool HasTags => SourceRow.HasTags;
    public string TagsLabel => SourceRow.TagsLabel;

    public bool HasAnyMeta => HasPriority || HasDueDate || HasRecurrence || HasTimeEstimate || HasTags;

    public bool IsBlocked => SourceRow.IsBlocked;

    public MaterialIconKind ListIconKind => PolarIconMap.Resolve(List.Icon);
    public string ListTitle => List.Title;

    public bool HasSubtasks => Subtasks.Count > 0;
    public string SubtaskProgressLabel => $"{Subtasks.Count(s => s.IsDone)}/{Subtasks.Count}";

    public ObservableCollection<PolarSubtaskRowViewModel> Subtasks { get; } = [];

    public PolarTaskDetailViewModel(PolarTaskService taskService, PolarTaskList list, PolarTaskRowViewModel sourceRow)
    {
        List = list;
        SourceRow = sourceRow;

        foreach (var subtask in taskService.Store.Subtasks
                     .Where(s => s.TaskId == sourceRow.Task.Id && !s.IsDeleted)
                     .OrderBy(s => s.OrderIndex))
        {
            var row = new PolarSubtaskRowViewModel(taskService, subtask);
            row.PropertyChanged += OnSubtaskPropertyChanged;
            Subtasks.Add(row);
        }
    }

    private void OnSubtaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PolarSubtaskRowViewModel.IsDone)) return;
        OnPropertyChanged(nameof(SubtaskProgressLabel));
        SourceRow.RefreshFromModel();
    }

    [RelayCommand]
    private void ToggleCompleted()
    {
        SourceRow.ToggleCompleted();
        SourceRow.RefreshFromModel();
        OnPropertyChanged(nameof(Completed));
        OnPropertyChanged(nameof(CompletedIcon));
        OnPropertyChanged(nameof(CompletedLabel));
    }
}
