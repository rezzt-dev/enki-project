using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the "preguntar antes de borrar" guard added to the editor toolbar's own delete
/// button (análisis alfa-3 §4.5) — a second entry point to the same underlying delete as
/// <c>NotesViewModel.DeleteNote</c>, reusing the same <see cref="AppSettings.ConfirmBeforeDelete"/>
/// gate and the same request/cancel/confirm shape already covered for Kanban in
/// <c>KanbanViewModelTests</c>.</summary>
public class EditorViewModelDeleteConfirmationTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public EditorViewModelDeleteConfirmationTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (EditorViewModel Vm, SettingsService Settings, VaultService Vault, string NotePath) CreateEditor()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var note = vault.CreateNote("Nota de prueba");
        var vm = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { }, settingsService: settings);
        return (vm, settings, vault, note.FullPath);
    }

    [Fact]
    public void RequestDelete_WithConfirmBeforeDeleteEnabled_OpensDialogWithoutDeleting()
    {
        var (vm, settings, _, notePath) = CreateEditor();
        settings.Settings.ConfirmBeforeDelete = true;

        vm.RequestDeleteCommand.Execute(null);

        Assert.True(vm.IsPendingDelete);
        Assert.True(File.Exists(notePath));
    }

    [Fact]
    public void RequestDelete_WithConfirmBeforeDeleteDisabled_DeletesImmediately()
    {
        var (vm, settings, _, notePath) = CreateEditor();
        settings.Settings.ConfirmBeforeDelete = false;

        vm.RequestDeleteCommand.Execute(null);

        Assert.False(vm.IsPendingDelete);
        Assert.False(File.Exists(notePath));
    }

    [Fact]
    public void ConfirmDelete_DeletesNoteAndClearsDialog()
    {
        var (vm, settings, _, notePath) = CreateEditor();
        settings.Settings.ConfirmBeforeDelete = true;

        vm.RequestDeleteCommand.Execute(null);
        vm.ConfirmDeleteCommand.Execute(null);

        Assert.False(vm.IsPendingDelete);
        Assert.False(File.Exists(notePath));
    }

    [Fact]
    public void CancelDelete_KeepsNoteAndClearsDialog()
    {
        var (vm, settings, _, notePath) = CreateEditor();
        settings.Settings.ConfirmBeforeDelete = true;

        vm.RequestDeleteCommand.Execute(null);
        vm.CancelDeleteCommand.Execute(null);

        Assert.False(vm.IsPendingDelete);
        Assert.True(File.Exists(notePath));
    }
}
