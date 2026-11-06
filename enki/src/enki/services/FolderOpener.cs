using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace enki.Services;

/// <summary>Abre una carpeta en el explorador de archivos del sistema — mismo patrón try/catch-e-ignorar
/// que <see cref="UrlOpener"/> (sin diálogo de error si no hay gestor de archivos asociado). En Linux se
/// invoca <c>xdg-open</c> explícitamente porque <c>Process.Start</c> con <c>UseShellExecute = true</c> no
/// resuelve una ruta de carpeta de forma tan fiable como una URL; en Windows/macOS,
/// <c>UseShellExecute = true</c> ya abre el explorador nativo directamente.</summary>
public static class FolderOpener
{
    public static void Open(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", path);
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Ignorar: sin gestor de archivos asociado o ruta no accesible.
            LogService.Warning($"No se pudo abrir la carpeta '{path}'", ex);
        }
    }

    /// <summary>Abre el explorador de archivos con <paramref name="filePath"/> ya seleccionado, en vez de
    /// solo abrir la carpeta contenedora — así el usuario no tiene que buscar el archivo entre el resto
    /// del contenido de la carpeta. Explorer (Windows) y Finder (macOS) soportan seleccionar un archivo
    /// concreto de forma nativa; ningún gestor de archivos de Linux tiene un mecanismo estándar para eso,
    /// así que ahí se cae a <see cref="Open"/> sobre la carpeta contenedora.</summary>
    public static void RevealFile(string filePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\""));
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\""));
            else if (Path.GetDirectoryName(filePath) is { } directory)
                Open(directory);
        }
        catch (Exception ex)
        {
            // Ignorar: sin gestor de archivos asociado o ruta no accesible.
            LogService.Warning($"No se pudo revelar el archivo '{filePath}'", ex);
        }
    }
}
