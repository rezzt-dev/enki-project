using System;
using System.IO;
using System.Text.Json;
using enki.Models;

namespace enki.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;
    public AppSettings Settings { get; private set; }

    /// <summary>True cuando <c>settings.json</c> existía pero no se pudo leer o deserializar (archivo
    /// dañado), por lo que <see cref="Settings"/> se reemplazó por una instancia nueva por defecto —
    /// incluido, si estaba vinculada, la pérdida del vínculo con Polar. No se activa en una instalación
    /// nueva donde el archivo simplemente no existe todavía. Mismo patrón que
    /// <see cref="KanbanService.LoadedFromCorruptData"/> (docs/analisis-proyecto/roadmap.md, Fase 1) —
    /// consultado por el compositor (<c>MainWindowViewModel</c>) justo después de construir este servicio
    /// para avisar al usuario en vez de resetear los ajustes en silencio.</summary>
    public bool LoadedFromCorruptData { get; private set; }

    /// <summary>Ruta absoluta de <c>settings.json</c> — expuesta para "Exportar/Importar ajustes"
    /// (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §1), que copian el fichero tal cual en
    /// vez de duplicar la serialización aquí.</summary>
    public string SettingsFilePath => _settingsPath;

    /// <summary>VaultPath as last known from disk (at load, or after this instance itself wrote it) —
    /// lets <see cref="Save"/> tell "this process just changed the vault" apart from "some other setting
    /// changed while an unrelated in-memory copy of VaultPath happened to be sitting here". See remarks
    /// on <see cref="Save"/>.</summary>
    private string? _lastKnownVaultPath;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, but lets tests point persistence at a throwaway directory instead of the developer's
    /// real settings file (see enki.Tests).</param>
    public SettingsService(string? storageDirectory = null)
    {
        var configDir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enki");
        Directory.CreateDirectory(configDir);
        _settingsPath = Path.Combine(configDir, "settings.json");
        Settings = Load();
        _lastKnownVaultPath = Settings.VaultPath;
    }

    private AppSettings Load()
    {
        if (JsonFileStore.TryLoad<AppSettings>(_settingsPath, out var loaded)) return loaded!;

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
        JsonFileStore.WithLock(_settingsPath, () =>
        {
            if (Settings.VaultPath == _lastKnownVaultPath)
            {
                var onDiskVaultPath = TryReadVaultPathFromDiskUnlocked();
                if (onDiskVaultPath is not null) Settings.VaultPath = onDiskVaultPath;
            }
            _lastKnownVaultPath = Settings.VaultPath;

            JsonFileStore.Save(_settingsPath, Settings);
        });
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
        catch
        {
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
    /// <see cref="AppSettings.PolarRefreshToken"/> redacted — <c>settings.json</c> on disk holds a live
    /// Supabase session, and "Exportar ajustes" is meant to move preferences between machines, not hand out
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
