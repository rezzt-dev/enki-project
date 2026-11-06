using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>Single source of truth for which colors a custom theme's CSS file may set, and what value each
/// one has in the default template (<see cref="ThemeCssParser.BuildTemplate"/>) — the same
/// <c>DefaultHex</c> values as the <c>Dark</c> <c>ThemeDictionaries</c> entry in <c>styles/Colors.axaml</c>,
/// since Enki is dark-first. Mirrors the shape of <c>ShortcutCatalog.All</c>: a flat, ordered list a parser
/// and a template builder both read from, so the two can never drift out of sync with each other.</summary>
public static class ThemeCatalog
{
    public static readonly IReadOnlyList<ThemeColorDefinition> All =
    [
        // ===== Fondos =====
        new("app-background", "AppBackgroundColor", "#121212", "Settings_Appearance_Theme_AppBackground", "Backgrounds"),
        new("app-background-alt", "AppBackgroundAltColor", "#0D0D0D", "Settings_Appearance_Theme_AppBackgroundAlt", "Backgrounds"),
        new("sidebar-background", "SidebarBackgroundColor", "#131313", "Settings_Appearance_Theme_SidebarBackground", "Backgrounds"),
        new("surface", "SurfaceColor", "#1A1A1A", "Settings_Appearance_Theme_Surface", "Backgrounds"),
        new("panel-background", "PanelBackgroundColor", "#1B1B1B", "Settings_Appearance_Theme_PanelBackground", "Backgrounds"),
        new("panel-background-hover", "PanelBackgroundHoverColor", "#1F1F1F", "Settings_Appearance_Theme_PanelBackgroundHover", "Backgrounds"),
        new("input-background", "InputBackgroundColor", "#141414", "Settings_Appearance_Theme_InputBackground", "Backgrounds"),
        new("row-background", "RowBackgroundColor", "#131313", "Settings_Appearance_Theme_RowBackground", "Backgrounds"),
        new("row-background-hover", "RowBackgroundHoverColor", "#181818", "Settings_Appearance_Theme_RowBackgroundHover", "Backgrounds"),

        // ===== Texto =====
        new("text-primary", "TextPrimaryColor", "#F5F5F5", "Settings_Appearance_Theme_TextPrimary", "Text"),
        new("text-secondary", "TextSecondaryColor", "#C6C6C6", "Settings_Appearance_Theme_TextSecondary", "Text"),
        new("text-muted", "TextMutedColor", "#474747", "Settings_Appearance_Theme_TextMuted", "Text"),

        // ===== Tinta y acento =====
        new("ink", "InkColor", "#FFFFFF", "Settings_Appearance_Theme_Ink", "Ink"),
        new("ink-on", "InkOnColor", "#000000", "Settings_Appearance_Theme_InkOn", "Ink"),
        new("accent", "AccentColor", "#FFFFFF", "Settings_Appearance_Theme_Accent", "Ink"),

        // ===== Scroll =====
        new("scroll-track", "ScrollTrackColor", "#121212", "Settings_Appearance_Theme_ScrollTrack", "Scroll"),

        // ===== Estado =====
        new("danger", "DangerColor", "#F87171", "Settings_Appearance_Theme_Danger", "Status"),
        new("success", "SuccessColor", "#4ADE80", "Settings_Appearance_Theme_Success", "Status"),
        new("highlight", "HighlightColor", "#F5C542", "Settings_Appearance_Theme_Highlight", "Status"),
    ];
}
