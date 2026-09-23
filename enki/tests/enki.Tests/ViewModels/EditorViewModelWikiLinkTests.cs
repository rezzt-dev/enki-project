using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 5 wikilink-integrity behaviors that live in <see cref="EditorViewModel"/>
/// (docs/analisis-proyecto/roadmap.md): warning before a delete that would leave other notes with a broken
/// link, reflecting the real final name when a rename collides with an existing file, and resolving
/// Ctrl+click against the same scope the rename cascade uses.</summary>
[Collection("LocalizationMutatingTests")]
public class EditorViewModelWikiLinkTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public EditorViewModelWikiLinkTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        return vault;
    }

    private NotificationService CreateNotificationService() =>
        new(new SettingsService(Path.Combine(_tempDir, "notifications")), Path.Combine(_tempDir, "notifications"));

    [Fact]
    public void Delete_WhenOtherNotesLinkToIt_WarnsWithBacklinkCount_ButStillDeletes()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Objetivo");
        File.WriteAllText(Path.Combine(_vaultDir, "Enlazadora.md"), "Ver [[Objetivo]].");
        var notifications = CreateNotificationService();
        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);

        editor.ConfirmDeleteCommand.Execute(null);

        Assert.False(File.Exists(target.FullPath));
        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Warning, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void Delete_WhenNothingLinksToIt_DoesNotNotify()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Solitaria");
        var notifications = CreateNotificationService();
        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);

        editor.ConfirmDeleteCommand.Execute(null);

        Assert.Empty(notifications.Notifications);
    }

    [Fact]
    public void TitleChange_ToAnAlreadyTakenName_KeepsRealFinalName_AndNotifies()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Original");
        vault.CreateNote("Ocupado");
        var notifications = CreateNotificationService();
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            notificationService: notifications);

        editor.Title = "Ocupado";

        Assert.Equal("Ocupado 2", editor.Title);
        Assert.Equal("Ocupado 2.md", Path.GetFileName(editor.Note.FullPath));
        Assert.Single(notifications.Notifications);
        Assert.Equal(NotificationSeverity.Info, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void OpenWikiLink_ResolvesAgainstScopeNotes_AndInvokesCallback()
    {
        var vault = CreateVault();
        var current = vault.CreateNote("Actual");
        var target = vault.CreateNote("Destino");
        NoteFile? opened = null;
        var editor = new EditorViewModel(vault, current, onBack: () => { }, onChanged: () => { },
            onOpenNote: n => opened = n);

        editor.OpenWikiLink("Destino");

        Assert.NotNull(opened);
        Assert.Equal(target.FullPath, opened!.FullPath);
    }

    [Fact]
    public void OpenWikiLink_UnresolvedTarget_DoesNothing()
    {
        var vault = CreateVault();
        var current = vault.CreateNote("Actual");
        var invoked = false;
        var editor = new EditorViewModel(vault, current, onBack: () => { }, onChanged: () => { },
            onOpenNote: _ => invoked = true);

        editor.OpenWikiLink("No existe");

        Assert.False(invoked);
    }

    /// <summary>Wikilink resolution stopped being scoped to "this note's own session" (see
    /// VaultService.GetAllNotesAcrossVault) so that a link inserted from the editor's new note-link picker
    /// resolves the same way regardless of which session the target note lives in — this is what makes
    /// Ctrl+click on a cross-session wikilink actually open the right note instead of silently doing
    /// nothing.</summary>
    [Fact]
    public void OpenWikiLink_ResolvesAgainstANoteInADifferentSession_AndInvokesCallback()
    {
        var vault = CreateVault();
        var current = vault.CreateNote("Actual");
        var sessionFolder = Path.Combine(_vaultDir, ".sessions", "otra-sesion");
        Directory.CreateDirectory(sessionFolder);
        var targetPath = Path.Combine(sessionFolder, "Destino.md");
        File.WriteAllText(targetPath, "# Destino");
        NoteFile? opened = null;
        var editor = new EditorViewModel(vault, current, onBack: () => { }, onChanged: () => { },
            onOpenNote: n => opened = n);

        editor.OpenWikiLink("Destino");

        Assert.NotNull(opened);
        Assert.Equal(targetPath, opened!.FullPath);
    }

    /// <summary>Regression test for a real bug found while widening wikilink resolution: the backlinks panel
    /// (EditorBacklinksViewModel.Refresh) used to scan VaultService.GetAllNotesRecursiveWithContent(VaultPath)
    /// directly, and VaultService.IsUnderHiddenDirectory excludes any path segment starting with "." relative
    /// to that root — so ".sessions" was excluded entirely and a note living inside *any* session always
    /// showed zero backlinks, even from another note in that exact same session.</summary>
    [Fact]
    public void Backlinks_ForANoteInsideASession_FindsALinkFromAnotherNoteInThatSameSession()
    {
        var vault = CreateVault();
        var sessionFolder = Path.Combine(_vaultDir, ".sessions", "mi-sesion");
        Directory.CreateDirectory(sessionFolder);
        var targetPath = Path.Combine(sessionFolder, "Objetivo.md");
        File.WriteAllText(targetPath, "# Objetivo");
        File.WriteAllText(Path.Combine(sessionFolder, "Enlazadora.md"), "Ver [[Objetivo]].");
        var target = vault.GetNote(targetPath)!;

        var editor = new EditorViewModel(vault, target, onBack: () => { }, onChanged: () => { });

        Assert.False(editor.Backlinks.IsEmpty);
        Assert.Single(editor.Backlinks.Items);
    }
}
