using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 5 wikilink-integrity behaviors that live in <see cref="NotesViewModel"/>
/// (docs/analisis-proyecto/roadmap.md): warning before deleting a note other notes still link to, and
/// surfacing a rename that collided with an existing name instead of leaving a "ghost rename" where the
/// grid shows one name but the file on disk kept the old one.</summary>
[Collection("LocalizationMutatingTests")]
public class NotesViewModelWikiLinkTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public NotesViewModelWikiLinkTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesViewModel Notes, VaultService Vault, NotificationService Notifications) CreateSetup()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var notifications = new NotificationService(settings, Path.Combine(_tempDir, "notifications"));
        var notes = new NotesViewModel(vault, session, settingsService: settings, notificationService: notifications);
        return (notes, vault, notifications);
    }

    [Fact]
    public void DeleteNote_WhenOtherNotesLinkToIt_WarnsBeforeDeleting()
    {
        var (notes, vault, notifications) = CreateSetup();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Ver [[Objetivo]].");
        notes.Reload();
        var card = notes.Notes.Single(c => c.Title == "Objetivo");

        notes.DeleteNoteCommand.Execute(card);

        Assert.False(File.Exists(target.FullPath));
        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Warning, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void CommitRenameNote_ToAnAlreadyTakenName_NotifiesTheRealFinalName()
    {
        var (notes, vault, notifications) = CreateSetup();
        vault.CreateNote("Original");
        vault.CreateNote("Ocupado");
        notes.Reload();
        var card = notes.Notes.Single(c => c.Title == "Original");
        card.EditingName = "Ocupado";

        notes.CommitRenameNoteCommand.Execute(card);

        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Info, notifications.Notifications[0].Severity);
        Assert.True(File.Exists(Path.Combine(_vaultDir, "Ocupado 2.md")));
    }

    [Fact]
    public void CommitRenameNote_ToAFreeName_DoesNotNotify()
    {
        var (notes, vault, notifications) = CreateSetup();
        vault.CreateNote("Original");
        notes.Reload();
        var card = notes.Notes.Single(c => c.Title == "Original");
        card.EditingName = "Libre";

        notes.CommitRenameNoteCommand.Execute(card);

        Assert.Empty(notifications.Notifications);
    }
}
