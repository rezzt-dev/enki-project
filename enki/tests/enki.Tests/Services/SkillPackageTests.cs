using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Paquetes de skills (<c>carpeta/SKILL.md</c> más archivos auxiliares) dentro de la
/// biblioteca: se listan como una sola skill, no como carpeta, y todas las operaciones los tratan como
/// una unidad.</summary>
public sealed class SkillPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-skill-packages-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_root, "library");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string CreatePackage(string parent, string name)
    {
        var package = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        File.WriteAllText(Path.Combine(package, "SKILL.md"), $"---\nname: {name}\ndescription: Paquete {name}\n---\nCuerpo");
        File.WriteAllText(Path.Combine(package, "reference.md"), "Referencia que no es una skill");
        File.WriteAllText(Path.Combine(package, "scripts", "run.sh"), "echo hola");
        return package;
    }

    [Fact]
    public void LoadSkills_ListsPackageOnceAndHidesItFromFolderTree()
    {
        Directory.CreateDirectory(Path.Combine(Library, "tema"));
        var package = CreatePackage(Path.Combine(Library, "tema"), "informes");
        File.WriteAllText(Path.Combine(Library, "suelta.md"), "---\nname: Suelta\n---\nCuerpo");
        var service = new SkillService(Library);

        var skills = service.LoadSkills();

        Assert.Equal(2, skills.Count);
        var packaged = skills.Single(s => s.IsPackage);
        Assert.Equal("informes", packaged.Id);
        Assert.Equal(package, packaged.PackageDirectory);
        Assert.Equal(Path.Combine(Library, "tema"), packaged.FolderPath);
        Assert.Equal("tema", packaged.RelativeFolderPath);
        Assert.Equal(["reference.md", "scripts/run.sh"], SkillService.GetPackageFiles(packaged));

        var tree = service.GetFolderTree();
        var folder = Assert.Single(tree);
        Assert.Equal("tema", Path.GetFileName(folder.FullPath));
        Assert.Empty(folder.Children);
        Assert.Equal(1, folder.SkillCount);
        Assert.Single(service.LoadSkillsIn(Path.Combine(Library, "tema")));
    }

    [Fact]
    public void SkillMdAtLibraryRoot_IsAPlainSkill()
    {
        Directory.CreateDirectory(Library);
        File.WriteAllText(Path.Combine(Library, "SKILL.md"), "---\nname: Raíz\n---\nCuerpo");

        var skill = Assert.Single(new SkillService(Library).LoadSkills());

        Assert.False(skill.IsPackage);
        Assert.Equal("SKILL", skill.Id);
    }

    [Fact]
    public void ConvertToPackage_MovesFileIntoItsOwnFolder()
    {
        Directory.CreateDirectory(Library);
        File.WriteAllText(Path.Combine(Library, "redactor.md"), "---\nname: Redactor\n---\nCuerpo");
        var service = new SkillService(Library);

        var converted = service.ConvertToPackage(service.LoadSkills().Single());

        Assert.True(converted.IsPackage);
        Assert.Equal("redactor", converted.Id);
        Assert.Equal(Path.Combine(Library, "redactor", "SKILL.md"), converted.FilePath);
        Assert.False(File.Exists(Path.Combine(Library, "redactor.md")));
        Assert.Contains("Cuerpo", File.ReadAllText(converted.FilePath));
    }

    [Fact]
    public void DuplicateMoveAndDelete_OperateOnTheWholePackage()
    {
        Directory.CreateDirectory(Library);
        CreatePackage(Library, "origen");
        var service = new SkillService(Library);
        var original = service.LoadSkills().Single();

        var copy = service.DuplicateSkill(original, "copia");
        Assert.True(File.Exists(Path.Combine(Library, "copia", "scripts", "run.sh")));
        Assert.True(copy.IsPackage);

        var moved = service.MoveSkill(copy, Path.Combine(Library, "destino"));
        Assert.Equal(Path.Combine(Library, "destino", "copia"), moved.PackageDirectory);
        Assert.True(File.Exists(Path.Combine(Library, "destino", "copia", "reference.md")));

        service.DeleteSkill(moved);
        Assert.False(Directory.Exists(Path.Combine(Library, "destino", "copia")));
        Assert.True(Directory.Exists(Path.Combine(Library, "origen")));
    }

    [Fact]
    public void Import_PackageFolderKeepsItsNameAndFilesAndRenamesOnConflict()
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        var package = CreatePackage(external, "analista");
        File.WriteAllText(Path.Combine(package, SkillInstallService.ManifestFileName), "{}");
        var service = new SkillService(Library);

        var first = service.Import([package], ConflictResolution.Rename);
        Assert.Equal(["analista"], first.Imported);
        Assert.True(File.Exists(Path.Combine(Library, "analista", "scripts", "run.sh")));
        Assert.False(File.Exists(Path.Combine(Library, "analista", SkillInstallService.ManifestFileName)));

        Assert.Equal(["analista"], SkillService.DetectImportConflicts([package], Library));
        var second = service.Import([package], ConflictResolution.Rename);

        Assert.Equal(["analista 2"], second.Imported);
        Assert.True(File.Exists(Path.Combine(Library, "analista 2", "SKILL.md")));
        Assert.Equal(2, service.LoadSkills().Count);
    }

    [Fact]
    public void Import_FolderWithNestedPackagesCopiesThemWhole()
    {
        var external = Path.Combine(_root, "coleccion");
        Directory.CreateDirectory(external);
        CreatePackage(external, "uno");
        File.WriteAllText(Path.Combine(external, "suelta.md"), "---\nname: Suelta\n---\nCuerpo");
        var service = new SkillService(Library);

        service.Import([external], ConflictResolution.Rename);

        Assert.True(File.Exists(Path.Combine(Library, "suelta.md")));
        Assert.True(File.Exists(Path.Combine(Library, "uno", "scripts", "run.sh")));
        Assert.Equal(2, service.LoadSkills().Count);
    }

    [Fact]
    public void Export_CopiesPackageWithItsFiles()
    {
        Directory.CreateDirectory(Library);
        CreatePackage(Library, "exportable");
        var service = new SkillService(Library);
        var destination = Path.Combine(_root, "export");

        SkillService.Export(service.LoadSkills(), destination);

        Assert.True(File.Exists(Path.Combine(destination, "exportable", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(destination, "exportable", "scripts", "run.sh")));
    }
}
