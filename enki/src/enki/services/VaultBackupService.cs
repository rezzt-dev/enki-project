using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using enki.Models;

namespace enki.Services;

/// <summary>Crea una copia de seguridad completa de Enki en un único .zip — no solo el vault (notas), sino
/// también los ficheros de estado que viven aparte en <c>%AppData%/Enki/*.json</c> (kanban, ajustes,
/// notificaciones, recordatorios, tareas Polar). Ver docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md:
/// Enki es "notas en Markdown en tu disco, sin nube", lo que hace la copia de seguridad más crítica que en
/// una app con backend — hasta esta funcionalidad no existía ninguna vía de respaldo.</summary>
public sealed class VaultBackupService
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly VaultService _vaultService;
    private readonly string _appDataDirectory;

    /// <param name="appDataDirectory">Overrides the default <c>%AppData%/Enki</c> folder — lets tests point
    /// at a throwaway directory instead of the developer's real app data, same pattern as
    /// <see cref="SettingsService"/>.</param>
    public VaultBackupService(VaultService vaultService, string? appDataDirectory = null)
    {
        _vaultService = vaultService;
        _appDataDirectory = appDataDirectory ?? AppDataDirectory.Resolve();
    }

    /// <summary>Empaqueta el vault activo bajo <c>vault/</c> y los ficheros de estado de
    /// <c>%AppData%/Enki</c> (kanban.json, settings.json, notifications.json, reminders.json,
    /// polar-tasks.json, dismissed-reminders.json…) bajo <c>app-data/</c> en un único .zip en
    /// <paramref name="destinationZipPath"/>. Sobrescribe si ya existe un fichero con ese nombre. Un fichero
    /// individual bloqueado o ilegible no aborta el resto de la copia — mismo criterio tolerante que
    /// <see cref="VaultService.GetVaultSizeBytes"/> — pero, a diferencia de antes, la ruta relativa de cada
    /// fichero que falló se devuelve en el resultado en vez de descartarse en silencio
    /// (docs/analisis-proyecto/roadmap.md, Fase 1), para que el llamador pueda avisar al usuario de que el
    /// backup quedó incompleto.</summary>
    public VaultBackupResult CreateBackup(string destinationZipPath)
    {
        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);

        var failedEntries = new List<string>();
        using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
        {
            if (!string.IsNullOrWhiteSpace(_vaultService.VaultPath) && Directory.Exists(_vaultService.VaultPath))
            {
                foreach (var file in Directory.EnumerateFiles(_vaultService.VaultPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_vaultService.VaultPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    var entryName = "vault/" + relative;
                    if (!TryAddFile(archive, file, entryName)) failedEntries.Add(entryName);
                }
            }

            if (Directory.Exists(_appDataDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_appDataDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var entryName = "app-data/" + Path.GetFileName(file);
                    // settings.json holds a live Polar/Supabase session (docs/analisis-proyecto/roadmap.md,
                    // Fase 4) — the backup must not become an unencrypted copy of a working login sitting in
                    // a .zip on disk. Every other app-data file (kanban, notifications, reminders,
                    // polar-tasks...) has no credentials in it and is copied as-is.
                    var added = string.Equals(Path.GetFileName(file), "settings.json", StringComparison.OrdinalIgnoreCase)
                        ? TryAddRedactedSettingsFile(archive, file, entryName)
                        : TryAddFile(archive, file, entryName);
                    if (!added) failedEntries.Add(entryName);
                }
            }
        }

        return new VaultBackupResult(failedEntries);
    }

    /// <summary>Backup automático periódico opcional (Ajustes › General, docs/analisis-proyecto/roadmap.md,
    /// Fase 1) — complemento al backup manual todo-o-nada de <see cref="CreateBackup"/>: sin esto, una
    /// edición destructiva entre dos backups manuales no tenía ninguna versión anterior a la que volver.
    /// No-op si <see cref="AppSettings.AutoBackupEnabled"/> está apagado o si todavía no ha pasado
    /// <see cref="AppSettings.AutoBackupIntervalDays"/> desde <see cref="AppSettings.LastAutoBackupUtc"/>.
    /// Nunca lanza — pensado para llamarse desde un timer periódico de fondo (<c>MainWindowViewModel</c>),
    /// donde un fallo debe quedar en el log en vez de tumbar cualquier otra cosa.</summary>
    public bool RunScheduledBackupIfDue(SettingsService settingsService)
    {
        var settings = settingsService.Settings;
        if (!settings.AutoBackupEnabled) return false;

        var intervalDays = Math.Max(1, settings.AutoBackupIntervalDays);
        var due = settings.LastAutoBackupUtc is not { } last || DateTime.UtcNow - last >= TimeSpan.FromDays(intervalDays);
        if (!due) return false;

        try
        {
            Directory.CreateDirectory(AutoBackupDirectory);
            var destination = Path.Combine(AutoBackupDirectory, $"Enki-AutoBackup-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");
            CreateBackup(destination);

            settings.LastAutoBackupUtc = DateTime.UtcNow;
            settingsService.Save();

            RotateOldBackups(AutoBackupDirectory, Math.Max(1, settings.AutoBackupRetentionCount));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("No se pudo crear el backup automático", ex);
            return false;
        }
    }

    private string AutoBackupDirectory => Path.Combine(_appDataDirectory, "backups");

    /// <summary>Keeps only the <paramref name="retentionCount"/> most recent automatic backups, deleting
    /// the rest — a locked/undeletable old backup is logged and skipped rather than aborting the rotation
    /// of the others.</summary>
    private static void RotateOldBackups(string directory, int retentionCount)
    {
        if (!Directory.Exists(directory)) return;

        var toDelete = Directory.EnumerateFiles(directory, "Enki-AutoBackup-*.zip")
            .OrderByDescending(File.GetCreationTimeUtc)
            .Skip(retentionCount);

        foreach (var file in toDelete)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo eliminar el backup automático antiguo '{file}'", ex);
            }
        }
    }

    private static bool TryAddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        try
        {
            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
            return true;
        }
        catch (IOException ex)
        {
            LogService.Warning($"No se pudo incluir '{sourcePath}' en el backup", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Warning($"No se pudo incluir '{sourcePath}' en el backup", ex);
            return false;
        }
    }

    /// <summary>Same as <see cref="TryAddFile"/> but for <c>settings.json</c> specifically: reads it,
    /// nulls out <see cref="AppSettings.PolarAccessToken"/>/<see cref="AppSettings.PolarRefreshToken"/>, and
    /// writes the redacted JSON into the zip entry instead of copying the raw file
    /// (docs/analisis-proyecto/roadmap.md, Fase 4). A settings.json that fails to parse is skipped (counted
    /// as a failed entry, same as a locked file) rather than falling back to including it unredacted.</summary>
    private static bool TryAddRedactedSettingsFile(ZipArchive archive, string sourcePath, string entryName)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath));
            if (settings is null) return false;

            settings.PolarAccessToken = null;
            settings.PolarRefreshToken = null;
            var redactedJson = JsonSerializer.Serialize(settings, IndentedJsonOptions);

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(redactedJson);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warning($"No se pudo incluir '{sourcePath}' (redactado) en el backup", ex);
            return false;
        }
    }
}

/// <summary>Result of <see cref="VaultBackupService.CreateBackup"/> — <see cref="FailedEntries"/> is empty
/// for a fully successful backup, or lists the zip-relative path of every file that couldn't be included
/// (locked, permission denied) so the caller can tell the user the backup is incomplete instead of it
/// looking identical to a complete one.</summary>
public sealed record VaultBackupResult(IReadOnlyList<string> FailedEntries)
{
    public bool IsComplete => FailedEntries.Count == 0;
}
