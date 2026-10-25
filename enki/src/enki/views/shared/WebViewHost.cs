using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using enki.Localization;
using enki.Services;

namespace enki.Views;

/// <summary>Comprueba la compatibilidad del navegador antes de adjuntarlo al árbol visual.
/// El grafo dispone de un renderizador Avalonia cuando estas dependencias no son utilizables.</summary>
public static class WebViewHost
{
    private static bool? _available;

    public static bool IsEngineAvailable()
    {
        if (_available is { } cached) return cached;

        bool available;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // El documento usa JavaScript moderno: Edge Legacy no es una alternativa compatible.
                available = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled;
            }
            else if (OperatingSystem.IsMacOS())
            {
                available = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView).IsInstalled;
            }
            else if (OperatingSystem.IsLinux())
            {
                // Avalonia 12.1 usa la API de backend de WPE. Algunas distribuciones publican
                // libWPEWebKit-2.0.so.1 sin webkit_web_view_backend_new: tener el paquete instalado
                // no basta. Este símbolo pertenece a WPE, no a WebKitGTK. No se debe retirar la
                // comprobación ni dejar que el adaptador se inicialice: puede fallar en async void.
                // El grafo nativo evita depender del ABI de WPE y del alojamiento GTK/X11.
                var wpeInfo = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit);
                bool wpeInstalled = wpeInfo.IsInstalled && wpeInfo.Engine != WebViewEngine.Unknown;
                available = wpeInstalled
                    && HasNativeExports("libWPEWebKit-2.0.so.1", "webkit_web_view_backend_new", "webkit_web_view_new")
                    && HasNativeExports("libwpe-1.0.so.1", "wpe_view_backend_dispatch_pointer_event")
                    && HasNativeExports("libWPEBackend-fdo-1.0.so.1", "wpe_fdo_initialize_shm", "wpe_view_backend_exportable_fdo_create");
            }
            else
            {
                available = false;
            }
        }
        catch (Exception ex)
        {
            available = false;
            LogService.Warning("No se pudo consultar la disponibilidad del WebView", ex);
        }

        _available = available;
        return available;
    }

    /// <summary>Valida las dependencias separadas del backend antes de su inicialización asíncrona.</summary>
    private static bool HasNativeExports(string library, params string[] symbols)
    {
        try
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
            {
                LogService.Warning($"WebView no compatible: no se pudo cargar {library}");
                return false;
            }
            try
            {
                foreach (var symbol in symbols)
                {
                    if (NativeLibrary.TryGetExport(handle, symbol, out _)) continue;
                    LogService.Warning($"WebView no compatible: {library} no exporta {symbol}");
                    return false;
                }
                return true;
            }
            finally { NativeLibrary.Free(handle); }
        }
        catch (Exception ex)
        {
            LogService.Warning($"No se pudo comprobar la biblioteca WebView {library}", ex);
            return false;
        }
    }

    public static Control BuildUnavailablePanel(string title, string? detailOverride = null)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Classes = { "section-title" },
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = detailOverride ?? (OperatingSystem.IsLinux()
                ? LocalizationService.Instance["WebView_Unavailable_Linux"]
                : LocalizationService.Instance["WebView_Unavailable_Generic"]),
            Classes = { "muted" },
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        });
        return new Border { Child = panel, Padding = new Avalonia.Thickness(32) };
    }
}
