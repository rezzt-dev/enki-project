using System.Diagnostics;
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
        catch
        {
            // Ignorar: sin gestor de archivos asociado o ruta no accesible.
        }
    }
}
