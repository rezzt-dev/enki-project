using System.Collections.Generic;

namespace enki.Models;

/// <summary>One theme that ships compiled into Enki — see <see cref="Services.BuiltInThemeCatalog"/> for
/// the full list. Unlike a user's <see cref="CustomThemeInfo"/>, a built-in theme has no <c>.css</c> file
/// on disk and is never listed in <c>AppSettings.CustomThemes</c>: its colors live in code, so it is
/// always available after a fresh install and can't be edited or deleted.</summary>
public sealed class BuiltInThemeDefinition
{
    public required string Id { get; init; }

    /// <summary>Display name shown in the theme picker.</summary>
    public required string Name { get; init; }

    /// <summary>Groups this theme under "Oscuros"/"Claros" in the picker.</summary>
    public required ThemeAppearance Appearance { get; init; }

    /// <summary>Map of <see cref="Services.ThemeCatalog"/> <c>ResourceKey</c> → <c>#RRGGBB</c> value, one
    /// entry per catalog slot.</summary>
    public required IReadOnlyDictionary<string, string> Colors { get; init; }
}
