using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the "preguntar antes de borrar" guard added to <see cref="FilesViewModel.DeleteFile"/>
/// (análisis alfa-3 §4.5) — before this, Archivos ignored <see cref="AppSettings.ConfirmBeforeDelete"/>
/// entirely and deleted on a single click, unlike Hábitos/Polar/Skills/Kanban/Calendario. Same
/// request/cancel/confirm shape already covered for Kanban in <c>KanbanViewModelTests</c>.</summary>
public class FilesViewModelDeleteConfirmationTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public FilesViewModelDeleteConfirmationTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (FilesViewModel Vm, SettingsService Settings, VaultService Vault) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new FilesViewModel(vault, sessions, notifications, settings);
        return (vm, settings, vault);
    }

    private static string ImportFile(VaultService vault, string storageDirectory, string name)
    {
        var sourcePath = Path.Combine(storageDirectory, name);
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        return vault.ImportFile(sourcePath, vault.VaultPath).FullPath;
    }

    [Fact]
    public void RequestDeleteFile_WithConfirmBeforeDeleteEnabled_OpensDialogWithoutDeleting()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var path = ImportFile(vault, _storageDirectory, "adjunto.png");
        vm.Reload();
        var card = vm.Files.Single(f => f.FileName == "adjunto.png");

        vm.RequestDeleteFileCommand.Execute(card);

        Assert.Same(card, vm.FilePendingDelete);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RequestDeleteFile_WithConfirmBeforeDeleteDisabled_DeletesImmediately()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = false;
        var path = ImportFile(vault, _storageDirectory, "adjunto.png");
        vm.Reload();
        var card = vm.Files.Single(f => f.FileName == "adjunto.png");

        vm.RequestDeleteFileCommand.Execute(card);

        Assert.Null(vm.FilePendingDelete);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ConfirmDeleteFile_DeletesPendingFileAndClearsDialog()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var path = ImportFile(vault, _storageDirectory, "adjunto.png");
        vm.Reload();
        var card = vm.Files.Single(f => f.FileName == "adjunto.png");

        vm.RequestDeleteFileCommand.Execute(card);
        vm.ConfirmDeleteFileCommand.Execute(null);

        Assert.Null(vm.FilePendingDelete);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CancelDeleteFile_KeepsFileAndClearsDialog()
    {
        var (vm, settings, vault) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        var path = ImportFile(vault, _storageDirectory, "adjunto.png");
        vm.Reload();
        var card = vm.Files.Single(f => f.FileName == "adjunto.png");

        vm.RequestDeleteFileCommand.Execute(card);
        vm.CancelDeleteFileCommand.Execute(null);

        Assert.Null(vm.FilePendingDelete);
        Assert.True(File.Exists(path));
    }
}
