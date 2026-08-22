using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the export commands of <see cref="NotesViewModel"/> and <see cref="EditorViewModel"/>
/// (propuesta 6 de docs/propuesta-nuevas-funcionalidades.md): que la exportación HTML sin ventana
/// principal (tests) es un no-op seguro en vez de una excepción.</summary>
public class NotesViewModelExportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public NotesViewModelExportTests()
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
        var session = new SessionService(vault);
        return (new NotesViewModel(vault, session, settingsService: settings), vault);
    }

    [Fact]
    public void ExportNoteAsHtml_IsASafeNoOp_WithoutMainWindow()
    {
        // App.MainWindowRef es null en tests — el comando debe degradar a no-op, no lanzar.
        var (notes, vault) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# A");
        notes.Reload();
        var card = notes.Notes.Single(c => c.Note.FileName == "A.md");

        notes.ExportNoteAsHtmlCommand.Execute(card);

        Assert.True(true); // llegar aquí ya es la aserción: nada lanzó
    }

    [Fact]
    public void EditorExportAsHtml_IsASafeNoOp_WithoutMainWindow()
    {
        var (notes, vault) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultDir, "A.md"), "# Desde disco");
        notes.OpenNote(vault.GetNotes().Single(n => n.FileName == "A.md"));
        var editor = notes.OpenEditors.Single();
        editor.Document.Text = "# En vivo";

        editor.ExportAsHtmlCommand.Execute(null);

        Assert.True(true); // llegar aquí ya es la aserción: nada lanzó
    }
}
