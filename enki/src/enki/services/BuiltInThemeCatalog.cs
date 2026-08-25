using System;
using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>The themes Enki ships with, compiled into the app so they're always available — no import
/// needed and, unlike a <see cref="CustomTheme"/>, no <c>.css</c> file on disk. Palettes are adapted from
/// the most-installed VS Code color themes (approximate ports, not pixel-perfect extractions) to fit
/// Enki's 19-slot <see cref="ThemeCatalog"/>, grouped into "Oscuros"/"Claros" by <see cref="ThemeAppearance"/>
/// for the picker in Ajustes › Apariencia. <see cref="CustomThemeService"/> checks this catalog before ever
/// touching disk, so these ids resolve instantly and can never be edited or deleted by the user.</summary>
public static class BuiltInThemeCatalog
{
    public static readonly IReadOnlyList<BuiltInThemeDefinition> All =
    [
        new()
        {
            Id = "github-dark-default",
            Name = "GitHub Dark Default",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#0D1117", "#010409", "#010409", "#161B22", "#161B22", "#1C2128", "#0D1117", "#0D1117", "#161B22",
                "#E6EDF3", "#8B949E", "#484F58",
                "#E6EDF3", "#0D1117", "#58A6FF",
                "#0D1117",
                "#F85149", "#3FB950", "#D29922"),
        },
        new()
        {
            Id = "one-dark-pro",
            Name = "One Dark Pro",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#282C34", "#21252B", "#21252B", "#2C313A", "#2C313A", "#333842", "#1D2025", "#21252B", "#2C313A",
                "#ABB2BF", "#828997", "#5C6370",
                "#ABB2BF", "#282C34", "#61AFEF",
                "#282C34",
                "#E06C75", "#98C379", "#E5C07B"),
        },
        new()
        {
            Id = "dracula",
            Name = "Dracula",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#282A36", "#21222C", "#21222C", "#343746", "#343746", "#44475A", "#21222C", "#282A36", "#343746",
                "#F8F8F2", "#C6C6DC", "#6272A4",
                "#F8F8F2", "#282A36", "#BD93F9",
                "#282A36",
                "#FF5555", "#50FA7B", "#F1FA8C"),
        },
        new()
        {
            Id = "ayu-mirage",
            Name = "Ayu Mirage",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#1F2430", "#191E2A", "#1A1F29", "#232834", "#232834", "#2A2F3B", "#191E2A", "#1F2430", "#232834",
                "#CBCCC6", "#9DA5B5", "#5C6773",
                "#CBCCC6", "#1F2430", "#FFA759",
                "#1F2430",
                "#FF3333", "#BAE67E", "#FFCC66"),
        },
        new()
        {
            Id = "monokai-pro-classic",
            Name = "Monokai Pro Classic",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#2D2A2E", "#221F22", "#221F22", "#363337", "#363337", "#403D41", "#221F22", "#2D2A2E", "#363337",
                "#FCFCFA", "#C1C0C0", "#727072",
                "#FCFCFA", "#2D2A2E", "#AB9DF2",
                "#2D2A2E",
                "#FF6188", "#A9DC76", "#FFD866"),
        },
        new()
        {
            Id = "night-owl",
            Name = "Night Owl",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#011627", "#010E1A", "#010E1A", "#0B2942", "#0B2942", "#122D42", "#010E1A", "#011627", "#0B2942",
                "#D6DEEB", "#A0B3C8", "#637777",
                "#D6DEEB", "#011627", "#82AAFF",
                "#011627",
                "#EF5350", "#ADDB67", "#FFEB95"),
        },
        new()
        {
            Id = "one-monokai",
            Name = "One Monokai",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#23272E", "#1B1F24", "#1B1F24", "#2A2E36", "#2A2E36", "#313640", "#1B1F24", "#23272E", "#2A2E36",
                "#ABB2BF", "#8992A6", "#5C6370",
                "#ABB2BF", "#23272E", "#AE81FF",
                "#23272E",
                "#F92672", "#A6E22E", "#FD971F"),
        },
        new()
        {
            Id = "tokyo-night",
            Name = "Tokyo Night",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#1A1B26", "#16161E", "#16161E", "#24283B", "#24283B", "#292E42", "#16161E", "#1A1B26", "#24283B",
                "#C0CAF5", "#9AA5CE", "#565F89",
                "#C0CAF5", "#1A1B26", "#7AA2F7",
                "#1A1B26",
                "#F7768E", "#9ECE6A", "#E0AF68"),
        },
        new()
        {
            Id = "shades-of-purple",
            Name = "Shades of Purple",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#2D2B55", "#1E1C3A", "#1E1C3A", "#34305F", "#34305F", "#3B3768", "#1E1C3A", "#2D2B55", "#34305F",
                "#E3DFFF", "#B4ADE3", "#6D6A9C",
                "#E3DFFF", "#2D2B55", "#FFD700",
                "#2D2B55",
                "#EC3A37", "#3AD900", "#F92AAA"),
        },
        new()
        {
            Id = "noctis-lux",
            Name = "Noctis Lux",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#F9F9F4", "#F1F1EA", "#F1F1EA", "#FFFFFF", "#FFFFFF", "#ECECE4", "#FFFFFF", "#F9F9F4", "#F1F1EA",
                "#202746", "#556074", "#A0A6B8",
                "#1B1D2B", "#FFFFFF", "#3F7CAD",
                "#F9F9F4",
                "#D6564C", "#3CA374", "#C99E4C"),
        },
        new()
        {
            Id = "catppuccin-mocha",
            Name = "Catppuccin Mocha",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#1E1E2E", "#181825", "#181825", "#313244", "#313244", "#45475A", "#181825", "#1E1E2E", "#313244",
                "#CDD6F4", "#A6ADC8", "#6C7086",
                "#CDD6F4", "#1E1E2E", "#CBA6F7",
                "#1E1E2E",
                "#F38BA8", "#A6E3A1", "#F9E2AF"),
        },
        new()
        {
            Id = "panda-theme",
            Name = "Panda Theme",
            Appearance = ThemeAppearance.Dark,
            Colors = Palette(
                "#1B1E28", "#15171F", "#15171F", "#21242E", "#21242E", "#282B36", "#15171F", "#1B1E28", "#21242E",
                "#E6E6E6", "#B3B1AD", "#676B79",
                "#E6E6E6", "#1B1E28", "#FF75B5",
                "#1B1E28",
                "#FF4B82", "#19F9D8", "#FFB86C"),
        },
        new()
        {
            Id = "github-light-default",
            Name = "GitHub Light Default",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FFFFFF", "#F6F8FA", "#F6F8FA", "#FFFFFF", "#F6F8FA", "#EAEEF2", "#FFFFFF", "#FFFFFF", "#F6F8FA",
                "#1F2328", "#59636E", "#8C959F",
                "#1F2328", "#FFFFFF", "#0969DA",
                "#FFFFFF",
                "#D1242F", "#1A7F37", "#9A6700"),
        },
        new()
        {
            Id = "solarized-light",
            Name = "Solarized Light",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FDF6E3", "#EEE8D5", "#EEE8D5", "#FDF6E3", "#EEE8D5", "#E4DEC9", "#FDF6E3", "#FDF6E3", "#EEE8D5",
                "#586E75", "#657B83", "#93A1A1",
                "#073642", "#FDF6E3", "#268BD2",
                "#FDF6E3",
                "#DC322F", "#859900", "#B58900"),
        },
        new()
        {
            Id = "one-light",
            Name = "One Light",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FAFAFA", "#F0F0F1", "#F0F0F1", "#FFFFFF", "#F0F0F1", "#E5E5E6", "#FFFFFF", "#FAFAFA", "#F0F0F1",
                "#383A42", "#696C77", "#A0A1A7",
                "#202227", "#FAFAFA", "#4078F2",
                "#FAFAFA",
                "#E45649", "#50A14F", "#C18401"),
        },
        new()
        {
            Id = "ayu-light",
            Name = "Ayu Light",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FAFAFA", "#F3F4F5", "#F3F4F5", "#FFFFFF", "#F3F4F5", "#E7E8E9", "#FFFFFF", "#FAFAFA", "#F3F4F5",
                "#5C6166", "#828C99", "#ABB0B6",
                "#272B33", "#FAFAFA", "#FA8D3E",
                "#FAFAFA",
                "#F51818", "#86B300", "#F2AE49"),
        },
        new()
        {
            Id = "material-theme-lighter",
            Name = "Material Theme Lighter",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FAFAFA", "#EEEEEE", "#EEEEEE", "#FFFFFF", "#EEEEEE", "#E0E0E1", "#FFFFFF", "#FAFAFA", "#EEEEEE",
                "#546E7A", "#78909C", "#B0BEC5",
                "#263238", "#FAFAFA", "#6182B8",
                "#FAFAFA",
                "#E53935", "#91B859", "#F6A434"),
        },
        new()
        {
            Id = "gruvbox-light",
            Name = "Gruvbox Light",
            Appearance = ThemeAppearance.Light,
            Colors = Palette(
                "#FBF1C7", "#F2E5BC", "#EBDBB2", "#FBF1C7", "#EBDBB2", "#D5C4A1", "#F2E5BC", "#FBF1C7", "#EBDBB2",
                "#3C3836", "#504945", "#928374",
                "#282828", "#FBF1C7", "#076678",
                "#FBF1C7",
                "#9D0006", "#79740E", "#B57614"),
        },
    ];

    public static bool TryGet(string id, out BuiltInThemeDefinition theme)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                theme = candidate;
                return true;
            }
        }

        theme = null!;
        return false;
    }

    /// <summary>Zips 19 hex values against <see cref="ThemeCatalog.All"/> in declaration order — the same
    /// order the constants above are laid out in (backgrounds, text, ink/accent, scroll, status) — into a
    /// <c>ResourceKey → hex</c> map. Keeps each theme definition above readable as a flat list of colors
    /// instead of 19 repeated key names.</summary>
    private static Dictionary<string, string> Palette(params string[] hexValues)
    {
        var slots = ThemeCatalog.All;
        if (hexValues.Length != slots.Count)
            throw new ArgumentException($"Expected {slots.Count} colors (one per ThemeCatalog slot), got {hexValues.Length}.");

        var colors = new Dictionary<string, string>(slots.Count, StringComparer.Ordinal);
        for (var i = 0; i < slots.Count; i++) colors[slots[i].ResourceKey] = hexValues[i];
        return colors;
    }
}
