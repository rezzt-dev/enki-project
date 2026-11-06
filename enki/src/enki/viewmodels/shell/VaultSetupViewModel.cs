using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class VaultSetupViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly Action _onVaultReady;
    private readonly SettingsService? _settingsService;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Explains why this screen showed up when it isn't simply a brand-new install — a
    /// previously configured vault that's now missing/moved, or a second Enki window that needs its own
    /// folder. Null on a first-ever launch, where there's nothing to explain yet.</summary>
    public string? InfoMessage { get; }

    /// <summary>Perfiles de uso entre los que elegir al crear un vault nuevo (ALT-02). Vacío si no hay
    /// <see cref="SettingsService"/> al que aplicarlos.</summary>
    public IReadOnlyList<ModuleProfileOption> ProfileOptions { get; }

    /// <summary>Perfil elegido; solo se aplica al crear un vault nuevo, nunca al abrir uno existente, cuyos
    /// ajustes ya son del usuario.</summary>
    [ObservableProperty]
    private ModuleProfileOption? _selectedProfile;

    public bool HasProfileOptions => ProfileOptions.Count > 0;

    public VaultSetupViewModel(VaultService vaultService, Action onVaultReady, string? infoMessage = null,
        SettingsService? settingsService = null)
    {
        _vaultService = vaultService;
        _onVaultReady = onVaultReady;
        _settingsService = settingsService;
        InfoMessage = infoMessage;

        ProfileOptions = settingsService is null
            ? []
            : ModuleProfileCatalog.All.Select(p => new ModuleProfileOption(
                p.Id,
                LocalizationService.Instance[$"ModuleProfile_{p.Id}_Title"],
                LocalizationService.Instance[$"ModuleProfile_{p.Id}_Description"])).ToArray();
        _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == ModuleProfileCatalog.Everything);
    }

    [RelayCommand]
    private async Task CreateNewVault() =>
        await PickAndInitialize(LocalizationService.Instance["VaultSetup_CreateFolderPickerTitle"], seedWelcomeNote: true);

    [RelayCommand]
    private async Task OpenExistingVault() =>
        await PickAndInitialize(LocalizationService.Instance["VaultSetup_OpenFolderPickerTitle"], seedWelcomeNote: false);

    /// <summary>Aplica el perfil elegido a los ajustes del vault recién creado. Se omite al abrir un vault
    /// existente o con ajustes previos: ahí los módulos activos son los que el usuario ya dejó configurados.</summary>
    private void ApplySelectedProfile(bool isNewVault)
    {
        if (!isNewVault || _settingsService is null) return;
        if (ModuleProfileCatalog.Find(SelectedProfile?.Id) is not { } profile) return;

        ModuleProfileCatalog.ApplyTo(_settingsService.Settings, profile);
        _settingsService.Save();
    }

    private async Task PickAndInitialize(string title, bool seedWelcomeNote)
    {
        ErrorMessage = null;

        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        var pickedFolder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(pickedFolder)) return;

        // The picked folder (e.g. "Documents") is a general-purpose folder, not the vault itself — the
        // actual vault lives nested one level inside it so Enki's own files never scatter alongside the
        // user's unrelated ones. See VaultService.ResolveVaultFolder for how an already-existing vault
        // (old-style flat, or already nested) is recognized and left untouched instead of double-nested.
        var folder = VaultService.ResolveVaultFolder(pickedFolder);

        try
        {
            // "Crear" sobre una carpeta que ya es un vault con ajustes propios no debe pisarlos con un perfil.
            var isFreshVault = seedWelcomeNote && !File.Exists(Path.Combine(folder, ".enki", "settings.json"));
            _vaultService.InitializeVault(folder, seedWelcomeNote);
            ApplySelectedProfile(isFreshVault);
            // Ensuring the vault has its "default" session happens centrally in
            // MainWindowViewModel.InitializeShell — covers this path plus a plain app startup against
            // an already-configured vault, so an old vault (created before sessions existed) also gets
            // one instead of only brand-new vaults.
            _onVaultReady();
        }
        // Specific, expected failure types get a fully-translated message (docs/agent-docs/
        // internacionalizacion-i18n.md §5.3) instead of leaking Exception.Message, which can come through
        // in the OS's own language rather than Enki's active one. PathTooLongException must be caught
        // before IOException since it derives from it.
        catch (UnauthorizedAccessException)
        {
            ErrorMessage = LocalizationService.Instance["VaultSetup_Error_AccessDenied"];
        }
        catch (PathTooLongException)
        {
            ErrorMessage = LocalizationService.Instance["VaultSetup_Error_PathTooLong"];
        }
        catch (IOException)
        {
            ErrorMessage = LocalizationService.Instance["VaultSetup_Error_IOFailure"];
        }
        catch (ArgumentException)
        {
            ErrorMessage = LocalizationService.Instance["VaultSetup_Error_InvalidPath"];
        }
        catch (Exception ex)
        {
            ErrorMessage = VaultFailureReasons.Describe(ex);
        }
    }
}

/// <summary>Una opción del selector de perfil de la pantalla de bienvenida.</summary>
public sealed record ModuleProfileOption(string Id, string Title, string Description);
