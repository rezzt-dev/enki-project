using enki.Localization;
using Xunit;

namespace enki.Tests.Localization;

/// <summary><see cref="LocalizationService.Instance"/> is a process-wide singleton, so every test here
/// restores the original culture in a <c>finally</c> block to avoid leaking state into other tests that
/// happen to run in the same process.</summary>
[Collection("LocalizationMutatingTests")]
public class LocalizationServiceTests
{
    [Fact]
    public void Indexer_ResolvesKeyForTheActiveCulture()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("fr-FR");
            Assert.Equal("Annuler", LocalizationService.Instance["Common_Cancel"]);

            LocalizationService.Instance.SetLanguage("de-DE");
            Assert.Equal("Abbrechen", LocalizationService.Instance["Common_Cancel"]);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    [Fact]
    public void SetLanguage_UpdatesCurrentCulture()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("es-419");
            Assert.Equal("es-419", LocalizationService.Instance.CurrentCulture.Name);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

#if DEBUG
    [Fact]
    public void Indexer_UnknownKey_ReturnsBracketedKeyInDebug()
    {
        Assert.Equal("[[Does_Not_Exist_Key]]", LocalizationService.Instance["Does_Not_Exist_Key"]);
    }
#endif
}
