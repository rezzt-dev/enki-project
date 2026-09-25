using System;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class FilesViewModel
{
    private string? _clipboardFilePath;
    private bool _clipboardCut;

    private static FileCardViewModel? ClipboardCard(object? item) => item switch
    {
        FileCardViewModel card => card,
        FileTreeItemViewModel node => node.Card,
        _ => null
    };

    private bool CanCopyEntry(object? item) => !IsSelectedSessionLocked && ClipboardCard(item) is not null;

    [RelayCommand(CanExecute = nameof(CanCopyEntry))]
    private void CopyEntry(object? item) => SetClipboardFile(item, false);

    [RelayCommand(CanExecute = nameof(CanCopyEntry))]
    private void CutEntry(object? item) => SetClipboardFile(item, true);

    private void SetClipboardFile(object? item, bool cut)
    {
        if (!CanCopyEntry(item)) return;
        _clipboardFilePath = ClipboardCard(item)!.File.FullPath;
        _clipboardCut = cut;
        PasteEntryCommand.NotifyCanExecuteChanged();
        CancelClipboardCommand.NotifyCanExecuteChanged();
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format(cut ? "Explorer_CutReady" : "Explorer_CopyReady"),
            LocalizedTextFormatter.Literal(Path.GetFileName(_clipboardFilePath!)), NotificationSeverity.Info);
    }

    private bool HasClipboardEntry => _clipboardFilePath is not null;

    [RelayCommand(CanExecute = nameof(HasClipboardEntry))]
    private void CancelClipboard()
    {
        _clipboardFilePath = null;
        PasteEntryCommand.NotifyCanExecuteChanged();
        CancelClipboardCommand.NotifyCanExecuteChanged();
    }

    private bool CanPasteEntry(object? _) => _clipboardFilePath is not null && !IsSelectedSessionLocked;

    [RelayCommand(CanExecute = nameof(CanPasteEntry))]
    private void PasteEntry(object? target)
    {
        if (!CanPasteEntry(target) || _clipboardFilePath is not { } source) return;
        var destination = target switch
        {
            string path => path,
            FileFolderViewModel folder => folder.FullPath,
            FileFolderCardViewModel folder => folder.FullPath,
            FileTreeItemViewModel { IsFolder: true } folder => folder.FullPath,
            FileTreeItemViewModel node => Path.GetDirectoryName(node.FullPath)!,
            _ => CurrentFolderPath
        };
        if (!TryVaultOperation("Files_Notify_MoveFailed_Title", () =>
            {
                if (_vaultService.IsPathInLockedSession(source)) throw new SessionLockedException(source);
                if (_vaultService.IsPathInLockedSession(destination)) throw new SessionLockedException(destination);
                if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
                if (_clipboardCut)
                {
                    if (!File.Exists(source)) throw new FileNotFoundException(null, source);
                    if (!PathsEqual(Path.GetDirectoryName(source), destination))
                    {
                        var file = new VaultFile { FullPath = source };
                        _vaultService.MoveFile(file, destination);
                        if (ActivePreview is { } preview && PathsEqual(preview.File.FullPath, source))
                        {
                            preview.File.FullPath = file.FullPath;
                            preview.UpdateFile(preview.File);
                            LoadPreviewContent(preview);
                        }
                    }
                    CancelClipboard();
                }
                else _vaultService.CopyFileToFolder(source, destination);
            })) return;
        Refresh();
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Explorer_Pasted"),
            LocalizedTextFormatter.Literal(Path.GetFileName(source)), NotificationSeverity.Success);
    }
}
