using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="NoteTemplateService"/> sobre un directorio temporal usado como carpeta de
/// plantillas —el mismo patrón de "constructor con storageDirectory" que
/// <see cref="SkillServiceTests"/>—, así que ningún caso toca un vault real.</summary>
public class NoteTemplateServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "enki-tests-templates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private NoteTemplateService CreateService() => new(_directory);

    private void WriteTemplate(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void LoadTemplates_MissingDirectory_ReturnsEmpty() =>
        Assert.Empty(CreateService().LoadTemplates());

    [Fact]
    public void EnsureInitialized_SeedsTheExampleTemplates()
    {
        var service = CreateService();
        var changed = 0;
        service.TemplatesChanged = () => changed++;

        service.EnsureInitialized();

        var templates = service.LoadTemplates();
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, t => t.RelativePath == "daily.md");
        Assert.Contains(templates, t => t.RelativePath == "meeting.md");
        Assert.All(templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        Assert.All(templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Equal(1, changed);
    }

    /// <summary>La plantilla diaria de ejemplo nombra la nota con la fecha: es la que hace que activarla en
    /// Ajustes no cambie el nombre que la nota diaria ha tenido siempre.</summary>
    [Fact]
    public void EnsureInitialized_DailySeed_DeclaresTheDateAsItsTitle()
    {
        var service = CreateService();
        service.EnsureInitialized();

        var daily = service.LoadTemplates().Single(t => t.RelativePath == "daily.md");

        Assert.Equal("{{date}}", daily.TitlePattern);
    }

    /// <summary>Borrar todas las plantillas y dejar la carpeta vacía es una decisión del usuario, no un
    /// estado que haya que "reparar" en el siguiente arranque.</summary>
    [Fact]
    public void EnsureInitialized_DoesNotReseedAnExistingFolder()
    {
        var service = CreateService();
        service.EnsureInitialized();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.md")) File.Delete(file);

        service.EnsureInitialized();

        Assert.Empty(service.LoadTemplates());
    }

    [Fact]
    public void Read_ParsesFrontMatterAndStripsItFromTheBody()
    {
        WriteTemplate("reunion.md", "---\nname: Reunión\ndescription: Acta\ntitle: \"{{date}} · Acta\"\n---\n\n# {{title}}\n");
        var service = CreateService();

        var template = service.LoadTemplates().Single();

        Assert.Equal("Reunión", template.Name);
        Assert.Equal("Acta", template.Description);
        Assert.Equal("{{date}} · Acta", template.TitlePattern);
        Assert.Equal("# {{title}}\n", template.Body);
        Assert.Equal("reunion.md", template.RelativePath);
    }

    [Fact]
    public void Read_WithoutFrontMatter_FallsBackToTheFileNameAndKeepsTheWholeBody()
    {
        WriteTemplate("suelta.md", "# Hola\n");
        var service = CreateService();

        var template = service.LoadTemplates().Single();

        Assert.Equal("suelta", template.Name);
        Assert.Equal(string.Empty, template.Description);
        Assert.Null(template.TitlePattern);
        Assert.Equal("# Hola\n", template.Body);
    }

    /// <summary>Un frontmatter abierto y sin cerrar no es frontmatter: se trata como contenido, que es lo
    /// que el usuario ve en el archivo.</summary>
    [Fact]
    public void Read_UnterminatedFrontMatter_IsTreatedAsBody()
    {
        WriteTemplate("rota.md", "---\nname: Rota\n\n# Texto\n");
        var service = CreateService();

        var template = service.LoadTemplates().Single();

        Assert.Equal("rota", template.Name);
        Assert.StartsWith("---", template.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadTemplates_IncludesSubfoldersWithTheirRelativePath()
    {
        WriteTemplate("trabajo/reunion.md", "---\nname: Reunión\n---\nCuerpo");
        WriteTemplate("diario.md", "Cuerpo");
        var service = CreateService();

        var templates = service.LoadTemplates();

        Assert.Equal(new[] { "diario.md", "trabajo/reunion.md" }, templates.Select(t => t.RelativePath).ToArray());
    }

    [Fact]
    public void FindTemplate_ResolvesByRelativePathAndIsNullWhenMissing()
    {
        WriteTemplate("trabajo/reunion.md", "---\nname: Reunión\n---\nCuerpo");
        var service = CreateService();

        Assert.Equal("Reunión", service.FindTemplate("trabajo/reunion.md")?.Name);
        Assert.Null(service.FindTemplate("trabajo/borrada.md"));
        Assert.Null(service.FindTemplate(null));
        Assert.Null(service.FindTemplate("  "));
    }

    [Fact]
    public void CreateTemplate_WritesAReadableTemplateAndNotifies()
    {
        var service = CreateService();
        var changed = 0;
        service.TemplatesChanged = () => changed++;

        var created = service.CreateTemplate("Mi plantilla");

        Assert.Equal("Mi plantilla", created.Name);
        Assert.Equal("Mi plantilla.md", created.RelativePath);
        Assert.False(string.IsNullOrWhiteSpace(created.Body));
        Assert.Equal(1, changed);
        Assert.Equal("Mi plantilla", service.LoadTemplates().Single().Name);
    }

    [Fact]
    public void CreateTemplate_WithATakenName_AddsANumericSuffixInsteadOfOverwriting()
    {
        var service = CreateService();
        service.CreateTemplate("Diario");

        var second = service.CreateTemplate("Diario");

        Assert.Equal("Diario 2.md", second.RelativePath);
        Assert.Equal(2, service.LoadTemplates().Count);
    }

    [Fact]
    public void DeleteTemplate_RemovesTheFileAndNotifies()
    {
        var service = CreateService();
        var template = service.CreateTemplate("Temporal");
        var changed = 0;
        service.TemplatesChanged = () => changed++;

        service.DeleteTemplate(template);

        Assert.False(File.Exists(template.FilePath));
        Assert.Empty(service.LoadTemplates());
        Assert.Equal(1, changed);
    }

    // ===================== Prepare =====================

    private static readonly DateTime Reference = new(2026, 9, 18, 21, 5, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Prepare_WithoutTitlePattern_UsesTheFallbackTitle()
    {
        WriteTemplate("nota.md", "---\nname: Nota\n---\n# {{title}}");
        var template = CreateService().LoadTemplates().Single();

        var draft = NoteTemplateService.Prepare(template, "Reunión semanal", Reference);

        Assert.Equal("Reunión semanal", draft.Title);
        Assert.Equal("# Reunión semanal", draft.Content);
        Assert.Null(draft.CaretOffset);
    }

    [Fact]
    public void Prepare_TitlePattern_DecidesTheTitleAndFeedsItToTheBody()
    {
        WriteTemplate("nota.md", "---\nname: Nota\ntitle: \"{{date}} · {{title}}\"\n---\n# {{title}}");
        var template = CreateService().LoadTemplates().Single();

        var draft = NoteTemplateService.Prepare(template, "Diario", Reference);

        Assert.Equal("2026-09-18 · Diario", draft.Title);
        Assert.Equal("# 2026-09-18 · Diario", draft.Content);
    }

    [Fact]
    public void Prepare_TitlePatternThatRendersEmpty_FallsBackInsteadOfLeavingTheNoteUnnamed()
    {
        WriteTemplate("nota.md", "---\nname: Nota\ntitle: \"{{cursor}}\"\n---\nCuerpo");
        var template = CreateService().LoadTemplates().Single();

        var draft = NoteTemplateService.Prepare(template, "Respaldo", Reference);

        Assert.Equal("Respaldo", draft.Title);
    }

    [Fact]
    public void Prepare_ReportsTheCaretOffsetRequestedByTheTemplate()
    {
        WriteTemplate("nota.md", "---\nname: Nota\n---\n# Hoy\n\n{{cursor}}");
        var template = CreateService().LoadTemplates().Single();

        var draft = NoteTemplateService.Prepare(template, "Hoy", Reference);

        Assert.Equal("# Hoy\n\n", draft.Content);
        Assert.Equal("# Hoy\n\n".Length, draft.CaretOffset);
    }
}
