using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre el flujo de plantillas de Documentos (.docs/project-info/plantillas-de-notas.md §4):
/// el selector, la creación de una nota a partir de una plantilla y la nota diaria con y sin plantilla
/// configurada.</summary>
public class NotesViewModelTemplatesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultDir;

    public NotesViewModelTemplatesTests()
    {
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private sealed record Setup(
        NotesViewModel Notes,
        VaultService Vault,
        SettingsService Settings,
        VaultSettingsService VaultSettings,
        NoteTemplateService Templates);

    private Setup CreateSetup()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var templates = new NoteTemplateService(vault);
        var vaultSettings = new VaultSettingsService(vault);
        var notes = new NotesViewModel(vault, session, settingsService: settings,
            templateService: templates, vaultSettingsService: vaultSettings);
        return new Setup(notes, vault, settings, vaultSettings, templates);
    }

    private void WriteTemplate(string fileName, string content)
    {
        var directory = Path.Combine(_vaultDir, ".enki", "templates");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    // ===================== Selector =====================

    [Fact]
    public void ShowTemplatePicker_LoadsTheTemplatesFromDisk()
    {
        WriteTemplate("diario.md", "---\nname: Diario\ndescription: Mi diario\n---\n# {{title}}");
        var setup = CreateSetup();

        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        Assert.True(setup.Notes.IsTemplatePickerOpen);
        Assert.True(setup.Notes.HasTemplates);
        var item = Assert.Single(setup.Notes.Templates);
        Assert.Equal("Diario", item.Name);
        Assert.Equal("Mi diario", item.Description);
        Assert.False(item.IsDailyTemplate);
    }

    [Fact]
    public void ShowTemplatePicker_WithoutTemplates_OpensTheEmptyState()
    {
        var setup = CreateSetup();

        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        Assert.True(setup.Notes.IsTemplatePickerOpen);
        Assert.False(setup.Notes.HasTemplates);
    }

    /// <summary>La plantilla elegida en Ajustes se marca en la lista, que es lo que pinta la insignia
    /// "DIARIA" del selector.</summary>
    [Fact]
    public void ShowTemplatePicker_MarksTheConfiguredDailyTemplate()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\n# {{title}}");
        var setup = CreateSetup();
        setup.VaultSettings.Settings.DailyNoteTemplate = "diario.md";

        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        Assert.True(Assert.Single(setup.Notes.Templates).IsDailyTemplate);
    }

    // ===================== Nueva nota desde plantilla =====================

    [Fact]
    public void CreateNoteFromTemplate_CreatesTheNoteWithTheRenderedBodyAndOpensIt()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\n# {{title}}\n\nHoy es {{date}}.");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);

        Assert.False(setup.Notes.IsTemplatePickerOpen);
        var note = Assert.Single(setup.Vault.GetNotes());
        Assert.Equal("Diario", note.Title);
        var content = setup.Vault.ReadContent(note);
        Assert.StartsWith("# Diario", content, StringComparison.Ordinal);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
        Assert.Equal(note.FullPath, setup.Notes.ActiveEditor?.Note.FullPath);
    }

    /// <summary>Con <c>title</c> en el frontmatter, la plantilla decide también cómo se llama la nota.</summary>
    [Fact]
    public void CreateNoteFromTemplate_TitlePattern_NamesTheNote()
    {
        WriteTemplate("diario.md", "---\nname: Diario\ntitle: \"{{date}} Diario\"\n---\nCuerpo");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);

        var expected = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " Diario";
        Assert.Equal(expected, Assert.Single(setup.Vault.GetNotes()).Title);
    }

    /// <summary>Crear dos notas seguidas de la misma plantilla no pisa la primera: el desempate por sufijo
    /// numérico de <see cref="VaultService.CreateNote"/> sigue valiendo con un título de plantilla.</summary>
    [Fact]
    public void CreateNoteFromTemplate_Twice_KeepsBothNotes()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);
        setup.Notes.ShowTemplatePickerCommand.Execute(null);
        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);

        Assert.Equal(2, setup.Vault.GetNotes().Count);
    }

    /// <summary>El frontmatter describe la plantilla, no la nota: nunca debe acabar copiado dentro de
    /// ella.</summary>
    [Fact]
    public void CreateNoteFromTemplate_DoesNotCopyTheFrontMatterIntoTheNote()
    {
        WriteTemplate("diario.md", "---\nname: Diario\ndescription: Solo para la lista\n---\n# Contenido");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);

        var content = setup.Vault.ReadContent(Assert.Single(setup.Vault.GetNotes()));
        Assert.Equal("# Contenido", content);
    }

    [Fact]
    public void CreateNoteFromTemplate_PlacesTheCaretWhereTheTemplateAsked()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\n# Hoy\n\n{{cursor}}");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateNoteFromTemplateCommand.Execute(setup.Notes.Templates[0]);

        Assert.Equal("# Hoy\n\n".Length, setup.Notes.ActiveEditor?.LastCaretOffset);
    }

    /// <summary>Las plantillas viven en <c>.enki/</c>, una carpeta oculta: ninguna debe aparecer como nota
    /// en la rejilla ni en los recorridos del vault.</summary>
    [Fact]
    public void Templates_AreNotListedAsNotes()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var setup = CreateSetup();

        Assert.Empty(setup.Vault.GetNotes());
        Assert.Empty(setup.Vault.GetAllNotesRecursive(_vaultDir));
    }

    // ===================== Gestión de plantillas =====================

    [Fact]
    public void CreateTemplate_OpensTheNewTemplateAsAnEditorTab()
    {
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.CreateTemplateCommand.Execute(null);

        Assert.False(setup.Notes.IsTemplatePickerOpen);
        var template = Assert.Single(setup.Templates.LoadTemplates());
        Assert.Equal(template.FilePath, setup.Notes.ActiveEditor?.Note.FullPath);
    }

    [Fact]
    public void EditTemplate_OpensTheTemplateFileInTheEditor()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var setup = CreateSetup();
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.EditTemplateCommand.Execute(setup.Notes.Templates[0]);

        Assert.False(setup.Notes.IsTemplatePickerOpen);
        Assert.EndsWith("diario.md", setup.Notes.ActiveEditor?.Note.FullPath ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestDeleteTemplate_WithConfirmationOff_DeletesRightAway()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var setup = CreateSetup();
        setup.Settings.Settings.ConfirmBeforeDelete = false;
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.RequestDeleteTemplateCommand.Execute(setup.Notes.Templates[0]);

        Assert.Null(setup.Notes.TemplatePendingDelete);
        Assert.Empty(setup.Notes.Templates);
        Assert.Empty(setup.Templates.LoadTemplates());
    }

    [Fact]
    public void RequestDeleteTemplate_WithConfirmationOn_AsksFirstAndCanBeCancelled()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var setup = CreateSetup();
        setup.Settings.Settings.ConfirmBeforeDelete = true;
        setup.Notes.ShowTemplatePickerCommand.Execute(null);

        setup.Notes.RequestDeleteTemplateCommand.Execute(setup.Notes.Templates[0]);
        Assert.NotNull(setup.Notes.TemplatePendingDelete);
        Assert.Single(setup.Templates.LoadTemplates());

        setup.Notes.CancelDeleteTemplateCommand.Execute(null);
        Assert.Null(setup.Notes.TemplatePendingDelete);
        Assert.Single(setup.Templates.LoadTemplates());

        setup.Notes.RequestDeleteTemplateCommand.Execute(setup.Notes.Templates[0]);
        setup.Notes.ConfirmDeleteTemplateCommand.Execute(null);
        Assert.Empty(setup.Templates.LoadTemplates());
    }

    // ===================== Nota diaria =====================

    [Fact]
    public void OpenDailyNote_WithoutTemplate_KeepsTheDateHeadingBehaviour()
    {
        var setup = CreateSetup();

        setup.Notes.OpenDailyNoteCommand.Execute(null);

        var note = Assert.Single(setup.Vault.GetNotes());
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(today, note.Title);
        Assert.Equal($"# {today}\n\n", setup.Vault.ReadContent(note));
    }

    [Fact]
    public void OpenDailyNote_WithTemplate_UsesItsBodyAndItsTitle()
    {
        WriteTemplate("daily.md", "---\nname: Diario\ntitle: \"{{date}}\"\n---\n# {{date}}\n\n## Foco\n- {{cursor}}");
        var setup = CreateSetup();
        setup.VaultSettings.Settings.DailyNoteTemplate = "daily.md";

        setup.Notes.OpenDailyNoteCommand.Execute(null);

        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var note = Assert.Single(setup.Vault.GetNotes());
        Assert.Equal(today, note.Title);
        Assert.Equal($"# {today}\n\n## Foco\n- ", setup.Vault.ReadContent(note));
        Assert.Equal($"# {today}\n\n## Foco\n- ".Length, setup.Notes.ActiveEditor?.LastCaretOffset);
    }

    /// <summary>La plantilla solo interviene al crear: volver a la nota del día no puede pisar lo que ya
    /// se escribió en ella.</summary>
    [Fact]
    public void OpenDailyNote_WhenTodaysNoteExists_DoesNotReapplyTheTemplate()
    {
        WriteTemplate("daily.md", "---\nname: Diario\ntitle: \"{{date}}\"\n---\nPlantilla");
        var setup = CreateSetup();
        setup.VaultSettings.Settings.DailyNoteTemplate = "daily.md";
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_vaultDir, today + ".md"), "Lo que ya escribí");

        setup.Notes.OpenDailyNoteCommand.Execute(null);

        var note = Assert.Single(setup.Vault.GetNotes());
        Assert.Equal("Lo que ya escribí", setup.Vault.ReadContent(note));
    }

    /// <summary>Un ajuste que apunta a una plantilla borrada no rompe nada: la nota diaria vuelve a su
    /// comportamiento de siempre.</summary>
    [Fact]
    public void OpenDailyNote_WithAMissingTemplate_FallsBackToTheDefaultBehaviour()
    {
        var setup = CreateSetup();
        setup.VaultSettings.Settings.DailyNoteTemplate = "borrada.md";

        setup.Notes.OpenDailyNoteCommand.Execute(null);

        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"# {today}\n\n", setup.Vault.ReadContent(Assert.Single(setup.Vault.GetNotes())));
    }
}
