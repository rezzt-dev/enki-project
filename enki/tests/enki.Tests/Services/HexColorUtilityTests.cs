using Avalonia.Media;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class HexColorUtilityTests
{
    [Theory]
    [InlineData("#FFFFFF", "#FFFFFF")]
    [InlineData("#ffffff", "#FFFFFF")]
    [InlineData("ffffff", "#FFFFFF")]
    [InlineData("#FFF", "#FFFFFF")]
    [InlineData("#f00", "#FF0000")]
    [InlineData("#FF0000", "#FF0000")]
    [InlineData("  #123456  ", "#123456")]
    public void NormalizeHex_ValidInput_ReturnsSixDigitUpperHexWithHash(string input, string expected)
    {
        Assert.Equal(expected, HexColorUtility.NormalizeHex(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#GGG")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#FF000080")]
    [InlineData("#80FF0000")]
    [InlineData("not-a-color")]
    [InlineData("rgb(255,0,0)")]
    public void NormalizeHex_InvalidInput_ReturnsNull(string? input)
    {
        Assert.Null(HexColorUtility.NormalizeHex(input));
    }

    [Fact]
    public void ColorToHex_ReturnsSixDigitUpperHex()
    {
        Assert.Equal("#123456", HexColorUtility.ColorToHex(Color.FromRgb(0x12, 0x34, 0x56)));
    }

    [Fact]
    public void ColorToHex_DropsAlpha()
    {
        Assert.Equal("#123456", HexColorUtility.ColorToHex(Color.FromArgb(0x80, 0x12, 0x34, 0x56)));
    }
}
