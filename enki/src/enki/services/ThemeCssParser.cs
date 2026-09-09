using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using enki.Localization;

namespace enki.Services;

public enum ThemeParseErrorKind
{
    /// <summary>The file has no <c>:root { ... }</c> block at all.</summary>
    MissingRootBlock,

    /// <summary>One or more <see cref="ThemeCatalog.All"/> variables are absent from the file.</summary>
    MissingVariables,

    /// <summary>The file declares custom properties that aren't part of <see cref="ThemeCatalog.All"/> —
    /// the enforcement mechanism behind "only colors are editable": the parser only ever recognizes this
    /// fixed whitelist of names, so a file that adds/renames a variable is rejected outright instead of the
    /// extra variable being silently ignored.</summary>
    UnexpectedVariables,

    /// <summary>A recognized variable's value isn't a bare <c>#RRGGBB</c> hex color.</summary>
    InvalidColorValue,
}

/// <summary>One validation problem found in an imported theme file, with enough detail for
/// <see cref="Services.CustomThemeService"/> to build a localized, user-facing message
/// (<see cref="Detail"/> is plain data — variable names, raw values — never pre-formatted text).</summary>
public sealed record ThemeParseError(ThemeParseErrorKind Kind, string Detail);

/// <summary>Result of <see cref="ThemeCssParser.Parse"/>: either every <see cref="ThemeCatalog.All"/> color
/// resolved successfully, or a non-empty list of problems and no colors at all — imports are all-or-nothing,
/// never partially applied.</summary>
public sealed record ThemeParseResult(bool Success, IReadOnlyDictionary<string, Color> Colors, IReadOnlyList<ThemeParseError> Errors)
{
    public static ThemeParseResult Failed(IReadOnlyList<ThemeParseError> errors) =>
        new(false, new Dictionary<string, Color>(), errors);
}

/// <summary>Parses/builds the custom-theme CSS format (docs: none yet, see <c>ThemeCatalog</c> and
/// <c>CustomThemeService</c> — this is a new, undocumented-in-proposal feature). Stateless — no
/// file I/O, no Avalonia application state — so it's unit-testable without a running app
/// (<c>enki.Tests/Services/ThemeCssParserTests.cs</c>). The format is genuinely valid CSS: a <c>:root</c>
/// block of custom properties, exactly the ones in <see cref="ThemeCatalog.All"/> and no others — this
/// closed vocabulary is what makes "only the colors are modifiable" true by construction rather than by
/// convention: any file that adds, removes or renames a variable fails validation instead of being
/// half-applied.</summary>
public static class ThemeCssParser
{
    private static readonly Regex CommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RootBlockRegex = new(@":root\s*\{([^}]*)\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex VariableRegex = new(@"--([a-zA-Z0-9-]+)\s*:\s*([^;]+);", RegexOptions.Compiled);

    public static ThemeParseResult Parse(string cssText)
    {
        var stripped = CommentRegex.Replace(cssText ?? "", "");

        var rootMatch = RootBlockRegex.Match(stripped);
        if (!rootMatch.Success)
            return ThemeParseResult.Failed([new ThemeParseError(ThemeParseErrorKind.MissingRootBlock, "")]);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in VariableRegex.Matches(rootMatch.Groups[1].Value))
        {
            // A later declaration of the same variable overrides an earlier one, same as real CSS cascade
            // within one block — not treated as an error.
            found[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }

        var expectedNames = ThemeCatalog.All.Select(d => d.CssVariable).ToHashSet(StringComparer.Ordinal);
        var foundNames = found.Keys.ToHashSet(StringComparer.Ordinal);

        var errors = new List<ThemeParseError>();

        var missing = expectedNames.Except(foundNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
            errors.Add(new ThemeParseError(ThemeParseErrorKind.MissingVariables, JoinVariableNames(missing)));

        var unexpected = foundNames.Except(expectedNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (unexpected.Count > 0)
            errors.Add(new ThemeParseError(ThemeParseErrorKind.UnexpectedVariables, JoinVariableNames(unexpected)));

        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        var invalidEntries = new List<string>();
        foreach (var definition in ThemeCatalog.All)
        {
            if (!found.TryGetValue(definition.CssVariable, out var rawValue)) continue; // already reported as missing

            var normalized = HexColorUtility.NormalizeHex(rawValue);
            if (normalized is null)
            {
                invalidEntries.Add($"--{definition.CssVariable}: {rawValue}");
                continue;
            }

            colors[definition.ResourceKey] = Color.Parse(normalized);
        }
        if (invalidEntries.Count > 0)
            errors.Add(new ThemeParseError(ThemeParseErrorKind.InvalidColorValue, string.Join(", ", invalidEntries)));

        return errors.Count > 0 ? ThemeParseResult.Failed(errors) : new ThemeParseResult(true, colors, []);
    }

    private static string JoinVariableNames(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"--{n}"));

    /// <summary>Genera la plantilla CSS con instrucciones en el idioma activo. Los nombres de
    /// variables y los valores serializados permanecen invariantes.</summary>
    public static string BuildTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("/*");
        sb.AppendLine(LocalizationService.Instance["Settings_Appearance_CustomTheme_TemplateInstructions"]);
        sb.AppendLine("*/");
        sb.AppendLine();
        sb.AppendLine(":root {");
        var groups = ThemeCatalog.All.GroupBy(d => d.CategoryKey).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            AppendGroup(sb, LocalizationService.Instance["Settings_Appearance_Theme_Category_" + groups[i].Key], groups[i].Select(d => d.CssVariable).ToArray());
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string label, string[] cssVariables)
    {
        sb.Append("  /* ").Append(label).AppendLine(" */");
        foreach (var variable in cssVariables)
        {
            var definition = ThemeCatalog.All.First(d => d.CssVariable == variable);
            sb.Append("  --").Append(definition.CssVariable).Append(": ").Append(definition.DefaultHex).AppendLine(";");
        }
    }
}
