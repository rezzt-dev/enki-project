using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="SkillService"/> using a throwaway temp directory as
/// <see cref="SkillService.SkillsDirectory"/> — same "storageDirectory constructor" pattern as
/// <see cref="KanbanServiceTests"/>. <see cref="SkillService.EnsureDefaultSkills"/> relies on the
/// <c>default-skills/*.md</c> content copied next to the test assembly by <c>enki.csproj</c>'s own
/// <c>Content</c> item, which flows through via the <c>ProjectReference</c>.</summary>
public class SkillServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-skills-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void LoadSkills_EmptyDirectory_ReturnsEmptyList()
    {
        var service = new SkillService(_storageDirectory);

        Assert.Empty(service.LoadSkills());
    }

    [Fact]
    public void EnsureDefaultSkills_CreatesDefaultsWhenEmpty()
    {
        var service = new SkillService(_storageDirectory);

        service.EnsureDefaultSkills();

        var skills = service.LoadSkills();
        Assert.NotEmpty(skills);
        Assert.All(skills, s => Assert.True(s.IsDefault));
    }

    [Fact]
    public void EnsureDefaultSkills_DoesNotOverwriteExisting()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "mi-skill.md"), "---\nname: Mía\n---\nContenido");

        service.EnsureDefaultSkills();

        var skills = service.LoadSkills();
        Assert.Single(skills);
        Assert.Equal("Mía", skills[0].Name);
        Assert.False(skills[0].IsDefault);
    }

    [Fact]
    public void ReadSkill_ParsesFrontmatter()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "traductor.md");
        File.WriteAllText(path,
            "---\nname: Traductor\ndescription: Traduce texto\nemoji: \"🌐\"\ntags:\n  - idiomas\n---\nCuerpo de la skill.");

        var skill = service.ReadSkill(path);

        Assert.Equal("traductor", skill.Id);
        Assert.Equal("Traductor", skill.Name);
        Assert.Equal("Traduce texto", skill.Description);
        Assert.Equal("🌐", skill.Emoji);
        Assert.Contains("idiomas", skill.Tags);
    }

    [Fact]
    public void ReadSkill_MissingFrontmatter_UsesFileName()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "sin-frontmatter.md");
        File.WriteAllText(path, "Solo texto, sin YAML.");

        var skill = service.ReadSkill(path);

        Assert.Equal("sin-frontmatter", skill.Id);
        Assert.Equal("sin-frontmatter", skill.Name);
    }

    [Fact]
    public void SaveSkill_WritesContentAndFrontmatter()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "editable.md");
        File.WriteAllText(path, "---\nname: Original\ndescription: Vieja\n---\nCuerpo original");
        var skill = service.ReadSkill(path);

        service.SaveSkill(skill, "---\nname: Nueva\ndescription: Nueva descripción\n---\nCuerpo nuevo");

        var reloaded = service.ReadSkill(path);
        Assert.Equal("Nueva", reloaded.Name);
        Assert.Equal("Nueva descripción", reloaded.Description);
        Assert.Contains("Cuerpo nuevo", reloaded.Content);
    }

    [Fact]
    public void SaveSkill_WithoutFrontmatter_AddsMinimalFrontmatter()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "plana.md");
        File.WriteAllText(path, "---\nname: Plana\n---\nCuerpo");
        var skill = service.ReadSkill(path);

        service.SaveSkill(skill, "Solo texto plano, sin frontmatter.");

        var raw = File.ReadAllText(path);
        Assert.StartsWith("---", raw);
        Assert.Contains("permissions:", raw);
    }

    /// <summary>Fase 1 (docs/analisis-proyecto/roadmap.md): <see cref="SkillService.SaveSkill"/> used to
    /// write directly with <see cref="File.WriteAllText(string, string)"/> — same structural defect as
    /// <see cref="VaultService.WriteContent"/> before it. A write into a folder that no longer exists must
    /// propagate instead of being silently swallowed, so <c>SkillEditorViewModel.Save</c> can react.</summary>
    [Fact]
    public void SaveSkill_WhenTargetFolderIsGone_PropagatesTheFailure()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "editable.md");
        File.WriteAllText(path, "---\nname: Original\ndescription: Vieja\n---\nCuerpo original");
        var skill = service.ReadSkill(path);
        Directory.Delete(_storageDirectory, recursive: true);

        Assert.ThrowsAny<IOException>(() => service.SaveSkill(skill, "Cuerpo nuevo"));
    }

    [Fact]
    public void DuplicateSkill_CreatesCopyWithNewId()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "origen.md");
        File.WriteAllText(path, "---\nname: Origen\n---\nCuerpo");
        var source = service.ReadSkill(path);

        var copy = service.DuplicateSkill(source, "copia");

        Assert.Equal("copia", copy.Id);
        Assert.False(copy.IsDefault);
        Assert.True(File.Exists(Path.Combine(_storageDirectory, "copia.md")));
        Assert.Equal(2, service.LoadSkills().Count);
    }

    [Fact]
    public void DeleteSkill_RemovesFile()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "borrable.md");
        File.WriteAllText(path, "---\nname: Borrable\n---\nCuerpo");
        var skill = service.ReadSkill(path);

        service.DeleteSkill(skill);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DeleteSkill_Default_ThrowsInvalidOperation()
    {
        var service = new SkillService(_storageDirectory);
        service.EnsureDefaultSkills();
        var defaultSkill = service.LoadSkills().First(s => s.IsDefault);

        Assert.Throws<InvalidOperationException>(() => service.DeleteSkill(defaultSkill));
    }

    [Fact]
    public void Import_CopiesFilesToSkillsDirectory()
    {
        var service = new SkillService(_storageDirectory);
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-skills-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "importada.md");
        File.WriteAllText(sourcePath, "---\nname: Importada\n---\nCuerpo importado");

        try
        {
            var result = service.Import([sourcePath], ConflictResolution.Cancel);

            Assert.Contains("importada.md", result.Imported);
            Assert.True(File.Exists(Path.Combine(_storageDirectory, "importada.md")));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Fact]
    public void Import_SkipsNonMarkdownFiles()
    {
        var service = new SkillService(_storageDirectory);
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-skills-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "nota.md"), "---\nname: Nota\n---\nCuerpo");
        File.WriteAllText(Path.Combine(sourceDirectory, "imagen.png"), "no-markdown");

        try
        {
            var result = service.Import([sourceDirectory], ConflictResolution.Cancel);

            Assert.Single(result.Imported);
            Assert.Contains("nota.md", result.Imported);
            Assert.False(File.Exists(Path.Combine(_storageDirectory, "imagen.png")));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Fact]
    public void Export_CopiesFilesToDestination()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "exportable.md");
        File.WriteAllText(path, "---\nname: Exportable\n---\nCuerpo");
        var skill = service.ReadSkill(path);

        var destinationDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-skills-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            SkillService.Export([skill], destinationDirectory);

            Assert.True(File.Exists(Path.Combine(destinationDirectory, "exportable.md")));
        }
        finally
        {
            if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, recursive: true);
        }
    }
}
