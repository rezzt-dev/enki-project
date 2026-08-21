using System;
using System.IO;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

[Collection("LocalizationMutatingTests")]
public class LanguageSetupViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-language-setup-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _originalCulture;

    public LanguageSetupViewModelTests()
    {
        _originalCulture = LocalizationService.Instance.CurrentCulture.Name;
    }

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(_originalCulture);
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void SelectLanguage_UpdatesCulture_PersistsSetting_AndInvokesCallback()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var callbackInvoked = false;
        var vm = new LanguageSetupViewModel(settingsService, () => callbackInvoked = true);

        vm.SelectLanguageCommand.Execute("fr-FR");

        Assert.True(callbackInvoked);
        Assert.Equal("fr-FR", LocalizationService.Instance.CurrentCulture.Name);
        Assert.Equal("fr-FR", settingsService.Settings.Language);
    }

    [Fact]
    public void SelectLanguage_UnknownCulture_IsIgnored()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.Language = "es-ES";
        var callbackInvoked = false;
        var vm = new LanguageSetupViewModel(settingsService, () => callbackInvoked = true);

        vm.SelectLanguageCommand.Execute("not-a-culture");

        Assert.False(callbackInvoked);
        Assert.Equal("es-ES", settingsService.Settings.Language);
    }

    [Fact]
    public void Languages_ExposesAllSupportedLanguagesWithNativeNames()
    {
        var vm = new LanguageSetupViewModel(new SettingsService(_storageDirectory), () => { });

        Assert.Equal(6, vm.Languages.Count);
        Assert.Contains(vm.Languages, l => l.CultureCode == "de-DE" && l.NativeName == "Deutsch");
        Assert.Contains(vm.Languages, l => l.CultureCode == "es-419" && l.NativeName == "Español (Latinoamérica)");
    }
}
