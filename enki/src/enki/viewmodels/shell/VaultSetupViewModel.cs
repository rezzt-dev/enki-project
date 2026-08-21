using System;
using System.Globalization;
using System.IO;
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

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Explains why this screen showed up when it isn't simply a brand-new install — a
    /// previously configured vault that's now missing/moved, or a second Enki window that needs its own
    /// folder. Null on a first-ever launch, where there's nothing to explain yet.</summary>
    public string? InfoMessage { get; }

    public VaultSetupViewModel(VaultService vaultService, Action onVaultReady, string? infoMessage = null)
    {
        _vaultService = vaultService;
        _onVaultReady = onVaultReady;
        InfoMessage = infoMessage;
    }

    [RelayCommand]
    private async Task CreateNewVault() =>
        await PickAndInitialize(LocalizationService.Instance["VaultSetup_CreateFolderPickerTitle"], seedWelcomeNote: true);

    [RelayCommand]
    private async Task OpenExistingVault() =>
        await PickAndInitialize(LocalizationService.Instance["VaultSetup_OpenFolderPickerTitle"], seedWelcomeNote: false);

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

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            _vaultService.InitializeVault(folder, seedWelcomeNote);
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
            // Last-resort fallback for exception types we can't give a fully-translated message to.
            // Exception.Message here is a documented limitation, not a bug to "fully fix" — a platform
            // exception's text is never guaranteed to be in Enki's active language unless Enki intercepts
            // it by type, as the cases above already do for the common, expectable ones.
            ErrorMessage = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["VaultSetup_Error_Generic"], ex.Message);
        }
    }
}
