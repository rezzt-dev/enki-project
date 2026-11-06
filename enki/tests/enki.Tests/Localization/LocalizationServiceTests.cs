using enki.Localization;
using System.Globalization;
using System.Collections.Generic;
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

    [Theory]
    [InlineData("not-a-culture")]
    [InlineData("ja-JP")]
    [InlineData("")]
    public void SetLanguage_UnsupportedChoice_UsesSourceCulture(string culture)
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage(culture);
            Assert.Equal("en-US", LocalizationService.Instance.CurrentCulture.Name);
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    [Fact]
    public void SetLanguage_UpdatesAmbientCultures_AndNotifiesAvaloniaIndexer()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        var changes = new List<string?>();
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => changes.Add(e.PropertyName);
        LocalizationService.Instance.PropertyChanged += OnChanged;
        try
        {
            LocalizationService.Instance.SetLanguage("en-GB");
            Assert.Equal("en-GB", CultureInfo.CurrentCulture.Name);
            Assert.Equal("en-GB", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("en-GB", CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("en-GB", CultureInfo.DefaultThreadCurrentUICulture?.Name);
            Assert.Contains("Item", changes);
        }
        finally
        {
            LocalizationService.Instance.PropertyChanged -= OnChanged;
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
