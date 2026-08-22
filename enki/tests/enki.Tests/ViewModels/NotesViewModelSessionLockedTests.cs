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
}
