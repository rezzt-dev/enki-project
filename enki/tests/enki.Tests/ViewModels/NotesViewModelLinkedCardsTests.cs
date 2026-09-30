using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the nota↔tarea relink that keeps <see cref="KanbanCard.LinkedNotePath"/> pointing at the
/// right file after a note is renamed or moved from the Notes grid
/// (docs/analisis-proyecto/04-oportunidades-de-integracion.md §2, "Capa 1").</summary>
public class NotesViewModelLinkedCardsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private (NotesViewModel Notes, KanbanService Kanban, KanbanCard Card) CreateLinkedSetup()
    {
        var vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);

        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);
        var session = new SessionService(vault);

        var kanban = new KanbanService(Path.Combine(_tempDir, "kanban-storage"));
        var card = kanban.DefaultBoard.Columns[0].Cards[0];

        File.WriteAllText(Path.Combine(vaultDir, "Nota.md"), "# Nota\n\nContenido.");
        var notes = new NotesViewModel(vault, session, kanban);
        var noteCard = notes.Notes.Single();
        card.LinkedNotePath = noteCard.Note.FullPath;

        return (notes, kanban, card);
    }

    [Fact]
    public void CommitRenameNote_RelinksCardsPointingAtTheOldPath()
    {
        var (notes, kanban, card) = CreateLinkedSetup();
        var noteCard = notes.Notes.Single();

        noteCard.EditingName = "Nota Renombrada";
        notes.CommitRenameNoteCommand.Execute(noteCard);

        Assert.EndsWith("Nota Renombrada.md", card.LinkedNotePath);
        Assert.Equal(noteCard.Note.FullPath, card.LinkedNotePath);
    }

    [Fact]
    public void MoveNoteToFolder_RelinksCardsPointingAtTheOldPath()
    {
        var (notes, kanban, card) = CreateLinkedSetup();
        var noteCard = notes.Notes.Single();
        var destination = Path.Combine(Path.GetDirectoryName(noteCard.Note.FullPath)!, "Sub");
        Directory.CreateDirectory(destination);

        notes.MoveNoteToFolder(noteCard, destination);

        Assert.Equal(noteCard.Note.FullPath, card.LinkedNotePath);
        Assert.StartsWith(destination, card.LinkedNotePath!);
    }
}
