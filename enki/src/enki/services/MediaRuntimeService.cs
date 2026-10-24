using System;
using System.IO;
using LibVLCSharp.Shared;

namespace enki.Services;

/// <summary>Carga el motor distribuido con Enki; en Linux lo prepara el instalador del paquete.</summary>
internal static class MediaRuntimeService
{
    public static void Initialize()
    {
        if (OperatingSystem.IsMacOS())
        {
            var root = Path.Combine(AppContext.BaseDirectory, "libvlc");
            var libraryPath = Path.Combine(root, "lib");
            if (!File.Exists(Path.Combine(libraryPath, "libvlc.dylib")))
                throw new DllNotFoundException("El paquete de Enki no contiene el motor multimedia de macOS.");
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", Path.Combine(root, "plugins"));
            Core.Initialize(libraryPath);
            return;
        }
        // Windows: VideoLAN.LibVLC.Windows incluye las bibliotecas y plugins junto al ejecutable.
        // Linux: Core.Initialize no admite una ruta; el gestor de paquetes resuelve las bibliotecas.
        Core.Initialize();
    }
}
