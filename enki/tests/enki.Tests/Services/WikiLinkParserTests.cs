using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class WikiLinkParserTests
{
    private static NoteFile Note(string path, string title) => new()
    {
        FullPath = path,
        Title = title
    };

    [Fact]
    public void ResolveTarget_MatchesByFileName_BeforeTitle()
    {
        var notes = new List<NoteFile>
        {
            Note("/vault/Nota A.md", "Otro título"),
            Note("/vault/Nota B.md", "Nota A")
        };

        var resolved = WikiLinkParser.ResolveTarget(notes, "Nota A");

        Assert.Same(notes[0], resolved);
    }

    [Fact]
    public void ResolveTarget_FallsBackToUniqueTitleMatch()
    {
        var notes = new List<NoteFile> { Note("/vault/archivo.md", "Mi Nota") };

        var resolved = WikiLinkParser.ResolveTarget(notes, "Mi Nota");

        Assert.Same(notes[0], resolved);
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenTitleIsAmbiguous()
    {
        var notes = new List<NoteFile>
        {
            Note("/vault/a.md", "Nueva nota"),
            Note("/vault/b.md", "Nueva nota")
        };

        var resolved = WikiLinkParser.ResolveTarget(notes, "Nueva nota");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveTarget_ReturnsNull_WhenNothingMatches()
    {
        var notes = new List<NoteFile> { Note("/vault/a.md", "Nota A") };

        var resolved = WikiLinkParser.ResolveTarget(notes, "No existe");

        Assert.Null(resolved);
    }

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 5 — distingue un target sin ninguna nota
    /// (<see cref="WikiLinkResolution.Broken"/>) de uno con más de una (<see cref="WikiLinkResolution.Ambiguous"/>),
    /// para que el editor pueda renderizarlos de forma distinta en vez de verse ambos igual de "muertos".</summary>
    [Fact]
    public void ResolveTargetWithStatus_NothingMatches_ReturnsBroken()
    {
        var notes = new List<NoteFile> { Note("/vault/a.md", "Nota A") };

        var (status, note) = WikiLinkParser.ResolveTargetWithStatus(notes, "No existe");

        Assert.Equal(WikiLinkResolution.Broken, status);
        Assert.Null(note);
    }

    [Fact]
    public void ResolveTargetWithStatus_AmbiguousTitle_ReturnsAmbiguous()
    {
        var notes = new List<NoteFile>
        {
            Note("/vault/a.md", "Nueva nota"),
            Note("/vault/b.md", "Nueva nota")
        };

        var (status, note) = WikiLinkParser.ResolveTargetWithStatus(notes, "Nueva nota");

        Assert.Equal(WikiLinkResolution.Ambiguous, status);
        Assert.Null(note);
    }

    /// <summary>Un nombre de fichero es único garantizado dentro de una sola carpeta (VaultService.CreateNote
    /// se encarga de eso), pero no vault-wide una vez que el conjunto de notas resuelto puede abarcar varias
    /// sesiones a la vez (VaultService.GetAllNotesAcrossVault) — dos sesiones distintas pueden tener cada una
    /// un fichero llamado igual. Antes de este fix, ResolveTargetWithStatus adivinaba el primero con
    /// FirstOrDefault en vez de reportar la ambigüedad, igual que ya hacía (correctamente) para títulos
    /// duplicados.</summary>
    [Fact]
    public void ResolveTargetWithStatus_AmbiguousFileName_ReturnsAmbiguous()
    {
        var notes = new List<NoteFile>
        {
            Note("/vault/.sessions/uno/Reunión.md", "Notas de la reunión"),
            Note("/vault/.sessions/dos/Reunión.md", "Otra reunión")
        };

        var (status, note) = WikiLinkParser.ResolveTargetWithStatus(notes, "Reunión");

        Assert.Equal(WikiLinkResolution.Ambiguous, status);
        Assert.Null(note);
    }

    [Fact]
    public void ResolveTargetWithStatus_UniqueMatch_ReturnsResolved()
    {
        var notes = new List<NoteFile> { Note("/vault/archivo.md", "Mi Nota") };

        var (status, note) = WikiLinkParser.ResolveTargetWithStatus(notes, "Mi Nota");

        Assert.Equal(WikiLinkResolution.Resolved, status);
        Assert.Same(notes[0], note);
    }

    [Fact]
    public void FindOccurrences_ReturnsPositionsAndRawText_ForEachLink()
    {
        var markdown = "Ver [[Nota A]] y [[Nota B|alias]].";

        var occurrences = WikiLinkParser.FindOccurrences(markdown).ToList();

        Assert.Equal(2, occurrences.Count);
        Assert.Equal("Nota A", occurrences[0].Target);
        Assert.Equal("[[Nota A]]", occurrences[0].RawText);
        Assert.Equal(markdown.IndexOf("[[Nota A]]", StringComparison.Ordinal), occurrences[0].Start);
        Assert.Equal("Nota B", occurrences[1].Target);
        Assert.Equal("[[Nota B|alias]]", occurrences[1].RawText);
    }
}
