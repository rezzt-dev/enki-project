using System;
using System.IO;

namespace enki.Services;

/// <summary>Ubica los datos de trabajo dentro del vault activo y traslada una sola vez los
/// ficheros de instalaciones anteriores. Conserva los originales para permitir recuperarlos.</summary>
internal static class VaultDataStorage
{
    private static readonly string[] LegacyFiles =
    [
        "kanban.json", "polar-tasks.json", "reminders.json",
        "dismissed-reminders.json", "notifications.json"
    ];

    public static string FilePath(VaultService vault, string fileName)
    {
        // La pantalla de configuración también puede registrar avisos antes de escoger un vault.
        var directory = vault.IsVaultConfigured
            ? Path.Combine(vault.VaultPath, fileName == "notifications.json" ? ".enki" : ".tasks")
            : AppDataDirectory.Resolve();
        return Path.Combine(directory, fileName);
    }

    /// <summary>Copia los datos antiguos al primer vault abierto sin sobrescribir datos que ya
    /// pertenezcan a él. El marcador global impide importarlos después en otro vault.</summary>
    public static void MigrateLegacyFiles(VaultService vault, string? legacyStorageDirectory = null)
    {
        if (!vault.IsVaultConfigured) return;

        var legacyDirectory = legacyStorageDirectory ?? AppDataDirectory.Resolve();
        var marker = Path.Combine(legacyDirectory, "vault-data-migrated");
        JsonFileStore.WithLock(marker, () => MigrateUnderLock(vault, legacyDirectory, marker));
    }

    private static void MigrateUnderLock(VaultService vault, string legacyDirectory, string marker)
    {
        if (File.Exists(marker)) return;

        foreach (var name in LegacyFiles)
        {
            var source = Path.Combine(legacyDirectory, name);
            var target = FilePath(vault, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".migration-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary);
                File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        Directory.CreateDirectory(legacyDirectory);
        var markerTemporary = marker + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(markerTemporary, vault.VaultPath);
            File.Move(markerTemporary, marker);
        }
        finally
        {
            if (File.Exists(markerTemporary)) File.Delete(markerTemporary);
        }
    }
}
