using System.Linq;
using enki.Localization;
using Xunit;

namespace enki.Tests.Localization;

public class SupportedLanguagesTests
{
    [Fact]
    public void All_HasExactlyTheSixRequiredLanguagesInOrder()
    {
        var codes = SupportedLanguages.All.Select(l => l.CultureCode).ToList();
        Assert.Equal(["en-US", "en-GB", "de-DE", "es-ES", "es-419", "fr-FR"], codes);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var found = SupportedLanguages.Find("FR-fr");
        Assert.NotNull(found);
        Assert.Equal("Français", found!.NativeName);
    }

    [Fact]
    public void Find_UnknownCode_ReturnsNull()
    {
        Assert.Null(SupportedLanguages.Find("xx-XX"));
    }
}
