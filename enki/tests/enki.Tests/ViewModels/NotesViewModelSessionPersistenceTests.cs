using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre que el selector de espacio de trabajo de Notas recuerda su elección entre arranques
/// (<see cref="Models.AppSettings.WorkspaceSessionId"/>): antes de esto la app reabría siempre en el vault,
/// por muy metido que el usuario estuviera en una sesión al cerrarla. Mismo montaje sobre directorio
/// temporal que el resto de tests de viewmodel de Notas.</summary>
public class NotesViewModelSessionPersistenceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NotesViewModelSessionPersistenceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions, SettingsService Settings) CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        return (vault, sessions, settings);
    }

    [Fact]
    public void SelectingASession_PersistsItsId_AndTheVaultPersistsAsNull()
    {
        var (vault, sessions, settings) = CreateServices();
        var session = sessions.Create("Trabajo");
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.ReloadSessions();

        notes.SelectSession(session.Id);

        Assert.Equal(session.Id, settings.Settings.WorkspaceSessionId);

        notes.SelectSession(null);

        Assert.Null(settings.Settings.WorkspaceSessionId);
    }

    [Fact]
    public void ANewViewModel_ReopensOnTheSessionThatWasSelectedWhenTheAppClosed()
    {
        var (vault, sessions, settings) = CreateServices();
        var session = sessions.Create("Trabajo");
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.ReloadSessions();
        notes.SelectSession(session.Id);

        // Un arranque nuevo: mismos servicios, viewmodel recién construido, ajustes leídos de disco.
        var reopenedSettings = new SettingsService(_storageDirectory);
        var reopened = new NotesViewModel(vault, sessions, settingsService: reopenedSettings);

        Assert.Equal(session.Id, reopened.SelectedSessionOption?.Session?.Id);
        Assert.Equal(session.RootPath, reopened.EffectiveRootPath);
    }

    /// <summary>Una sesión borrada (o de otro vault, tras cambiar de vault) no puede reabrirse: cae al
    /// vault en silencio, igual que una ruta de pestaña que ya no existe.</summary>
    [Fact]
    public void AStoredSessionThatNoLongerExists_FallsBackToTheVault()
    {
        var (vault, sessions, settings) = CreateServices();
        settings.Settings.WorkspaceSessionId = "sesion-que-ya-no-existe";
        settings.Save();

        var notes = new NotesViewModel(vault, sessions, settingsService: settings);

        Assert.True(notes.SelectedSessionOption?.IsVault);
        Assert.Equal(vault.VaultPath, notes.EffectiveRootPath);
    }

    /// <summary>Una sesión cifrada arranca bloqueada y aun así se vuelve a elegir: la sección enseña su
    /// estado bloqueado en vez de devolver al usuario al vault a su espalda.</summary>
    [Fact]
    public void AnEncryptedSession_IsReopenedEvenThoughItStartsLocked()
    {
        var (vault, sessions, settings) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.ReloadSessions();
        notes.SelectSession(session.Id);
        sessions.Lock(session); // como en el arranque siguiente

        var reopened = new NotesViewModel(vault, sessions, settingsService: new SettingsService(_storageDirectory));

        Assert.Equal(session.Id, reopened.SelectedSessionOption?.Session?.Id);
        Assert.True(reopened.IsSelectedSessionLocked);
    }

    [Fact]
    public void SelectingAnEditorTab_UsesItsSessionForNewFolders()
    {
        var (vault, sessions, settings) = CreateServices();
        var first = sessions.Create("Trabajo");
        var second = sessions.Create("Personal");
        var note = vault.CreateNote("Apunte", first.RootPath);
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);

        notes.SelectSession(second.Id);
        notes.OpenNote(note);

        Assert.Equal(first.Id, notes.SelectedSessionOption?.Session?.Id);
        notes.ShowBrowsingModeCommand.Execute(null);
        notes.CreateFolderDialogCommand.Execute(null);
        notes.FolderManager.Name = "Nueva carpeta";
        notes.FolderManager.ApplyCommand.Execute(null);

        Assert.True(Directory.Exists(Path.Combine(first.RootPath, "Nueva carpeta")));
        Assert.False(Directory.Exists(Path.Combine(second.RootPath, "Nueva carpeta")));
    }

    [Fact]
    public void ChangingTheSessionPicker_LeavesAnEditorFromAnotherSession()
    {
        var (vault, sessions, settings) = CreateServices();
        var first = sessions.Create("Trabajo");
        var second = sessions.Create("Personal");
        var note = vault.CreateNote("Apunte", first.RootPath);
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.OpenNote(note);

        notes.SelectSession(second.Id);

        Assert.Null(notes.ActiveEditor);
        Assert.Equal(second.RootPath, notes.EffectiveRootPath);
        Assert.Contains(notes.OpenEditors, editor => editor.Note.FullPath == note.FullPath);
    }

    [Fact]
    public void CutFolder_RemainsDimmedUntilCancelled()
    {
        var (vault, sessions, settings) = CreateServices();
        vault.CreateManagedFolder(_vaultPath, "Origen");
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        var folder = Assert.Single(notes.RootFolders, item => item.Name == "Origen");

        notes.CutEntryCommand.Execute(folder);
        Assert.True(folder.IsCut);
        Assert.True(Assert.Single(notes.BrowserItems.OfType<NoteFolderCardViewModel>(),
            item => item.Name == "Origen").IsCut);

        notes.CancelClipboardCommand.Execute(null);
        Assert.False(folder.IsCut);
    }

    [Fact]
    public void MovingAnOpenNotesFolderToAnotherSession_FollowsTheEditor()
    {
        var (vault, sessions, settings) = CreateServices();
        var first = sessions.Create("Trabajo");
        var second = sessions.Create("Personal");
        var folder = vault.CreateManagedFolder(first.RootPath, "Proyecto");
        var note = vault.CreateNote("Apunte", folder.FullPath);
        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.OpenNote(note);

        notes.MoveFolderTo(folder.FullPath, second.RootPath);

        Assert.Equal(second.Id, notes.SelectedSessionOption?.Session?.Id);
        Assert.StartsWith(Path.Combine(second.RootPath, "Proyecto"), notes.ActiveEditor!.Note.FullPath);
        Assert.True(Directory.Exists(Path.Combine(second.RootPath, "Proyecto")));
    }
}
