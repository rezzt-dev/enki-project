using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the locked-session guard added to <see cref="FilesViewModel"/>: browsing into a
/// password-protected session that isn't unlocked must not list its attachments' file names (metadata,
/// not decrypted content, but still potentially sensitive) — same "exclude entirely while locked" policy
/// as the combined session graph. Same temp-directory construction pattern as the other ViewModel tests.</summary>
public class FilesViewModelEncryptionTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public FilesViewModelEncryptionTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (FilesViewModel Vm, SessionService Sessions, VaultService Vault) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new FilesViewModel(vault, sessions, notifications);
        return (vm, sessions, vault);
    }

    [Fact]
    public void SelectingLockedEncryptedSession_HidesItsFiles()
    {
        var (vm, sessions, vault) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "una-contraseña-larga");

        var sourcePath = Path.Combine(_storageDirectory, "imagen.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        vault.ImportFile(sourcePath, session.RootPath);

        sessions.Lock(session);
        vm.ReloadSessions();
        vm.SelectSession(session.Id);

        Assert.True(vm.IsSelectedSessionLocked);
        Assert.Empty(vm.Files);
        Assert.Empty(vm.RootFolders);
    }

    [Fact]
    public void SelectingUnlockedEncryptedSession_ShowsItsFiles()
    {
        var (vm, sessions, vault) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "una-contraseña-larga");

        var sourcePath = Path.Combine(_storageDirectory, "imagen.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        vault.ImportFile(sourcePath, session.RootPath);

        vm.ReloadSessions();
        vm.SelectSession(session.Id);

        Assert.False(vm.IsSelectedSessionLocked);
        Assert.Contains(vm.Files, f => f.FileName == "imagen.png");
    }

    [Fact]
    public void SelectingPlainVault_IsNeverReportedAsLocked()
    {
        var (vm, _, _) = CreateViewModel();

        vm.SelectSession(null);

        Assert.False(vm.IsSelectedSessionLocked);
    }
}
