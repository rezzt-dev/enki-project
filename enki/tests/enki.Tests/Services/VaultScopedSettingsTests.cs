using System;
using System.IO;
using System.Text.Json;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class VaultScopedSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-vault-settings-" + Guid.NewGuid().ToString("N"));
    private string Global => Path.Combine(_root, "global");
    private string VaultA => Path.Combine(_root, "a");
    private string VaultB => Path.Combine(_root, "b");
    private static string VaultFile(string path) => Path.Combine(path, ".enki", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Save_LoadsPreferencesFromVault_AndKeepsCredentialsGlobal()
    {
        var settings = new SettingsService(Global);
        var vault = new VaultService(settings);
        vault.SetVaultPath(VaultA);
        settings.Settings.Theme = "Light";
        settings.Settings.Language = "fr-FR";
        settings.Settings.StatisticsCollapsedSections.Add("tasks");
        settings.Settings.CalendarShowWeekends = false;
        settings.Settings.PolarAccessToken = "secret";
        settings.Save();

        var vaultJson = File.ReadAllText(VaultFile(VaultA));
        var globalJson = File.ReadAllText(Path.Combine(Global, "settings.json"));
        Assert.Contains("Light", vaultJson);
        Assert.Contains("fr-FR", vaultJson);
        Assert.Contains("tasks", vaultJson);
        Assert.DoesNotContain("secret", vaultJson);
        Assert.DoesNotContain("Theme", globalJson);
        Assert.Contains("secret", globalJson);

        var reopened = new SettingsService(Global);
        Assert.Equal("Light", reopened.Settings.Theme);
        Assert.False(reopened.Settings.CalendarShowWeekends);
        Assert.Equal("secret", reopened.Settings.PolarAccessToken);
    }

    [Fact]
    public void ChangeVault_ReloadsEachVaultsOwnPreferences()
    {
        var settings = new SettingsService(Global);
        var vault = new VaultService(settings);
        vault.SetVaultPath(VaultA);
        settings.Settings.Theme = "Light";
        settings.Save();
        vault.SetVaultPath(VaultB);
        Assert.Equal("Dark", settings.Settings.Theme);
        settings.Settings.EditorFontSize = 19;
        settings.Save();
        vault.SetVaultPath(VaultA);
        Assert.Equal("Light", settings.Settings.Theme);
        Assert.Equal(14, settings.Settings.EditorFontSize);
        vault.SetVaultPath(VaultB);
        Assert.Equal(19, settings.Settings.EditorFontSize);
    }

    [Fact]
    public void FirstVault_MigratesTemporaryPreferences()
    {
        var settings = new SettingsService(Global);
        settings.Settings.Language = "de-DE";
        settings.Settings.Theme = "Light";
        settings.Save();
        new VaultService(settings).SetVaultPath(VaultA);

        Assert.Equal("de-DE", new SettingsService(Global).Settings.Language);
        Assert.Contains("Light", File.ReadAllText(VaultFile(VaultA)));
        Assert.DoesNotContain("Language", File.ReadAllText(Path.Combine(Global, "settings.json")));
    }

    [Fact]
    public void Migration_DoesNotOverwriteVaultValues_AndPreservesOtherVaultSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultFile(VaultA))!);
        File.WriteAllText(VaultFile(VaultA), "{\"Theme\":\"Dark\",\"UiZoomPercent\":130}");
        Directory.CreateDirectory(Global);
        File.WriteAllText(Path.Combine(Global, "settings.json"),
            JsonSerializer.Serialize(new { VaultPath = VaultA, Theme = "Light", Language = "es-ES", PolarAccessToken = "token" }));

        var settings = new SettingsService(Global);
        Assert.Equal("Dark", settings.Settings.Theme);
        Assert.Equal("es-ES", settings.Settings.Language);
        Assert.Equal("token", settings.Settings.PolarAccessToken);
        Assert.Equal(130, new VaultSettingsService(new VaultService(settings)).Settings.UiZoomPercent);
        Assert.DoesNotContain("Theme", File.ReadAllText(Path.Combine(Global, "settings.json")));
    }

    [Fact]
    public void VaultSettingsSave_PreservesAppPreferences()
    {
        var settings = new SettingsService(Global);
        var vault = new VaultService(settings);
        vault.SetVaultPath(VaultA);
        settings.Settings.Theme = "Light";
        settings.Save();
        var vaultSettings = new VaultSettingsService(vault);
        vaultSettings.Settings.UiZoomPercent = 150;
        vaultSettings.Save();
        Assert.Equal("Light", new SettingsService(Global).Settings.Theme);
        Assert.Equal(150, new VaultSettingsService(vault).Settings.UiZoomPercent);
    }

    [Fact]
    public void Migration_CopiesVisualAssetsWithoutReplacingVaultFiles()
    {
        Directory.CreateDirectory(Path.Combine(Global, "themes"));
        File.WriteAllText(Path.Combine(Global, "themes", "custom.css"), "legacy theme");
        File.WriteAllText(Path.Combine(Global, "brand-logo.png"), "legacy logo");
        File.WriteAllText(Path.Combine(Global, "settings.json"), "{\"Theme\":\"Light\"}");
        Directory.CreateDirectory(Path.Combine(VaultA, ".enki", "themes"));
        File.WriteAllText(Path.Combine(VaultA, ".enki", "themes", "custom.css"), "vault theme");

        new VaultService(new SettingsService(Global)).SetVaultPath(VaultA);

        Assert.Equal("vault theme", File.ReadAllText(Path.Combine(VaultA, ".enki", "themes", "custom.css")));
        Assert.Equal("legacy logo", File.ReadAllText(Path.Combine(VaultA, ".enki", "brand-logo.png")));
    }

    [Fact]
    public void ImportPreferences_WritesActiveVault_AndKeepsInstallationData()
    {
        var settings = new SettingsService(Global);
        var vault = new VaultService(settings);
        vault.SetVaultPath(VaultA);
        settings.Settings.PolarAccessToken = "local token";
        settings.Save();

        settings.ImportPreferences("{\"Theme\":\"Light\",\"VaultPath\":\"other\",\"PolarAccessToken\":\"foreign token\"}");

        Assert.Equal(VaultA, settings.Settings.VaultPath);
        Assert.Equal("local token", settings.Settings.PolarAccessToken);
        Assert.Equal("Light", new SettingsService(Global).Settings.Theme);
    }

    [Fact]
    public void CorruptVaultSettings_AreBackedUpBeforeMigration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultFile(VaultA))!);
        File.WriteAllText(VaultFile(VaultA), "{broken json");
        Directory.CreateDirectory(Global);
        File.WriteAllText(Path.Combine(Global, "settings.json"),
            JsonSerializer.Serialize(new { VaultPath = VaultA, Language = "es-ES" }));

        var settings = new SettingsService(Global);

        Assert.Equal("es-ES", settings.Settings.Language);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(VaultFile(VaultA))!, "settings.json.corrupt-*"));
        Assert.Contains("{broken json", File.ReadAllText(Directory.GetFiles(
            Path.GetDirectoryName(VaultFile(VaultA))!, "settings.json.corrupt-*")[0]));
    }
}
