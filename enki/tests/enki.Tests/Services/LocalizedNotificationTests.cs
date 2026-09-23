using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

[Collection("LocalizationMutatingTests")]
public sealed class LocalizedNotificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-i18n-" + Guid.NewGuid().ToString("N"));
    private readonly string _originalCulture = LocalizationService.Instance.CurrentCulture.Name;

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(_originalCulture);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("es-419")]
    public void PersistedNotification_ChangesLanguage_WithoutTranslatingUserTitle(string culture)
    {
        LocalizationService.Instance.SetLanguage("es-ES");
        var settings = new SettingsService(_directory);
        var service = new NotificationService(settings, _directory);
        const string userTitle = "Mi nota — Save — Grüße";
        service.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteRenameCollision_Title"),
            LocalizedTextFormatter.Format("Notification_NoteRenameCollision_Message", userTitle));

        LocalizationService.Instance.SetLanguage(culture);
        var restored = new NotificationService(settings, _directory);
        var item = new NotificationItemViewModel(Assert.Single(restored.Notifications));
        Assert.Equal(LocalizationService.Instance["Notification_NoteRenameCollision_Title"], item.Title);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance["Notification_NoteRenameCollision_Message"], userTitle), item.Message);
        Assert.Contains(userTitle, item.Message);
    }

    [Fact]
    public void NestedPlural_ReevaluatesFrenchZeroRule_AfterSerialization()
    {
        LocalizationService.Instance.SetLanguage("en-US");
        var text = LocalizedTextFormatter.Join(" / ",
            LocalizedTextFormatter.PluralFormat("Notes_CountLabel", 0, 0),
            LocalizedTextFormatter.Format("Common_Cancel"));
        var restored = JsonSerializer.Deserialize<LocalizedText>(JsonSerializer.Serialize(text));
        Assert.Equal("0 notes / Cancel", LocalizedTextFormatter.Resolve(restored));
        LocalizationService.Instance.SetLanguage("fr-FR");
        Assert.Equal("0 note / Annuler", LocalizedTextFormatter.Resolve(restored));
    }

    [Fact]
    public void LegacyNotification_PreservesOriginalText()
    {
        var item = new NotificationItemViewModel(new AppNotification
        {
            Id = "legacy", Title = "Título histórico", Message = "Texto sin metadatos"
        });
        LocalizationService.Instance.SetLanguage("de-DE");
        Assert.Equal("Título histórico", item.Title);
        Assert.Equal("Texto sin metadatos", item.Message);
    }

    [Fact]
    public void TemplateWithoutArguments_PreservesLiteralCssBraces()
    {
        LocalizationService.Instance.SetLanguage("fr-FR");
        Assert.Equal(LocalizationService.Instance["Settings_Appearance_CustomTheme_Error_NoRootBlock"],
            LocalizedTextFormatter.Resolve(LocalizedTextFormatter.Format("Settings_Appearance_CustomTheme_Error_NoRootBlock")));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ExportedTemplate_IsLocalized_AndRemainsValidCss(string culture)
    {
        LocalizationService.Instance.SetLanguage(culture);
        var template = ThemeCssParser.BuildTemplate();
        Assert.Contains(LocalizationService.Instance["Settings_Appearance_CustomTheme_TemplateInstructions"], template);
        Assert.True(ThemeCssParser.Parse(template).Success);
    }
}
