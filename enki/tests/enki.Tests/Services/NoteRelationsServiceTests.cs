using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Notas relacionadas y menciones sin enlazar (MED-02). Todo el almacenamiento vive en una
/// carpeta temporal propia: nunca se toca un vault real.</summary>
public sealed class NoteRelationsServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public NoteRelationsServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private VaultService CreateVault()
    {
        var vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);
        var vault = new VaultService(new SettingsService(_tempDir));
        vault.SetVaultPath(vaultDir);
        return vault;
    }

    private static NoteFile Note(string name, params string[] tags) => new()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "relations", name + ".md"),
        Title = name,
        Tags = [.. tags]
    };

    private static List<(NoteFile, string)> Vault(params (NoteFile Note, string Content)[] notes) => [.. notes];

    // ── relacionadas ────────────────────────────────────────────────────────────────────

    [Fact]
    public void FindRelated_SharedTags_ListsTheNoteWithItsReason()
    {
        var target = Note("Objetivo", "estudio", "mates");
        var other = Note("Apuntes", "mates", "otra");

        var result = NoteRelationsService.FindRelated(Vault((target, ""), (other, "")), target);

        var related = Assert.Single(result);
        Assert.Equal("Apuntes", related.Title);
        Assert.Equal(["mates"], related.SharedTags);
        Assert.Equal("#mates", related.Reason);
    }

    [Fact]
    public void FindRelated_SharedOutgoingLink_Counts_AndOutweighsATag()
    {
        var hub = Note("Hub");
        var target = Note("Objetivo", "x");
        var viaTag = Note("ConEtiqueta", "x");
        var viaLink = Note("ConEnlace");

        var result = NoteRelationsService.FindRelated(
            Vault((hub, ""), (target, "Ver [[Hub]]"), (viaTag, ""), (viaLink, "También [[Hub]]")), target);

        Assert.Equal(["ConEnlace", "ConEtiqueta"], result.Select(r => r.Title));
        Assert.Equal(["Hub"], result[0].SharedLinks);
    }

    [Fact]
    public void FindRelated_SkipsNotesAlreadyLinkedInEitherDirection()
    {
        var target = Note("Objetivo", "x");
        var linksToTarget = Note("Entrante", "x");
        var linkedByTarget = Note("Saliente", "x");
        var free = Note("Libre", "x");

        var result = NoteRelationsService.FindRelated(
            Vault((target, "Ver [[Saliente]]"), (linksToTarget, "Mira [[Objetivo]]"), (linkedByTarget, ""), (free, "")), target);

        Assert.Equal("Libre", Assert.Single(result).Title);
    }

    [Fact]
    public void FindRelated_WithoutTagsOrLinks_ReturnsNothing()
    {
        var target = Note("Objetivo");
        var other = Note("Otra", "x");

        Assert.Empty(NoteRelationsService.FindRelated(Vault((target, ""), (other, "")), target));
    }

    [Fact]
    public void FindRelated_RespectsTheLimit()
    {
        var target = Note("Objetivo", "x");
        var notes = Enumerable.Range(0, 12).Select(i => (Note("N" + i, "x"), "")).ToList();
        notes.Add((target, ""));

        Assert.Equal(5, NoteRelationsService.FindRelated(notes, target, maxResults: 5).Count);
    }

    // ── menciones sin enlazar ───────────────────────────────────────────────────────────

    [Fact]
    public void FindUnlinkedMentions_PlainText_ReturnsContext()
    {
        var target = Note("Termodinámica");
        var other = Note("Física");

        var result = NoteRelationsService.FindUnlinkedMentions(
            Vault((target, ""), (other, "Primera línea.\nEl capítulo de termodinámica cae en el examen.\nFin")), target);

        var mention = Assert.Single(result);
        Assert.Equal("Física", mention.Title);
        Assert.Equal("termodinámica", mention.MatchedText);
        Assert.Equal("El capítulo de termodinámica cae en el examen.", mention.Context);
        Assert.Equal("Termodinámica", mention.LinkName);
    }

    [Theory]
    [InlineData("Ya enlazada: [[Termodinámica]] y nada más.")]
    [InlineData("Con alias: [[Termodinámica|la térmica]] y ya.")]
    [InlineData("Enlace markdown: [Termodinámica](https://x.org/a) listo.")]
    [InlineData("Código en línea: `Termodinámica` aquí.")]
    [InlineData("Dirección: https://ejemplo.org/Termodinámica fin.")]
    [InlineData("---\ntitulo: Termodinámica\n---\nSin menciones.")]
    [InlineData("```\nTermodinámica\n```\nSin menciones.")]
    [InlineData("~~~python\nprint('Termodinámica')\n~~~\nSin menciones.")]
    [InlineData("Dentro de otra palabra: Termodinámicas y subtermodinámica.")]
    [InlineData("Incrustada: ![[Termodinámica]] fin.")]
    [InlineData("Fórmula $Termodinámica$ y también $$\nTermodinámica\n$$ fin.")]
    public void FindUnlinkedMentions_IgnoresCodeLinksAndProperties(string content)
    {
        var target = Note("Termodinámica");
        var other = Note("Otra");

        Assert.Empty(NoteRelationsService.FindUnlinkedMentions(Vault((target, ""), (other, content)), target));
    }

    [Fact]
    public void FindUnlinkedMentions_IgnoresNotesThatAlreadyLinkTheTarget()
    {
        var target = Note("Termodinámica");
        var linked = Note("Enlazada");

        var result = NoteRelationsService.FindUnlinkedMentions(
            Vault((target, ""), (linked, "Véase [[Termodinámica]]. La termodinámica vuelve a aparecer.")), target);

        Assert.Empty(result);
    }

    [Fact]
    public void FindUnlinkedMentions_MentionAfterCodeBlock_IsStillFound()
    {
        var target = Note("Termodinámica");
        var other = Note("Otra");

        var result = NoteRelationsService.FindUnlinkedMentions(
            Vault((target, ""), (other, "```\nTermodinámica\n```\n\nY fuera del bloque, Termodinámica.")), target);

        Assert.Equal("Y fuera del bloque, Termodinámica.", Assert.Single(result).Context);
    }

    [Fact]
    public void FindUnlinkedMentions_ShortOrAmbiguousTitles_AreNotOffered()
    {
        var shortTarget = Note("IA");
        var other = Note("Otra");
        Assert.Empty(NoteRelationsService.FindUnlinkedMentions(Vault((shortTarget, ""), (other, "Hablo de IA.")), shortTarget));

        // Dos notas con el mismo nombre de archivo y título: ningún enlace apunta a una sola de ellas.
        var twinA = new NoteFile { FullPath = Path.Combine(_tempDir, "a", "Gemela.md"), Title = "Gemela" };
        var twinB = new NoteFile { FullPath = Path.Combine(_tempDir, "b", "Gemela.md"), Title = "Gemela" };
        Assert.Empty(NoteRelationsService.FindUnlinkedMentions(
            Vault((twinA, ""), (twinB, ""), (other, "Hablo de Gemela.")), twinA));
    }

    [Fact]
    public void FindUnlinkedMentions_TitleDifferentFromFileName_LinksByFileName()
    {
        var target = new NoteFile { FullPath = Path.Combine(_tempDir, "termo.md"), Title = "La termodinámica" };
        var other = Note("Otra");

        var result = NoteRelationsService.FindUnlinkedMentions(
            Vault((target, ""), (other, "Repaso de la termodinámica del lunes.")), target);

        var mention = Assert.Single(result);
        Assert.Equal("termo", mention.LinkName);
        Assert.Equal("la termodinámica", mention.MatchedText);
    }

    // ── enlazar ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LinkFirstMention_ExactCase_WritesPlainWikilink()
    {
        var target = Note("Termodinámica");

        var result = NoteRelationsService.LinkFirstMention("Estudio Termodinámica hoy. Termodinámica otra vez.", target, "Termodinámica");

        Assert.Equal("Estudio [[Termodinámica]] hoy. Termodinámica otra vez.", result);
    }

    [Fact]
    public void LinkFirstMention_DifferentCase_KeepsTheWrittenTextAsAlias()
    {
        var target = Note("Termodinámica");

        var result = NoteRelationsService.LinkFirstMention("Cae la termodinámica.", target, "Termodinámica");

        Assert.Equal("Cae la [[Termodinámica|termodinámica]].", result);
    }

    [Fact]
    public void LinkFirstMention_SkipsCodeAndLeavesTheRestUntouched()
    {
        var target = Note("Termodinámica");
        var content = "`Termodinámica`\r\n\r\nTexto Termodinámica final\r\n";

        var result = NoteRelationsService.LinkFirstMention(content, target, "Termodinámica");

        Assert.Equal("`Termodinámica`\r\n\r\nTexto [[Termodinámica]] final\r\n", result);
    }

    [Fact]
    public void LinkFirstMention_WithoutAMention_ReturnsNull()
    {
        var target = Note("Termodinámica");

        Assert.Null(NoteRelationsService.LinkFirstMention("Ya está [[Termodinámica]].", target, "Termodinámica"));
    }

    [Fact]
    public void VaultLinkMention_RewritesTheNoteOnDisk_AndKeepsAVersion()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Termodinámica");
        var source = vault.CreateNote("Física", initialContent: "# Física\n\nRepaso de termodinámica.\n");
        var mention = Assert.Single(NoteRelationsService.FindUnlinkedMentions(vault.GetAllNotesAcrossVaultWithContent(), target));

        var changed = vault.LinkMention(mention, target);

        Assert.True(changed);
        Assert.Equal("# Física\n\nRepaso de [[Termodinámica|termodinámica]].\n", File.ReadAllText(source.FullPath));
        Assert.NotEmpty(vault.GetVersionHistory(source));
    }

    [Fact]
    public void VaultLinkMention_AfterTheTextChanged_DoesNotTouchTheNote()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Termodinámica");
        var source = vault.CreateNote("Física", initialContent: "Repaso de termodinámica.\n");
        var mention = Assert.Single(NoteRelationsService.FindUnlinkedMentions(vault.GetAllNotesAcrossVaultWithContent(), target));

        // El usuario (u otro programa) ya la enlazó a mano entre el cálculo y el clic.
        File.WriteAllText(source.FullPath, "Repaso de [[Termodinámica]].\n");

        Assert.False(vault.LinkMention(mention, target));
        Assert.Equal("Repaso de [[Termodinámica]].\n", File.ReadAllText(source.FullPath));
    }

    [Fact]
    public void VaultLinkMention_KeepsEditsMadeExternallyAfterTheScan()
    {
        var vault = CreateVault();
        var target = vault.CreateNote("Termodinámica");
        var source = vault.CreateNote("Física", initialContent: "Repaso de termodinámica.\n");
        var mention = Assert.Single(NoteRelationsService.FindUnlinkedMentions(vault.GetAllNotesAcrossVaultWithContent(), target));

        var timestamp = File.GetLastWriteTimeUtc(source.FullPath);
        File.WriteAllText(source.FullPath, "Texto nuevo externo. Repaso de termodinámica.\n");
        File.SetLastWriteTimeUtc(source.FullPath, timestamp);

        Assert.True(vault.LinkMention(mention, target));
        Assert.Equal("Texto nuevo externo. Repaso de [[Termodinámica|termodinámica]].\n", File.ReadAllText(source.FullPath));
    }

    [Fact]
    public void MaskNonProse_KeepsLengthAndLineBreaks()
    {
        const string content = "---\na: b\n---\ntexto `code` [x](y)\n```\nfence\n```\nfin";

        var masked = NoteRelationsService.MaskNonProse(content);

        Assert.Equal(content.Length, masked.Length);
        Assert.Equal(content.Count(c => c == '\n'), masked.Count(c => c == '\n'));
        Assert.DoesNotContain("code", masked);
        Assert.DoesNotContain("fence", masked);
        Assert.Contains("texto", masked);
        Assert.Contains("fin", masked);
    }

    [Fact]
    public void FindUnlinkedMentions_LongNotes_StayFast()
    {
        var target = Note("Termodinámica");
        var big = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet `código` [[otra]] texto largo de relleno.\n", 20_000));
        var other = Note("Grande");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = NoteRelationsService.FindUnlinkedMentions(Vault((target, ""), (other, big + "Termodinámica")), target);
        watch.Stop();

        Assert.Single(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"tardó {watch.Elapsed}");
    }

    [Fact]
    public void FindMentionedNotes_ListsNotesNamedInTheTarget_ButNotAlreadyLinkedOnes()
    {
        var target = Note("Viaje");
        var budget = Note("Presupuesto anual");
        var hotel = Note("Hotel");
        var linked = Note("Vuelos");
        var notes = Vault(
            (target, "# Viaje\n\nRevisar el presupuesto anual y reservar [[Vuelos]]. Los vuelos ya están.\n`Hotel` en código no cuenta."),
            (budget, "# Presupuesto anual"),
            (hotel, "# Hotel"),
            (linked, "# Vuelos"));

        var result = NoteRelationsService.FindMentionedNotes(notes, target);

        var mention = Assert.Single(result);
        Assert.Equal("Presupuesto anual", mention.Title);
        Assert.Equal("presupuesto anual", mention.MatchedText);
        Assert.Equal("Presupuesto anual", mention.LinkName);
        Assert.Contains("Revisar el presupuesto anual", mention.Context);
    }
}
