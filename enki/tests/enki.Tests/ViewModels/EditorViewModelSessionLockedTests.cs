using System;
using System.IO;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 0 crash fix (docs/version-alfa-3/01-analisis-detallado.md §4.1, hallazgo
/// crítico, "escenario reproducible en caliente"): before this, locking a session while one of its notes
/// was still open in an editor tab made the very next autosave throw <see cref="Models.SessionLockedException"/>
/// straight out of the debounce timer's <c>Tick</c>, with nothing to catch it. Same temp-directory-per-test
/// pattern as <see cref="EditorViewModelSaveFailureTests"/> and the other encryption tests.</summary>
public class EditorViewModelSessionLockedTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public EditorViewModelSessionLockedTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions, NotificationService Notifications) CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        return (vault, sessions, notifications);
    }

    [Fact]
    public void Save_AfterSessionIsLockedWhileTabStillOpen_KeepsContentDirty_AndNotifiesInsteadOfThrowing()
    {
        var (vault, sessions, notifications) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "# Nota secreta\n\nContenido inicial.");

        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);
        var edited = "# Nota secreta\n\nContenido editado mientras la sesión ya estaba bloqueada.";
        editor.OnTextChanged(edited); // marks the tab dirty, same as a live keystroke would

        sessions.Lock(session);

        var exception = Record.Exception(() => editor.Save(edited));

        Assert.Null(exception);
        Assert.True(editor.IsDirty);
        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Error, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void Save_AfterSessionIsUnlockedAgain_SucceedsOnRetry()
    {
        var (vault, sessions, notifications) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "# Nota secreta\n\nContenido inicial.");

        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);
        var edited = "# Nota secreta\n\nContenido editado mientras la sesión estaba bloqueada.";
        editor.OnTextChanged(edited);

        sessions.Lock(session);
        editor.Save(edited);
        Assert.True(editor.IsDirty);

        Assert.True(sessions.Unlock(session, "super-secreta-123"));
        editor.Save(edited);

        Assert.False(editor.IsDirty);
        Assert.Equal(edited, vault.ReadContent(note));
    }
}
