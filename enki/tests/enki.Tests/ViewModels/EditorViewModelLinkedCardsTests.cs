using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the two Fase 5 pieces that live on <see cref="EditorViewModel"/>: the derived "Tareas
/// vinculadas" list (reverse side of <see cref="KanbanCard.LinkedNotePath"/>) and clickable wikilinks
/// (docs/analisis-proyecto/03-problemas-y-deuda-tecnica.md §6,
/// docs/analisis-proyecto/04-oportunidades-de-integracion.md §2).</summary>
public class EditorViewModelLinkedCardsTests : IDisposable
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

    [Fact]
    public void LinkedCards_ContainsCardsWhoseLinkedNotePathMatchesThisNote()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();

        var kanban = new KanbanService(Path.Combine(_tempDir, "kanban-storage"));
        var linkedCard = kanban.Board.Columns[0].Cards[0];
        linkedCard.LinkedNotePath = note.FullPath;

        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            kanbanService: kanban);

        var item = Assert.Single(editor.LinkedCards);
        Assert.Equal(linkedCard.Id, item.Id);
        Assert.Equal(linkedCard.Title, item.Title);
        Assert.True(editor.HasLinkedCards);
    }

    [Fact]
    public void OpenLinkedCardCommand_InvokesCallbackWithCardId()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var note = vault.GetNotes().Single();

        string? openedCardId = null;
        var editor = new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { },
            onOpenLinkedCard: id => openedCardId = id);

        editor.OpenLinkedCardCommand.Execute("card-1");

        Assert.Equal("card-1", openedCardId);
    }

    [Fact]
    public void OpenWikiLink_ResolvesNoteInSameWorkspace_AndInvokesCallback()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Origen.md"), "# Origen\n\nVer [[Destino]].");
        File.WriteAllText(Path.Combine(vaultDir, "Destino.md"), "# Destino\n\nContenido.");
        var origin = vault.GetNotes().First(n => n.Title == "Origen");

        NoteFile? opened = null;
        var editor = new EditorViewModel(vault, origin, onBack: () => { }, onChanged: () => { },
            onOpenNote: n => opened = n);

        editor.OpenWikiLink("Destino");

        Assert.NotNull(opened);
        Assert.Equal("Destino", opened!.Title);
    }

    [Fact]
    public void OpenWikiLink_DoesNothing_WhenTargetDoesNotResolve()
    {
        var vault = CreateVault(out var vaultDir);
        File.WriteAllText(Path.Combine(vaultDir, "Origen.md"), "# Origen\n\nContenido.");
        var origin = vault.GetNotes().Single();

        var invoked = false;
        var editor = new EditorViewModel(vault, origin, onBack: () => { }, onChanged: () => { },
            onOpenNote: _ => invoked = true);

        editor.OpenWikiLink("No existe");

        Assert.False(invoked);
    }
}
