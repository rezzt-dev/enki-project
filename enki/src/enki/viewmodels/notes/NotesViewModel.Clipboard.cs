using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class NotesViewModel
{
    private string? _clipboardNotePath;
    private bool _clipboardCut;
    private bool _clipboardIsFolder;

    private static NoteFile? ClipboardNote(object? item) => item switch
    {
        NoteCardViewModel card => card.Note,
        NoteTreeItemViewModel node => node.Note,
        _ => null
    };

    private bool CanCopyEntry(object? item) => !IsSelectedSessionLocked && (ClipboardNote(item) is not null || ManagedFolderPath(item) is not null);

    [RelayCommand(CanExecute = nameof(CanCopyEntry))]
    private void CopyEntry(object? item) => SetClipboardNote(item, false);

    [RelayCommand(CanExecute = nameof(CanCopyEntry))]
    private void CutEntry(object? item) => SetClipboardNote(item, true);

    private void SetClipboardNote(object? item, bool cut)
    {
        if (!CanCopyEntry(item)) return;
        _clipboardIsFolder = ManagedFolderPath(item) is not null;
        _clipboardNotePath = ManagedFolderPath(item) ?? ClipboardNote(item)!.FullPath;
        _clipboardCut = cut;
        PasteEntryCommand.NotifyCanExecuteChanged();
        CancelClipboardCommand.NotifyCanExecuteChanged();
        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format(cut ? "Explorer_CutReady" : "Explorer_CopyReady"),
            LocalizedTextFormatter.Literal(Path.GetFileName(_clipboardNotePath!)), NotificationSeverity.Info);
    }

    private bool HasClipboardEntry => _clipboardNotePath is not null;

    [RelayCommand(CanExecute = nameof(HasClipboardEntry))]
    private void CancelClipboard()
    {
        _clipboardNotePath = null;
        PasteEntryCommand.NotifyCanExecuteChanged();
        CancelClipboardCommand.NotifyCanExecuteChanged();
    }

    private bool CanPasteEntry(object? _) => _clipboardNotePath is not null && !IsSelectedSessionLocked;

    [RelayCommand(CanExecute = nameof(CanPasteEntry))]
    private void PasteEntry(object? target)
    {
        if (!CanPasteEntry(target) || _clipboardNotePath is not { } source) return;
        var destination = target switch
        {
            string path => path,
            NoteFolderViewModel folder => folder.FullPath,
            NoteFolderCardViewModel folder => string.IsNullOrEmpty(folder.FullPath) ? EffectiveRootPath : folder.FullPath,
            NoteTreeItemViewModel { IsFolder: true } folder => folder.FullPath,
            NoteTreeItemViewModel node => Path.GetDirectoryName(node.FullPath)!,
            _ => CurrentFolderPath
        };
        try
        {
            if (_clipboardIsFolder)
            {
                var folder = _vaultService.TransferFolder(source, destination, _clipboardCut);
                if (_clipboardCut) CancelClipboard();
                _notificationService?.PushLocalized(LocalizedTextFormatter.Format("Explorer_Pasted"),
                    LocalizedTextFormatter.Literal(folder.Name), NotificationSeverity.Success);
                return;
            }
            var editor = OpenEditors.FirstOrDefault(e => VaultService.PathsEqual(e.Note.FullPath, source));
            if (editor is not null && !editor.PrepareFileTransfer()) return;
            var note = _vaultService.GetNote(source) ?? throw new FileNotFoundException(null, source);
            var result = _vaultService.TransferNote(note, destination, _clipboardCut);
            if (_clipboardCut)
            {
                _kanbanService?.RelinkNotePath(source, result.FullPath);
                editor?.SyncTransferredNote(result);
                CancelClipboard();
                PersistOpenTabs();
            }
            ReloadFolders();
            Reload();
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Explorer_Pasted"),
                LocalizedTextFormatter.Literal(Path.GetFileName(result.FullPath)), NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
        }
    }
}
