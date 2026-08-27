using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre el último tramo de la búsqueda de texto completo: que abrir un resultado cuya
/// coincidencia está en el cuerpo de la nota deje el cursor <em>en esa línea</em> y no al principio del
/// documento. Encontrar la nota no sirve de mucho si el usuario tiene luego que volver a buscar la frase
/// dentro de ella a mano.</summary>
public class NotesViewModelSearchNavigationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public NotesViewModelSearchNavigationTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesViewModel Notes, VaultService Vault) CreateSetup()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        return (new NotesViewModel(vault, new SessionService(vault), settingsService: settings), vault);
    }

    [Fact]
    public void OpenNote_WithACaretOffset_PositionsTheNewTabAtThatOffset()
    {
        var (notes, vault) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A\n\nprimera\nsegunda con la aguja");
        var note = vault.GetNotes().Single();
        var offset = vault.ReadContent(note).IndexOf("aguja", StringComparison.Ordinal);

        notes.OpenNote(note, offset);

        Assert.Equal(offset, notes.ActiveEditor!.LastCaretOffset);
    }

    [Fact]
    public void OpenNote_WithACaretOffset_MovesTheCaretOfAnAlreadyOpenTab()
    {
        var (notes, vault) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A\n\nprimera\nsegunda con la aguja");
        var note = vault.GetNotes().Single();
        notes.OpenNote(note);

        var requested = -1;
        notes.ActiveEditor!.CaretNavigationRequested += o => requested = o;
        var offset = vault.ReadContent(note).IndexOf("aguja", StringComparison.Ordinal);

        notes.OpenNote(note, offset);

        Assert.Single(notes.OpenEditors);
        Assert.Equal(offset, requested);
        Assert.Equal(offset, notes.ActiveEditor!.LastCaretOffset);
    }

    [Fact]
    public void OpenNote_WithoutACaretOffset_LeavesTheTabWhereItWas()
    {
        var (notes, vault) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A\n\ncuerpo");
        var note = vault.GetNotes().Single();
        notes.OpenNote(note);
        notes.ActiveEditor!.LastCaretOffset = 4;

        notes.OpenNote(note);

        Assert.Equal(4, notes.ActiveEditor!.LastCaretOffset);
    }

    [Fact]
    public void SearchText_FiltersTheGridByNoteBody_NotJustTheFirst160Characters()
    {
        var (notes, _) = CreateSetup();
        var relleno = string.Join(" ", Enumerable.Repeat("relleno", 60));
        File.WriteAllText(Path.Combine(_vaultDir, "larga.md"), $"# Larga\n\n{relleno}\n\nla aguja del pajar");
        File.WriteAllText(Path.Combine(_vaultDir, "otra.md"), "# Otra\n\nnada que ver");
        notes.Reload();

        notes.SearchText = "la aguja del pajar";
        notes.Reload();

        var card = Assert.Single(notes.Notes);
        Assert.Equal("Larga", card.Note.Title);
    }
}
