using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre que el selector de espacio de trabajo de Archivos recuerda su elección entre arranques
/// (<see cref="Models.AppSettings.FilesSessionId"/>). Es el gemelo de
/// <see cref="NotesViewModelSessionPersistenceTests"/>: Archivos tiene el mismo selector que Notas y hasta
/// ahora era el único de los dos que reabría siempre en el vault, por muy metido que el usuario estuviera
/// en una sesión al cerrar. Cada uno guarda su elección en su propia clave, así que aquí se comprueba
/// además que no se pisan.</summary>
public class FilesViewModelSessionPersistenceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public FilesViewModelSessionPersistenceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions, NotificationService Notifications, SettingsService Settings)
        CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        return (vault, sessions, notifications, settings);
    }

    [Fact]
    public void SelectingASession_PersistsItsId_AndTheVaultPersistsAsNull()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var session = sessions.Create("Trabajo");
        var files = new FilesViewModel(vault, sessions, notifications, settings);
        files.ReloadSessions();

        files.SelectSession(session.Id);

        Assert.Equal(session.Id, settings.Settings.FilesSessionId);

        files.SelectSession(null);

        Assert.Null(settings.Settings.FilesSessionId);
    }

    [Fact]
    public void ANewViewModel_ReopensOnTheSessionThatWasSelectedWhenTheAppClosed()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var session = sessions.Create("Trabajo");
        var files = new FilesViewModel(vault, sessions, notifications, settings);
        files.ReloadSessions();
        files.SelectSession(session.Id);

        // Un arranque nuevo: mismos servicios, viewmodel recién construido, ajustes leídos de disco.
        var reopenedSettings = new SettingsService(_storageDirectory);
        var reopened = new FilesViewModel(vault, sessions, notifications, reopenedSettings);

        Assert.Equal(session.Id, reopened.SelectedSessionOption?.Session?.Id);
        Assert.Equal(session.RootPath, reopened.EffectiveRootPath);
    }

    /// <summary>Una sesión borrada (o de otro vault, tras cambiar de vault) no puede reabrirse: cae al
    /// vault en silencio, mismo criterio tolerante que el resto de la restauración de estado.</summary>
    [Fact]
    public void AStoredSessionThatNoLongerExists_FallsBackToTheVault()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        settings.Settings.FilesSessionId = "sesion-que-ya-no-existe";
        settings.Save();

        var files = new FilesViewModel(vault, sessions, notifications, settings);

        Assert.True(files.SelectedSessionOption?.IsVault);
        Assert.Equal(vault.VaultPath, files.EffectiveRootPath);
    }

    /// <summary>Una sesión protegida arranca bloqueada y aun así se vuelve a elegir: la sección enseña su
    /// estado bloqueado ("desbloquéala en Sesiones") en vez de devolver al usuario al vault a su espalda.
    /// En Archivos esto importa más que en Notas, porque estando bloqueada la sección ni siquiera lista los
    /// nombres de los ficheros — el estado que se reabre es visible de inmediato.</summary>
    [Fact]
    public void AnEncryptedSession_IsReopenedEvenThoughItStartsLocked()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "super-secreta-123");
        var files = new FilesViewModel(vault, sessions, notifications, settings);
        files.ReloadSessions();
        files.SelectSession(session.Id);
        sessions.Lock(session); // como en el arranque siguiente

        var reopened = new FilesViewModel(vault, sessions, notifications, new SettingsService(_storageDirectory));

        Assert.Equal(session.Id, reopened.SelectedSessionOption?.Session?.Id);
        Assert.True(reopened.IsSelectedSessionLocked);
    }

    /// <summary>Las dos secciones guardan en claves distintas: elegir un espacio en Archivos no arrastra a
    /// Notas al mismo sitio, que es justo lo que pasaría compartiendo una sola clave.</summary>
    [Fact]
    public void TheFilesChoice_IsIndependentOfTheNotesOne()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var trabajo = sessions.Create("Trabajo");
        var personal = sessions.Create("Personal");

        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.ReloadSessions();
        notes.SelectSession(personal.Id);

        var files = new FilesViewModel(vault, sessions, notifications, settings);
        files.ReloadSessions();
        files.SelectSession(trabajo.Id);

        Assert.Equal(trabajo.Id, settings.Settings.FilesSessionId);
        Assert.Equal(personal.Id, settings.Settings.NotesSessionId);
        Assert.Equal(personal.Id, notes.SelectedSessionOption?.Session?.Id);
    }
}
