using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class NotesViewModel
{
    public FolderManagerViewModel FolderManager { get; private set; } = null!;

    private static string? ManagedFolderPath(object? item) => item switch
    {
        string path => path,
        NoteFolderViewModel folder => folder.FullPath,
        NoteFolderCardViewModel { IsParent: false } folder => folder.FullPath,
        NoteTreeItemViewModel { IsFolder: true } folder => folder.FullPath,
        FileFolderViewModel folder => folder.FullPath,
        FileFolderCardViewModel { IsParent: false } folder => folder.FullPath,
        FileTreeItemViewModel { IsFolder: true } folder => folder.FullPath,
        _ => null
    };

    [RelayCommand]
    private void ManageFolder(object? item)
    {
        if (!IsSelectedSessionLocked && ManagedFolderPath(item) is { } path)
            FolderManager.Open(path, EffectiveRootPath);
    }

    [RelayCommand]
    private void CreateFolderDialog(object? item)
    {
        if (!IsSelectedSessionLocked)
            FolderManager.Open(ManagedFolderPath(item) ?? CurrentFolderPath, EffectiveRootPath, create: true);
    }

    private void InitializeFolderManager()
    {
        FolderManager = new FolderManagerViewModel(_vaultService, _notificationService);
        _vaultService.FolderChanging += PrepareFolderMutation;
        _vaultService.FolderChanged += ApplyFolderMutation;
    }

    private void PrepareFolderMutation(FolderMutation change)
    {
        foreach (var editor in OpenEditors.Where(e => VaultService.IsWithinFolder(e.Note.FullPath, change.SourcePath)).ToArray())
            if (!editor.PrepareFileTransfer()) change.Cancel = true;
    }

    private void ApplyFolderMutation(FolderMutation change)
    {
        if (change.DestinationPath != change.SourcePath)
        {
            foreach (var editor in OpenEditors.Where(e => VaultService.IsWithinFolder(e.Note.FullPath, change.SourcePath)).ToArray())
            {
                if (change.DestinationPath is null) CloseEditor(editor);
                else
                {
                    var target = Path.Combine(change.DestinationPath, Path.GetRelativePath(change.SourcePath, editor.Note.FullPath));
                    if (_vaultService.GetNote(target) is { } note) editor.SyncTransferredNote(note);
                }
            }
            if (change.DestinationPath is not null && Directory.Exists(change.DestinationPath))
                foreach (var newPath in Directory.GetFiles(change.DestinationPath, "*.md", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    _kanbanService?.RelinkNotePath(Path.Combine(change.SourcePath, Path.GetRelativePath(change.DestinationPath, newPath)), newPath);
            var current = CurrentFolder?.FullPath;
            if (current is not null && VaultService.IsWithinFolder(current, change.SourcePath))
            {
                var remapped = change.DestinationPath is null ? Path.GetDirectoryName(change.SourcePath)! :
                    Path.Combine(change.DestinationPath, Path.GetRelativePath(change.SourcePath, current));
                CurrentFolder = VaultService.PathsEqual(remapped, EffectiveRootPath) ? null : new NoteFolderViewModel(new NoteFolder { FullPath = remapped });
            }
            PersistOpenTabs();
        }
        ReloadFolders();
        Reload();
    }
}
