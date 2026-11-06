using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>ALT-03: los temas de alto contraste cumplen WCAG AA (4,5:1) en cada pareja de texto sobre
/// fondo, y el contraste alto del sistema activa uno cuando el usuario no ha elegido tema.</summary>
public class HighContrastThemeTests : IDisposable
{
    private const double MinimumRatio = 4.5;

    private static readonly string[] s_backgrounds =
    [
        "AppBackgroundColor", "AppBackgroundAltColor", "SidebarBackgroundColor", "SurfaceColor",
        "PanelBackgroundColor", "PanelBackgroundHoverColor", "InputBackgroundColor", "RowBackgroundColor",
        "RowBackgroundHoverColor"
    ];

    private static readonly string[] s_foregrounds =
    [
        "TextPrimaryColor", "TextSecondaryColor", "TextMutedColor", "InkColor", "AccentColor",
        "DangerColor", "SuccessColor", "HighlightColor"
    ];

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-highcontrast-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    public static IEnumerable<object[]> HighContrastIds() =>
    [
        [BuiltInThemeCatalog.HighContrastDarkId],
        [BuiltInThemeCatalog.HighContrastLightId]
    ];

    [Theory]
    [MemberData(nameof(HighContrastIds))]
    public void EveryTextColor_ReachesAaOnEveryBackground(string themeId)
    {
        Assert.True(BuiltInThemeCatalog.TryGet(themeId, out var theme));

        foreach (var background in s_backgrounds)
        foreach (var foreground in s_foregrounds)
        {
            var ratio = ContrastRatio(theme.Colors[foreground], theme.Colors[background]);
            Assert.True(ratio >= MinimumRatio,
                $"{themeId}: {foreground} {theme.Colors[foreground]} sobre {background} {theme.Colors[background]} = {ratio:0.00}:1");
        }
    }

    [Theory]
    [MemberData(nameof(HighContrastIds))]
    public void TextOnInkAndAccentFills_ReachesAa(string themeId)
    {
        Assert.True(BuiltInThemeCatalog.TryGet(themeId, out var theme));

        Assert.True(ContrastRatio(theme.Colors["InkOnColor"], theme.Colors["InkColor"]) >= MinimumRatio);
        Assert.True(ContrastRatio(theme.Colors["InkOnColor"], theme.Colors["AccentColor"]) >= MinimumRatio);
    }

    [Fact]
    public void HighContrastThemes_AreOfferedInTheirOwnAppearanceGroup()
    {
        Assert.True(BuiltInThemeCatalog.TryGet(BuiltInThemeCatalog.HighContrastDarkId, out var dark));
        Assert.True(BuiltInThemeCatalog.TryGet(BuiltInThemeCatalog.HighContrastLightId, out var light));

        Assert.Equal(enki.Models.ThemeAppearance.Dark, dark.Appearance);
        Assert.Equal(enki.Models.ThemeAppearance.Light, light.Appearance);
    }

    [Theory]
    [InlineData("Dark", BuiltInThemeCatalog.HighContrastDarkId)]
    [InlineData("Light", BuiltInThemeCatalog.HighContrastLightId)]
    public void SystemHighContrast_WithoutAChosenTheme_ActivatesTheMatchingOne(string variant, string expectedId)
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.Theme = variant;
        var service = new CustomThemeService(settingsService);

        service.ApplyActiveOnStartup(() => true);

        Assert.Equal(expectedId, service.ActiveThemeId);
    }

    [Fact]
    public void SystemHighContrast_DoesNotOverrideAThemeTheUserChose()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var service = new CustomThemeService(settingsService);
        Assert.True(service.Activate("dracula"));

        service.ApplyActiveOnStartup(() => true);

        Assert.Equal("dracula", service.ActiveThemeId);
    }

    [Fact]
    public void WithoutSystemHighContrast_NothingIsActivated()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var service = new CustomThemeService(settingsService);

        service.ApplyActiveOnStartup(() => false);

        Assert.Null(service.ActiveThemeId);
    }

    /// <summary>Relación de contraste WCAG 2.x entre dos colores <c>#RRGGBB</c>.</summary>
    private static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        var a = Luminance(foregroundHex);
        var b = Luminance(backgroundHex);
        var (light, dark) = a >= b ? (a, b) : (b, a);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var value = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
