using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Multi-vault sin reiniciar (T4, .docs/project-info/multi-vault-en-caliente.md) — la parte que
/// vive en los servicios: la lista de recientes que mantiene <see cref="SettingsService"/>, el sondeo de
/// disponibilidad de <see cref="VaultService.DescribeVaultUnavailability"/> y, lo más fácil de romper sin
/// notarlo, que la lista sea <em>global</em> y sobreviva a cambiar de vault en vez de quedarse guardada
/// dentro del que estaba abierto al escribirla.</summary>
public class RecentVaultsTests : IDisposable
{
    private readonly TestPolarCredentialStore _credentialStore = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private string StorageDirectory => Path.Combine(_root, "app-data");

    private string NewVaultFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RegisterRecentVault_PutsTheNewestFirstAndKeepsOneEntryPerFolder()
    {
        var settings = new SettingsService(StorageDirectory, _credentialStore);
        var a = NewVaultFolder("a");
        var b = NewVaultFolder("b");

        settings.RegisterRecentVault(a);
        settings.RegisterRecentVault(b);
        settings.RegisterRecentVault(a);

        Assert.Equal([a, b], settings.Settings.RecentVaults.Select(v => v.Path));
        Assert.Equal("a", settings.Settings.RecentVaults[0].DisplayName);
    }

    [Fact]
    public void RegisterRecentVault_CapsTheListAtTenAndDropsTheOldest()
    {
        var settings = new SettingsService(StorageDirectory, _credentialStore);
        var folders = Enumerable.Range(0, AppSettings.MaxRecentVaults + 3)
            .Select(i => NewVaultFolder("vault-" + i)).ToList();

        foreach (var folder in folders) settings.RegisterRecentVault(folder);

        Assert.Equal(AppSettings.MaxRecentVaults, settings.Settings.RecentVaults.Count);
        Assert.Equal(folders[^1], settings.Settings.RecentVaults[0].Path);
        Assert.DoesNotContain(folders[0], settings.Settings.RecentVaults.Select(v => v.Path));
    }

    [Fact]
    public void RemoveRecentVault_DropsTheEntryAndLeavesTheFolderUntouched()
    {
        var settings = new SettingsService(StorageDirectory, _credentialStore);
        var folder = NewVaultFolder("keep-my-files");
        File.WriteAllText(Path.Combine(folder, "note.md"), "# hola");
        settings.RegisterRecentVault(folder);

        settings.RemoveRecentVault(folder);

        Assert.Empty(settings.Settings.RecentVaults);
        Assert.True(File.Exists(Path.Combine(folder, "note.md")));
    }

    /// <summary>El fallo que esta funcionalidad tenía garantizado si <c>RecentVaults</c> se hubiera
    /// guardado como una preferencia más: al cambiar de vault, <see cref="SettingsService.SwitchVault"/>
    /// reemplaza la instancia de <see cref="AppSettings"/> por una compuesta desde
    /// <c>&lt;vault&gt;/.enki/settings.json</c>, y la lista se habría quedado en el vault anterior — es
    /// decir, invisible justo desde donde hace falta para volver.</summary>
    [Fact]
    public void RecentVaults_SurviveSwitchingVaultAndReloadingFromDisk()
    {
        var a = NewVaultFolder("personal");
        var b = NewVaultFolder("trabajo");

        var settings = new SettingsService(StorageDirectory, _credentialStore);
        var vault = new VaultService(settings);
        vault.SetVaultPath(a);
        vault.SetVaultPath(b);

        Assert.Equal([b, a], settings.Settings.RecentVaults.Select(v => v.Path));

        var reopened = new SettingsService(StorageDirectory, _credentialStore);
        Assert.Equal([b, a], reopened.Settings.RecentVaults.Select(v => v.Path));
    }

    [Fact]
    public void Normalize_RepairsADamagedList()
    {
        Directory.CreateDirectory(StorageDirectory);
        File.WriteAllText(Path.Combine(StorageDirectory, "settings.json"), """
            {"RecentVaults":[
              {"Path":"","DisplayName":"sin ruta","LastOpenedUtc":"2026-01-01T00:00:00Z"},
              {"Path":"/tmp/enki-dup","DisplayName":"vieja","LastOpenedUtc":"2026-01-01T00:00:00Z"},
              {"Path":"/tmp/enki-dup","DisplayName":"nueva","LastOpenedUtc":"2026-02-01T00:00:00Z"},
              {"Path":"/tmp/enki-otra","DisplayName":"otra","LastOpenedUtc":"2026-03-01T00:00:00Z"}]}
            """);

        var settings = new SettingsService(StorageDirectory, _credentialStore);

        Assert.Equal(["/tmp/enki-otra", "/tmp/enki-dup"], settings.Settings.RecentVaults.Select(v => v.Path));
        Assert.Equal("nueva", settings.Settings.RecentVaults[1].DisplayName);
    }

    [Fact]
    public void DescribeVaultUnavailability_IsNullOnlyForAReadableFolder()
    {
        var folder = NewVaultFolder("readable");

        Assert.Null(VaultService.DescribeVaultUnavailability(folder));
        Assert.NotNull(VaultService.DescribeVaultUnavailability(Path.Combine(_root, "never-created")));
        Assert.NotNull(VaultService.DescribeVaultUnavailability(""));
    }

    /// <summary>Aislamiento entre vaults: volver a A después de pasar por B tiene que releer el disco. Si
    /// la caché de notas no se vacía al cambiar de vault, la nota que se editó fuera mientras A no estaba
    /// abierto se sirve con el contenido que tenía antes de irse.</summary>
    [Fact]
    public void SetVaultPath_DropsTheNoteCacheOfThePreviousVault()
    {
        var a = NewVaultFolder("a");
        var b = NewVaultFolder("b");
        var notePath = Path.Combine(a, "nota.md");
        File.WriteAllText(notePath, "# antes");

        var settings = new SettingsService(StorageDirectory, _credentialStore);
        var vault = new VaultService(settings);
        vault.SetVaultPath(a);
        var before = vault.GetAllNotesAcrossVaultWithContent().Single();
        Assert.Contains("antes", before.Content, StringComparison.Ordinal);

        vault.SetVaultPath(b);
        // Editada desde fuera, y con la misma marca de tiempo que tenía: sin vaciar la caché, la
        // comprobación de frescura por mtime no bastaría para descartar la entrada anterior.
        var stamp = File.GetLastWriteTimeUtc(notePath);
        File.WriteAllText(notePath, "# después");
        File.SetLastWriteTimeUtc(notePath, stamp);
        vault.SetVaultPath(a);

        Assert.Contains("después", vault.GetAllNotesAcrossVaultWithContent().Single().Content, StringComparison.Ordinal);
    }
}
