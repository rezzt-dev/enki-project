using System.IO;
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
        JsonFileStore.Save(SettingsPath, Settings);
    }
}
