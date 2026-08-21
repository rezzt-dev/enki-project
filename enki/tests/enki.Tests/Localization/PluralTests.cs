using enki.Localization;
using Xunit;

namespace enki.Tests.Localization;

[Collection("LocalizationMutatingTests")]
public class PluralTests
{
    [Theory]
    [InlineData(1, "1 day ago")]
    [InlineData(2, "2 days ago")]
    public void Get_EnUs_PicksOneOrOtherByCount(int count, string expected)
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("en-US");
            Assert.Equal(expected, Plural.Get("Common_RelativeTime_DaysAgo", count, count));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    /// <summary>docs/agent-docs/internacionalizacion-i18n.md §1.4 — French is the one supported language
    /// where 0 counts as singular, unlike the other 5 (singular only at exactly 1).</summary>
    [Theory]
    [InlineData(0, "il y a 0 jour")]
    [InlineData(1, "il y a 1 jour")]
    [InlineData(2, "il y a 2 jours")]
    public void Get_FrFr_TreatsZeroAsSingular(int count, string expected)
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("fr-FR");
            Assert.Equal(expected, Plural.Get("Common_RelativeTime_DaysAgo", count, count));
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }
}
