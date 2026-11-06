using enki.Localization;

namespace enki.Models;

/// <summary>Un comando invocable desde el slash menu del editor.</summary>
public sealed class SlashCommand
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }

    /// <summary>Literal insert text for structural Markdown syntax that carries no natural-language
    /// content (e.g. "# ", "- [ ] ") — never needs translation. Ignored when <see cref="InsertTextKey"/>
    /// is set.</summary>
    public string InsertTextLiteral { get; init; } = string.Empty;

    /// <summary>Resource key for insert text that DOES carry user-visible language content (e.g. table
    /// header placeholders) — resolved via <see cref="LocalizationService"/> at the moment of insertion
    /// rather than cached at startup, so a language change mid-session affects the next insertion (see
    /// docs/agent-docs/internacionalizacion-i18n.md §5.5).</summary>
    public string? InsertTextKey { get; init; }

    /// <summary>The Markdown inserted into the user's note when this command is picked.</summary>
    public string InsertText => InsertTextKey is null ? InsertTextLiteral : LocalizationService.Instance[InsertTextKey];

    public required int CursorOffset { get; init; }
}
