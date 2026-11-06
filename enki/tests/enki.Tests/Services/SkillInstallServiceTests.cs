using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="SkillInstallService"/> con una carpeta personal temporal: ningún test toca el
/// <c>~/.claude</c>, <c>~/.agents</c> ni <c>~/.gemini</c> reales del desarrollador, y las variables de
/// entorno se sustituyen por una función vacía.</summary>
public sealed class SkillInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-skill-install-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");
    private string Library => Path.Combine(_root, "library");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SkillInstallService CreateInstaller() => new(Home, _ => null);

    private SkillFile WriteSkill(string fileName, string content)
    {
        Directory.CreateDirectory(Library);
        var path = Path.Combine(Library, fileName);
        File.WriteAllText(path, content);
        return new SkillService(Library).ReadSkill(path);
    }

    private string ClaudeSkillDir(string name) => Path.Combine(Home, ".claude", "skills", name);

    [Fact]
    public void GetTargets_ResolvesUserFoldersAndHonoursClaudeConfigDir()
    {
        var configured = Path.Combine(_root, "claude-config");
        var installer = new SkillInstallService(Home, name => name == "CLAUDE_CONFIG_DIR" ? configured : null);

        var targets = installer.GetTargets();

        Assert.Equal(Path.Combine(configured, "skills"), targets.Single(t => t.Kind == SkillInstallTargetKind.ClaudeCode).SkillsDirectory);
        Assert.Equal(Path.Combine(Home, ".agents", "skills"), targets.Single(t => t.Kind == SkillInstallTargetKind.Codex).SkillsDirectory);
        Assert.Equal(Path.Combine(Home, ".gemini", "skills"), targets.Single(t => t.Kind == SkillInstallTargetKind.GeminiCli).SkillsDirectory);
        Assert.All(targets, target => Assert.False(target.IsAssistantDetected));
    }

    [Theory]
    [InlineData("Revisión de código", "revision-de-codigo")]
    [InlineData("Diseño   año 2", "diseno-ano-2")]
    [InlineData("--Mi_Skill--", "mi-skill")]
    public void GetInstallName_FollowsAgentSkillsNaming(string id, string expected)
    {
        Assert.Equal(expected, SkillInstallService.GetInstallName(new SkillFile { Id = id, FilePath = "/x.md" }));
    }

    [Fact]
    public void GetInstallName_LimitsLengthAndFallsBackForSymbolsOnly()
    {
        Assert.True(SkillInstallService.GetInstallName(new SkillFile { Id = new string('a', 100), FilePath = "/a.md" }).Length <= 64);
        Assert.StartsWith("skill-", SkillInstallService.GetInstallName(new SkillFile { Id = "★★", FilePath = "/b.md" }));
    }

    [Fact]
    public void Install_WritesNativeSkillWithoutEnkiFieldsAndKeepsNativeOnes()
    {
        var skill = WriteSkill("Revisor PR.md",
            "---\nname: Revisor de PR\ndescription: Revisa pull requests. Úsala al revisar cambios.\nemoji: \"🔍\"\ntags:\n  - git\n  - revisión\npermissions: []\nallowed-tools: Bash(git diff *)\n---\n\nRevisa el diff.\n");

        var status = CreateInstaller().Install(skill, SkillInstallTargetKind.ClaudeCode);

        Assert.Equal(SkillInstallState.Installed, status.State);
        var installed = File.ReadAllText(Path.Combine(ClaudeSkillDir("revisor-pr"), "SKILL.md"));
        Assert.StartsWith("---\nname: revisor-pr\n", installed);
        Assert.Contains("description: Revisa pull requests. Úsala al revisar cambios.", installed);
        Assert.Contains("allowed-tools: Bash(git diff *)", installed);
        Assert.DoesNotContain("emoji", installed);
        Assert.DoesNotContain("tags", installed);
        Assert.DoesNotContain("revisión", installed);
        Assert.DoesNotContain("permissions", installed);
        Assert.Contains("Revisa el diff.", installed);

        var manifest = JsonSerializer.Deserialize<SkillInstallManifest>(
            File.ReadAllText(Path.Combine(ClaudeSkillDir("revisor-pr"), SkillInstallService.ManifestFileName)))!;
        Assert.Equal(Path.GetFullPath(skill.FilePath), manifest.SourcePath);
        Assert.Equal("enki", manifest.Generator);
    }

    [Fact]
    public void Install_GeneratesDescriptionAndLeavesNoStagingFolders()
    {
        var skill = WriteSkill("sin-descripcion.md", "Solo cuerpo, sin frontmatter.");

        CreateInstaller().Install(skill, SkillInstallTargetKind.Codex);

        var installed = File.ReadAllText(Path.Combine(Home, ".agents", "skills", "sin-descripcion", "SKILL.md"));
        Assert.Contains("name: sin-descripcion", installed);
        Assert.Contains("description: sin-descripcion", installed);
        Assert.Contains("Solo cuerpo, sin frontmatter.", installed);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Home, ".agents"), ".enki-*"));
        Assert.Contains(SkillInstallService.Validate(skill), w => w.ResourceKey == "SkillInstall_Warning_NoFrontMatter");
    }

    [Fact]
    public void SyncInstalled_UpdatesOutdatedCopies()
    {
        var installer = CreateInstaller();
        var skill = WriteSkill("notas.md", "---\nname: Notas\ndescription: Primera\n---\nUno");
        installer.Install(skill, SkillInstallTargetKind.ClaudeCode);

        File.WriteAllText(skill.FilePath, "---\nname: Notas\ndescription: Segunda\n---\nDos");
        skill = new SkillService(Library).ReadSkill(skill.FilePath);
        Assert.Equal(SkillInstallState.Outdated, installer.GetStatus(skill, SkillInstallTargetKind.ClaudeCode).State);

        var (updated, failed) = installer.SyncInstalled(skill);

        Assert.Single(updated);
        Assert.Empty(failed);
        Assert.Equal(SkillInstallState.Installed, installer.GetStatus(skill, SkillInstallTargetKind.ClaudeCode).State);
        Assert.Contains("Dos", File.ReadAllText(Path.Combine(ClaudeSkillDir("notas"), "SKILL.md")));
        // Los destinos donde no estaba instalada siguen sin tocarse.
        Assert.False(Directory.Exists(Path.Combine(Home, ".gemini")));
    }

    [Fact]
    public void ExternalEdits_AreDetectedAndNeverOverwrittenSilently()
    {
        var installer = CreateInstaller();
        var skill = WriteSkill("guia.md", "---\nname: Guía\ndescription: Original\n---\nCuerpo");
        installer.Install(skill, SkillInstallTargetKind.ClaudeCode);
        var installedPath = Path.Combine(ClaudeSkillDir("guia"), "SKILL.md");
        File.AppendAllText(installedPath, "\nCambio hecho a mano.");

        File.WriteAllText(skill.FilePath, "---\nname: Guía\ndescription: Nueva\n---\nCuerpo nuevo");
        skill = new SkillService(Library).ReadSkill(skill.FilePath);

        Assert.Equal(SkillInstallState.ModifiedExternally, installer.GetStatus(skill, SkillInstallTargetKind.ClaudeCode).State);
        Assert.Empty(installer.SyncInstalled(skill).Updated);
        Assert.Contains("Cambio hecho a mano.", File.ReadAllText(installedPath));
        Assert.Throws<InvalidOperationException>(() => installer.Install(skill, SkillInstallTargetKind.ClaudeCode));

        installer.Install(skill, SkillInstallTargetKind.ClaudeCode, overwriteExternalChanges: true);

        Assert.DoesNotContain("Cambio hecho a mano.", File.ReadAllText(installedPath));
        Assert.Equal(SkillInstallState.Installed, installer.GetStatus(skill, SkillInstallTargetKind.ClaudeCode).State);
    }

    [Fact]
    public void ForeignSkillWithSameName_IsNeverReplacedOrRemoved()
    {
        var installer = CreateInstaller();
        var foreign = ClaudeSkillDir("deploy");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "SKILL.md"), "---\nname: deploy\ndescription: Mía\n---\nNo tocar");
        var skill = WriteSkill("deploy.md", "---\nname: Deploy\ndescription: De Enki\n---\nOtra");

        Assert.Equal(SkillInstallState.NameTaken, installer.GetStatus(skill, SkillInstallTargetKind.ClaudeCode).State);
        Assert.Throws<InvalidOperationException>(() => installer.Install(skill, SkillInstallTargetKind.ClaudeCode));
        Assert.Throws<InvalidOperationException>(() => installer.Uninstall(skill, SkillInstallTargetKind.ClaudeCode));

        var listed = installer.ListSystemSkills().Single();
        Assert.False(listed.IsManagedByEnki);
        Assert.Throws<InvalidOperationException>(() => installer.Uninstall(listed));
        Assert.Contains("No tocar", File.ReadAllText(Path.Combine(foreign, "SKILL.md")));
    }

    [Fact]
    public void AnotherLibrarySkillWithSameInstallName_IsNameTaken()
    {
        var installer = CreateInstaller();
        var first = WriteSkill("doble.md", "---\nname: A\ndescription: A\n---\nA");
        installer.Install(first, SkillInstallTargetKind.ClaudeCode);
        Directory.CreateDirectory(Path.Combine(Library, "otra"));
        File.WriteAllText(Path.Combine(Library, "otra", "doble.md"), "---\nname: B\ndescription: B\n---\nB");
        var second = new SkillService(Library).ReadSkill(Path.Combine(Library, "otra", "doble.md"));

        Assert.Equal(SkillInstallState.NameTaken, installer.GetStatus(second, SkillInstallTargetKind.ClaudeCode).State);
    }

    [Fact]
    public void Package_InstallsAuxiliaryFilesWithoutGitAndUninstallsCleanly()
    {
        var installer = CreateInstaller();
        var package = Path.Combine(Library, "informe");
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        Directory.CreateDirectory(Path.Combine(package, ".git"));
        File.WriteAllText(Path.Combine(package, "SKILL.md"), "---\nname: Informe\ndescription: Genera informes\n---\nUsa scripts/run.sh");
        File.WriteAllText(Path.Combine(package, "scripts", "run.sh"), "echo hola");
        File.WriteAllText(Path.Combine(package, "reference.md"), "Referencia");
        File.WriteAllText(Path.Combine(package, ".git", "HEAD"), "ref");
        var skill = new SkillService(Library).ReadSkill(Path.Combine(package, "SKILL.md"));

        installer.Install(skill, SkillInstallTargetKind.GeminiCli);

        var installed = Path.Combine(Home, ".gemini", "skills", "informe");
        Assert.Equal("echo hola", File.ReadAllText(Path.Combine(installed, "scripts", "run.sh")));
        Assert.True(File.Exists(Path.Combine(installed, "reference.md")));
        Assert.False(Directory.Exists(Path.Combine(installed, ".git")));
        Assert.Equal(SkillInstallState.Installed, installer.GetStatus(skill, SkillInstallTargetKind.GeminiCli).State);

        File.WriteAllText(Path.Combine(package, "scripts", "run.sh"), "echo adiós");
        Assert.Equal(SkillInstallState.Outdated, installer.GetStatus(skill, SkillInstallTargetKind.GeminiCli).State);

        installer.Uninstall(skill, SkillInstallTargetKind.GeminiCli);
        Assert.False(Directory.Exists(installed));
        Assert.True(File.Exists(Path.Combine(package, "scripts", "run.sh")));
    }

    [Fact]
    public void MovedSource_IsAdoptedAndResynced()
    {
        var installer = CreateInstaller();
        var library = new SkillService(Library);
        var skill = WriteSkill("viaje.md", "---\nname: Viaje\ndescription: Se mueve\n---\nCuerpo");
        installer.Install(skill, SkillInstallTargetKind.ClaudeCode);

        var moved = library.MoveSkill(skill, Path.Combine(Library, "tema"));

        Assert.Equal(SkillInstallState.Outdated, installer.GetStatus(moved, SkillInstallTargetKind.ClaudeCode).State);
        Assert.Single(installer.SyncInstalled(moved).Updated);
        var listed = installer.ListSystemSkills().Single();
        Assert.True(listed.IsManagedByEnki);
        Assert.False(listed.IsSourceMissing);
        Assert.Equal(Path.GetFullPath(moved.FilePath), listed.SourcePath);
    }

    [Fact]
    public void ListSystemSkills_FlagsMissingSourceAndAllowsUninstallOfManagedOnes()
    {
        var installer = CreateInstaller();
        var skill = WriteSkill("huerfana.md", "---\nname: Huérfana\ndescription: Sin origen\n---\nCuerpo");
        installer.Install(skill, SkillInstallTargetKind.Codex);
        File.Delete(skill.FilePath);

        var listed = installer.ListSystemSkills().Single();

        Assert.True(listed.IsManagedByEnki);
        Assert.True(listed.IsSourceMissing);
        // El listado muestra el name instalado, ya normalizado al estándar.
        Assert.Equal("huerfana", listed.Name);
        installer.Uninstall(listed);
        Assert.Empty(installer.ListSystemSkills());
    }

    [Fact]
    public void LongDescription_IsTruncatedToTheStandardLimit()
    {
        var skill = WriteSkill("larga.md", $"---\nname: Larga\ndescription: {new string('x', 1500)}\n---\nCuerpo");

        var rendered = SkillInstallService.RenderSkillMarkdown(skill);
        var description = SkillService.ReadFrontMatterSummary(rendered).Description!;

        Assert.True(description.Length <= SkillInstallService.MaxDescriptionLength);
        Assert.Contains(SkillInstallService.Validate(skill), w => w.ResourceKey == "SkillInstall_Warning_LongDescription");
    }
}
