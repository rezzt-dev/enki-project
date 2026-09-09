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

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void FirstRun_SeedsKanbanOnlyAfterLanguageSelection(string culture)
    {
        LocalizationService.Instance.SetLanguage("en-US");
        var settings = new SettingsService(_storageDirectory);
        var kanban = new KanbanService(_storageDirectory, deferSeedCreation: true);
        Assert.Empty(kanban.Board.Columns);
        var vm = new LanguageSetupViewModel(settings, kanban.EnsureSeedBoard);
        vm.SelectLanguageCommand.Execute(culture);
        Assert.Equal(LocalizationService.Instance["Kanban_Seed_ColumnTodo"], kanban.Board.Columns[0].Title);
        var title = kanban.Board.Columns[0].Title;
        LocalizationService.Instance.SetLanguage("es-ES");
        var restored = new KanbanService(_storageDirectory, deferSeedCreation: true);
        restored.EnsureSeedBoard();
        Assert.Equal(title, restored.Board.Columns[0].Title);
        Assert.Equal(culture, new SettingsService(_storageDirectory).Settings.Language);
    }

    [Fact]
    public void SelectLanguage_SaveFailure_KeepsSetupOpenAndAllowsRetry()
    {
        var settings = new SettingsService(_storageDirectory);
        Directory.CreateDirectory(settings.SettingsFilePath);
        var completed = false;
        var vm = new LanguageSetupViewModel(settings, () => completed = true);
        var original = LocalizationService.Instance.CurrentCulture.Name;
        vm.SelectLanguageCommand.Execute("fr-FR");
        Assert.False(completed);
        Assert.NotEmpty(vm.ErrorMessage!);
        Assert.Null(settings.Settings.Language);
        Assert.Equal(original, LocalizationService.Instance.CurrentCulture.Name);
        Directory.Delete(settings.SettingsFilePath);
        vm.SelectLanguageCommand.Execute("fr-FR");
        Assert.True(completed);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>En el primer arranque la cultura activa sigue siendo la de origen: sin resolver el aviso en
    /// la cultura recién pulsada, quien elige alemán y se topa con un settings.json no escribible recibe la
    /// explicación en inglés. Se comparan dos idiomas en vez de fijar el texto esperado para no atar el test
    /// a la redacción del recurso.</summary>
    [Fact]
    public void SelectLanguage_SaveFailure_ReportsReasonInTheLanguageJustPicked()
    {
        var settings = new SettingsService(_storageDirectory);
        Directory.CreateDirectory(settings.SettingsFilePath);
        var vm = new LanguageSetupViewModel(settings, () => { });
        var active = LocalizationService.Instance.CurrentCulture.Name;

        vm.SelectLanguageCommand.Execute("de-DE");
        var german = vm.ErrorMessage;
        vm.SelectLanguageCommand.Execute("fr-FR");
        var french = vm.ErrorMessage;

        Assert.NotEmpty(german!);
        Assert.NotEmpty(french!);
        Assert.NotEqual(german, french);
        Assert.Equal(active, LocalizationService.Instance.CurrentCulture.Name);
        Directory.Delete(settings.SettingsFilePath);
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
