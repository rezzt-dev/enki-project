using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public sealed class FolderManagementTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "enki-folder-vm-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;
    private readonly NotesViewModel _notes;
    private readonly FilesViewModel _files;
    public FolderManagementTests()
    {
        var settings = new SettingsService(_storage);
        _vault = new VaultService(settings);
        _vault.InitializeVault(Path.Combine(_storage, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(_vault);
        var notifications = new NotificationService(settings, _storage);
        _notes = new NotesViewModel(_vault, sessions, settingsService: settings, notificationService: notifications);
        _files = new FilesViewModel(_vault, sessions, notifications, settings);
    }
    public void Dispose()
    {
        foreach (var editor in _notes.OpenEditors.ToArray()) _notes.CloseEditorTabCommand.Execute(editor);
        Directory.Delete(_storage, true);
    }

    [Fact]
    public void CreateDialog_CancelDoesNotCreateAnything_ConfirmRefreshesBothSections()
    {
        _notes.CreateFolderDialogCommand.Execute(null);
        _notes.FolderManager.Name = "Cancelled";
        _notes.FolderManager.CloseCommand.Execute(null);
        Assert.Empty(_vault.GetFolderTree());
        _files.CreateFolderDialogCommand.Execute(null);
        _files.FolderManager.Name = "Shared";
        _files.FolderManager.ApplyCommand.Execute(null);
        Assert.False(_files.FolderManager.IsOpen);
        Assert.Equal("Shared", Assert.Single(_notes.RootFolders).Name);
        Assert.Equal("Shared", Assert.Single(_files.RootFolders).Name);
    }

    [Fact]
    public void FilesRenameFolder_UpdatesOpenNoteAndSubsequentSaveUsesNewPath()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        var note = _vault.CreateNote("note", source.FullPath);
        _notes.OpenNote(note);
        _files.ManageFolderCommand.Execute(source.FullPath);
        _files.FolderManager.Name = "renamed";
        _files.FolderManager.ApplyCommand.Execute(null);
        var editor = Assert.Single(_notes.OpenEditors);
        Assert.Equal(Path.Combine(_vault.VaultPath, "renamed", "note.md"), editor.Note.FullPath);
        editor.Document.Text = "updated";
        editor.OnTextChanged("updated");
        Assert.True(editor.PrepareFileTransfer());
        Assert.Equal("updated", File.ReadAllText(editor.Note.FullPath));
        Assert.False(Directory.Exists(source.FullPath));
    }

    [Fact]
    public void NotesDeleteFolder_RequiresConfirmation_ClosesOpenEditors_AndCanRestore()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        _notes.OpenNote(_vault.CreateNote("note", source.FullPath));
        _notes.DeleteFolderCommand.Execute(Assert.Single(_notes.RootFolders));
        Assert.True(Directory.Exists(source.FullPath));
        Assert.True(_notes.FolderManager.IsDelete);
        _notes.FolderManager.ApplyCommand.Execute(null);
        Assert.Empty(_notes.OpenEditors);
        Assert.Empty(_files.RootFolders);
        var restored = _vault.RestoreFolder(Assert.Single(_vault.GetTrashedFolders()));
        Assert.True(File.Exists(Path.Combine(restored.FullPath, "note.md")));
        Assert.Single(_notes.RootFolders);
    }

    [Fact]
    public void FolderTransfer_ExternalEditOfOpenNoteAbortsWithoutMoving()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        var note = _vault.CreateNote("note", source.FullPath);
        _notes.OpenNote(note);
        File.WriteAllText(note.FullPath, "external edit");
        _files.ManageFolderCommand.Execute(source.FullPath);
        _files.FolderManager.Name = "renamed";
        _files.FolderManager.ApplyCommand.Execute(null);
        Assert.True(_files.FolderManager.IsOpen);
        Assert.NotEmpty(_files.FolderManager.Error);
        Assert.True(Directory.Exists(source.FullPath));
        Assert.Equal("external edit", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public void FolderClipboard_CutPasteMovesHierarchyAndClearsCut()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        var destination = _vault.CreateManagedFolder(_vault.VaultPath, "destination");
        _vault.CreateNote("note", source.FullPath);
        _notes.CutEntryCommand.Execute(new NoteFolderCardViewModel(source.Name, source.FullPath));
        _notes.PasteEntryCommand.Execute(destination.FullPath);
        Assert.True(File.Exists(Path.Combine(destination.FullPath, "source", "note.md")));
        Assert.False(Directory.Exists(source.FullPath));
        Assert.False(_notes.PasteEntryCommand.CanExecute(null));
    }

    [Fact]
    public void DestinationPicker_ExcludesSourceAndDescendants()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        _vault.CreateManagedFolder(source.FullPath, "nested");
        _notes.ManageFolderCommand.Execute(source.FullPath);
        Assert.DoesNotContain(_notes.FolderManager.Destinations, d => VaultService.IsWithinFolder(d.FullPath, source.FullPath));
        _notes.FolderManager.SelectOperationCommand.Execute("Move");
        Assert.False(_notes.FolderManager.ApplyCommand.CanExecute(null));
    }
    [Fact]
    public void RestoreWithCollision_DoesNotRetargetAnOpenNoteInReplacementFolder()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        _vault.CreateNote("note", source.FullPath);
        _vault.DeleteFolder(source);
        var replacement = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        var replacementNote = _vault.CreateNote("note", replacement.FullPath);
        _vault.WriteContent(replacementNote, "replacement");
        _notes.OpenNote(replacementNote);
        _vault.RestoreFolder(Assert.Single(_vault.GetTrashedFolders()));
        Assert.Equal(replacementNote.FullPath, Assert.Single(_notes.OpenEditors).Note.FullPath);
        Assert.Equal("replacement", _notes.ActiveEditor!.Document.Text);
    }

}
