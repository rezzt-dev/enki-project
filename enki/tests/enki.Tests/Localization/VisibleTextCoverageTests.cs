using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace enki.Tests.Localization;

/// <summary>Detecta textos de interfaz sin recurso y referencias inexistentes, además de claves
/// presentes pero copiadas del inglés. Las coincidencias lingüísticas legítimas son explícitas.</summary>
public sealed class VisibleTextCoverageTests
{
    private static readonly string SourceDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki"));

    [Fact]
    public void XamlVisibleText_UsesResourcesExceptSymbolsBrandAndShortcuts()
    {
        var properties = new HashSet<string>
        {
            "Text", "Content", "Header", "Watermark", "Title", "ToolTip.Tip", "AutomationProperties.Name"
        };
        var allowed = new HashSet<string> { "ENKI", "Ctrl+F", "Esc", "Aa" };
        var leaks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceDirectory, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (var attribute in XDocument.Load(file).Descendants().Attributes())
            {
                var value = attribute.Value;
                if (properties.Contains(attribute.Name.LocalName) && !value.StartsWith('{')
                    && Regex.IsMatch(value, @"\p{L}{2}") && !allowed.Contains(value))
                    leaks.Add($"{Path.GetRelativePath(SourceDirectory, file)}: {attribute.Name}={value}");
            }
        }
        Assert.Empty(leaks);
    }

    [Fact]
    public void LiteralResourceReferences_ExistInSource()
    {
        var keys = Load("").Keys.ToHashSet();
        var pattern = new Regex("(?:\\bloc:Tr\\s+|(?:LocalizationService.Instance|_localization|\\bloc)\\[\"|LocalizedTextFormatter.Format\\(\")([A-Za-z][A-Za-z0-9_]+)");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f) is ".cs" or ".axaml"))
        {
            foreach (var line in File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            foreach (Match match in pattern.Matches(line))
            {
                var key = match.Groups[1].Value;
                if (!key.EndsWith('_') && !keys.Contains(key)) missing.Add($"{file}: {key}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void AmericanEnglish_MatchesNeutralSource()
    {
        var source = Load("");
        var american = Load("en-US");
        foreach (var pair in source) Assert.Equal(pair.Value, american[pair.Key]);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void GermanAndFrench_DoNotContainUnreviewedEnglishCopies(string culture)
    {
        // Coincidencias revisadas: términos compartidos, nombres de producto, abreviaturas y formatos.
        var allowed = culture == "de-DE"
            ? new HashSet<string>
            {
                "AGENDA", "Agenda", "Audio", "Backlinks", "Code", "Community", "DASHBOARD", "DETAILS", "Dashboard", "Details", "EDITOR", "Editor", "FR", "Horizontal", "Info", "KANBAN", "Kanban", "Link", "MIN", "MO", "Name", "Navigation", "OK", "PDFs", "POLAR · SUPABASE", "Polar", "SA", "Skills", "Status", "TAGS", "Tags", "Text", "Version {0}", "Video", "[text](url)", "min", "{0}", "{0} MIN", "{0} PX", "{0} min", "{0} {1}", "{0}%", "{0}:00"
            }
            : new HashSet<string>
            {
                "AGENDA", "Agenda", "Application", "Audio", "Code", "DESCRIPTION", "Danger", "Date", "Description", "Documents", "Horizontal", "Image", "Images", "Info", "KANBAN", "Kanban", "MIN", "Modules", "NOTES", "NOTIFICATIONS", "Navigation", "Notes", "Notifications", "OK", "PDFs", "POLAR · SUPABASE", "Polar", "Permissions", "SA", "SESSIONS", "Saturation", "Sections", "Sessions", "Surface", "Type", "Version {0}", "Vertical", "Visible", "Volume", "min", "notes", "{0}", "{0} MIN", "{0} PX", "{0} document", "{0} documents", "{0} min", "{0} note", "{0} notes", "{0} session", "{0} sessions", "{0} h", "{0} {1}", "{0}%", "{0}:00"
            };
        var source = Load("");
        var translation = Load(culture);
        var untranslated = translation.Where(p => p.Value == source[p.Key] && !allowed.Contains(p.Value));
        Assert.Empty(untranslated);
    }

    /// <summary>Catches stale translations that were correct for an older, longer source string and
    /// still carry its English wording after the source was shortened — e.g. a value of
    /// <c>'{0}' was imported and activated.</c> against a source of <c>'{0}' was imported.</c>.
    /// <see cref="GermanAndFrench_DoNotContainUnreviewedEnglishCopies"/> only compares for exact
    /// equality, so it never sees these. A multi-word source phrase appearing verbatim inside a
    /// German or French value is always a leak; single shared tokens are covered by the exact-copy
    /// allow lists and never reach this check.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("es-419")]
    public void Translations_DoNotEmbedTheEnglishSourcePhrase(string culture)
    {
        var source = Load("");
        var translation = Load(culture);
        var leaks = translation.Where(p =>
        {
            var english = source[p.Key].TrimEnd('.', '!', ':', ';', ' ');
            if (p.Value == source[p.Key] || english.Length == 0) return false;
            if (english.Count(char.IsWhiteSpace) < 2) return false;
            return p.Value.Contains(english, StringComparison.Ordinal);
        }).Select(p => $"{p.Key}: {p.Value}");
        Assert.Empty(leaks);
    }

    private static Dictionary<string, string> Load(string culture) => XDocument.Load(Path.Combine(
            SourceDirectory, "localization", "Strings" + (culture.Length == 0 ? "" : "." + culture) + ".resx"))
        .Root!.Elements("data").ToDictionary(e => e.Attribute("name")!.Value, e => e.Element("value")!.Value);
}
