using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="VaultBackupService"/> — la copia de seguridad manual pedida en
/// docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md, el hueco de mayor riesgo real señalado por
/// ese diagnóstico. Verifica que el .zip resultante contiene tanto el vault (bajo <c>vault/</c>) como los
/// ficheros de estado de <c>%AppData%/Enki</c> (bajo <c>app-data/</c>), y que un fichero bloqueado no
/// aborta el resto de la copia.</summary>
public class VaultBackupServiceTests : IDisposable
{
    private readonly TestPolarCredentialStore _credentialStore = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _appDataDirectory;
    private readonly string _vaultPath;

    public VaultBackupServiceTests()
    {
        _appDataDirectory = Path.Combine(_root, "app-data");
        _vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_appDataDirectory);
        Directory.CreateDirectory(_vaultPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private VaultBackupService CreateService()
    {
        var settings = new SettingsService(_appDataDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return new VaultBackupService(vault, _appDataDirectory);
    }

    [Fact]
    public void CreateBackup_IncludesVaultNotesAndAppDataJsonFiles()
    {
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota\n\nContenido.");
        Directory.CreateDirectory(Path.Combine(_vaultPath, "Sub"));
        File.WriteAllText(Path.Combine(_vaultPath, "Sub", "Anidada.md"), "# Anidada");
        File.WriteAllText(Path.Combine(_appDataDirectory, "kanban.json"), "{}");
        // settings.json already exists here because SettingsService created it in CreateService().

        var service = CreateService();
        var destination = Path.Combine(_root, "backup.zip");
        service.CreateBackup(destination);

        Assert.True(File.Exists(destination));
        using var archive = ZipFile.OpenRead(destination);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("vault/Nota.md", entryNames);
        Assert.Contains("vault/Sub/Anidada.md", entryNames);
        Assert.Contains("app-data/kanban.json", entryNames);
        Assert.Contains("app-data/settings.json", entryNames);
    }

    [Fact]
    public void CreateBackup_OverwritesExistingZipAtDestination()
    {
        var service = CreateService();
        var destination = Path.Combine(_root, "backup.zip");

        File.WriteAllText(destination, "not actually a zip");
        service.CreateBackup(destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, e => e.FullName == "app-data/settings.json");
    }

    [Fact]
    public void CreateBackup_OnUnix_CreatesPrivateArchive()
    {
        if (OperatingSystem.IsWindows()) return;
        var destination = Path.Combine(_root, "private-backup.zip");

        CreateService().CreateBackup(destination);

        Assert.Equal(PrivateDataPermissions.FileMode, File.GetUnixFileMode(destination));
    }

    [Fact]
    public void CreateBackup_DoesNotFollowSymbolicLinksOutsideVault()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "privado.txt"), "secreto");
        Directory.CreateSymbolicLink(Path.Combine(_vaultPath, "enlace"), outside);
        var destination = Path.Combine(_root, "no-links.zip");

        CreateService().CreateBackup(destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("privado", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("vault/enlace", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBackup_WhenCommitFails_PreservesPreviousDestinationAndRemovesTemporary()
    {
        var settings = new SettingsService(_appDataDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var service = new VaultBackupService(vault, _appDataDirectory,
            _ => throw new InvalidOperationException("fallo simulado antes del commit"));
        var destination = Path.Combine(_root, "backup.zip");
        File.WriteAllText(destination, "backup anterior");

        Assert.Throws<InvalidOperationException>(() => service.CreateBackup(destination));

        Assert.Equal("backup anterior", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(_root, "backup.zip.tmp-*"));
    }

    [Fact]
    public void RestoreBackup_RoundTripsVaultIntoANewFolder()
    {
        File.WriteAllText(Path.Combine(_vaultPath, "Nota.md"), "# Nota restaurable");
        Directory.CreateDirectory(Path.Combine(_vaultPath, "Adjuntos"));
        File.WriteAllBytes(Path.Combine(_vaultPath, "Adjuntos", "x.bin"), [1, 2, 3]);
        var service = CreateService();
        var backup = Path.Combine(_root, "roundtrip.zip");
        service.CreateBackup(backup);
        var restoredPath = Path.Combine(_root, "vault-restaurado");

        var result = service.RestoreBackup(backup, restoredPath);

        Assert.Equal(Path.GetFullPath(restoredPath), result.VaultPath);
        Assert.Equal("# Nota restaurable", File.ReadAllText(Path.Combine(restoredPath, "Nota.md")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(restoredPath, "Adjuntos", "x.bin")));
        Assert.NotNull(result.SettingsJson);
    }

    [Fact]
    public void RestoreBackup_WithZipSlipEntry_RejectsItWithoutCreatingDestination()
    {
        var service = CreateService();
        var backup = Path.Combine(_root, "malicious.zip");
        using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("vault/../../escape.md").Open());
            writer.Write("fuera");
        }
        var destination = Path.Combine(_root, "restored");

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backup, destination));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(_root, "escape.md")));
    }

    [Fact]
    public void CreateBackup_MissingVaultFolder_StillBacksUpAppData()
    {
        var settings = new SettingsService(_appDataDirectory);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultPath);
        // The folder existed a moment ago (SetVaultPath creates it) but got removed afterwards — e.g. an
        // external drive unplugged — so GetVaultSizeBytes's own Directory.Exists guard must be mirrored here.
        Directory.Delete(_vaultPath, recursive: true);
        var service = new VaultBackupService(vault, _appDataDirectory);

        var destination = Path.Combine(_root, "backup.zip");
        service.CreateBackup(destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, e => e.FullName == "app-data/settings.json");
    }

    /// <summary>Fase 1 (docs/analisis-proyecto/roadmap.md): antes un fichero bloqueado/ilegible se omitía
    /// del .zip en silencio, sin que nada distinguiera un backup completo de uno incompleto. La aserción
    /// principal (no debe lanzar) es válida en cualquier entorno; la comprobación de <c>FailedEntries</c>
    /// solo aplica cuando el permiso realmente se hizo cumplir (no corriendo como root).</summary>
    [Fact]
    public void CreateBackup_WithAnUnreadableFile_ReportsItInsteadOfSilentlyDroppingIt()
    {
        if (OperatingSystem.IsWindows()) return; // UnixFileMode no aplica en Windows.

        var unreadablePath = Path.Combine(_vaultPath, "Bloqueada.md");
        File.WriteAllText(unreadablePath, "contenido");
        File.SetUnixFileMode(unreadablePath, UnixFileMode.None);

        try
        {
            var service = CreateService();
            var destination = Path.Combine(_root, "backup.zip");

            var result = service.CreateBackup(destination);

            if (!result.IsComplete)
            {
                Assert.Contains("vault/Bloqueada.md", result.FailedEntries);
                using var archive = ZipFile.OpenRead(destination);
                Assert.DoesNotContain(archive.Entries, e => e.FullName == "vault/Bloqueada.md");
            }
        }
        finally
        {
            File.SetUnixFileMode(unreadablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ===================== Redacción de credenciales Polar (Fase 4) =====================

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 4: the .zip backup must not become an unencrypted
    /// copy of a live Polar/Supabase session sitting on disk.</summary>
    [Fact]
    public void CreateBackup_RedactsPolarTokensInsideSettingsJson()
    {
        var settings = new SettingsService(_appDataDirectory, _credentialStore);
        settings.Settings.PolarAccessToken = "super-secret-access-token";
        settings.Settings.PolarRefreshToken = "super-secret-refresh-token";
        settings.Settings.Theme = "Dark";
        settings.Save();
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var service = new VaultBackupService(vault, _appDataDirectory);

        var destination = Path.Combine(_root, "backup.zip");
        service.CreateBackup(destination);

        using var archive = ZipFile.OpenRead(destination);
        var entry = archive.GetEntry("app-data/settings.json");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        var content = reader.ReadToEnd();

        Assert.DoesNotContain("super-secret-access-token", content);
        Assert.DoesNotContain("super-secret-refresh-token", content);
        Assert.Contains("\"PolarLinked\"", content);
        using var vaultReader = new StreamReader(archive.GetEntry("vault/.enki/settings.json")!.Open());
        Assert.Contains("\"Dark\"", vaultReader.ReadToEnd());
    }

    [Fact]
    public void CreateBackup_RedactsLegacyTokensInGlobalAndVaultSettings()
    {
        var settings = new SettingsService(_appDataDirectory, _credentialStore);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        File.WriteAllText(Path.Combine(_appDataDirectory, "settings.json"),
            "{\"PolarAccessToken\":\"global-secret\",\"Other\":7}");
        var vaultSettings = Path.Combine(_vaultPath, ".enki", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(vaultSettings)!);
        File.WriteAllText(vaultSettings, "{\"PolarRefreshToken\":\"vault-secret\",\"Other\":8}");

        var destination = Path.Combine(_root, "legacy-backup.zip");
        new VaultBackupService(vault, _appDataDirectory).CreateBackup(destination);

        using var archive = ZipFile.OpenRead(destination);
        foreach (var entryName in new[] { "app-data/settings.json", "vault/.enki/settings.json" })
        {
            using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
            var json = reader.ReadToEnd();
            Assert.DoesNotContain("secret", json);
            Assert.Contains("Other", json);
        }
    }

    // ===================== Backup automático (Fase 1) =====================

    [Fact]
    public void RunScheduledBackupIfDue_WhenDisabled_DoesNothing()
    {
        var service = CreateService();
        var settingsService = new SettingsService(_appDataDirectory);

        var ran = service.RunScheduledBackupIfDue(settingsService);

        Assert.False(ran);
        Assert.False(Directory.Exists(Path.Combine(_appDataDirectory, "backups")));
    }

    [Fact]
    public void RunScheduledBackupIfDue_WhenEnabledAndNeverRunBefore_CreatesABackup()
    {
        var service = CreateService();
        var settingsService = new SettingsService(_appDataDirectory);
        settingsService.Settings.AutoBackupEnabled = true;
        settingsService.Save();

        var ran = service.RunScheduledBackupIfDue(settingsService);

        Assert.True(ran);
        var backupsDir = Path.Combine(_appDataDirectory, "backups");
        Assert.Single(Directory.EnumerateFiles(backupsDir, "*.zip"));
        Assert.NotNull(settingsService.Settings.LastAutoBackupUtc);
    }

    [Fact]
    public void RunScheduledBackupIfDue_WhenNotDueYet_DoesNotCreateASecondBackup()
    {
        var service = CreateService();
        var settingsService = new SettingsService(_appDataDirectory);
        settingsService.Settings.AutoBackupEnabled = true;
        settingsService.Save();
        service.RunScheduledBackupIfDue(settingsService);

        var ranAgain = service.RunScheduledBackupIfDue(settingsService);

        Assert.False(ranAgain);
        var backupsDir = Path.Combine(_appDataDirectory, "backups");
        Assert.Single(Directory.EnumerateFiles(backupsDir, "*.zip"));
    }

    [Fact]
    public void RunScheduledBackupIfDue_RotatesOldBackupsBeyondRetentionCount()
    {
        var service = CreateService();
        var settingsService = new SettingsService(_appDataDirectory);
        settingsService.Settings.AutoBackupEnabled = true;
        settingsService.Settings.AutoBackupRetentionCount = 2;
        settingsService.Save();
        var backupsDir = Path.Combine(_appDataDirectory, "backups");
        Directory.CreateDirectory(backupsDir);

        // Simulates 3 previous automatic backups already on disk, older than whatever this run creates.
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(backupsDir, $"Enki-AutoBackup-old-{i}.zip");
            File.WriteAllText(path, "not actually a zip");
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10 + i));
        }

        service.RunScheduledBackupIfDue(settingsService);

        Assert.Equal(2, Directory.EnumerateFiles(backupsDir, "*.zip").Count());
    }
}
