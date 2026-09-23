using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using enki.Models;

namespace enki.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;
    private readonly IPolarCredentialStore _credentialStore;
    private PolarCredentials _persistedCredentials = new(null, null);
    private bool _legacyCredentialsPending;
    private string? _vaultSettingsPath;
    private static readonly string[] InstallationKeys =
    [
        nameof(AppSettings.VaultPath), nameof(AppSettings.PolarLinked), nameof(AppSettings.PolarUserId),
        nameof(AppSettings.PolarEmail), nameof(AppSettings.PolarAccessToken),
        nameof(AppSettings.PolarRefreshToken), nameof(AppSettings.PolarAccessTokenExpiresAtUtcMs),
        nameof(AppSettings.PolarLastSyncAtUtcMs), nameof(AppSettings.DebugLoggingEnabled),
        // T4: la lista de vaults recientes describe la instalación, no un vault. Guardada como preferencia
        // acabaría dentro de <vault>/.enki/settings.json, es decir, invisible desde cualquier otro vault —
        // justo desde donde hace falta para volver. Lo mismo para el ajuste de bandeja de T2, que decide
        // qué pasa al cerrar la ventana antes incluso de que haya un vault abierto.
        nameof(AppSettings.RecentVaults), nameof(AppSettings.MinimizeToTray)
    ];
    /// <summary>Notifica un guardado completado para actualizar las preferencias visibles.</summary>
    public event Action? SettingsChanged;
    /// <summary>Se dispara cuando una escritura de tokens falla por falta de acceso al almacén seguro.</summary>
    public event Action? CredentialStoreFailed;
    /// <summary>Se dispara al recuperar un <c>.enki/settings.json</c> corrupto e indica dónde quedó su copia.</summary>
    public event Action<string>? VaultSettingsRecoveredFromCorruptData;

    public AppSettings Settings { get; private set; }

    /// <summary>True cuando <c>settings.json</c> existía pero no se pudo leer o deserializar (archivo
    /// dañado), por lo que <see cref="Settings"/> se reemplazó por una instancia nueva por defecto —
    /// incluido, si estaba vinculada, la pérdida del vínculo con Polar. No se activa en una instalación
    /// nueva donde el archivo simplemente no existe todavía. Mismo patrón que
    /// <see cref="KanbanService.LoadedFromCorruptData"/> (docs/analisis-proyecto/roadmap.md, Fase 1) —
    /// consultado por el compositor (<c>MainWindowViewModel</c>) justo después de construir este servicio
    /// para avisar al usuario en vez de resetear los ajustes en silencio.</summary>
    public bool LoadedFromCorruptData { get; private set; }

    /// <summary>Copia de seguridad creada al detectar ajustes corruptos del vault durante el arranque.</summary>
    public string? CorruptVaultSettingsBackupPath { get; private set; }

    /// <summary>El almacén seguro no estuvo disponible al cargar la sesión. Si había tokens heredados,
    /// permanecen en settings.json hasta que una migración posterior pueda completarse.</summary>
    public bool CredentialStoreUnavailable { get; private set; }

    /// <summary>Ruta del archivo global de instalación y autenticación. Las preferencias activas
    /// se guardan en <c>&lt;vault&gt;/.enki/settings.json</c>.</summary>
    public string SettingsFilePath => _settingsPath;

    /// <summary>Carpeta de recursos visuales asociada a las preferencias activas.</summary>
    public string PreferenceDirectory => _vaultSettingsPath is null
        ? Path.GetDirectoryName(_settingsPath)! : Path.GetDirectoryName(_vaultSettingsPath)!;

    /// <summary>VaultPath as last known from disk (at load, or after this instance itself wrote it) —
    /// lets <see cref="Save"/> tell "this process just changed the vault" apart from "some other setting
    /// changed while an unrelated in-memory copy of VaultPath happened to be sitting here". See remarks
    /// on <see cref="Save"/>.</summary>
    private string? _lastKnownVaultPath;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, but lets tests point persistence at a throwaway directory instead of the developer's
    /// real settings file (see enki.Tests).</param>
    public SettingsService(string? storageDirectory = null)
        : this(storageDirectory, null) { }

    internal SettingsService(string? storageDirectory, IPolarCredentialStore? credentialStore)
    {
        var configDir = storageDirectory ?? AppDataDirectory.Resolve();
        try
        {
            PrivateDataPermissions.HardenExistingTree(configDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudieron restringir los permisos de '{configDir}'", ex);
            Directory.CreateDirectory(configDir);
        }
        _settingsPath = Path.Combine(configDir, "settings.json");
        _credentialStore = credentialStore ?? PolarCredentialStore.Create(configDir);
        Settings = Load();
        _lastKnownVaultPath = Settings.VaultPath;
        if (!string.IsNullOrWhiteSpace(Settings.VaultPath) && Directory.Exists(Settings.VaultPath))
            SwitchVault(Settings.VaultPath);
    }

    private AppSettings Load()
    {
        if (JsonFileStore.TryLoad<AppSettings>(_settingsPath, out var loaded))
        {
            // Antes de nada: un archivo dañado o de otra versión puede traer números imposibles, y llegan
            // hasta los viewmodels sin que nadie los mire. Ver AppSettings.Normalize.
            loaded!.Normalize();
            var legacy = new PolarCredentials(loaded.PolarAccessToken, loaded.PolarRefreshToken);
            _legacyCredentialsPending = legacy.AccessToken is not null || legacy.RefreshToken is not null;
            if (!loaded.PolarLinked && !_legacyCredentialsPending) return loaded;
            try
            {
                var secure = _credentialStore.Read();
                if (secure is null && _legacyCredentialsPending)
                {
                    _credentialStore.Write(legacy);
                    secure = legacy;
                }
                if (secure is not null)
                {
                    loaded.PolarAccessToken = secure.AccessToken;
                    loaded.PolarRefreshToken = secure.RefreshToken;
                    _persistedCredentials = secure;
                }
                if (_legacyCredentialsPending)
                {
                    RemoveLegacyCredentialsFromDisk();
                    _legacyCredentialsPending = false;
                }
            }
            catch (Exception ex) when (IsCredentialStoreFailure(ex))
            {
                CredentialStoreUnavailable = true;
                if (_legacyCredentialsPending) _persistedCredentials = legacy;
                LogService.Warning("No se pudo acceder al almacén seguro de Polar", ex);
            }
            return loaded;
        }

        if (File.Exists(_settingsPath)) LoadedFromCorruptData = true;
        return new AppSettings();
    }

    /// <summary>Two Enki windows can run at once (see SingleInstanceGuard), each potentially pointed at a
    /// different vault, but both share this one settings file. Without care, an unrelated setting change
    /// in window A (theme, font size, sidebar width…) would blindly re-serialize window A's whole
    /// in-memory <see cref="AppSettings"/> — including its now-stale copy of VaultPath — and stomp over
    /// whatever vault window B just picked. So: only let VaultPath through to disk when this instance
    /// itself changed it (tracked via <see cref="_lastKnownVaultPath"/>); otherwise pull whatever VaultPath
    /// is currently on disk into memory first, so an unrelated save preserves another window's choice
    /// instead of reverting it. The read-then-write runs inside one <see cref="JsonFileStore.WithLock"/>
    /// call (gap 32) so a second instance's own <see cref="Save"/> can't land between the read and the
    /// write and get silently overwritten.</summary>
    public void Save()
    {
        var currentCredentials = new PolarCredentials(Settings.PolarAccessToken, Settings.PolarRefreshToken);
        if (_legacyCredentialsPending || currentCredentials != _persistedCredentials)
        {
            try
            {
                if (currentCredentials.AccessToken is null && currentCredentials.RefreshToken is null)
                    _credentialStore.Delete();
                else
                    _credentialStore.Write(currentCredentials);
                _persistedCredentials = currentCredentials;
                _legacyCredentialsPending = false;
                CredentialStoreUnavailable = false;
            }
            catch (Exception ex) when (IsCredentialStoreFailure(ex))
            {
                var alreadyUnavailable = CredentialStoreUnavailable;
                CredentialStoreUnavailable = true;
                if (!alreadyUnavailable) CredentialStoreFailed?.Invoke();
                if (!_legacyCredentialsPending || currentCredentials != _persistedCredentials)
                    throw new IOException("No se pudo guardar la sesión Polar en el almacén seguro.", ex);
                // Conserva los tokens heredados hasta poder completar la migración.
            }
        }
        if (_vaultSettingsPath is not null)
        {
            var preferences = PreferencesOnly(JsonSerializer.SerializeToNode(Settings)!.AsObject());
            Directory.CreateDirectory(Path.GetDirectoryName(_vaultSettingsPath)!);
            JsonFileStore.WithLock(_vaultSettingsPath, () =>
            {
                var merged = ReadVaultObject(_vaultSettingsPath, ReportCorruptVaultSettings);
                foreach (var entry in preferences) merged[entry.Key] = entry.Value?.DeepClone();
                JsonFileStore.Save(_vaultSettingsPath, merged);
            });
        }
        JsonFileStore.WithLock(_settingsPath, () =>
        {
            if (Settings.VaultPath == _lastKnownVaultPath)
            {
                var onDiskVaultPath = TryReadVaultPathFromDiskUnlocked();
                if (onDiskVaultPath is not null) Settings.VaultPath = onDiskVaultPath;
            }
            _lastKnownVaultPath = Settings.VaultPath;

            var global = JsonSerializer.SerializeToNode(Settings)!.AsObject();
            if (!_legacyCredentialsPending)
            {
                global.Remove(nameof(AppSettings.PolarAccessToken));
                global.Remove(nameof(AppSettings.PolarRefreshToken));
            }
            if (_vaultSettingsPath is not null)
                foreach (var key in global.Select(entry => entry.Key).Where(key => !InstallationKeys.Contains(key)).ToArray())
                    global.Remove(key);
            JsonFileStore.Save(_settingsPath, global);
        });
        SettingsChanged?.Invoke();
    }

    private void RemoveLegacyCredentialsFromDisk()
    {
        JsonFileStore.WithLock(_settingsPath, () =>
        {
            var json = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject
                ?? throw new InvalidDataException("settings.json no contiene un objeto JSON.");
            json.Remove(nameof(AppSettings.PolarAccessToken));
            json.Remove(nameof(AppSettings.PolarRefreshToken));
            JsonFileStore.Save(_settingsPath, json);
        });
    }

    private static bool IsCredentialStoreFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || ex is TypeInitializationException { InnerException: { } inner } && IsCredentialStoreFailure(inner);

    /// <summary>Cambia el origen de las preferencias al vault activo y migra las claves antiguas
    /// una sola vez. Las claves ya presentes en el vault siempre tienen prioridad.</summary>
    public void SwitchVault(string path)
    {
        if (!Directory.Exists(path)) return;
        var target = Path.Combine(path, ".enki", "settings.json");
        if (_vaultSettingsPath == target) return;
        var oldSettings = Settings;
        var migrateAssets = _vaultSettingsPath is null &&
            JsonFileStore.TryLoad<JsonObject>(_settingsPath, out var legacy) && legacy is not null &&
            legacy.ContainsKey(nameof(AppSettings.Theme));
        var pending = _vaultSettingsPath is null
            ? PreferencesOnly(JsonSerializer.SerializeToNode(oldSettings)!.AsObject()) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        JsonFileStore.WithLock(target, () =>
        {
            var vault = ReadVaultObject(target, ReportCorruptVaultSettings);
            if (pending is not null)
            {
                // Incluye elecciones hechas antes de seleccionar el primer vault.
                foreach (var entry in pending)
                    if (!vault.ContainsKey(entry.Key)) vault[entry.Key] = entry.Value?.DeepClone();
                JsonFileStore.Save(target, vault);
            }
        });
        if (migrateAssets)
        {
            var legacyDirectory = Path.GetDirectoryName(_settingsPath)!;
            var targetDirectory = Path.GetDirectoryName(target)!;
            var oldThemes = Path.Combine(legacyDirectory, "themes");
            if (Directory.Exists(oldThemes))
            {
                var newThemes = Path.Combine(targetDirectory, "themes");
                Directory.CreateDirectory(newThemes);
                foreach (var source in Directory.EnumerateFiles(oldThemes, "*.css"))
                {
                    var destination = Path.Combine(newThemes, Path.GetFileName(source));
                    if (!File.Exists(destination)) File.Copy(source, destination);
                }
            }
            foreach (var source in Directory.EnumerateFiles(legacyDirectory, "brand-logo.*"))
            {
                var destination = Path.Combine(targetDirectory, Path.GetFileName(source));
                if (!File.Exists(destination)) File.Copy(source, destination);
            }
        }
        _vaultSettingsPath = target;
        var global = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject();
        var previous = JsonSerializer.SerializeToNode(oldSettings)!.AsObject();
        foreach (var key in InstallationKeys)
            if (previous.TryGetPropertyValue(key, out var value)) global[key] = value?.DeepClone();
        var stored = ReadVaultObject(target, ReportCorruptVaultSettings);
        foreach (var entry in stored)
            if (!InstallationKeys.Contains(entry.Key) && global.ContainsKey(entry.Key))
                global[entry.Key] = entry.Value?.DeepClone();
        Settings = global.Deserialize<AppSettings>() ?? new AppSettings();
        // Las preferencias del vault que se acaban de fusionar vienen de su propio settings.json, que es
        // tan editable a mano como el global — mismo saneado que en Load().
        Settings.Normalize();
        Settings.VaultPath = path;
        Save();
    }

    /// <summary>Sube <paramref name="path"/> al primer puesto de <see cref="AppSettings.RecentVaults"/> y
    /// persiste la lista (T4). Lo llama <see cref="VaultService.SetVaultPath"/>, no los viewmodels: así
    /// toda ruta que llegue a ser el vault activo —la elegida en el selector, la del diálogo de carpeta,
    /// la restaurada desde un backup, la del primer arranque— entra en la lista por el mismo sitio y con
    /// el mismo orden, sin que cada camino tenga que acordarse.
    ///
    /// <para>Reutiliza la entrada existente si ya conocía la carpeta, conservando el
    /// <see cref="RecentVault.DisplayName"/> que tuviera, y descarta por el final al llegar a
    /// <see cref="AppSettings.MaxRecentVaults"/>.</para></summary>
    public void RegisterRecentVault(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var full = Path.GetFullPath(path);
        var existing = Settings.RecentVaults.Find(v => VaultService.PathsEqual(v.Path, full));
        if (existing is not null) Settings.RecentVaults.Remove(existing);

        Settings.RecentVaults.Insert(0, new RecentVault
        {
            Path = full,
            DisplayName = string.IsNullOrWhiteSpace(existing?.DisplayName)
                ? DefaultVaultDisplayName(full) : existing.DisplayName,
            LastOpenedUtc = DateTime.UtcNow
        });
        if (Settings.RecentVaults.Count > AppSettings.MaxRecentVaults)
            Settings.RecentVaults.RemoveRange(AppSettings.MaxRecentVaults,
                Settings.RecentVaults.Count - AppSettings.MaxRecentVaults);
        Save();
    }

    /// <summary>"Quitar de recientes" — saca la entrada de la lista y nada más: no borra, mueve ni toca un
    /// solo archivo del vault, que sigue ahí para volver a abrirlo desde el diálogo de carpeta. Quitar el
    /// vault activo tampoco lo cierra; simplemente dejará de aparecer en el selector cuando se cambie a
    /// otro.</summary>
    public void RemoveRecentVault(string path)
    {
        var removed = Settings.RecentVaults.RemoveAll(v => VaultService.PathsEqual(v.Path, path));
        if (removed == 0) return;
        Save();
    }

    /// <summary>Nombre de la carpeta del vault, con la ruta completa como red de seguridad para una raíz
    /// (<c>C:\</c>, <c>/</c>) donde <see cref="Path.GetFileName(string)"/> devuelve cadena vacía.</summary>
    private static string DefaultVaultDisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? fullPath : name;
    }

    private static JsonObject PreferencesOnly(JsonObject source)
    {
        foreach (var key in InstallationKeys) source.Remove(key);
        return source;
    }

    /// <summary>Importa preferencias sin cambiar la ruta del vault ni la sesión local.</summary>
    public void ImportPreferences(string json)
    {
        var imported = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Invalid settings JSON");
        var current = JsonSerializer.SerializeToNode(Settings)!.AsObject();
        foreach (var entry in PreferencesOnly(imported))
            if (current.ContainsKey(entry.Key)) current[entry.Key] = entry.Value?.DeepClone();
        Settings = current.Deserialize<AppSettings>() ?? throw new InvalidDataException("Invalid settings JSON");
        // La entrada menos confiable de las tres: un archivo que el usuario elige en un diálogo y que pudo
        // escribir cualquiera. Sin esto, "Importar ajustes" era la vía directa a un zoom o un cuerpo de
        // letra imposibles.
        Settings.Normalize();
        Save();
    }

    internal static JsonObject ReadVaultObject(string path, Action<string>? recovered = null)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"Invalid vault settings: {path}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Antes de continuar con valores por defecto, conserva los bytes originales.
            var backup = $"{path}.corrupt-{Guid.NewGuid():N}";
            File.Copy(path, backup);
            JsonFileStore.Save(path, new JsonObject());
            recovered?.Invoke(backup);
            return new JsonObject();
        }
    }

    private void ReportCorruptVaultSettings(string backupPath)
    {
        CorruptVaultSettingsBackupPath = backupPath;
        VaultSettingsRecoveredFromCorruptData?.Invoke(backupPath);
    }

    /// <summary>Only ever called from inside the <see cref="JsonFileStore.WithLock"/> block in
    /// <see cref="Save"/> — the lock it needs is already held by the caller (a named
    /// <see cref="System.Threading.Mutex"/> is re-entrant for the thread that owns it), so this reads the
    /// file directly instead of going through <see cref="JsonFileStore.TryLoad{T}"/> and taking the lock a
    /// second time.</summary>
    private string? TryReadVaultPathFromDiskUnlocked()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json)?.VaultPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Warning($"No se pudo conservar VaultPath desde '{_settingsPath}'", ex);
            return null;
        }
    }

    /// <summary>"Restablecer ajustes a valores por defecto" (Ajustes › General, zona de peligro,
    /// docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md) — reemplaza <see cref="Settings"/> por una
    /// instancia nueva de <see cref="AppSettings"/>, conservando solo <c>VaultPath</c> y <c>Language</c> para
    /// no desvincular el vault activo ni forzar al usuario a re-elegir idioma como efecto colateral de
    /// "quiero valores por defecto" (docs/agent-docs/internacionalizacion-i18n.md §2.2 — el idioma ya fue una
    /// elección activa del usuario, igual que el vault). No refresca en caliente ningún viewmodel ya cargado
    /// (ese cableado no existe hoy para ningún ajuste) — el llamador debe pedir al usuario que reinicie Enki
    /// para que el resto de la app relea estos valores.</summary>
    public void ResetToDefaults()
    {
        var vaultPath = Settings.VaultPath;
        var language = Settings.Language;
        Settings = new AppSettings { VaultPath = vaultPath, Language = language };
        Save();
    }

    /// <summary>Builds the JSON <see cref="ViewModels.SettingsViewModel.ExportSettingsCommand"/> writes to
    /// the user-chosen destination, with <see cref="AppSettings.PolarAccessToken"/>/
    /// <see cref="AppSettings.PolarRefreshToken"/> redacted — legacy <c>settings.json</c> files may still
    /// hold a live Supabase session until migration succeeds. "Exportar ajustes" must not hand out
    /// a working login that anyone who receives the exported file (backup drive, USB, support ticket) could
    /// replay (docs/analisis-proyecto/roadmap.md, Fase 4). Round-trips a fresh copy of <see cref="Settings"/>
    /// instead of mutating it in place, so the live in-memory session the rest of the running app still uses
    /// is never touched.</summary>
    public string BuildRedactedExportJson()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!;
        copy.PolarAccessToken = null;
        copy.PolarRefreshToken = null;
        return JsonSerializer.Serialize(copy, IndentedJsonOptions);
    }
}
