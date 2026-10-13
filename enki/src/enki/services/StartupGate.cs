using System;
using System.IO;
using enki.Localization;

namespace enki.Services;

/// <summary>Qué tiene que cumplir este arranque para que Enki vaya directo a la shell, sin pedirle nada antes
/// al usuario (idioma en un primer arranque, elegir vault cuando no hay ninguna ruta guardada).
///
/// <para>La decisión vive aquí, y no dentro del ViewModel, porque la necesitan dos sitios en dos momentos
/// distintos. <c>MainWindow</c> la consulta antes de construir nada: si va a abrir la shell, la ventana se
/// muestra ya con la pantalla de arranque puesta y deja todo lo pesado detrás de ella; si no, no hay nada
/// que tapar y construye la shell tal cual. <c>MainWindowViewModel.ShowVaultSetupOrShell</c> es quien la
/// ejecuta de verdad. Con la condición escrita en los dos sitios, cualquier cambio en el flujo de primer
/// arranque dejaría a la ventana cubriendo la pantalla de configuración de vault con una cortina que nadie
/// va a retirar — que es un cuelgue a ojos del usuario.</para></summary>
public static class StartupGate
{
    /// <summary>Tiempo mínimo visible del arranque. La carga transcurre dentro de este intervalo;
    /// si tarda más, la pantalla permanece hasta que la sección inicial esté lista.</summary>
    public static readonly TimeSpan MinimumDisplayDuration = TimeSpan.FromSeconds(2);

    /// <summary>Si hay un vault abierto de verdad: una ruta guardada que además sigue existiendo en disco.
    /// Una carpeta movida o una unidad externa desenchufada no cuentan, y eso es lo que hace que la shell
    /// caiga a la pantalla de vault en vez de arrancar contra una ruta que ya no está.</summary>
    public static bool IsVaultConfigured(string? vaultPath) =>
        !string.IsNullOrWhiteSpace(vaultPath) && Directory.Exists(vaultPath);

    /// <summary>Si el arranque va a terminar en la shell sin pasar por ninguna pantalla de configuración:
    /// idioma ya elegido (<see cref="SupportedLanguages.Find"/>), vault configurado que sigue existiendo, y
    /// esta es la única instancia de Enki en marcha — una segunda instancia nunca abre el mismo vault, ver
    /// <c>SingleInstanceGuard</c>.</summary>
    public static bool OpensShellDirectly(SettingsService settingsService)
    {
        var settings = settingsService.Settings;
        return SupportedLanguages.Find(settings.Language) is not null
            && IsVaultConfigured(settings.VaultPath)
            && SingleInstanceGuard.IsFirstInstance;
    }
}
