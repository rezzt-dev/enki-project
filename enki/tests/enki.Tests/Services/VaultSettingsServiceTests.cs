using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="VaultSettingsService"/> — the per-vault counterpart to
/// <see cref="SettingsServiceTests"/>, introduced for docs/funcionalidad-zoom-interfaz.md so the UI zoom
/// level travels with the vault instead of being shared globally like the rest of <c>AppSettings</c>.
/// Same throwaway-temp-directory pattern as <see cref="VaultServiceTests"/>.</summary>
public class VaultSettingsServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public VaultSettingsServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    [Fact]
    public void Load_MissingFile_FallsBackToDefaultZoom()
    {
        var service = new VaultSettingsService(CreateVault());

        Assert.Equal(100, service.Settings.UiZoomPercent);
    }

    [Fact]
    public void Save_PersistsAcrossInstancesOfTheSameVault()
    {
        var vault = CreateVault();
        var first = new VaultSettingsService(vault);
        first.Settings.UiZoomPercent = 150;
        first.Save();

        var second = new VaultSettingsService(vault);

        Assert.Equal(150, second.Settings.UiZoomPercent);
    }

    [Fact]
    public void Save_WritesInsideTheVaultFolder_AsItsOwnFile()
    {
        var vault = CreateVault();
        var service = new VaultSettingsService(vault);
        service.Settings.UiZoomPercent = 150;
        service.Save();

        var vaultSettingsPath = Path.Combine(_vaultPath, ".enki", "settings.json");
        Assert.True(File.Exists(vaultSettingsPath));
        Assert.Contains("150", File.ReadAllText(vaultSettingsPath));
    }

    [Fact]
    public void Reload_PicksUpTheNewlyActiveVaultsOwnZoom()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);

        var vaultAPath = Path.Combine(_storageDirectory, "vault-a");
        var vaultBPath = Path.Combine(_storageDirectory, "vault-b");
        vaultService.InitializeVault(vaultAPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vaultService);
        vaultSettingsService.Settings.UiZoomPercent = 200;
        vaultSettingsService.Save();

        vaultService.SetVaultPath(vaultBPath);
        vaultSettingsService.Reload();

        Assert.Equal(100, vaultSettingsService.Settings.UiZoomPercent);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        var vault = CreateVault();
        var enkiDir = Path.Combine(_vaultPath, ".enki");
        Directory.CreateDirectory(enkiDir);
        File.WriteAllText(Path.Combine(enkiDir, "settings.json"), "{ not valid json ");

        var service = new VaultSettingsService(vault);

        Assert.Equal(100, service.Settings.UiZoomPercent);
    }

    [Fact]
    public void Save_WithNoVaultConfigured_DoesNotThrowOrWriteAnyFile()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultService = new VaultService(settingsService);
        var service = new VaultSettingsService(vaultService);

        service.Settings.UiZoomPercent = 150;
        service.Save();

        Assert.False(Directory.Exists(_vaultPath));
    }
}
