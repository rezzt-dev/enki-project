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
    // ===================== Carpetas (organizar la biblioteca por temas) =====================

    [Fact]
    public void LoadSkills_IncludesSkillsInSubfolders_AndReportsTheirRelativeFolder()
    {
        var service = new SkillService(_storageDirectory);
        var nested = Path.Combine(_storageDirectory, "escritura", "blog");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_storageDirectory, "raiz.md"), "---\nname: Raíz\n---\nCuerpo");
        File.WriteAllText(Path.Combine(nested, "redactor.md"), "---\nname: Redactor\n---\nCuerpo");

        var skills = service.LoadSkills();

        Assert.Equal(2, skills.Count);
        var root = skills.Single(s => s.Name == "Raíz");
        var nestedSkill = skills.Single(s => s.Name == "Redactor");
        Assert.Equal(string.Empty, root.RelativeFolderPath);
        Assert.True(root.IsAtRoot);
        Assert.Equal("escritura/blog", nestedSkill.RelativeFolderPath);
        Assert.False(nestedSkill.IsAtRoot);
    }

    [Fact]
    public void LoadSkillsIn_ReturnsOnlyThatFolderWithoutDescending()
    {
        var service = new SkillService(_storageDirectory);
        var nested = Path.Combine(_storageDirectory, "escritura");
        Directory.CreateDirectory(Path.Combine(nested, "blog"));
        File.WriteAllText(Path.Combine(nested, "directa.md"), "---\nname: Directa\n---\nCuerpo");
        File.WriteAllText(Path.Combine(nested, "blog", "profunda.md"), "---\nname: Profunda\n---\nCuerpo");

        var skills = service.LoadSkillsIn(nested);

        Assert.Single(skills);
        Assert.Equal("Directa", skills[0].Name);
    }

    [Fact]
    public void GetFolderTree_NestsSubfoldersAndCountsSkillsDirectlyAndInTotal()
    {
        var service = new SkillService(_storageDirectory);
        var parent = Path.Combine(_storageDirectory, "escritura");
        var child = Path.Combine(parent, "blog");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(parent, "una.md"), "---\nname: Una\n---\nCuerpo");
        File.WriteAllText(Path.Combine(child, "otra.md"), "---\nname: Otra\n---\nCuerpo");

        var tree = service.GetFolderTree();

        var folder = Assert.Single(tree);
        Assert.Equal("escritura", folder.Name);
        Assert.Equal(1, folder.SkillCount);
        Assert.Equal(2, folder.TotalSkillCount);
        var subfolder = Assert.Single(folder.Children);
        Assert.Equal("blog", subfolder.Name);
        Assert.Equal(1, subfolder.TotalSkillCount);
    }

    [Fact]
    public void CreateFolder_AddsANumericSuffixInsteadOfReusingAnExistingName()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);

        var first = service.CreateFolder(_storageDirectory, "escritura");
        var second = service.CreateFolder(_storageDirectory, "escritura");

        Assert.Equal("escritura", first.Name);
        Assert.Equal("escritura 2", second.Name);
        Assert.True(Directory.Exists(second.FullPath));
    }

    [Fact]
    public void RenameFolder_MovesTheDirectoryAndTheSkillsInsideFollowIt()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "vieja");
        File.WriteAllText(Path.Combine(folder.FullPath, "dentro.md"), "---\nname: Dentro\n---\nCuerpo");

        service.RenameFolder(folder, "nueva");

        Assert.False(Directory.Exists(Path.Combine(_storageDirectory, "vieja")));
        var skill = Assert.Single(service.LoadSkills());
        Assert.Equal("nueva", skill.RelativeFolderPath);
    }

    [Fact]
    public void DeleteFolder_RemovesTheFolderAndEverythingInsideIt()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "temporal");
        File.WriteAllText(Path.Combine(folder.FullPath, "dentro.md"), "---\nname: Dentro\n---\nCuerpo");

        service.DeleteFolder(folder);

        Assert.False(Directory.Exists(folder.FullPath));
        Assert.Empty(service.LoadSkills());
    }

    /// <summary>Borrar la carpeta que contiene una skill default sería la puerta trasera para eliminar
    /// justo lo que <see cref="SkillService.DeleteSkill"/> protege.</summary>
    [Fact]
    public void DeleteFolder_WithADefaultSkillInside_ThrowsInvalidOperation()
    {
        var service = new SkillService(_storageDirectory);
        service.EnsureDefaultSkills();
        var defaultSkill = service.LoadSkills().First(s => s.IsDefault);
        var folder = service.CreateFolder(_storageDirectory, "guardada");
        service.MoveSkill(defaultSkill, folder.FullPath);

        Assert.Throws<InvalidOperationException>(() => service.DeleteFolder(folder));
        Assert.True(Directory.Exists(folder.FullPath));
    }

    [Fact]
    public void MoveSkill_MovesTheFileAndKeepsItMarkedAsDefaultWhenItIsOne()
    {
        var service = new SkillService(_storageDirectory);
        service.EnsureDefaultSkills();
        var defaultSkill = service.LoadSkills().First(s => s.IsDefault);
        var folder = service.CreateFolder(_storageDirectory, "sistema");

        var moved = service.MoveSkill(defaultSkill, folder.FullPath);

        Assert.False(File.Exists(defaultSkill.FilePath));
        Assert.Equal("sistema", moved.RelativeFolderPath);
        Assert.True(moved.IsDefault);
    }

    [Fact]
    public void MoveSkill_WhenTheNameIsTakenAtDestination_AddsASuffixInsteadOfOverwriting()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "destino");
        File.WriteAllText(Path.Combine(_storageDirectory, "misma.md"), "---\nname: Origen\n---\nCuerpo origen");
        File.WriteAllText(Path.Combine(folder.FullPath, "misma.md"), "---\nname: Destino\n---\nCuerpo destino");
        var source = service.ReadSkill(Path.Combine(_storageDirectory, "misma.md"));

        var moved = service.MoveSkill(source, folder.FullPath);

        Assert.Equal("misma 2.md", Path.GetFileName(moved.FilePath));
        Assert.Contains("Cuerpo destino", File.ReadAllText(Path.Combine(folder.FullPath, "misma.md")));
    }

    [Fact]
    public void MoveFolder_IntoItsOwnDescendant_ThrowsInvalidOperation()
    {
        var service = new SkillService(_storageDirectory);
        var parent = service.CreateFolder(_storageDirectory, "padre");
        var child = service.CreateFolder(parent.FullPath, "hija");

        Assert.Throws<InvalidOperationException>(() => service.MoveFolder(parent, child.FullPath));
    }

    [Fact]
    public void DuplicateSkill_PutsTheCopyBesideTheOriginal_NotAtTheLibraryRoot()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "escritura");
        var path = Path.Combine(folder.FullPath, "origen.md");
        File.WriteAllText(path, "---\nname: Origen\n---\nCuerpo");
        var source = service.ReadSkill(path);

        var copy = service.DuplicateSkill(source, "copia");

        Assert.Equal("escritura", copy.RelativeFolderPath);
        Assert.True(File.Exists(Path.Combine(folder.FullPath, "copia.md")));
    }

    [Fact]
    public void Import_LandsInTheTargetFolderAndRecreatesTheSourceSubfolders()
    {
        var service = new SkillService(_storageDirectory);
        var target = service.CreateFolder(_storageDirectory, "importadas");
        var sourceDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-skills-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "sub"));
        File.WriteAllText(Path.Combine(sourceDirectory, "arriba.md"), "---\nname: Arriba\n---\nCuerpo");
        File.WriteAllText(Path.Combine(sourceDirectory, "sub", "abajo.md"), "---\nname: Abajo\n---\nCuerpo");

        try
        {
            var result = service.Import([sourceDirectory], ConflictResolution.Cancel, target.FullPath);

            Assert.Equal(2, result.Imported.Count);
            Assert.True(File.Exists(Path.Combine(target.FullPath, "arriba.md")));
            Assert.True(File.Exists(Path.Combine(target.FullPath, "sub", "abajo.md")));
            Assert.Contains(service.LoadSkills(), s => s.RelativeFolderPath == "importadas/sub");
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    [Fact]
    public void Export_RecreatesTheFolderEachSkillLivedIn()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "escritura");
        File.WriteAllText(Path.Combine(folder.FullPath, "dentro.md"), "---\nname: Dentro\n---\nCuerpo");
        File.WriteAllText(Path.Combine(_storageDirectory, "fuera.md"), "---\nname: Fuera\n---\nCuerpo");
        var skills = service.LoadSkills();

        var destinationDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-skills-dst-" + Guid.NewGuid().ToString("N"));
        try
        {
            SkillService.Export(skills, destinationDirectory);

            Assert.True(File.Exists(Path.Combine(destinationDirectory, "fuera.md")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "escritura", "dentro.md")));
        }
        finally
        {
            if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, recursive: true);
        }
    }

    /// <summary>Una biblioteca cuyas skills el usuario movió todas a carpetas no está vacía: volver a
    /// copiar las default sobre ella las duplicaría.</summary>
    [Fact]
    public void EnsureDefaultSkills_DoesNotRecopy_WhenTheOnlySkillsLiveInASubfolder()
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, "mias");
        File.WriteAllText(Path.Combine(folder.FullPath, "mi-skill.md"), "---\nname: Mía\n---\nCuerpo");

        service.EnsureDefaultSkills();

        var skill = Assert.Single(service.LoadSkills());
        Assert.Equal("Mía", skill.Name);
    }
    [Fact]
    public void SaveSkill_ExternalChangePreservesDiskAndDraft()
    {
        var service = new SkillService(_storageDirectory);
        var skill = service.CreateSkill(_storageDirectory, "Original");
        File.WriteAllText(skill.FilePath, "Cambio externo");
        Assert.Throws<IOException>(() => service.SaveSkill(skill, "Borrador"));
        Assert.Equal("Cambio externo", File.ReadAllText(skill.FilePath));
        Assert.Empty(Directory.GetFiles(_storageDirectory, "*.tmp-*"));
    }

    [Fact]
    public void Import_SameFileIsSkippedWithoutThrowing()
    {
        var service = new SkillService(_storageDirectory);
        var skill = service.CreateSkill(_storageDirectory, "Original");
        var result = service.Import([skill.FilePath], ConflictResolution.Overwrite);
        Assert.Single(result.Skipped);
        Assert.Empty(result.Imported);
        Assert.Equal(skill.Content, File.ReadAllText(skill.FilePath));
    }

    [Fact]
    public void CreateFolder_NameCollidingWithAFileUsesSuffix()
    {
        var service = new SkillService(_storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "folder"), "contenido");
        var folder = service.CreateFolder(_storageDirectory, "folder");
        Assert.Equal("folder 2", Path.GetFileName(folder.FullPath));
        Assert.True(Directory.Exists(folder.FullPath));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void CreateFolder_DotNamesStayWithinLibrary(string name)
    {
        var service = new SkillService(_storageDirectory);
        var folder = service.CreateFolder(_storageDirectory, name);
        Assert.Equal(_storageDirectory, Path.GetDirectoryName(folder.FullPath));
    }

    [Fact]
    public void CreateSkill_SameNameDoesNotOverwrite()
    {
        var service = new SkillService(_storageDirectory);
        var first = service.CreateSkill(_storageDirectory, "Analista: experto");
        var second = service.CreateSkill(_storageDirectory, "Analista: experto");
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.Equal("Analista: experto", service.ReadSkill(second.FilePath).Name);
        Assert.Equal(first.Content, File.ReadAllText(first.FilePath));
    }

}
