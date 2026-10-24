using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class EditorBacklinksServiceTests : IDisposable
{
    private readonly string _tempDir;

    public EditorBacklinksServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* no-op */ }
    }

    private NoteFile CreateNote(string fileName, string content, string? title = null)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return new NoteFile { FullPath = path, Title = title ?? Path.GetFileNameWithoutExtension(fileName) };
    }

    /// <summary>Pairs each note with the content it was created with — <see cref="EditorBacklinksService.FindBacklinks"/>
    /// takes pre-read content instead of re-reading from disk itself (docs/analisis-proyecto/roadmap.md,
    /// Fase 6, análisis §3), same shape <see cref="VaultService.GetAllNotesRecursiveWithContent"/> hands it
    /// in production.</summary>
    private static List<(NoteFile Note, string Content)> WithContent(params NoteFile[] notes) =>
        notes.Select(n => (n, File.ReadAllText(n.FullPath))).ToList();

    [Fact]
    public void FindBacklinks_NoMatches_ReturnsEmpty()
    {
        var target = CreateNote("Objetivo.md", "# Objetivo");
        var other = CreateNote("a.md", "Texto sin enlace.");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target, other), target);

        Assert.Empty(result);
    }

    [Fact]
    public void FindBacklinks_MatchByFileName_WithAlias_ReturnsContext()
    {
        var target = CreateNote("Objetivo.md", "# Objetivo");
        var other = CreateNote("a.md", "Véase [[Objetivo|alias]] para más info.");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target, other), target);

        var backlink = Assert.Single(result);
        Assert.Equal(other.FullPath, backlink.FullPath);
        Assert.Contains("[[Objetivo|alias]]", backlink.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void FindBacklinks_MatchWithHeadingAnchor_IgnoresAnchor()
    {
        var target = CreateNote("Objetivo.md", "# Objetivo");
        var other = CreateNote("a.md", "Enlace a [[Objetivo#Sección]].");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target, other), target);

        Assert.Single(result);
    }

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 5 — la resolución debe usar exactamente el mismo
    /// criterio que <see cref="WikiLinkParser.ResolveTarget"/> (nombre de fichero primero, título como
    /// respaldo), en vez de una comparación de texto por título independiente. Aquí el link usa el
    /// *título* de la nota objetivo, no su nombre de fichero.</summary>
    [Fact]
    public void FindBacklinks_MatchByUnambiguousTitle_ResolvesLikeWikiLinkParser()
    {
        var target = CreateNote("nota-1.md", "# Mi Gran Nota", title: "Mi Gran Nota");
        var other = CreateNote("a.md", "Enlace a [[Mi Gran Nota]].");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target, other), target);

        Assert.Single(result);
    }

    /// <summary>Un título ambiguo (compartido por dos notas) no debe resolver a ninguna — mismo criterio
    /// que <see cref="WikiLinkParser.ResolveTargetWithStatus"/> devuelve <c>Ambiguous</c> en vez de adivinar.</summary>
    [Fact]
    public void FindBacklinks_AmbiguousTitle_DoesNotMatch()
    {
        var target = CreateNote("nota-1.md", "# Nueva nota", title: "Nueva nota");
        var otherWithSameTitle = CreateNote("nota-2.md", "# Nueva nota", title: "Nueva nota");
        var linker = CreateNote("a.md", "Enlace a [[Nueva nota]].");

        var result = EditorBacklinksService.FindBacklinks(
            WithContent(target, otherWithSameTitle, linker), target);

        Assert.Empty(result);
    }

    [Fact]
    public void FindBacklinks_ExcludesTargetItself()
    {
        var target = CreateNote("Objetivo.md", "[[Objetivo]]");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target), target);

        Assert.Empty(result);
    }

    [Fact]
    public void FindBacklinks_UnclosedLink_DoesNotMatch()
    {
        var target = CreateNote("Objetivo.md", "# Objetivo");
        var other = CreateNote("a.md", "Texto [[Objetivo incompleto.");

        var result = EditorBacklinksService.FindBacklinks(WithContent(target, other), target);

        Assert.Empty(result);
    }

    [Fact]
    public void WarnIfDeletingLeavesBrokenLinks_NoNotificationService_DoesNotThrow()
    {
        var target = CreateNote("Objetivo.md", "# Objetivo");
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_tempDir, seedWelcomeNote: false);

        var exception = Record.Exception(() =>
            EditorBacklinksService.WarnIfDeletingLeavesBrokenLinks(vault, notificationService: null, target));

        Assert.Null(exception);
    }
}
