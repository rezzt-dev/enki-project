using System;

namespace enki.Models;

/// <summary>Una entrada de la lista de vaults recientes (<see cref="AppSettings.RecentVaults"/>), el
/// respaldo del selector "Cambiar de vault" de la cabecera del sidebar y de la paleta de comandos.
///
/// <para>Vive en los ajustes <b>globales</b> y no en <see cref="VaultSettings"/>: la lista describe qué
/// vaults conoce esta instalación, así que guardarla dentro de uno de ellos la dejaría invisible desde
/// todos los demás — que es justo cuando hace falta. Por eso <c>RecentVaults</c> está en
/// <c>SettingsService.InstallationKeys</c> y sobrevive a <c>SettingsService.SwitchVault</c>.</para>
///
/// <para>Solo guarda la ruta y cómo llamarla; si esa ruta sigue existiendo y es legible se comprueba en
/// caliente cada vez que se abre el selector (<c>ViewModels.VaultSwitcherViewModel</c>), nunca se
/// persiste: un disco externo desconectado hoy vuelve a estar disponible mañana, y una entrada borrada
/// automáticamente por estar "no disponible" sería justo la que el usuario querría recuperar.</para></summary>
public sealed class RecentVault
{
    /// <summary>Ruta absoluta de la carpeta del vault, tal cual se pasó a
    /// <see cref="Services.VaultService.SetVaultPath"/>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Nombre que se muestra en el selector. Por defecto el de la carpeta; se guarda aparte para
    /// que renombrar la carpeta fuera de Enki no deje la entrada sin rótulo.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Última vez que este vault se abrió desde Enki — ordena la lista de más reciente a más
    /// antiguo y decide cuál se descarta al llegar al máximo de <see cref="AppSettings.MaxRecentVaults"/>.</summary>
    public DateTime LastOpenedUtc { get; set; }
}
