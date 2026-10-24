using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>One row inside the task editor's subtasks list. Unlike Kanban's card editor (which stages
/// subtasks in memory and only writes them on "Guardar"), this operates directly and immediately against
/// <see cref="PolarTaskService"/> — see docs/supabase-sync/01's warning that Polar's own "delete all +
/// reinsert" approach to subtasks is unsafe once sync is involved; toggling/removing here always issues a
/// single targeted update to the one row that changed.</summary>
public sealed partial class PolarSubtaskRowViewModel : ObservableObject
{
    private readonly PolarTaskService _taskService;

    public PolarSubtask Subtask { get; }

    public string Id => Subtask.Id;
    public string Title => Subtask.Title;
    public bool IsDone => Subtask.Completed;
    public MaterialIconKind CheckIcon => IsDone ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline;

    public PolarSubtaskRowViewModel(PolarTaskService taskService, PolarSubtask subtask)
    {
        _taskService = taskService;
        Subtask = subtask;
    }

    [RelayCommand]
    private void Toggle()
    {
        _taskService.SetSubtaskCompleted(Subtask, !Subtask.Completed);
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CheckIcon));
    }
}
