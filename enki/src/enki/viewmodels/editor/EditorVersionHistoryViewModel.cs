using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Backs the editor's "History" side-panel tab (historial local de versiones) — lists every
/// snapshot <see cref="VaultService"/> has captured for the open note, and drives the compare/restore modal:
/// pick a version, see it diffed against what's on screen right now, and either discard it or restore it
/// (which itself snapshots the current content first — see <see cref="VaultService.RestoreVersion"/> — so a
/// restore is never a one-way door).</summary>
public sealed partial class EditorVersionHistoryViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly NotificationService? _notificationService;
    private readonly Func<string> _getCurrentText;
    private readonly Action<string> _applyRestoredContent;
    private readonly Func<string, bool>? _restoreVersion;
    private NoteFile _note;

    public ObservableCollection<NoteVersionItemViewModel> Items { get; } = [];

    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    private NoteVersionItemViewModel? _selectedVersion;

    partial void OnSelectedVersionChanged(NoteVersionItemViewModel? value) => OnPropertyChanged(nameof(ConfirmRestoreMessage));

    /// <summary>"The current content will be replaced with the version from {0}. ..." with
    /// <see cref="SelectedVersion"/>'s timestamp filled in — computed here rather than in XAML because
    /// Avalonia has no built-in binding for "localized string with one placeholder."</summary>
    public string ConfirmRestoreMessage => SelectedVersion is null
        ? string.Empty
        : string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Editor_History_ConfirmRestore_Message"], SelectedVersion.CreatedLabel);

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private bool _isPendingRestore;

    public EditorVersionHistoryViewModel(VaultService vaultService, NoteFile note, NotificationService? notificationService,
        Func<string> getCurrentText, Action<string> applyRestoredContent, Func<string, bool>? restoreVersion = null)
    {
        _vaultService = vaultService;
        _note = note;
        _notificationService = notificationService;
        _getCurrentText = getCurrentText;
        _applyRestoredContent = applyRestoredContent;
        _restoreVersion = restoreVersion;
        Reload();
    }

    /// <summary>Re-points this panel at a different note — called by <c>EditorViewModel.SyncRenamedNote</c>
    /// so a rename (which moves the note's history folder alongside it, see
    /// <see cref="VaultService.RenameNote"/>) doesn't leave this tab reading from a path that no longer has
    /// anything under it.</summary>
    public void Retarget(NoteFile note)
    {
        _note = note;
        Reload();
    }

    /// <summary>Re-reads this note's version list from disk — called after every mutation (restore, delete)
    /// and whenever the side panel switches to this tab, since another tab or a background autosave tick can
    /// have added a snapshot since this list was last built.</summary>
    public void Reload()
    {
        Items.Clear();
        foreach (var version in _vaultService.GetVersionHistory(_note))
            Items.Add(new NoteVersionItemViewModel(version));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Re-announces every label this view model resolves against the active language directly
    /// (not through a recreated <see cref="NoteVersionItemViewModel"/>, see <see cref="Reload"/>) —
    /// called by <c>EditorViewModel.RefreshLocalizedLabels</c> on a hot language switch.</summary>
    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(ConfirmRestoreMessage));

    [RelayCommand]
    private void OpenPreview(NoteVersionItemViewModel item)
    {
        SelectedVersion = item;
        BuildDiff(item);
        IsPreviewOpen = true;
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        IsPendingRestore = false;
        SelectedVersion = null;
        DiffLines.Clear();
    }

    private void BuildDiff(NoteVersionItemViewModel item)
    {
        DiffLines.Clear();
        var versionContent = _vaultService.ReadVersionContent(_note, item.Id) ?? string.Empty;
        foreach (var line in TextDiffService.Compare(versionContent, _getCurrentText()))
            DiffLines.Add(line);
    }

    [RelayCommand]
    private void RequestRestore() => IsPendingRestore = true;

    [RelayCommand]
    private void CancelRestore() => IsPendingRestore = false;

    [RelayCommand]
    private void ConfirmRestore()
    {
        if (SelectedVersion is not { } item) return;
        IsPendingRestore = false;

        try
        {
            if (_restoreVersion is not null)
            {
                if (!_restoreVersion(item.Id)) return;
            }
            else
            {
                _vaultService.RestoreVersion(_note, item.Id, unsavedContent: _getCurrentText());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error($"No se pudo restaurar la versión '{item.Id}' de '{_note.FullPath}'", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NoteVersionRestoreFailed_Title"),
                LocalizedTextFormatter.Format("Notification_NoteVersionRestoreFailed_Message", _note.Title, VaultFailureReasons.DescribeLocalized(ex)),
                NotificationSeverity.Error);
            return;
        }

        var restoredContent = _vaultService.ReadContent(_note);
        _applyRestoredContent(restoredContent);

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteVersionRestored_Title"),
            LocalizedTextFormatter.Format("Notification_NoteVersionRestored_Message", item.CreatedLabel),
            NotificationSeverity.Success);

        IsPreviewOpen = false;
        SelectedVersion = null;
        DiffLines.Clear();
        Reload();
    }

    [RelayCommand]
    private void DeleteVersion(NoteVersionItemViewModel item)
    {
        _vaultService.DeleteVersion(_note, item.Id);
        if (SelectedVersion == item) ClosePreview();
        Reload();
    }
}
