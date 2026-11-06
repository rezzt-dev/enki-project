using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public sealed class ExplorerClipboardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-clipboard-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;
    private readonly NotesViewModel _notes;
    private readonly FilesViewModel _files;

    public ExplorerClipboardTests()
    {
        var settings = new SettingsService(_directory);
        _vault = new VaultService(settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(_vault);
        var notifications = new NotificationService(settings, _directory);
        _notes = new NotesViewModel(_vault, sessions, settingsService: settings, notificationService: notifications);
        _files = new FilesViewModel(_vault, sessions, notifications, settings);
    }

    [Fact]
    public void Notes_CopyCanPasteRepeatedly_AndCancelDisablesPaste()
    {
        _vault.CreateNote("Nota");
        _notes.Reload();
        _notes.CopyEntryCommand.Execute(_notes.Notes.Single());
        _notes.PasteEntryCommand.Execute(null);
        _notes.PasteEntryCommand.Execute(null);
        Assert.Equal(3, _vault.GetNotes().Count);
        Assert.True(_notes.PasteEntryCommand.CanExecute(null));
        _notes.CancelClipboardCommand.Execute(null);
        Assert.False(_notes.PasteEntryCommand.CanExecute(null));
    }

    [Fact]
    public void Notes_CutOpenNote_RetargetsEditorAndConsumesClipboard()
    {
        var note = _vault.CreateNote("Nota");
        _notes.Reload();
        var card = _notes.Notes.Single();
        _notes.OpenCardCommand.Execute(card);
        var folder = VaultService.CreateFolder(_vault.VaultPath);
        _notes.CutEntryCommand.Execute(card);
        _notes.PasteEntryCommand.Execute(new NoteFolderViewModel(folder));
        Assert.False(File.Exists(note.FullPath));
        Assert.Equal(folder.FullPath, Path.GetDirectoryName(_notes.ActiveEditor!.Note.FullPath));
        Assert.False(_notes.PasteEntryCommand.CanExecute(null));
    }

    [Fact]
    public void Notes_ExternalConflictDoesNotMoveOpenNote()
    {
        var note = _vault.CreateNote("Nota");
        _notes.Reload();
        var card = _notes.Notes.Single();
        _notes.OpenCardCommand.Execute(card);
        File.WriteAllText(note.FullPath, "contenido externo");
        var folder = VaultService.CreateFolder(_vault.VaultPath);
        _notes.CutEntryCommand.Execute(card);
        _notes.PasteEntryCommand.Execute(new NoteFolderViewModel(folder));
        Assert.True(File.Exists(note.FullPath));
        Assert.Empty(Directory.GetFiles(folder.FullPath));
        Assert.True(_notes.PasteEntryCommand.CanExecute(null));
    }

    [Fact]
    public void Files_CutDoesNotDeleteUntilPaste_AndDoesNotOverwrite()
    {
        var path = Path.Combine(_vault.VaultPath, "test.txt");
        File.WriteAllText(path, "original");
        var folder = VaultService.CreateFolder(_vault.VaultPath);
        File.WriteAllText(Path.Combine(folder.FullPath, "test.txt"), "existente");
        _files.Refresh();
        _files.CutEntryCommand.Execute(_files.Files.Single());
        Assert.True(File.Exists(path));
        _files.PasteEntryCommand.Execute(new FileFolderViewModel(folder));
        Assert.False(File.Exists(path));
        Assert.Equal("original", File.ReadAllText(Path.Combine(folder.FullPath, "test 2.txt")));
        Assert.Equal("existente", File.ReadAllText(Path.Combine(folder.FullPath, "test.txt")));
        Assert.False(_files.PasteEntryCommand.CanExecute(null));
    }

    [Fact]
    public void FailedPaste_KeepsClipboardForRetry()
    {
        var path = Path.Combine(_vault.VaultPath, "test.txt");
        File.WriteAllText(path, "original");
        _files.Refresh();
        _files.CutEntryCommand.Execute(_files.Files.Single());
        File.Delete(path);
        _files.PasteEntryCommand.Execute(null);
        Assert.True(_files.PasteEntryCommand.CanExecute(null));
        File.WriteAllText(path, "restaurado");
        _files.PasteEntryCommand.Execute(null);
        Assert.False(_files.PasteEntryCommand.CanExecute(null));
    }

    public void Dispose()
    {
        _notes.FlushAllEditors();
        Directory.Delete(_directory, recursive: true);
    }
}
