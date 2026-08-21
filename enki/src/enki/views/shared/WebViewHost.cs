using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using enki.Localization;

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
                // via an unhandled async exception even when GetAdapterInfo(WebKitGtk) reports installed
                // (observed firsthand: webkit2gtk present, but GTK itself failed to initialize here).
                available = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WpeWebKit).IsInstalled;
            }
            else
            {
                available = false;
            }
        }
        catch
        {
            available = false;
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
