using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>El selector "Cambiar de vault" de la cabecera del sidebar (T4,
/// .docs/project-info/multi-vault-en-caliente.md): lista de carpetas abiertas recientemente, "abrir otra
/// carpeta como vault…" y "quitar de recientes".
///
/// <para>Este viewmodel <b>no cambia de vault</b>: solo presenta la lista y delega en
/// <c>MainWindowViewModel.SwitchToVaultAsync</c> a través de <paramref name="openVault"/>. El cambio en
/// caliente toca todos los módulos a la vez (volcar editores, parar temporizadores, bloquear sesiones,
/// recargar servicios), así que vive donde está la composición, no aquí — el mismo reparto que hace
/// <see cref="QuickSwitcherViewModel"/>, que busca notas pero no sabe abrirlas.</para></summary>
public sealed partial class VaultSwitcherViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly VaultService _vaultService;
    private readonly Func<string, Task> _openVault;
    private readonly Func<Task<string?>> _pickVaultFolder;

    public VaultSwitcherViewModel(SettingsService settingsService, VaultService vaultService,
        Func<string, Task> openVault, Func<Task<string?>> pickVaultFolder)
    {
        _settingsService = settingsService;
        _vaultService = vaultService;
        _openVault = openVault;
        _pickVaultFolder = pickVaultFolder;
        Refresh();
    }

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Nombre de la carpeta del vault abierto, el rótulo que se ve en el sidebar con el selector
    /// cerrado.</summary>
    [ObservableProperty]
    private string _activeVaultName = string.Empty;

    /// <summary>Ruta completa del vault abierto — se enseña bajo el nombre porque dos vaults llamados
    /// "notas" en carpetas distintas es exactamente el caso que esta funcionalidad viene a resolver.</summary>
    [ObservableProperty]
    private string _activeVaultPath = string.Empty;

    public ObservableCollection<RecentVaultItemViewModel> Recents { get; } = [];

    /// <summary>Falso cuando la única carpeta conocida es la que ya está abierta: sin ninguna otra a la
    /// que ir, la lista se oculta y solo queda "abrir otra carpeta…", que es la acción que de verdad
    /// aplica entonces.</summary>
    public bool HasOtherVaults { get; private set; }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    partial void OnIsOpenChanged(bool value)
    {
        // Al abrir, no al cerrar: comprobar disponibilidad toca el disco una vez por entrada, y el
        // momento en que eso importa es justo antes de enseñar la lista.
        if (value) Refresh();
    }

    /// <summary>Reconstruye la lista desde <c>AppSettings.RecentVaults</c> y vuelve a comprobar, entrada
    /// por entrada, si su carpeta sigue disponible. Público porque el shell también lo llama tras cambiar
    /// de vault, para que el rótulo del sidebar y la marca de "activo" reflejen el cambio sin tener que
    /// volver a abrir el selector.</summary>
    public void Refresh()
    {
        var activePath = _vaultService.VaultPath;
        ActiveVaultPath = activePath;
        ActiveVaultName = DisplayNameFor(activePath);

        Recents.Clear();
        var others = 0;
        foreach (var vault in _settingsService.Settings.RecentVaults)
        {
            var isActive = !string.IsNullOrEmpty(activePath) && VaultService.PathsEqual(vault.Path, activePath);
            if (!isActive) others++;
            Recents.Add(new RecentVaultItemViewModel(vault, isActive, VaultService.DescribeVaultUnavailability(vault.Path)));
        }

        HasOtherVaults = others > 0;
        OnPropertyChanged(nameof(HasOtherVaults));
    }

    /// <summary>Cambia al vault de la fila pulsada. Una fila no disponible no hace nada al pulsarla (su
    /// botón ya está deshabilitado en la vista) y la del vault activo tampoco: recargar los doce módulos
    /// para acabar exactamente donde se estaba no es lo que nadie espera de ese clic.</summary>
    [RelayCommand]
    private async Task OpenRecent(RecentVaultItemViewModel? item)
    {
        if (item is null || item.IsActive || !item.IsAvailable) return;

        IsOpen = false;
        await _openVault(item.Path);
        Refresh();
    }

    /// <summary>"Quitar de recientes": saca la entrada de la lista sin tocar un solo archivo del vault
    /// (ver <see cref="SettingsService.RemoveRecentVault"/>). No cierra el selector — quitar dos entradas
    /// seguidas es lo normal cuando se está limpiando la lista.</summary>
    [RelayCommand]
    private void RemoveRecent(RecentVaultItemViewModel? item)
    {
        if (item is null) return;

        _settingsService.RemoveRecentVault(item.Path);
        Refresh();
    }

    /// <summary>"Abrir otra carpeta como vault…" — el mismo diálogo y la misma resolución de carpeta que
    /// Ajustes › General, provisto por el shell (que es quien puede llegar a un <c>TopLevel</c>).</summary>
    [RelayCommand]
    private async Task BrowseForVault()
    {
        IsOpen = false;
        if (await _pickVaultFolder() is not { } folder) return;

        await _openVault(folder);
        Refresh();
    }

    /// <summary>Los rótulos de las filas ("hace 3 días", el motivo de no disponible) se resuelven al
    /// construirlas, así que un cambio de idioma en caliente necesita reconstruirlas — ver
    /// <c>MainWindowViewModel.RefreshAllLocalizedLabels</c>.</summary>
    public void RefreshLocalizedLabels() => Refresh();

    private static string DisplayNameFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var name = System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
