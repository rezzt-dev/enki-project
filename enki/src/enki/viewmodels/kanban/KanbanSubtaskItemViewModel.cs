using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>A single checklist row inside <see cref="KanbanCardEditorViewModel"/> — a draft copy,
/// only written back to the underlying <see cref="enki.Models.KanbanCard"/> when the editor is saved.</summary>
public sealed partial class KanbanSubtaskItemViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isDone;

    public MaterialIconKind CheckIcon => IsDone ? MaterialIconKind.CheckboxMarkedOutline : MaterialIconKind.CheckboxBlankOutline;

    public KanbanSubtaskItemViewModel(string id, string title, bool isDone)
    {
        Id = id;
        Title = title;
        IsDone = isDone;
    }

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(CheckIcon));

    [RelayCommand]
    private void Toggle() => IsDone = !IsDone;
}
