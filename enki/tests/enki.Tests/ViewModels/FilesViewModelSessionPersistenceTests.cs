using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre la selección compartida de Notas y Archivos y su persistencia.</summary>
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

        Assert.Equal(session.Id, settings.Settings.WorkspaceSessionId);

        files.SelectSession(null);

        Assert.Null(settings.Settings.WorkspaceSessionId);
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
        settings.Settings.WorkspaceSessionId = "sesion-que-ya-no-existe";
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

    /// <summary>Cambiar cualquiera de los dos selectores actualiza el otro, incluido el vault.</summary>
    [Fact]
    public void NotesAndFiles_AlwaysShareTheSelectedSession()
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

        Assert.Equal(trabajo.Id, settings.Settings.WorkspaceSessionId);
        Assert.Equal(trabajo.Id, notes.SelectedSessionOption?.Session?.Id);

        notes.SelectSession(personal.Id);
        Assert.Equal(personal.Id, files.SelectedSessionOption?.Session?.Id);

        notes.OpenNote(vault.CreateNote("Apunte", trabajo.RootPath));
        Assert.Equal(trabajo.Id, files.SelectedSessionOption?.Session?.Id);

        files.SelectSession(null);
        Assert.True(notes.SelectedSessionOption?.IsVault);
        Assert.Null(settings.Settings.WorkspaceSessionId);
    }

    [Fact]
    public void ChangingSession_UpdatesFolderButtonDestinationInNotesAndFiles()
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var session = sessions.Create("Trabajo");
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        var files = new FilesViewModel(vault, sessions, notifications, settings);
        var notesButtonPath = notes.EffectiveRootPath;
        var filesButtonPath = files.EffectiveRootPath;
        notes.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(notes.EffectiveRootPath)) notesButtonPath = notes.EffectiveRootPath;
        };
        files.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(files.EffectiveRootPath)) filesButtonPath = files.EffectiveRootPath;
        };

        notes.SelectSession(session.Id);

        Assert.Equal(session.RootPath, notesButtonPath);
        Assert.Equal(session.RootPath, filesButtonPath);
        notes.CreateFolderDialogCommand.Execute(notesButtonPath);
        Assert.True(notes.FolderManager.IsOpen);
        notes.FolderManager.Name = "Notas";
        notes.FolderManager.ApplyCommand.Execute(null);
        files.CreateFolderDialogCommand.Execute(filesButtonPath);
        Assert.True(files.FolderManager.IsOpen);
        files.FolderManager.Name = "Archivos";
        files.FolderManager.ApplyCommand.Execute(null);
        Assert.True(Directory.Exists(Path.Combine(session.RootPath, "Notas")));
        Assert.True(Directory.Exists(Path.Combine(session.RootPath, "Archivos")));

        files.SelectSession(null);

        Assert.Equal(vault.VaultPath, notesButtonPath);
        Assert.Equal(vault.VaultPath, filesButtonPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacySelections_MigrateToOneWorkspaceSelection(bool notesHadASelection)
    {
        var (vault, sessions, notifications, settings) = CreateServices();
        var trabajo = sessions.Create("Trabajo");
        var personal = sessions.Create("Personal");
        if (notesHadASelection) settings.Settings.NotesSessionId = personal.Id;
        settings.Settings.FilesSessionId = trabajo.Id;
        settings.Save();

        var reopenedSettings = new SettingsService(_storageDirectory);
        var files = new FilesViewModel(vault, sessions, notifications, reopenedSettings);
        var notes = new NotesViewModel(vault, sessions, settingsService: reopenedSettings);

        var expectedId = notesHadASelection ? personal.Id : trabajo.Id;
        Assert.Equal(expectedId, reopenedSettings.Settings.WorkspaceSessionId);
        Assert.Equal(expectedId, files.SelectedSessionOption?.Session?.Id);
        Assert.Equal(expectedId, notes.SelectedSessionOption?.Session?.Id);
    }
}
