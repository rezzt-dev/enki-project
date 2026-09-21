using System;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Controls;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsSearchUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _language = LocalizationService.Instance.CurrentCulture.Name;

    public SettingsSearchUiTests()
    {
        HeadlessApp.EnsureInitialized();
        LocalizationService.Instance.SetLanguage("es-ES");
    }

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(_language);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private (SettingsView View, SettingsViewModel Vm, Window Window) CreateView()
    {
        var settings = new SettingsService(_directory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var vaultSettings = new VaultSettingsService(vault);
        var auth = new PolarAuthService(settings);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), new PolarTaskService(_directory),
            new ReminderService(_directory), settings);
        var vm = new SettingsViewModel(settings, new CustomThemeService(settings), new BrandLogoService(_directory), vaultSettings, vault,
            new VaultBackupService(vault, _directory), auth, sync, new NotificationService(settings, _directory),
            (_, _) => Task.FromResult(false), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { },
            _ => { }, _ => { }, _ => { }, _ => { }, () => { });
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 900 };
        window.Show();
        return (view, vm, window);
    }

    private static bool Shown(Control control) => control.IsVisible &&
        control.GetLogicalAncestors().OfType<Control>().All(c => c.IsVisible);

    [Fact]
    public void Search_FindsDynamicShortcuts_AndFiltersTheirSiblings()
    {
        var (view, vm, window) = CreateView();
        try
        {
            var target = vm.ShortcutCategories.SelectMany(c => c.Items).First();
            vm.SearchText = target.Label;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var rows = view.GetVisualDescendants().OfType<SettingsSearchOption>()
                .Where(o => o.DataContext is ShortcutRowViewModel && Shown(o)).ToArray();
            Assert.NotEmpty(rows);
            Assert.Contains(rows, row => ReferenceEquals(row.DataContext, target));
            Assert.All(rows, row => Assert.True(SettingsSearchMatcher.Matches(target.Label,
                [((ShortcutRowViewModel)row.DataContext!).Label])));
            Assert.False(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("Settings_Nav_General", "Nav_Statistics")]
    [InlineData("Settings_Nav_Dashboard", "DashboardLayout_ModularGrid_Title")]
    public void Search_FindsDynamicOptions_FromAnySection(string sectionKey, string labelKey)
    {
        var (view, vm, window) = CreateView();
        try
        {
            var label = LocalizationService.Instance[labelKey];
            vm.SearchText = LocalizationService.Instance[sectionKey] + " " + label;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label && Shown(t));
            Assert.False(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Search_DoesNotCombineWordsFromUnrelatedOptions()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SearchText = "idioma depuracion";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Search_FiltersIndividualEditorOptions_AndRestoresSelectedSection()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SearchText = "ANCHO editor";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var sliders = view.GetLogicalDescendants().OfType<Slider>().ToArray();
            var shown = sliders.Where(Shown).ToArray();
            Assert.Single(shown);
            Assert.Equal(vm.EditorColumnWidth, shown[0].Value);
            vm.ClearSearchCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var sections = view.GetLogicalDescendants().OfType<SettingsSearchOption>()
                .Where(o => o.Section is not null && o.IsVisible).ToArray();
            Assert.Single(sections);
            Assert.Equal(SettingsSection.General, sections[0].Section);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Search_TracksLanguageChanges_AndDisplaysNoResults()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SearchText = "filtro etiquetas";
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
            LocalizationService.Instance.SetLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
            vm.SearchText = "tag filter";
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
            vm.SearchText = "zzzz-no-such-option";
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<TextBlock>("SearchNoResults")!.IsVisible);
        }
        finally { window.Close(); }
    }
}
