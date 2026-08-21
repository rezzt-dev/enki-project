using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Own folder-row viewmodel for the Archivos section, wrapping the same <see cref="NoteFolder"/>
/// model Notas already uses (the folder tree is genuinely shared between both sections — same folders on
/// disk). A separate class from <see cref="NoteFolderViewModel"/> only because its
/// <c>[RelayCommand]</c>s and the XAML bindings around them are type-coupled to <c>NotesViewModel</c>,
/// not because the folder concept itself differs.</summary>
public sealed partial class FileFolderViewModel : ObservableObject
{
    public NoteFolder Folder { get; private set; }
    public int Depth { get; }

    public string FullPath => Folder.FullPath;
    public string Name => Folder.Name;

    public ObservableCollection<FileFolderViewModel> Children { get; } = [];

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

    public FileFolderViewModel(NoteFolder folder, int depth = 0)
    {
        Folder = folder;
        Depth = depth;
        foreach (var child in folder.Children) Children.Add(new FileFolderViewModel(child, depth + 1));
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
