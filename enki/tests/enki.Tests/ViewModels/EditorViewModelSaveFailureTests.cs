using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 1 failure path (docs/analisis-proyecto/roadmap.md, "Persistencia atómica del
/// contenido de usuario", top 10 #1/#2): before this, a <see cref="VaultService.WriteContent"/> failure
/// escaped uncaught from the autosave timer's <c>Tick</c>, with nothing to keep the pending edit or retry.
/// Every test here drops the vault folder out from under an already-open tab (unplugged drive, deleted
/// out-of-band) to force a real I/O failure, exactly the scenario the roadmap describes.</summary>
[Collection("LocalizationMutatingTests")]
public class EditorViewModelSaveFailureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private VaultService CreateVault(out string vaultDir)
    {
        vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);
        return vault;
    }

    private NotificationService CreateNotificationService() =>
        new(new SettingsService(Path.Combine(_tempDir, "notifications")), Path.Combine(_tempDir, "notifications"));

    [Fact]
    public void Save_WhenVaultUnavailable_KeepsContentDirty_AndNotifiesInsteadOfThrowing()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();
        var notifications = CreateNotificationService();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);

        editor.OnTextChanged("# Nota\n\nContenido editado.");
        Directory.Delete(vaultDir, recursive: true);

        var exception = Record.Exception(() => editor.Save("# Nota\n\nContenido editado."));

        Assert.Null(exception);
        Assert.True(editor.IsDirty);
        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Error, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void Save_AfterVaultBecomesAvailableAgain_SucceedsOnRetry()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: CreateNotificationService());
        editor.OnTextChanged("# Nota\n\nContenido editado.");

        Directory.Move(vaultDir, vaultDir + "-moved");
        editor.Save("# Nota\n\nContenido editado.");
        Assert.True(editor.IsDirty);

        Directory.Move(vaultDir + "-moved", vaultDir);
        editor.Save("# Nota\n\nContenido editado.");

        Assert.False(editor.IsDirty);
        Assert.Equal("# Nota\n\nContenido editado.", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public void TitleChange_WhenRenameFails_RevertsTitle_AndNotifiesInsteadOfThrowing()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();
        var originalPath = note.FullPath;
        var notifications = CreateNotificationService();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);
        Directory.Delete(vaultDir, recursive: true);

        var exception = Record.Exception(() => editor.Title = "Nota renombrada");

        Assert.Null(exception);
        Assert.Equal("Nota", editor.Title);
        Assert.Equal(originalPath, editor.Note.FullPath);
        Assert.Single(notifications.Notifications);
    }
}
