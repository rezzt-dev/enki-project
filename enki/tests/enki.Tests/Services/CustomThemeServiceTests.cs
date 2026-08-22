using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="CustomThemeService"/> against a real (throwaway-directory)
/// <see cref="SettingsService"/> — same pattern as <see cref="SkillServiceTests"/>. Every test runs without
/// an <see cref="Avalonia.Application"/>, same as <c>SettingsViewModelTests</c>; <see cref="ThemeRegistry"/>
/// and <c>RequestedThemeVariant</c> both no-op when <c>Avalonia.Application.Current</c> is null, so only the
/// persisted state (<see cref="SettingsService.Settings"/>) is asserted here.</summary>
public class CustomThemeServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-themes-" + Guid.NewGuid().ToString("N"));

    private readonly string _sourceDirectory;

    public CustomThemeServiceTests()
    {
        _sourceDirectory = Path.Combine(_storageDirectory, "source-files");
        Directory.CreateDirectory(_sourceDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (CustomThemeService Service, SettingsService SettingsService) CreateService()
    {
        var settingsService = new SettingsService(_storageDirectory);
        return (new CustomThemeService(settingsService), settingsService);
    }

    private string WriteSourceFile(string fileName, string content)
    {
        var path = Path.Combine(_sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Import_ValidTemplate_RegistersTheme_UsingFileNameAsTitle()
    {
        var (service, settingsService) = CreateService();
        var sourcePath = WriteSourceFile("My Theme.css", ThemeCssParser.BuildTemplate());

        var result = service.Import(sourcePath);

        Assert.True(result.Success);
        Assert.NotNull(result.ThemeId);
        var registered = Assert.Single(settingsService.Settings.CustomThemes);
        Assert.Equal(result.ThemeId, registered.Id);
        Assert.Equal("My Theme", registered.Name);
    }

    [Fact]
    public void Import_InvalidFile_TouchesNoState_AndReturnsErrors()
    {
        var (service, settingsService) = CreateService();
        var sourcePath = WriteSourceFile("broken.css", ":root { --not-a-real-variable: #000000; }");

        var result = service.Import(sourcePath);

        Assert.False(result.Success);
        Assert.Null(result.ThemeId);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(settingsService.Settings.CustomThemes);
    }

    [Fact]
    public void Activate_PersistsActiveThemeId()
    {
        var (service, settingsService) = CreateService();
        var sourcePath = WriteSourceFile("theme.css", ThemeCssParser.BuildTemplate());
        var themeId = service.Import(sourcePath).ThemeId!;

        var activated = service.Activate(themeId);

        Assert.True(activated);
        Assert.Equal(themeId, service.ActiveThemeId);
        Assert.Equal(themeId, settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void Activate_UnknownId_ReturnsFalse_AndChangesNothing()
    {
        var (service, settingsService) = CreateService();

        var activated = service.Activate("does-not-exist");

        Assert.False(activated);
        Assert.Null(settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void Deactivate_ClearsActiveThemeId()
    {
        var (service, settingsService) = CreateService();
        var themeId = service.Import(WriteSourceFile("theme.css", ThemeCssParser.BuildTemplate())).ThemeId!;
        service.Activate(themeId);

        service.Deactivate();

        Assert.Null(service.ActiveThemeId);
        Assert.Null(settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void Delete_RemovesRegistration_AndFileFromDisk()
    {
        var (service, settingsService) = CreateService();
        var themeId = service.Import(WriteSourceFile("theme.css", ThemeCssParser.BuildTemplate())).ThemeId!;
        var themeFilePath = Path.Combine(Path.GetDirectoryName(settingsService.SettingsFilePath)!, "themes", $"{themeId}.css");
        Assert.True(File.Exists(themeFilePath));

        service.Delete(themeId);

        Assert.Empty(settingsService.Settings.CustomThemes);
        Assert.False(File.Exists(themeFilePath));
    }

    [Fact]
    public void Delete_TheActiveTheme_DeactivatesItFirst()
    {
        var (service, settingsService) = CreateService();
        var themeId = service.Import(WriteSourceFile("theme.css", ThemeCssParser.BuildTemplate())).ThemeId!;
        service.Activate(themeId);

        service.Delete(themeId);

        Assert.Null(settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void ApplyActiveOnStartup_WithStaleThemeId_ClearsIt_InsteadOfThrowing()
    {
        var (_, settingsService) = CreateService();
        settingsService.Settings.ActiveCustomThemeId = "a-ghost-id-from-a-deleted-theme";
        settingsService.Save();

        var reloadedService = new CustomThemeService(settingsService);
        reloadedService.ApplyActiveOnStartup();

        Assert.Null(settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void ApplyActiveOnStartup_WithNoActiveTheme_IsANoOp()
    {
        var (service, settingsService) = CreateService();

        service.ApplyActiveOnStartup();

        Assert.Null(settingsService.Settings.ActiveCustomThemeId);
    }

    [Fact]
    public void Create_PersistsThemeWithDefaultColors()
    {
        var (service, settingsService) = CreateService();

        var theme = service.Create("My Theme");

        Assert.Equal("My Theme", theme.Name);
        Assert.Equal(ThemeCatalog.All.Count, theme.Colors.Count);
        Assert.Contains(settingsService.Settings.CustomThemes, t => t.Id == theme.Id);
        Assert.True(File.Exists(Path.Combine(_storageDirectory, "themes", $"{theme.Id}.css")));
    }

    [Fact]
    public void Save_WritesModifiedColors_AndLoadReadsThemBack()
    {
        var (service, _) = CreateService();
        var theme = service.Create("Editable");
        theme.Colors["AppBackgroundColor"] = "#FF00FF";

        service.Save(theme);
        var loaded = service.Load(theme.Id);

        Assert.NotNull(loaded);
        Assert.Equal("#FF00FF", loaded!.Colors["AppBackgroundColor"]);
    }

    [Fact]
    public void Rename_UpdatesPersistedName()
    {
        var (service, settingsService) = CreateService();
        var theme = service.Create("Old");

        service.Rename(theme.Id, "Renamed");

        Assert.Equal("Renamed", settingsService.Settings.CustomThemes[0].Name);
    }
}
