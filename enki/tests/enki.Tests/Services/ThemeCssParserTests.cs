using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Sistema de importación de temas personalizados (Ajustes › Apariencia) — cubre
/// <see cref="ThemeCssParser"/>, la pieza que hace que "solo los colores son modificables" sea cierto por
/// construcción: solo reconoce el vocabulario cerrado de <see cref="ThemeCatalog.All"/>, así que cualquier
/// variable añadida/renombrada/eliminada se rechaza en vez de aplicarse a medias.</summary>
public class ThemeCssParserTests
{
    [Fact]
    public void BuildTemplate_ParsesSuccessfully_WithEveryCatalogColorAtItsDefault()
    {
        var template = ThemeCssParser.BuildTemplate();

        var result = ThemeCssParser.Parse(template);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(ThemeCatalog.All.Count, result.Colors.Count);
        foreach (var definition in ThemeCatalog.All)
        {
            Assert.True(result.Colors.TryGetValue(definition.ResourceKey, out var color));
            Assert.Equal(Avalonia.Media.Color.Parse(definition.DefaultHex), color);
        }
    }

    [Fact]
    public void Parse_MissingRootBlock_ReturnsNoRootBlockError()
    {
        var result = ThemeCssParser.Parse("--app-background: #000000;");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Kind == ThemeParseErrorKind.MissingRootBlock);
    }

    [Fact]
    public void Parse_MissingVariable_ReturnsMissingVariablesError_NamingIt()
    {
        var withoutInk = ThemeCatalog.All.Where(d => d.CssVariable != "ink");
        var css = ":root {\n" + string.Join("\n", withoutInk.Select(d => $"  --{d.CssVariable}: {d.DefaultHex};")) + "\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, e => e.Kind == ThemeParseErrorKind.MissingVariables);
        Assert.Contains("--ink", error.Detail);
    }

    [Fact]
    public void Parse_UnexpectedVariable_ReturnsUnexpectedVariablesError_EvenIfEveryRealOneIsPresent()
    {
        var css = ":root {\n"
            + string.Join("\n", ThemeCatalog.All.Select(d => $"  --{d.CssVariable}: {d.DefaultHex};"))
            + "\n  --brand-accent: #FF00FF;\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, e => e.Kind == ThemeParseErrorKind.UnexpectedVariables);
        Assert.Contains("--brand-accent", error.Detail);
        // Only-colors-are-editable is enforced by construction: an otherwise-valid file with one extra
        // variable is rejected wholesale, not "accepted minus the unknown key".
        Assert.Empty(result.Colors);
    }

    [Fact]
    public void Parse_InvalidHexValue_ReturnsInvalidColorValueError()
    {
        var withBadInk = ThemeCatalog.All.Select(d => d.CssVariable == "ink" ? "  --ink: not-a-color;" : $"  --{d.CssVariable}: {d.DefaultHex};");
        var css = ":root {\n" + string.Join("\n", withBadInk) + "\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors, e => e.Kind == ThemeParseErrorKind.InvalidColorValue);
        Assert.Contains("--ink", error.Detail);
        Assert.Contains("not-a-color", error.Detail);
    }

    [Theory]
    [InlineData("#FFF")]
    [InlineData("#123")]
    [InlineData("ffffff")]
    public void Parse_AcceptsCommonHexFormats(string hex)
    {
        var lines = ThemeCatalog.All.Select(d =>
            d.CssVariable == "ink"
                ? $"  --{d.CssVariable}: {hex};"
                : $"  --{d.CssVariable}: {d.DefaultHex};");
        var css = ":root {\n" + string.Join("\n", lines) + "\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_IgnoresComments_AndAcceptsAValidFile()
    {
        var css = "/* header comment\n   spanning lines */\n:root {\n"
            + string.Join("\n", ThemeCatalog.All.Select(d => $"  /* {d.CssVariable} */ --{d.CssVariable}: {d.DefaultHex};"))
            + "\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.True(result.Success);
        Assert.Equal(ThemeCatalog.All.Count, result.Colors.Count);
    }

    [Fact]
    public void Parse_ReportsEveryProblemAtOnce_NotJustTheFirst()
    {
        // Missing "ink", plus an unexpected extra variable — both should show up together so the user
        // doesn't have to re-import repeatedly to discover each problem one at a time.
        var withoutInk = ThemeCatalog.All.Where(d => d.CssVariable != "ink");
        var css = ":root {\n"
            + string.Join("\n", withoutInk.Select(d => $"  --{d.CssVariable}: {d.DefaultHex};"))
            + "\n  --brand-accent: #FF00FF;\n}";

        var result = ThemeCssParser.Parse(css);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Kind == ThemeParseErrorKind.MissingVariables);
        Assert.Contains(result.Errors, e => e.Kind == ThemeParseErrorKind.UnexpectedVariables);
    }
}
