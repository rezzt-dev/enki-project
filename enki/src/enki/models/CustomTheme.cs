using System.Collections.Generic;

namespace enki.Models;

/// <summary>Complete in-memory representation of a user-created theme. <see cref="CustomThemeInfo"/>
/// is the small persisted record kept in <see cref="AppSettings.CustomThemes"/>; the actual color
/// dictionary lives in a <c>.css</c> file managed by <see cref="Services.CustomThemeService"/>. This
/// class is used by the theme editor and the service to load, edit and preview colors before writing
/// them back to disk.</summary>
public sealed class CustomTheme
{
    public string Id { get; set; } = "";

    /// <summary>Display name shown in the theme list and editor.</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether this theme ships with Enki and cannot be deleted/renamed by the user.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Map of <see cref="Services.ThemeCatalog"/> <c>ResourceKey</c> → <c>#RRGGBB</c> value.</summary>
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>Returns a shallow copy of this theme with a new <see cref="Id"/>, keeping the same
    /// name and colors. Used by "Duplicate".</summary>
    public CustomTheme Clone()
    {
        return new CustomTheme
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Name = Name,
            IsBuiltIn = false,
            Colors = new Dictionary<string, string>(Colors, System.StringComparer.Ordinal)
        };
    }
}
