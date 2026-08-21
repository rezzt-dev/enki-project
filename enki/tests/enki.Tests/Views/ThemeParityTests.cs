using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace enki.Tests.Views;

/// <summary>Fase 6 (roadmap §11, "Tests de zoom y tema") closing gap 25's other half: Fase 1 audited
/// <c>Colors.axaml</c>'s <c>Dark</c>/<c>Light</c> <c>ThemeDictionaries</c> "brush por brush" and fixed the
/// two colors that were hardcoded outside of either, but left no regression test — nothing stopped a future
/// change from adding a color key to one theme and forgetting the other, silently breaking whichever theme
/// is active when a brush falls back to nothing. This test parses the real <c>Colors.axaml</c> (same
/// "read the actual source tree" approach as <see cref="PopupZoomAndThemeAuditTests"/>) and asserts the
/// <c>Dark</c> and <c>Light</c> dictionaries declare the exact same set of <c>x:Key</c>s.</summary>
public class ThemeParityTests
{
    private static readonly string ColorsFilePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "styles", "Colors.axaml"));

    [Fact]
    public void DarkAndLightThemeDictionaries_DeclareTheSameColorKeys()
    {
        var content = File.ReadAllText(ColorsFilePath);

        var darkKeys = ExtractKeys(content, "Dark");
        var lightKeys = ExtractKeys(content, "Light");

        Assert.NotEmpty(darkKeys);
        var missingFromLight = darkKeys.Except(lightKeys).ToList();
        var missingFromDark = lightKeys.Except(darkKeys).ToList();

        Assert.True(missingFromLight.Count == 0,
            $"Colors.axaml: keys defined in Dark but missing from Light: {string.Join(", ", missingFromLight)}");
        Assert.True(missingFromDark.Count == 0,
            $"Colors.axaml: keys defined in Light but missing from Dark: {string.Join(", ", missingFromDark)}");
    }

    private static string[] ExtractKeys(string content, string themeKey)
    {
        var block = Regex.Match(content, $@"<ResourceDictionary x:Key=""{themeKey}"">(.*?)</ResourceDictionary>", RegexOptions.Singleline);
        Assert.True(block.Success, $"Colors.axaml no longer declares a ThemeDictionaries entry for '{themeKey}'.");

        return Regex.Matches(block.Groups[1].Value, @"x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }
}
