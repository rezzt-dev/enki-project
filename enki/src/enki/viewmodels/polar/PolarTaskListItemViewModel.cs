using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Row in the lists sidebar of the "Gestión de tareas" view.</summary>
public sealed partial class PolarTaskListItemViewModel : ObservableObject
{
    public PolarTaskList List { get; }

    public string Id => List.Id;
    public string Title => List.Title;
    public MaterialIconKind IconKind => PolarIconMap.Resolve(List.Icon);
    public bool IsDependencyChain => List.IsDependencyChain;

    [ObservableProperty]
    private int _taskCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"2/8" instead of a bare total, so the sidebar shows progress at a glance instead of
    /// making the user open every list to see how much is left.</summary>
    public string BadgeLabel => $"{PendingCount}/{TaskCount}";

    public bool IsFullyDone => TaskCount > 0 && PendingCount == 0;

    public PolarTaskListItemViewModel(PolarTaskList list)
    {
        List = list;
    }

    partial void OnTaskCountChanged(int value)
    {
        OnPropertyChanged(nameof(BadgeLabel));
        OnPropertyChanged(nameof(IsFullyDone));
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(BadgeLabel));
        OnPropertyChanged(nameof(IsFullyDone));
    }

    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IconKind));
        OnPropertyChanged(nameof(IsDependencyChain));
    }
}
