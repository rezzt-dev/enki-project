using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class PersonalizationProfileServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-profile-tests-" + Guid.NewGuid().ToString("N"));

    private (SettingsService Settings, VaultSettingsService Panel, CustomThemeService Themes, PersonalizationProfileService Profile, string Vault) Create(string name)
    {
        var directory = Path.Combine(_directory, name);
        var settings = new SettingsService(directory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
        var panel = new VaultSettingsService(vault);
        var themes = new CustomThemeService(settings);
        return (settings, panel, themes, new PersonalizationProfileService(settings, panel, themes), vault.VaultPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void RoundTrip_ReproducesAppearanceShortcutsSidebarAndCustomGrid_WithoutUserData()
    {
        var source = Create("source");
        var target = Create("target");
        var theme = source.Themes.Create("Noche añil");
        theme.Colors[ThemeCatalog.All[0].ResourceKey] = "#123456";
        source.Themes.Save(theme);
        source.Settings.Settings.ActiveCustomThemeId = theme.Id;
        source.Settings.Settings.ShortcutOverrides[ShortcutCatalog.All[0].Id] = "Ctrl+Alt+F9";
        source.Settings.Settings.SidebarMainSectionOrder = ["Notes", "Calendar"];
        source.Settings.Settings.SidebarBottomSectionOrder = ["Files"];
        source.Settings.Settings.SidebarSectionNames["Notes"] = "Cuaderno";
        source.Settings.Settings.NotesPanelOnRight = true;
        source.Settings.Settings.UseSubtleAnimations = true;
        source.Settings.Settings.InterfaceFontFamily = "My font";
        source.Settings.Settings.NotesPanelEnabled = false;
        source.Settings.Settings.PolarEmail = "private@example.invalid";
        source.Settings.Settings.OpenNotePaths = ["private/note.md"];
        source.Settings.Settings.Ai.CodexCliPath = "private/cli";
        source.Panel.Settings.DashboardLayoutStyle = DashboardLayoutEngine.Custom;
        source.Panel.Settings.DashboardGridColumns = 8;
        source.Panel.Settings.DashboardGridRows = 6;
        source.Panel.Settings.DashboardSectionOrder = ["Today", "QuickAccess"];
        source.Panel.Settings.DashboardHiddenSections = ["Habits"];
        source.Panel.Settings.DashboardCustomLayout["Today"] = [1, 2, 2, 3];
        source.Panel.Settings.UiZoomPercent = 130;
        var localTheme = target.Themes.Create("Local");
        File.WriteAllText(Path.Combine(target.Vault, "note.md"), "Conservar esta nota");
        var note = File.ReadAllBytes(Path.Combine(target.Vault, "note.md"));
        target.Panel.Settings.VaultId = "target-id";
        target.Panel.Settings.DailyNoteTemplate = "local.md";
        target.Panel.Save();
        target.Settings.Settings.PolarEmail = "local@example.invalid";
        target.Settings.Settings.OpenNotePaths = ["local.md"];
        target.Settings.Save();
        var json = source.Profile.Export();
        Assert.DoesNotContain(source.Vault, json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("Polar", json);
        Assert.DoesNotContain("VaultId", json);
        target.Profile.Apply(json);
        Assert.Equal(target.Vault, target.Settings.Settings.VaultPath);
        Assert.Equal("local@example.invalid", target.Settings.Settings.PolarEmail);
        Assert.Equal(new[] { "local.md" }, target.Settings.Settings.OpenNotePaths);
        Assert.Equal(note, File.ReadAllBytes(Path.Combine(target.Vault, "note.md")));
        Assert.Equal("target-id", target.Panel.Settings.VaultId);
        Assert.Equal("local.md", target.Panel.Settings.DailyNoteTemplate);
        Assert.True(target.Settings.Settings.NotesPanelOnRight);
        Assert.True(target.Settings.Settings.UseSubtleAnimations);
        Assert.False(target.Settings.Settings.NotesPanelEnabled);
        Assert.Equal("My font", target.Settings.Settings.InterfaceFontFamily);
        Assert.Equal("Cuaderno", target.Settings.Settings.SidebarSectionNames["Notes"]);
        Assert.Equal("Notes", target.Settings.Settings.SidebarMainSectionOrder[0]);
        Assert.Equal("Files", target.Settings.Settings.SidebarBottomSectionOrder[0]);
        Assert.Equal("Ctrl+Alt+F9", target.Settings.Settings.ShortcutOverrides[ShortcutCatalog.All[0].Id]);
        Assert.Equal(DashboardLayoutEngine.Custom, target.Panel.Settings.DashboardLayoutStyle);
        Assert.Equal(new[] { 1, 2, 2, 3 }, target.Panel.Settings.DashboardCustomLayout["Today"]);
        Assert.Equal(130, target.Panel.Settings.UiZoomPercent);
        Assert.Equal(8, target.Panel.Settings.DashboardGridColumns);
        Assert.Equal(new[] { "Habits" }, target.Panel.Settings.DashboardHiddenSections);
        Assert.Equal("Today", target.Panel.Settings.DashboardSectionOrder[0]);
        Assert.NotNull(target.Themes.Load(localTheme.Id));
        var imported = target.Themes.Load(target.Settings.Settings.ActiveCustomThemeId!)!;
        Assert.NotEqual(theme.Id, imported.Id);
        Assert.Equal(theme.Name, imported.Name);
        Assert.Equal(theme.Colors[ThemeCatalog.All[0].ResourceKey], imported.Colors[ThemeCatalog.All[0].ResourceKey]);
        var reloaded = new SettingsService(Path.Combine(_directory, "target"));
        Assert.Equal(imported.Id, reloaded.Settings.ActiveCustomThemeId);
        Assert.True(reloaded.Settings.NotesPanelOnRight);
        Assert.Equal(new[] { "local.md" }, reloaded.Settings.OpenNotePaths);
        Assert.Equal("local@example.invalid", reloaded.Settings.PolarEmail);
        target.Panel.Reload();
        Assert.Equal(130, target.Panel.Settings.UiZoomPercent);
    }

    [Theory]
    [InlineData("VaultPath")]
    [InlineData("PolarAccessToken")]
    [InlineData("Ai")]
    [InlineData("OpenNotePaths")]
    public void Import_RejectsUnlistedPreferences_BeforeAnyWrite(string key)
    {
        var target = Create("target");
        var profile = JsonNode.Parse(target.Profile.Export())!;
        profile["Preferences"]![key] = "do-not-import";
        var settingsPath = Path.Combine(target.Vault, ".enki", "settings.json");
        var before = File.ReadAllText(settingsPath);
        Assert.Throws<InvalidDataException>(() => target.Profile.Apply(profile.ToJsonString()));
        Assert.Equal(before, File.ReadAllText(settingsPath));
        Assert.Empty(target.Themes.Themes);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing-header")]
    [InlineData("missing-preference")]
    [InlineData("null-preference")]
    [InlineData("shortcut")]
    [InlineData("unknown-shortcut")]
    [InlineData("theme")]
    [InlineData("missing-theme")]
    [InlineData("layout")]
    [InlineData("geometry")]
    [InlineData("private-dashboard")]
    public void Validate_RejectsInvalidProfiles(string kind)
    {
        var source = Create("source");
        var profile = JsonNode.Parse(source.Profile.Export())!.AsObject();
        switch (kind)
        {
            case "version": profile["Version"] = 200; break;
            case "missing-header": profile.Remove("Format"); break;
            case "missing-preference": profile["Preferences"]!.AsObject().Remove("Theme"); break;
            case "null-preference": profile["Preferences"]!["ShortcutOverrides"] = null; break;
            case "shortcut": profile["Preferences"]!["ShortcutOverrides"]![ShortcutCatalog.All[0].Id] = "not-a-key"; break;
            case "unknown-shortcut": profile["Preferences"]!["ShortcutOverrides"]!["unknown"] = "Ctrl+F9"; break;
            case "theme": profile["Themes"]!.AsArray().Add(new JsonObject { ["Id"] = "../../escape", ["Name"] = "Bad", ["Colors"] = new JsonObject() }); break;
            case "missing-theme": profile["Preferences"]!["ActiveCustomThemeId"] = "missing"; break;
            case "layout": profile["Dashboard"]!["DashboardLayoutStyle"] = "unknown"; break;
            case "geometry": profile["Dashboard"]!["DashboardCustomLayout"]!["Today"] = new JsonArray(0, int.MaxValue, 1, 1); break;
            case "private-dashboard": profile["Dashboard"]!["VaultId"] = "foreign"; break;
        }
        Assert.Throws<InvalidDataException>(() => PersonalizationProfileService.Validate(profile.ToJsonString()));
    }

    [Fact]
    public void Validate_NormalizesUnsafeSizesAndZoom_BeforePreview()
    {
        var source = Create("source");
        var profile = JsonNode.Parse(source.Profile.Export())!;
        profile["Preferences"]!["EditorFontSize"] = -10;
        profile["Dashboard"]!["UiZoomPercent"] = 100000;
        profile["Dashboard"]!["DashboardGridColumns"] = 10000;
        var normalized = JsonNode.Parse(PersonalizationProfileService.Validate(profile.ToJsonString()))!;
        Assert.Equal(12, normalized["Preferences"]!["EditorFontSize"]!.GetValue<double>());
        Assert.Equal(VaultSettings.MaxUiZoomPercent, normalized["Dashboard"]!["UiZoomPercent"]!.GetValue<int>());
        Assert.Equal(DashboardLayoutEngine.MaxGridDimension, normalized["Dashboard"]!["DashboardGridColumns"]!.GetValue<int>());
    }

    [Fact]
    public void BuiltInTheme_RoundTripsWithoutThemeFiles_AndClearsOldOverrides()
    {
        var source = Create("source");
        var target = Create("target");
        source.Settings.Settings.ActiveCustomThemeId = BuiltInThemeCatalog.HighContrastDarkId;
        target.Settings.Settings.ShortcutOverrides[ShortcutCatalog.All[0].Id] = "Ctrl+Alt+F9";
        target.Profile.Apply(source.Profile.Export());
        Assert.Equal(BuiltInThemeCatalog.HighContrastDarkId, target.Settings.Settings.ActiveCustomThemeId);
        Assert.Empty(target.Settings.Settings.ShortcutOverrides);
        Assert.Empty(target.Themes.Themes);
    }

    [Fact]
    public void Read_RejectsOversizedFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "large.json");
        using (var stream = File.Create(path)) stream.SetLength(PersonalizationProfileService.MaxFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => PersonalizationProfileService.Read(path));
    }

    [Fact]
    public void Apply_FailedWrite_RemovesStagedThemesAndPreservesMemory()
    {
        var source = Create("source");
        source.Themes.Create("Imported");
        var target = Create("target");
        var settingsPath = Path.Combine(target.Vault, ".enki", "settings.json");
        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);
        Assert.ThrowsAny<IOException>(() => target.Profile.Apply(source.Profile.Export()));
        Assert.Empty(target.Settings.Settings.CustomThemes);
        Assert.Null(target.Settings.Settings.ActiveCustomThemeId);
        Assert.Empty(Directory.GetFiles(Path.Combine(target.Vault, ".enki", "themes")));
    }
}
