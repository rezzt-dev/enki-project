using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class FilesViewModel
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
        if (ActivePreview is { } preview && VaultService.IsWithinFolder(preview.File.FullPath, change.SourcePath))
            preview.MediaPreview?.Close();
    }

    private void ApplyFolderMutation(FolderMutation change)
    {
        if (change.DestinationPath != change.SourcePath)
        {
            if (change.DestinationPath is not null) _folderRemap = (change.SourcePath, change.DestinationPath);
            if (ActivePreview is { } preview && VaultService.IsWithinFolder(preview.File.FullPath, change.SourcePath))
            {
                if (change.DestinationPath is null) ActivePreview = null;
                else
                {
                    preview.File.FullPath = RemapPath(preview.File.FullPath);
                    preview.UpdateFile(preview.File);
                    LoadPreviewContent(preview);
                }
            }
        }
        try { RemapTreeSelection(); Refresh(); }
        finally { _folderRemap = null; }
    }
}
