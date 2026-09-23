using System;
using System.IO;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 0 crash fix for Notas (docs/version-alfa-3/01-analisis-detallado.md §4.1,
/// hallazgo crítico): before this, every one of these paths read a locked session's note content without
/// checking first, so <see cref="Models.SessionLockedException"/> escaped uncaught — from shell startup
/// (<see cref="NotesViewModel.RestoreOpenTabs"/> and the recent-notes list, both restored from
/// <see cref="AppSettings"/> at construction), and from the notes grid itself
/// (<see cref="NotesViewModel.Reload"/>) whenever a locked session was the one selected in the workspace
/// picker. Same temp-directory-per-test pattern as the other encryption tests.</summary>
public class NotesViewModelSessionLockedTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NotesViewModelSessionLockedTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions, SettingsService Settings, NotificationService Notifications)
        CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        return (vault, sessions, settings, notifications);
    }

    [Fact]
    public void RestoreOpenTabs_SkipsNoteFromLockedSession_InsteadOfThrowing()
    {
        var (vault, sessions, settings, _) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        sessions.Lock(session); // every encrypted session starts locked again on the next real launch

        settings.Settings.OpenNotePaths = [note.FullPath];
        settings.Save();

        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        var exception = Record.Exception(notes.RestoreOpenTabs);

        Assert.Null(exception);
        Assert.Empty(notes.OpenEditors);
    }

    [Fact]
    public void Construction_WithRecentNoteFromLockedSession_DoesNotThrow()
    {
        var (vault, sessions, settings, _) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        sessions.Lock(session);

        settings.Settings.RecentNotePaths = [note.FullPath];
        settings.Save();

        // RefreshRecentNotes runs from the constructor itself (docs/ajustes-docs/
        // 02-productividad-como-app-de-escritorio.md §2), so a locked session's note left in "recent" from
        // a previous run used to throw straight out of shell initialization.
        var exception = Record.Exception(() => new NotesViewModel(vault, sessions, settingsService: settings));

        Assert.Null(exception);
    }

    [Fact]
    public void SelectingLockedEncryptedSession_HidesItsNotes_InsteadOfThrowing()
    {
        var (vault, sessions, settings, _) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        sessions.Lock(session);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings);
        notes.ReloadSessions();

        var exception = Record.Exception(() => notes.SelectSession(session.Id));

        Assert.Null(exception);
        Assert.True(notes.IsSelectedSessionLocked);
        Assert.Empty(notes.Notes);
    }

    [Fact]
    public void ExportNoteAsHtml_ForNoteInLockedSession_NotifiesInsteadOfThrowing()
    {
        var (vault, sessions, settings, notifications) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        sessions.Lock(session);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        var card = new NoteCardViewModel(note);

        notes.ExportNoteAsHtmlCommand.Execute(card);

        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Error, notifications.Notifications[0].Severity);
    }

    /// <summary>Covers Fase 1 (docs/version-alfa-3/02-roadmap-implementacion.md): locking a session used to
    /// leave any tab already open on one of its notes editable and silently failing to autosave. Called
    /// while the session is still unlocked (mirrors how <see cref="SessionsViewModel.LockSession"/>
    /// actually invokes it, before dropping the DEK), so the flush below can still encrypt+write.</summary>
    [Fact]
    public void CloseEditorsForLockedSession_ClosesItsOpenTabs_AfterFlushingPendingChanges()
    {
        var (vault, sessions, settings, notifications) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota abierta", session.RootPath);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        notes.OpenNote(note);
        var editor = Assert.Single(notes.OpenEditors);
        editor.OnTextChanged("contenido editado sin guardar todavía");
        Assert.True(editor.IsDirty);

        notes.CloseEditorsForLockedSession(session);

        Assert.Empty(notes.OpenEditors);
        Assert.Null(notes.ActiveEditor);
        Assert.Equal("contenido editado sin guardar todavía", vault.ReadContent(note));
    }

    [Fact]
    public void CloseEditorsForLockedSession_LeavesTabsFromOtherSessionsAndVaultRootOpen()
    {
        var (vault, sessions, settings, notifications) = CreateServices();
        var (lockedSession, _) = sessions.CreateEncrypted("Se bloquea", "super-secreta-123");
        var otherSession = sessions.Create("Sigue abierta");
        var lockedNote = vault.CreateNote("Nota bloqueada", lockedSession.RootPath);
        var otherNote = vault.CreateNote("Nota de otra sesión", otherSession.RootPath);
        var rootNote = vault.CreateNote("Nota suelta", vault.VaultPath);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        notes.OpenNote(lockedNote);
        notes.OpenNote(otherNote);
        notes.OpenNote(rootNote);

        notes.CloseEditorsForLockedSession(lockedSession);

        Assert.Equal(2, notes.OpenEditors.Count);
        Assert.DoesNotContain(notes.OpenEditors, e => VaultService.PathsEqual(e.Note.FullPath, lockedNote.FullPath));
        Assert.Contains(notes.OpenEditors, e => VaultService.PathsEqual(e.Note.FullPath, otherNote.FullPath));
        Assert.Contains(notes.OpenEditors, e => VaultService.PathsEqual(e.Note.FullPath, rootNote.FullPath));
    }

    [Fact]
    public void CloseEditorsForLockedSession_WithNoOpenTabsForThatSession_IsANoOp()
    {
        var (vault, sessions, settings, notifications) = CreateServices();
        var (lockedSession, _) = sessions.CreateEncrypted("Se bloquea", "super-secreta-123");
        var otherNote = vault.CreateNote("Nota suelta", vault.VaultPath);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        notes.OpenNote(otherNote);

        notes.CloseEditorsForLockedSession(lockedSession);

        Assert.Single(notes.OpenEditors);
        Assert.Empty(notifications.Notifications);
    }

    [Fact]
    public void CloseEditorsForLockedSession_NotifiesTheUser()
    {
        var (vault, sessions, settings, notifications) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota abierta", session.RootPath);

        var notes = new NotesViewModel(vault, sessions, settingsService: settings, notificationService: notifications);
        notes.OpenNote(note);

        notes.CloseEditorsForLockedSession(session);

        var pushed = Assert.Single(notifications.Notifications);
        Assert.Contains(session.Name, pushed.Message);
    }
}
