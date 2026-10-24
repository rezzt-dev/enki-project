using System;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una fila del selector "Cambiar de vault" (T4,
/// .docs/project-info/multi-vault-en-caliente.md). Se construye de cero en cada apertura del selector
/// (<see cref="VaultSwitcherViewModel.Refresh"/>) en vez de mantenerse viva y actualizarse, porque lo que
/// distingue una fila de otra —si su carpeta sigue estando y se deja leer— se comprueba contra el disco en
/// ese mismo momento: un disco externo se conecta y se desconecta sin avisar a nadie.</summary>
public sealed partial class RecentVaultItemViewModel : ObservableObject
{
    public RecentVaultItemViewModel(RecentVault vault, bool isActive, LocalizedText? unavailableReason)
    {
        Path = vault.Path;
        DisplayName = string.IsNullOrWhiteSpace(vault.DisplayName) ? vault.Path : vault.DisplayName;
        LastOpenedUtc = vault.LastOpenedUtc;
        IsActive = isActive;
        _unavailableReason = unavailableReason;
    }

    private readonly LocalizedText? _unavailableReason;

    public string Path { get; }

    public string DisplayName { get; }

    public DateTime LastOpenedUtc { get; }

    /// <summary>El vault que está abierto ahora mismo. Se muestra marcado y su fila no cambia de vault al
    /// pulsarla (sería un recargado completo para acabar donde ya se estaba), pero sí sigue ofreciendo
    /// "quitar de recientes", que es una acción sobre la lista y no sobre el vault.</summary>
    public bool IsActive { get; }

    public bool IsAvailable => _unavailableReason is null;

    /// <summary>Motivo concreto ("la carpeta ya no existe", "acceso denegado"…) para la fila atenuada.
    /// Cadena vacía cuando el vault está disponible, de modo que el <c>TextBlock</c> que lo muestra puede
    /// ocultarse con el propio <see cref="IsAvailable"/> sin un converter extra.</summary>
    public string UnavailableReason => LocalizedTextFormatter.Resolve(_unavailableReason);

    /// <summary>"hace 3 días" — mismo formateador relativo que usan las notas recientes y el registro de
    /// avisos, para que el selector no estrene su propia forma de decir cuándo.</summary>
    public string LastOpenedLabel => RelativeTimeFormatter.Format(LastOpenedUtc);
}
