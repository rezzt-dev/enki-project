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
    public void SidePanels_AreSearchable_AndEachSelectorChangesItsOwnPreference()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SearchText = "paneles laterales";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            foreach (var section in new[] { "Notes", "Files", "Skills", "Assistant" })
            {
                var segments = view.GetVisualDescendants().OfType<RadioButton>()
                    .Where(b => b.GroupName == section + "PanelPosition").ToArray();
                Assert.Equal(2, segments.Length);
                Assert.All(segments, segment => Assert.True(Shown(segment)));
                Assert.True(segments[0].IsChecked);
                segments[1].IsChecked = true;
                Assert.True(segments[1].IsChecked);
                Assert.False(segments[0].IsChecked);
            }
            Assert.True(vm.SidePanels.NotesOnRight);
            Assert.True(vm.SidePanels.FilesOnRight);
            Assert.True(vm.SidePanels.SkillsOnRight);
            Assert.True(vm.SidePanels.AssistantOnRight);
            var notes = view.GetVisualDescendants().OfType<RadioButton>()
                .First(b => b.GroupName == "NotesPanelPosition");
            notes.IsChecked = true;
            Assert.False(vm.SidePanels.NotesOnRight);
            Assert.True(vm.SidePanels.FilesOnRight);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void MotionMode_IsSearchableAndChangesFromBothSegments()
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SearchText = "animaciones";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var segments = view.GetVisualDescendants().OfType<RadioButton>()
                .Where(b => b.GroupName == "MotionMode").ToArray();
            Assert.Equal(2, segments.Length);
            Assert.All(segments, segment => Assert.True(Shown(segment)));
            segments[1].IsChecked = true;
            Assert.True(vm.UseSubtleAnimations);
            Assert.True(MotionService.UseSubtleAnimations);
            segments[0].IsChecked = true;
            Assert.False(vm.UseSubtleAnimations);
            Assert.False(MotionService.UseSubtleAnimations);
        }
        finally { window.Close(); MotionService.Apply(false); }
    }

    [Fact]
    public void Navigation_OpensEverySectionWithoutSearch_AndKeepsHeadingInSync()
    {
        var (view, vm, window) = CreateView();
        try
        {
            window.Width = 1000;
            window.Height = 700;
            foreach (var item in vm.Sections)
            {
                vm.SearchText = "previous search";
                var button = view.GetVisualDescendants().OfType<Button>()
                    .Single(b => Equals(b.CommandParameter, item.Section));
                button.Command!.Execute(button.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.False(vm.IsSearching);
                Assert.Equal(item.Label, vm.CurrentSectionTitle);
                Assert.Equal(item.Description, vm.CurrentSectionDescription);
                Assert.Same(item, Assert.Single(vm.Sections, section => section.IsActive));
                var content = Assert.Single(view.GetLogicalDescendants().OfType<SettingsSearchOption>(),
                    option => option.Section is not null && Shown(option));
                Assert.Equal(item.Section, content.Section);
                Assert.True(content.Bounds.Width > 0);
                Assert.True(content.Bounds.Width <= 660);
            }
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(SettingsSection.Vault, "Settings_General_VaultFolder_Title")]
    [InlineData(SettingsSection.Backups, "Settings_General_DangerZone_AutoBackup_Label")]
    [InlineData(SettingsSection.Navigation, "Settings_General_Sidebar_Title")]
    [InlineData(SettingsSection.Kanban, "Settings_Appearance_KanbanTags_Title")]
    [InlineData(SettingsSection.Dashboard, "Settings_General_Dashboard_Title")]
    [InlineData(SettingsSection.Maintenance, "Settings_Updates_Title")]
    public void Navigation_ShowsRelocatedOptionsInTheirDestination(SettingsSection section, string key)
    {
        var (view, vm, window) = CreateView();
        try
        {
            vm.SelectSectionCommand.Execute(section);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var options = view.GetLogicalDescendants().OfType<SettingsSearchOption>()
                .Where(o => o.Section is null && o.ResourceKeys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(key)).ToArray();
            Assert.NotEmpty(options);
            Assert.All(options, option => Assert.True(Shown(option)));
            vm.SelectSectionCommand.Execute(SettingsSection.General);
            Dispatcher.UIThread.RunJobs();
            Assert.All(options, option => Assert.False(Shown(option)));
        }
        finally { window.Close(); }
    }

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
    [InlineData("Settings_Nav_Navigation", "Nav_Statistics")]
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
