namespace enki.Models;

/// <summary>One imported custom theme, persisted in <see cref="AppSettings.CustomThemes"/>. The actual
/// colors live in the <c>.css</c> file at <c>&lt;themes-directory&gt;/{Id}.css</c> (see
/// <see cref="Services.CustomThemeService"/>) — this record only tracks enough to list and re-load it.
/// A plain settable class, not a record, matching the rest of <see cref="AppSettings"/>'s persisted shape
/// so <c>System.Text.Json</c> round-trips it without a matching constructor.</summary>
public sealed class CustomThemeInfo
{
    public string Id { get; set; } = "";

    /// <summary>Display name — the original file's name without its <c>.css</c> extension.</summary>
    public string Name { get; set; } = "";
}
