using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text;
using Xunit;

namespace enki.Tests.Localization;

/// <summary>Verifica que todos los recursos .resx secundarios tengan exactamente el mismo
/// conjunto de claves que el recurso fuente (en-US) y que los placeholders se conserven.
/// Véase docs/agent-docs/internacionalizacion-i18n.md §7.1.</summary>
public class ResourceKeyParityTests
{
    private static readonly string s_resourcesDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "localization"));

    private static readonly string s_sourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki"));

    private static readonly string[] s_cultures =
    {
        "en-US", "en-GB", "de-DE", "es-ES", "es-419", "fr-FR"
    };

    [Fact]
    public void AllCultures_HaveSameKeysAsSource()
    {
        var sourceKeys = LoadKeys("Strings.resx");

        foreach (var culture in s_cultures)
        {
            var keys = LoadKeys($"Strings.{culture}.resx");

            Assert.Equal(sourceKeys.Count, keys.Count);
            Assert.Equal(sourceKeys, keys);
        }
    }

    [Fact]
    public void AllValues_AreNonEmpty()
    {
        foreach (var file in s_cultures.Select(c => $"Strings.{c}.resx").Prepend("Strings.resx"))
        {
            var values = LoadValues(file);
            Assert.All(values, v => Assert.False(string.IsNullOrWhiteSpace(v),
                $"{file} contains an empty value for a key."));
        }
    }

    [Fact]
    public void AllCultures_PreservePlaceholders()
    {
        var sourceValues = LoadKeyValues("Strings.resx");

        foreach (var culture in s_cultures)
        {
            var translated = LoadKeyValues($"Strings.{culture}.resx");

            foreach (var key in sourceValues.Keys)
            {
                var sourcePlaceholders = ExtractPlaceholders(sourceValues[key]);
                var translatedPlaceholders = ExtractPlaceholders(translated[key]);
                Assert.Equal(sourcePlaceholders, translatedPlaceholders);
            }
        }
    }

    /// <summary>Guards against the exact gap that let <c>Plural.Get("Sessions_NoteCount", ...)</c> and
    /// <c>Plural.Get("Notes_WordCount", ...)</c> reference resource keys whose <c>_one</c>/<c>_other</c>
    /// pair didn't exist in any <c>.resx</c> at all — <see cref="AllCultures_HaveSameKeysAsSource"/> can't
    /// catch that because every file agreed on the omission, so parity held while the binding rendered as
    /// literal <c>[[Key_one]]</c>/<c>[[Key_other]]</c> in Debug. Scans every <c>.cs</c> file for
    /// <c>Plural.Get("Key", ...)</c> call sites and asserts both suffixed keys exist in the source resx.</summary>
    [Fact]
    public void PluralGetCalls_HaveCorrespondingOneAndOtherKeys()
    {
        var sourceKeys = LoadKeys("Strings.resx");
        var callSitePattern = new Regex(@"Plural\.Get\(\s*""([^""]+)""");
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(s_sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in callSitePattern.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;
                foreach (var suffix in new[] { "_one", "_other" })
                {
                    if (!sourceKeys.Contains(key + suffix))
                        missing.Add($"{key}{suffix} (referenced from {Path.GetFileName(file)})");
                }
            }
        }

        Assert.True(missing.Count == 0, "Missing Plural.Get resource keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void FormattedResources_HaveValidCompositeFormatSyntax()
    {
        foreach (var file in s_cultures.Select(c => $"Strings.{c}.resx").Prepend("Strings.resx"))
        foreach (var pair in LoadKeyValues(file))
        {
            if (!Regex.IsMatch(pair.Value, @"\{[0-9]+")) continue;
            var error = Record.Exception(() => CompositeFormat.Parse(pair.Value));
            Assert.True(error is null, $"{file}: {pair.Key}: {error?.Message}");
        }
    }

    private static HashSet<string> LoadKeys(string fileName)
    {
        var path = Path.Combine(s_resourcesDirectory, fileName);
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .Select(e => e.Attribute("name")?.Value!)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();
    }

    private static List<string> LoadValues(string fileName)
    {
        var path = Path.Combine(s_resourcesDirectory, fileName);
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .Elements("value")
            .Select(e => e.Value)
            .ToList();
    }

    private static Dictionary<string, string> LoadKeyValues(string fileName)
    {
        var path = Path.Combine(s_resourcesDirectory, fileName);
        var document = XDocument.Load(path);
        return document.Root!
            .Elements("data")
            .ToDictionary(
                e => e.Attribute("name")?.Value!,
                e => e.Element("value")?.Value ?? string.Empty);
    }

    private static HashSet<string> ExtractPlaceholders(string value) =>
        Regex.Matches(value, @"(?<!\{)\{([0-9]+)(?:,[^:}]+)?(?::[^}]+)?\}")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
}
