using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace enki.Services;

/// <summary>Applies/reverts a custom theme's colors at runtime by writing directly into
/// <c>Application.Current.Resources</c> — the exact same technique
/// <c>SettingsViewModel.ApplyInterfaceFont</c> already uses to override the interface font keys live
/// (indexer assignment on the top-level resource dictionary, no rebuild of
/// <c>Application.Styles</c>/<c>MergedDictionaries</c>). A key set directly on <c>Application.Resources</c>
/// is resolved before Avalonia falls back to <c>Colors.axaml</c>'s <c>Dark</c>/<c>Light</c>
/// <c>ThemeDictionaries</c>, so this wins regardless of <c>RequestedThemeVariant</c> — a custom theme is a
/// third, independent palette, not a variant of light/dark. Every consumer in <c>Styles.axaml</c> already
/// reads these keys through <c>{DynamicResource}</c>, so applying/clearing here take effect immediately with
/// no further wiring.</summary>
public static class ThemeRegistry
{
    private static readonly string[] SystemAccentKeys =
    {
        "SystemAccentColor",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
    };

    public static void Apply(IReadOnlyDictionary<string, Color> colors)
    {
        if (Avalonia.Application.Current is not { } app) return;
        foreach (var (key, color) in colors) app.Resources[key] = color;
    }

    /// <summary>Generates the Fluent <c>SystemAccentColor*</c> palette from the chosen
    /// <c>AccentColor</c> and writes it into <c>Application.Current.Resources</c>. This lets focus rings,
    /// checked states and selection highlights follow the user's accent instead of the default Fluent blue.</summary>
    public static void ApplySystemAccent(IReadOnlyDictionary<string, Color> colors)
    {
        if (Avalonia.Application.Current is not { } app) return;
        if (!colors.TryGetValue("AccentColor", out var accent)) return;

        var variants = ComputeAccentVariants(accent);
        for (var i = 0; i < SystemAccentKeys.Length; i++)
            app.Resources[SystemAccentKeys[i]] = variants[i];
    }

    /// <summary>Removes every color a custom theme could have set, letting resolution fall back to
    /// <c>Colors.axaml</c>'s <c>ThemeDictionaries</c> again. Iterates <see cref="ThemeCatalog"/> instead of
    /// whatever the last-applied theme happened to contain, so it's safe to call unconditionally (e.g. at
    /// startup before deciding whether a custom theme should apply) without tracking "is one currently
    /// applied" separately.</summary>
    public static void Clear()
    {
        if (Avalonia.Application.Current is not { } app) return;
        foreach (var key in ThemeCatalog.All.Select(d => d.ResourceKey)) app.Resources.Remove(key);
        foreach (var key in SystemAccentKeys) app.Resources.Remove(key);
    }

    private static Color[] ComputeAccentVariants(Color accent)
    {
        // Convert to HSL, adjust lightness for the darker/lighter Fluent variants, then convert back.
        var hsl = RgbToHsl(accent);
        return new[]
        {
            accent,
            HslToRgb(hsl.H, hsl.S, Math.Max(0, hsl.L - 0.08)),
            HslToRgb(hsl.H, hsl.S, Math.Max(0, hsl.L - 0.16)),
            HslToRgb(hsl.H, hsl.S, Math.Max(0, hsl.L - 0.24)),
            accent,
            accent,
            accent,
        };
    }

    private static (double H, double S, double L) RgbToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h, s;

        if (Math.Abs(max - min) < 0.0001)
        {
            h = s = 0;
        }
        else
        {
            var d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            h = max switch
            {
                _ when max == r => (g - b) / d + (g < b ? 6 : 0),
                _ when max == g => (b - r) / d + 2,
                _ => (r - g) / d + 4,
            };
            h /= 6;
        }

        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double r, g, b;

        if (Math.Abs(s) < 0.0001)
        {
            r = g = b = l;
        }
        else
        {
            double HueToRgb(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2.0) return q;
                if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
                return p;
            }

            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        return Color.FromRgb(
            (byte)Math.Clamp((int)(r * 255), 0, 255),
            (byte)Math.Clamp((int)(g * 255), 0, 255),
            (byte)Math.Clamp((int)(b * 255), 0, 255));
    }
}
