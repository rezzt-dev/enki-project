using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the "preguntar antes de borrar" guard added to <see cref="NotesViewModel.DeleteNote"/>
/// (análisis alfa-3 §4.5) — before this, Notas ignored <see cref="AppSettings.ConfirmBeforeDelete"/>
/// entirely and deleted on a single click, unlike Hábitos/Polar/Skills/Kanban/Calendario. Same
/// request/cancel/confirm shape already covered for Kanban in <c>KanbanViewModelTests</c>.</summary>
public class NotesViewModelDeleteConfirmationTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NotesViewModelDeleteConfirmationTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (NotesViewModel Vm, SettingsService Settings, VaultService Vault) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        return (vm, settings, vault);
    }

    [Fact]
    public void RequestDeleteNote_WithConfirmBeforeDeleteEnabled_OpensDialogWithoutDeleting()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var note = vault.CreateNote("Nota de prueba");
        vm.Reload();
        var card = vm.Notes.Single(n => n.Title == "Nota de prueba");

        vm.RequestDeleteNoteCommand.Execute(card);

        Assert.Same(card, vm.NotePendingDelete);
        Assert.True(File.Exists(note.FullPath));
    }

    [Fact]
    public void RequestDeleteNote_WithConfirmBeforeDeleteDisabled_DeletesImmediately()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = false;
        var note = vault.CreateNote("Nota de prueba");
        vm.Reload();
        var card = vm.Notes.Single(n => n.Title == "Nota de prueba");

        vm.RequestDeleteNoteCommand.Execute(card);

        Assert.Null(vm.NotePendingDelete);
        Assert.False(File.Exists(note.FullPath));
    }

    [Fact]
    public void ConfirmDeleteNote_DeletesPendingNoteAndClearsDialog()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var note = vault.CreateNote("Nota de prueba");
        vm.Reload();
        var card = vm.Notes.Single(n => n.Title == "Nota de prueba");

        vm.RequestDeleteNoteCommand.Execute(card);
        vm.ConfirmDeleteNoteCommand.Execute(null);

        Assert.Null(vm.NotePendingDelete);
        Assert.False(File.Exists(note.FullPath));
    }

    [Fact]
    public void CancelDeleteNote_KeepsNoteAndClearsDialog()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var note = vault.CreateNote("Nota de prueba");
        vm.Reload();
        var card = vm.Notes.Single(n => n.Title == "Nota de prueba");

        vm.RequestDeleteNoteCommand.Execute(card);
        vm.CancelDeleteNoteCommand.Execute(null);

        Assert.Null(vm.NotePendingDelete);
        Assert.True(File.Exists(note.FullPath));
    }
}
