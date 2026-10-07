using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using enki.Localization;
using enki.Services;

namespace enki.Views;

/// <summary>Shared platform-WebView-runtime detection and fallback UI, used by every feature that embeds
/// a <see cref="Avalonia.Controls.NativeWebView"/> (Markdown preview, session graph). The runtime isn't
/// guaranteed present — no WPE/WebKitGTK on Linux, an old WebView2-less Windows, etc. — and attaching a
/// <c>NativeWebView</c> without checking first can crash the whole process with an unhandled async
/// exception, so every call site must gate on <see cref="IsEngineAvailable"/> before creating one.</summary>
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
                available = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled
                            || WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView1).IsInstalled;
            }
            else if (OperatingSystem.IsMacOS())
            {
                available = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView).IsInstalled;
            }
            else if (OperatingSystem.IsLinux())
            {
                // Only WPE is safe to trust here: Avalonia's WebView package guards the WPE path with
                // its own internal availability check before touching it, but falls through to a GTK
                // adapter unconditionally otherwise — and that path can still crash the whole process
                // via an unhandled async exception even when GetAdapterInfo(WebKitGtk) reports installed.
                //
                // Observed on rolling-release distros (Arch/XeroLinux): webkit2gtk-4.1 may be present but
                // the symbol webkit_web_view_backend_new it exposes at runtime can be missing (the function
                // was added later and some distro versions ship without it). Attaching a NativeWebView then
                // throws EntryPointNotFoundException and takes down the process. WPE WebKit ships its own
                // copy of the backend symbols and is therefore the only reliable adapter on Linux.
                //
                // When WPE is installed AND actually usable, use it; otherwise fail closed (no WebView) so
                // the rest of the app keeps working and the user sees the unavailable-panel fallback.
                //
                // Defense in depth: even when GetAdapterInfo says WPE is installed, verify at runtime that
                // the critical symbol webkit_web_view_backend_new exists in libWPEWebKit-2.0.so.1 (the
                // function Avalonia's WpeWebViewAdapter.Initialize() calls). Some distro versions of WPE
                // ship without it and would otherwise crash the process the moment a NativeWebView is
                // attached. Only when the symbol is present do we consider the engine available.
                var wpeInfo = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit);
                bool wpeInstalled = wpeInfo.IsInstalled && wpeInfo.Engine != WebViewEngine.Unknown;
                if (wpeInstalled)
                {
                    // Check the critical symbol directly — this is the one whose absence crashes the
                    // process. We load the exact same library name Avalonia's WpeInterop uses.
                    try
                    {
                        var handle = NativeLibrary.Load("libWPEWebKit-2.0.so.1");
                        available = NativeLibrary.TryGetExport(handle, "webkit_web_view_backend_new", out _);
                        NativeLibrary.Free(handle);
                    }
                    catch
                    {
                        available = false;
                    }
                }
                else
                {
                    available = false;
                }
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
