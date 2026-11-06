using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="DailyNoteService"/> sobre un vault temporal: encontrar la nota de un día,
/// crearla aplicando la plantilla configurada, no duplicarla nunca y resolver de una pasada qué días de un
/// intervalo tienen nota (lo que pinta el calendario de constancia).</summary>
public class DailyNoteServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-daily-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public DailyNoteServiceTests() => _vaultPath = Path.Combine(_storageDirectory, "vault");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (DailyNoteService Daily, VaultService Vault, VaultSettingsService VaultSettings) CreateSetup()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettings = new VaultSettingsService(vault);
        var templates = new NoteTemplateService(vault);
        return (new DailyNoteService(vault, templates, vaultSettings), vault, vaultSettings);
    }

    /// <summary>Escribe una plantilla en la carpeta de plantillas del vault y la deja configurada como la
    /// de la nota diaria, que es lo que hace Ajustes › Editor › Plantillas.</summary>
    private void ConfigureTemplate(VaultSettingsService vaultSettings, string fileName, string content)
    {
        var directory = Path.Combine(_vaultPath, ".enki", NoteTemplateService.TemplatesFolderName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        vaultSettings.Settings.DailyNoteTemplate = fileName;
        vaultSettings.Save();
    }

    private static readonly DateOnly SampleDate = new(2026, 9, 18);

    [Fact]
    public void OpenOrCreate_WithoutTemplate_CreatesTheIsoNamedNote()
    {
        var (daily, _, _) = CreateSetup();

        var opening = daily.OpenOrCreate(SampleDate, _vaultPath);

        Assert.True(opening.Created);
        Assert.Equal("2026-09-18", opening.Note.FileNameWithoutExtension);
        Assert.True(File.Exists(Path.Combine(_vaultPath, "2026-09-18.md")));
    }

    [Fact]
    public void OpenOrCreate_AppliesTheConfiguredTemplateOnCreation()
    {
        var (daily, vault, vaultSettings) = CreateSetup();
        ConfigureTemplate(vaultSettings, "daily.md",
            "---\nname: Diario\ntitle: \"Diario {{date}}\"\n---\n\n# {{date}}\n\n## Hoy\n{{cursor}}\n");

        var opening = daily.OpenOrCreate(SampleDate, _vaultPath);

        Assert.True(opening.Created);
        Assert.Equal("Diario 2026-09-18", opening.Note.FileNameWithoutExtension);
        var content = vault.ReadContent(opening.Note);
        Assert.Contains("# 2026-09-18", content);
        Assert.Contains("## Hoy", content);
        Assert.NotNull(opening.CaretOffset);
    }

    [Fact]
    public void OpenOrCreate_Twice_ReturnsTheSameNoteInsteadOfASecondOne()
    {
        var (daily, _, _) = CreateSetup();

        var first = daily.OpenOrCreate(SampleDate, _vaultPath);
        var second = daily.OpenOrCreate(SampleDate, _vaultPath);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Note.FullPath, second.Note.FullPath);
        Assert.Single(Directory.GetFiles(_vaultPath, "*.md"));
    }

    /// <summary>Configurar una plantilla con otro <c>title</c> no puede crear una segunda nota para un día
    /// que ya tenía la suya con el nombre anterior.</summary>
    [Fact]
    public void OpenOrCreate_FindsAPreExistingIsoNamedNoteEvenWithATemplateConfigured()
    {
        var (daily, _, vaultSettings) = CreateSetup();
        File.WriteAllText(Path.Combine(_vaultPath, "2026-09-18.md"), "# 2026-09-18\n\nya escrito\n");
        ConfigureTemplate(vaultSettings, "daily.md", "---\nname: Diario\ntitle: \"Diario {{date}}\"\n---\n\n## Hoy\n");

        var opening = daily.OpenOrCreate(SampleDate, _vaultPath);

        Assert.False(opening.Created);
        Assert.Equal("2026-09-18", opening.Note.FileNameWithoutExtension);
        Assert.Single(Directory.GetFiles(_vaultPath, "*.md"));
    }

    [Fact]
    public void OpenOrCreate_FindsTheNoteInAnySubfolderOfTheVault()
    {
        var (daily, _, _) = CreateSetup();
        var folder = Path.Combine(_vaultPath, "diario");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "2026-09-18.md"), "# 2026-09-18\n");

        var opening = daily.OpenOrCreate(SampleDate, _vaultPath);

        Assert.False(opening.Created);
        Assert.Equal(Path.Combine(folder, "2026-09-18.md"), opening.Note.FullPath);
    }

    [Fact]
    public void DateOf_RecognizesADailyNoteAndIgnoresAnyOtherNote()
    {
        var (daily, vault, _) = CreateSetup();
        var note = vault.CreateNote("2026-09-18", _vaultPath);
        var other = vault.CreateNote("Ideas sueltas", _vaultPath);

        Assert.Equal(SampleDate, daily.DateOf(note));
        Assert.Null(daily.DateOf(other));
    }

    /// <summary>El día lo decide el nombre del archivo, no el primer encabezado: una nota cualquiera que
    /// empiece por "# 2026-09-18" no es la nota diaria de ese día.</summary>
    [Fact]
    public void DateOf_IgnoresTheNoteTitleInsideTheContent()
    {
        var (daily, vault, _) = CreateSetup();
        var note = vault.CreateNote("Ideas", _vaultPath, "# 2026-09-18\n\ntexto\n");

        Assert.Null(daily.DateOf(note));
    }

    [Fact]
    public void FindInRange_ReturnsOnlyTheDaysInsideTheWindow()
    {
        var (daily, _, _) = CreateSetup();
        foreach (var name in new[] { "2026-09-10", "2026-09-18", "2026-09-30", "Ideas" })
            File.WriteAllText(Path.Combine(_vaultPath, name + ".md"), "# " + name + "\n");

        var found = daily.FindInRange(new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 25));

        Assert.Equal([SampleDate], found.Keys.OrderBy(d => d));
    }

    [Fact]
    public void PendingSections_ListsWhatTheTemplateWouldAdd()
    {
        var (daily, _, vaultSettings) = CreateSetup();
        ConfigureTemplate(vaultSettings, "daily.md", "---\nname: Diario\n---\n\n## Hoy\n\n## Mañana\n");

        var pending = daily.PendingSections(SampleDate, "# 2026-09-18\n\n## Hoy\n\n- hecho\n");

        Assert.Equal(["Mañana"], pending.Select(section => section.Heading));
    }

    [Fact]
    public void BuildMergedContent_WithoutAConfiguredTemplate_IsNull()
    {
        var (daily, _, _) = CreateSetup();

        Assert.Null(daily.BuildMergedContent(SampleDate, "# 2026-09-18\n"));
        Assert.Empty(daily.PendingSections(SampleDate, "# 2026-09-18\n"));
    }

    [Fact]
    public void BuildMergedContent_AddsTheMissingSectionsWithTheirVariablesExpanded()
    {
        var (daily, _, vaultSettings) = CreateSetup();
        ConfigureTemplate(vaultSettings, "daily.md", "---\nname: Diario\n---\n\n## Hoy\n\n## Resumen de {{date}}\n");

        var merged = daily.BuildMergedContent(SampleDate, "## Hoy\n\n- hecho\n");

        Assert.NotNull(merged);
        Assert.Contains("## Resumen de 2026-09-18", merged);
        Assert.Contains("- hecho", merged);
    }
}
