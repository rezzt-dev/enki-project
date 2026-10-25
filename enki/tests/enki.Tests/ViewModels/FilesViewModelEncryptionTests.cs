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
    public void LockingSession_ClosesAnOpenMediaPreviewOnRefresh()
    {
        var (vm, sessions, vault) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Media", "una-contraseña-larga");
        var source = Path.Combine(_storageDirectory, "sound.wav");
        File.WriteAllBytes(source, [1, 2, 3]);
        vault.ImportFile(source, session.RootPath);
        vm.ReloadSessions();
        vm.SelectSession(session.Id);
        vm.OpenCardCommand.Execute(vm.Files.Single());
        Assert.NotNull(vm.ActivePreview!.MediaPreview);
        sessions.Lock(session);
        vm.Refresh();
        Assert.Null(vm.ActivePreview);
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

    /// <summary>Importar dentro de una sesión protegida y bloqueada cifra con una clave que no está en
    /// memoria: <c>SessionEncryptionService</c> lanza <see cref="SessionLockedException"/> desde dentro del
    /// manejador del "soltar", así que sin este guardado la excepción salía a la cara del usuario. Debe
    /// avisar y no importar nada, no reventar.</summary>
    [Fact]
    public void ImportingIntoALockedSession_NotifiesInsteadOfThrowing()
    {
        var (vm, sessions, vault) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "una-contraseña-larga");
        sessions.Lock(session);
        vm.ReloadSessions();
        vm.SelectSession(session.Id);

        var sourcePath = Path.Combine(_storageDirectory, "entrante.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

        var exception = Record.Exception(() => vm.ImportPaths([sourcePath], session.RootPath));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(session.RootPath, "entrante.png")));
    }

    [Fact]
    public void SelectingPlainVault_IsNeverReportedAsLocked()
    {
        var (vm, _, _) = CreateViewModel();

        vm.SelectSession(null);

        Assert.False(vm.IsSelectedSessionLocked);
    }
}
