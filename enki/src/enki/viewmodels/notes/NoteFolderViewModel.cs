using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class NoteFolderViewModel : ObservableObject
{
    public NoteFolder Folder { get; private set; }
    public int Depth { get; }

    public string FullPath => Folder.FullPath;
    public string Name => Folder.Name;

    public ObservableCollection<NoteFolderViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    [ObservableProperty]
    private bool _isDropTarget;

    public NoteFolderViewModel(NoteFolder folder, int depth = 0)
    {
        Folder = folder;
        Depth = depth;
        foreach (var child in folder.Children) Children.Add(new NoteFolderViewModel(child, depth + 1));
    }

    public void UpdateFolder(NoteFolder folder) => Folder = folder;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void BeginRename()
    {
        EditingName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
