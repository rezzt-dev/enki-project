using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using enki.Models;

namespace enki.Services;

/// <summary>Crea una copia de seguridad completa de Enki en un único .zip: el vault (notas,
/// tareas y avisos) y la configuración del equipo en <c>%AppData%/Enki</c>.
/// Ver docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md:
/// Enki es "notas en Markdown en tu disco, sin nube", lo que hace la copia de seguridad más crítica que en
/// una app con backend — hasta esta funcionalidad no existía ninguna vía de respaldo.</summary>
public sealed class VaultBackupService
{
    private const long MaxRestoredSettingsBytes = 4L * 1024 * 1024;
    private const int MaxArchiveEntries = 100_000;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private static readonly EnumerationOptions VaultEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private readonly VaultService _vaultService;
    private readonly string _appDataDirectory;
    private readonly Action<string>? _beforeBackupCommit;

    /// <param name="appDataDirectory">Overrides the default <c>%AppData%/Enki</c> folder — lets tests point
    /// at a throwaway directory instead of the developer's real app data, same pattern as
    /// <see cref="SettingsService"/>.</param>
    public VaultBackupService(VaultService vaultService, string? appDataDirectory = null)
        : this(vaultService, appDataDirectory, null) { }

    internal VaultBackupService(
        VaultService vaultService, string? appDataDirectory, Action<string>? beforeBackupCommit)
    {
        _vaultService = vaultService;
        _appDataDirectory = appDataDirectory ?? AppDataDirectory.Resolve();
        _beforeBackupCommit = beforeBackupCommit;
    }

    /// <summary>Empaqueta el vault activo bajo <c>vault/</c> y los ficheros de estado de
    /// <c>%AppData%/Enki</c> (settings.json y posibles ficheros heredados) bajo <c>app-data/</c> en
    /// <paramref name="destinationZipPath"/>. Sobrescribe si ya existe un fichero con ese nombre. Un fichero
    /// individual bloqueado o ilegible no aborta el resto de la copia — mismo criterio tolerante que
    /// <see cref="VaultService.GetVaultSizeBytes"/> — pero, a diferencia de antes, la ruta relativa de cada
    /// fichero que falló se devuelve en el resultado en vez de descartarse en silencio
    /// (docs/analisis-proyecto/roadmap.md, Fase 1), para que el llamador pueda avisar al usuario de que el
    /// backup quedó incompleto.</summary>
    public VaultBackupResult CreateBackup(string destinationZipPath)
    {
        var destination = Path.GetFullPath(destinationZipPath);
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        var failedEntries = new List<string>();
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                if (!string.IsNullOrWhiteSpace(_vaultService.VaultPath) && Directory.Exists(_vaultService.VaultPath))
                {
                    // Un enlace a ~/ o un ciclo no forma parte del vault y no puede ampliar el backup a
                    // datos arbitrarios del equipo. ReparsePoint cubre enlaces de archivos y carpetas.
                    foreach (var file in Directory.EnumerateFiles(_vaultService.VaultPath, "*", VaultEnumerationOptions))
                    {
                        var fullFile = Path.GetFullPath(file);
                        if (string.Equals(fullFile, destination, VaultService.PathComparison)
                            || string.Equals(fullFile, temporary, VaultService.PathComparison))
                            continue;
                        var relative = Path.GetRelativePath(_vaultService.VaultPath, file).Replace(Path.DirectorySeparatorChar, '/');
                        var entryName = "vault/" + relative;
                        var isVaultSettings = string.Equals(relative, ".enki/settings.json", StringComparison.OrdinalIgnoreCase);
                        var added = isVaultSettings
                            ? TryAddRedactedSettingsFile(archive, file, entryName)
                            : TryAddFile(archive, file, entryName);
                        if (!added) failedEntries.Add(entryName);
                    }
                }

                if (Directory.Exists(_appDataDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(_appDataDirectory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        var entryName = "app-data/" + Path.GetFileName(file);
                        // Un settings.json heredado puede conservar una sesión Polar si todavía no se pudo
                        // migrar. El backup siempre redacta sus tokens, igual que los del vault.
                        var added = string.Equals(Path.GetFileName(file), "settings.json", StringComparison.OrdinalIgnoreCase)
                            ? TryAddRedactedSettingsFile(archive, file, entryName)
                            : TryAddFile(archive, file, entryName);
                        if (!added) failedEntries.Add(entryName);
                    }
                }
            }

            // El backup anterior sigue intacto durante toda la compresión. Solo se reemplaza cuando el
            // ZIP nuevo ya se cerró correctamente y su directorio central quedó escrito.
            _beforeBackupCommit?.Invoke(temporary);
            File.Move(temporary, destination, overwrite: true);
            PrivateDataPermissions.EnsurePrivateFile(destination);
            return new VaultBackupResult(failedEntries);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo eliminar el backup temporal '{temporary}'", ex);
            }
            throw;
        }
    }

    /// <summary>Restaura <c>vault/</c> a una carpeta nueva. Primero valida todas las entradas y extrae a
    /// un directorio temporal hermano; el destino solo aparece al completar todo. <c>app-data/settings.json</c>
    /// se devuelve al llamador para importar sus preferencias mediante el <see cref="SettingsService"/>
    /// activo, sin sobrescribir a ciegas credenciales ni estado vivo de la aplicación.</summary>
    public VaultRestoreResult RestoreBackup(string sourceZipPath, string destinationVaultPath)
    {
        var source = Path.GetFullPath(sourceZipPath);
        var destination = Path.GetFullPath(destinationVaultPath);
        EnsureNewRestoreDestination(destination);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("La carpeta de restauración no tiene un directorio padre válido.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.restore-{Guid.NewGuid():N}");
        string? settingsJson = null;

        try
        {
            using var archive = ZipFile.OpenRead(source);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException("La copia de seguridad está vacía o contiene demasiadas entradas.");

            // Se validan todas antes de crear un solo fichero. También se rechazan enlaces simbólicos de
            // ZIP Unix: seguirlos durante o después de extraer invalidaría la contención de rutas.
            var validated = archive.Entries.Select(ValidateEntry).ToList();
            if (!validated.Any(item => item.Root == BackupEntryRoot.Vault && !item.IsDirectory))
                throw new InvalidDataException("La copia no contiene ningún archivo bajo vault/.");

            Directory.CreateDirectory(temporary);
            // Resuelve primero todas las rutas: una entrada maliciosa situada al final no consigue ni
            // siquiera una extracción parcial dentro del temporal antes de ser rechazada.
            foreach (var item in validated.Where(item => item.Root == BackupEntryRoot.Vault))
                _ = ContainedPath(temporary, item.RelativePath);
            foreach (var item in validated)
            {
                if (item.Root == BackupEntryRoot.AppSettings)
                {
                    if (item.Entry.Length > MaxRestoredSettingsBytes)
                        throw new InvalidDataException("app-data/settings.json supera el tamaño permitido.");
                    using var reader = new StreamReader(item.Entry.Open());
                    settingsJson = reader.ReadToEnd();
                    _ = JsonNode.Parse(settingsJson) as JsonObject
                        ?? throw new InvalidDataException("app-data/settings.json no contiene un objeto JSON válido.");
                    continue;
                }

                if (item.Root != BackupEntryRoot.Vault) continue;
                var target = ContainedPath(temporary, item.RelativePath);
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = item.Entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            Directory.Move(temporary, destination);
            return new VaultRestoreResult(destination, settingsJson);
        }
        catch
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Warning($"No se pudo eliminar la restauración temporal '{temporary}'", ex);
            }
            throw;
        }
    }

    private void EnsureNewRestoreDestination(string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("La carpeta de restauración ya existe.");

        if (string.IsNullOrWhiteSpace(_vaultService.VaultPath)) return;
        var current = Path.GetFullPath(_vaultService.VaultPath);
        var prefix = current.EndsWith(Path.DirectorySeparatorChar) ? current : current + Path.DirectorySeparatorChar;
        if (string.Equals(destination, current, VaultService.PathComparison)
            || destination.StartsWith(prefix, VaultService.PathComparison))
            throw new InvalidOperationException("La copia no se puede restaurar sobre el vault activo ni dentro de él.");
    }

    private static ValidatedBackupEntry ValidateEntry(ZipArchiveEntry entry)
    {
        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            throw new InvalidDataException($"La entrada '{entry.FullName}' es un enlace simbólico no permitido.");

        var normalized = entry.FullName.Replace('\\', '/');
        var isDirectory = normalized.EndsWith('/');
        if (normalized.StartsWith("vault/", StringComparison.Ordinal))
        {
            var relative = normalized["vault/".Length..];
            if (relative.Length == 0 && !isDirectory)
                throw new InvalidDataException("Entrada vault/ inválida.");
            EnsureSafeRelativeEntry(relative, entry.FullName);
            return new ValidatedBackupEntry(entry, BackupEntryRoot.Vault, relative, isDirectory);
        }

        if (string.Equals(normalized, "app-data/settings.json", StringComparison.Ordinal))
            return new ValidatedBackupEntry(entry, BackupEntryRoot.AppSettings, string.Empty, false);
        if (normalized.StartsWith("app-data/", StringComparison.Ordinal))
        {
            EnsureSafeRelativeEntry(normalized["app-data/".Length..], entry.FullName);
            return new ValidatedBackupEntry(entry, BackupEntryRoot.IgnoredAppData, string.Empty, isDirectory);
        }

        throw new InvalidDataException($"La entrada '{entry.FullName}' no pertenece a vault/ ni app-data/.");
    }

    private static void EnsureSafeRelativeEntry(string relative, string original)
    {
        if (relative.StartsWith('/') || relative.Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException($"La entrada '{original}' contiene una ruta no segura.");
    }

    private static string ContainedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, fullRoot, VaultService.PathComparison)
            && !candidate.StartsWith(prefix, VaultService.PathComparison))
            throw new InvalidDataException($"La entrada '{relative}' intenta salir de la carpeta de restauración.");
        return candidate;
    }

    private enum BackupEntryRoot { Vault, AppSettings, IgnoredAppData }
    private sealed record ValidatedBackupEntry(
        ZipArchiveEntry Entry, BackupEntryRoot Root, string RelativePath, bool IsDirectory);

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
            PrivateDataPermissions.EnsurePrivateDirectory(AutoBackupDirectory);
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
        FileStream source;
        try
        {
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

        // Desde aquí el origen ya se abrió. Un IOException durante CopyTo/Dispose puede ser disco lleno,
        // desconexión del USB o fallo del ZIP de destino: no se degrada a "entrada omitida", porque eso
        // permitiría reemplazar una copia anterior sana por un archivo truncado.
        using (source)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            source.CopyTo(entryStream);
        }
        return true;
    }

    /// <summary>Elimina los tokens de cualquier settings.json incluido en el backup, conservando incluso
    /// propiedades que una versión futura de Enki todavía no conozca. Si falla el análisis, omite la
    /// entrada y la registra como fallida; nunca copia el archivo sin redactar.</summary>
    private static bool TryAddRedactedSettingsFile(ZipArchive archive, string sourcePath, string entryName)
    {
        string redactedJson;
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject;
            if (settings is null) return false;
            settings.Remove(nameof(AppSettings.PolarAccessToken));
            settings.Remove(nameof(AppSettings.PolarRefreshToken));
            redactedJson = settings.ToJsonString(IndentedJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warning($"No se pudo incluir '{sourcePath}' (redactado) en el backup", ex);
            return false;
        }


        // Igual que TryAddFile: una vez leído y saneado el origen, cualquier error pertenece a la
        // creación del destino y debe abortar el commit atómico completo.
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(redactedJson);
        return true;
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

/// <summary>Resultado de una restauración segura. Las preferencias globales redacted del backup se
/// entregan por separado para que el compositor las fusione sin recuperar secretos ni una ruta antigua.</summary>
public sealed record VaultRestoreResult(string VaultPath, string? SettingsJson);
