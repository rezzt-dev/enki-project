using System;
using System.IO;
using System.Text.Json;
using enki.Models;

namespace enki.Services;

/// <summary>Persists <see cref="VaultSettings"/> inside the active vault, mirroring how
/// <see cref="SessionService"/> resolves its own storage root from <see cref="VaultService.VaultPath"/>
/// live instead of a fixed folder (<c>SessionsRoot</c>) — here that's <c>&lt;vault&gt;/.enki/settings.json</c>,
/// same hidden-folder-inside-the-vault convention as <c>.trash</c>/<c>.sessions</c>. Unlike
/// <see cref="SessionService"/>, this does cache its loaded state in <see cref="Settings"/> (so
/// <c>ViewModels.MainWindowViewModel</c> has something to bind a live UI-zoom scale to), which is why it
/// needs an explicit <see cref="Reload"/> for when the active vault changes underneath it.</summary>
public sealed class VaultSettingsService
{
    private readonly VaultService _vaultService;

    public VaultSettings Settings { get; private set; }

    /// <summary>Se dispara tras guardar los ajustes del vault para actualizar las vistas que dependen
    /// de ellos sin esperar a reiniciar la aplicación.</summary>
    public event Action? SettingsChanged;

    public VaultSettingsService(VaultService vaultService)
    {
        _vaultService = vaultService;
        Settings = Load();
    }

    private string SettingsPath => Path.Combine(_vaultService.VaultPath, ".enki", "settings.json");

    private VaultSettings Load()
    {
        if (!_vaultService.IsVaultConfigured) return new VaultSettings();
        var loaded = JsonFileStore.TryLoad<VaultSettings>(SettingsPath, out var result) && result is not null ? result : new VaultSettings();
        loaded.Normalize();
        return loaded;
    }

    /// <summary>Re-reads settings for whichever vault <see cref="VaultService"/> now points at — called
    /// from <c>MainWindowViewModel.InitializeShell</c>/<c>OnVaultChanged</c>, the same two points where
    /// the rest of the vault-scoped state (Dashboard, Notes, Sessions…) already reloads.</summary>
    public void Reload() => Settings = Load();

    public void Save()
    {
        if (!_vaultService.IsVaultConfigured) return;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        JsonFileStore.WithLock(SettingsPath, () =>
        {
            var merged = SettingsService.ReadVaultObject(SettingsPath);
            var own = JsonSerializer.SerializeToNode(Settings)!.AsObject();
            foreach (var entry in own) merged[entry.Key] = entry.Value?.DeepClone();
            JsonFileStore.Save(SettingsPath, merged);
        });
        SettingsChanged?.Invoke();
    }

    /// <summary>Identidad del vault abierto (<see cref="VaultSettings.VaultId"/>), creándola y guardándola si
    /// todavía no la tiene. Si el vault no se puede escribir (solo lectura, disco lleno), se usa una identidad
    /// derivada de su ruta: es estable mientras la carpeta no se mueva y no impide usar el asistente.</summary>
    public string EnsureVaultId()
    {
        if (Settings.VaultId is { Length: > 0 } existing) return existing;
        if (!_vaultService.IsVaultConfigured) return PathIdentity(_vaultService.VaultPath);
        var created = Guid.NewGuid().ToString("N");
        Settings.VaultId = created;
        try
        {
            Save();
            return created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo guardar la identidad del vault; se usa la de su ruta", ex);
            Settings.VaultId = null;
            return PathIdentity(_vaultService.VaultPath);
        }
    }

    /// <summary>Identidad de reserva para un vault sin <see cref="VaultSettings.VaultId"/>: un resumen de su
    /// ruta completa, con un prefijo que la distingue de las generadas.</summary>
    public static string PathIdentity(string vaultPath) => string.IsNullOrWhiteSpace(vaultPath)
        ? "pathnone"
        : "path" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(vaultPath)))).ToLowerInvariant()[..28];
}
