using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Services;
using enki.Localization;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="SettingsViewModel"/> against a real (throwaway-directory)
/// <see cref="SettingsService"/> — same pattern as the rest of the JSON-backed services
/// (03-problemas-y-deuda-tecnica.md §8 flags this viewmodel as untested). Every navigation/live callback is
/// a no-op unless a test needs to observe it.</summary>
[Collection("LocalizationMutatingTests")]
public class SettingsViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SettingsViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private static (VaultBackupService BackupService, PolarSyncService SyncService, NotificationService NotificationService, CustomThemeService CustomThemeService, BrandLogoService BrandLogoService)
        CreateExtraDependencies(SettingsService settingsService, VaultService vault, PolarAuthService authService, string storageDirectory)
    {
        var backupService = new VaultBackupService(vault, storageDirectory);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(storageDirectory);
        var reminderService = new ReminderService(storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var notificationService = new NotificationService(settingsService, storageDirectory);
        var customThemeService = new CustomThemeService(settingsService);
        var brandLogoService = new BrandLogoService(storageDirectory);
        return (backupService, syncService, notificationService, customThemeService, brandLogoService);
    }

    private (SettingsViewModel Settings, SettingsService SettingsService, VaultService Vault, VaultSettingsService VaultSettingsService) CreateViewModel(KanbanService? kanbanService = null)
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);

        SettingsViewModel? vm = null;
        vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            switchVault: (_, _) => Task.FromResult(true), onTitleBarModeChanged: _ => { }, onNotificationsPanelChanged: _ => { },
            onRemindersPanelChanged: _ => { }, onCalendarShowSidebarScrollbarChanged: _ => { },
            onSidebarSectionChanged: (_, _) => { }, onSidebarSectionOrderChanged: () => { }, onCalendarSettingsChanged: () => { }, onDashboardShowScrollbarChanged: _ => { },
            onKanbanBoardOrientationChanged: _ => { },
            onPolarShowTaskTagsChanged: _ => { }, onUiZoomChanged: _ => { },
            onLanguageChanged: () => vm!.RefreshLocalizedLabels(), kanbanService: kanbanService);

        return (vm, settingsService, vault, vaultSettingsService);
    }

    [Fact]
    public void MultipleBoards_CanOnlyBeDisabledAfterConsolidatingToOneBoard()
    {
        var kanban = new KanbanService(Path.Combine(_storageDirectory, "boards"));
        var (vm, settings, _, _) = CreateViewModel(kanban);
        Assert.False(vm.KanbanMultipleBoardsEnabled);
        vm.KanbanMultipleBoardsEnabled = true;
        Assert.True(settings.Settings.KanbanMultipleBoardsEnabled);
        var board = kanban.CreateBoard("Estudios");
        vm.KanbanMultipleBoardsEnabled = false;
        Assert.True(vm.KanbanMultipleBoardsEnabled);
        Assert.Equal(2, kanban.Boards.Count);
        kanban.DeleteEmptyBoard(board.Id);
        vm.KanbanMultipleBoardsEnabled = false;
        Assert.False(settings.Settings.KanbanMultipleBoardsEnabled);
        Assert.Single(kanban.Boards);
    }

    [Fact]
    public void Navigation_LocalizesAllDestinationsAndSelectedHeading()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            var (vm, _, _, _) = CreateViewModel();
            Assert.Equal(Enum.GetValues<SettingsSection>().Length, vm.Sections.Count);
            Assert.Equal(4, vm.Sections.Count(section => section.StartsGroup));
            vm.SelectSectionCommand.Execute(SettingsSection.Backups);
            foreach (var language in SupportedLanguages.All)
            {
                LocalizationService.Instance.SetLanguage(language.CultureCode);
                vm.RefreshLocalizedLabels();
                Assert.All(vm.Sections, section =>
                {
                    Assert.DoesNotContain("Settings_", section.Label);
                    Assert.DoesNotContain("Settings_", section.Description);
                    Assert.DoesNotContain("Settings_", section.GroupLabel);
                });
                Assert.Equal(LocalizationService.Instance["Settings_Nav_Backups"], vm.CurrentSectionTitle);
            }
        }
        finally { LocalizationService.Instance.SetLanguage(original); }
    }

    [Fact]
    public void KanbanTags_PersistsVisibility_AndNotifiesListeners()
    {
        var (vm, service, _, _) = CreateViewModel();
        Assert.True(vm.KanbanShowTagFilter);
        var changes = 0;
        service.SettingsChanged += () => changes++;
        vm.KanbanShowTagFilter = false;
        Assert.False(new SettingsService(_storageDirectory).Settings.KanbanShowTagFilter);
        Assert.Equal(1, changes);
        vm.KanbanShowTagFilter = true;
        Assert.True(new SettingsService(_storageDirectory).Settings.KanbanShowTagFilter);
    }

    [Fact]
    public void Search_ClearingAndNavigationRestoreNormalSections()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.SearchText = "etiquetas";
        Assert.True(vm.IsSearching);
        vm.ClearSearchCommand.Execute(null);
        Assert.False(vm.IsSearching);
        vm.SearchText = "fuente";
        vm.SelectSectionCommand.Execute(SettingsSection.Editor);
        Assert.Equal(SettingsSection.Editor, vm.SelectedSection);
        Assert.False(vm.IsSearching);
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void LanguageSwitch_PersistsChoice_AndKeepsDefaultFontSemantics(string culture)
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("es-ES");
            var (vm, settings, _, _) = CreateViewModel();
            vm.SelectedLanguage = SupportedLanguages.Find(culture);
            Assert.Equal(culture, LocalizationService.Instance.CurrentCulture.Name);
            Assert.Equal(culture, new SettingsService(_storageDirectory).Settings.Language);
            Assert.Equal(SettingsViewModel.DefaultFontLabel, vm.InterfaceFontFamily);
            Assert.Equal(SettingsViewModel.DefaultFontLabel, vm.EditorFontFamily);
            Assert.Equal(SettingsViewModel.DefaultCodeFontLabel, vm.CodeFontFamily);
            Assert.Null(settings.Settings.InterfaceFontFamily);
            Assert.Null(settings.Settings.EditorFontFamily);
            Assert.Null(settings.Settings.CodeFontFamily);
            vm.FlushPendingSave();
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    [Fact]
    public void LanguageSwitch_PreservesCustomFonts()
    {
        var original = LocalizationService.Instance.CurrentCulture.Name;
        try
        {
            LocalizationService.Instance.SetLanguage("es-ES");
            var (vm, settings, _, _) = CreateViewModel();
            vm.InterfaceFontFamily = "Space Grotesk";
            vm.EditorFontFamily = "JetBrains Mono";
            vm.CodeFontFamily = "Cascadia Code";
            vm.SelectedLanguage = SupportedLanguages.Find("fr-FR");
            Assert.Equal("Space Grotesk", vm.InterfaceFontFamily);
            Assert.Equal("JetBrains Mono", vm.EditorFontFamily);
            Assert.Equal("Cascadia Code", vm.CodeFontFamily);
            Assert.Equal("Space Grotesk", settings.Settings.InterfaceFontFamily);
            Assert.Equal("JetBrains Mono", settings.Settings.EditorFontFamily);
            Assert.Equal("Cascadia Code", settings.Settings.CodeFontFamily);
            vm.FlushPendingSave();
        }
        finally
        {
            LocalizationService.Instance.SetLanguage(original);
        }
    }

    [Fact]
    public void CodeFont_PersistsAndUpdatesOpenEditors()
    {
        var (vm, _, _, _) = CreateViewModel();
        var changes = 0;
        void OnChanged() => changes++;
        EditorPreferences.Changed += OnChanged;
        try
        {
            vm.CodeFontFamily = "Cascadia Code";
            Assert.Equal("Cascadia Code", new SettingsService(_storageDirectory).Settings.CodeFontFamily);
            Assert.Equal("Cascadia Code", EditorPreferences.CodeFontFamily);
            Assert.Equal(1, changes);

            vm.CodeFontFamily = SettingsViewModel.DefaultCodeFontLabel;
            Assert.Null(new SettingsService(_storageDirectory).Settings.CodeFontFamily);
            Assert.Null(EditorPreferences.CodeFontFamily);
            Assert.Equal(2, changes);
        }
        finally
        {
            EditorPreferences.Changed -= OnChanged;
        }
    }

    [Fact]
    public void Constructor_ReadsInitialValues_FromSettingsService()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.EditorFontSize = 16;
        settingsService.Settings.Theme = "Dark";
        settingsService.Save();
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            (_, _) => Task.FromResult(true), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { });

        Assert.Equal(16, vm.EditorFontSize);
        Assert.True(vm.IsDarkTheme);
        Assert.Equal(_vaultPath, vm.VaultPath);
        Assert.Equal(100, vm.UiZoomPercent);
    }

    [Fact]
    public void OnIsDarkThemeChanged_PersistsTheme_Immediately()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.IsDarkTheme = false;

        var reloaded = new SettingsService(_storageDirectory);
        Assert.Equal("Light", reloaded.Settings.Theme);
    }

    /// <summary>Regression for análisis alfa-3 §4.5 (Media): the "at least one sidebar section visible"
    /// guard used to count 12 flags while only 9 of them actually populate <c>MainWindowViewModel.NavItems</c>
    /// (Recordatorios/Notificaciones/Estadísticas render as their own fixed buttons instead). That let the
    /// last of the 9 real <c>NavItems</c> entries be turned off as long as any of those other three stayed
    /// on, leaving the primary nav list completely empty. Turning off every <c>NavItems</c>-backed section
    /// down to the last one, with Reminders/Notifications/Statistics still enabled, must now still block.</summary>
    [Fact]
    public void BlockIfLastEnabledSidebarSection_IgnoresReminderNotificationStatisticsFlags()
    {
        var (vm, _, _, _) = CreateViewModel();

        // Skills starts disabled by default; turn off every other NavItems-backed section but Dashboard.
        vm.CalendarPanelEnabled = false;
        vm.KanbanPanelEnabled = false;
        vm.NotesPanelEnabled = false;
        vm.FilesPanelEnabled = false;
        vm.SessionsPanelEnabled = false;
        vm.TrashPanelEnabled = false;
        vm.TagManagerPanelEnabled = false;
        vm.HabitsPanelEnabled = false;

        // Reminders/Notifications/Statistics stay on — under the old (buggy) 12-flag count this alone would
        // have been enough to let Dashboard, the last real NavItems entry, be turned off too.
        Assert.True(vm.RemindersSidebarEnabled);
        Assert.True(vm.NotificationsSidebarEnabled);
        Assert.True(vm.StatisticsPanelEnabled);

        vm.DashboardPanelEnabled = false;

        Assert.True(vm.DashboardPanelEnabled);
    }

    /// <summary>Complement of the test above: Reminders/Notifications/Statistics don't populate
    /// <c>NavItems</c> at all, so hiding all three down to zero must never be blocked by the sidebar guard —
    /// unlike before this fix, where their flags fed the same shared counter as the 9 real sections.</summary>
    [Fact]
    public void RemindersNotificationsStatistics_CanAllBeDisabled_WithoutTriggeringGuard()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.RemindersSidebarEnabled = false;
        vm.NotificationsSidebarEnabled = false;
        vm.StatisticsPanelEnabled = false;

        Assert.False(vm.RemindersSidebarEnabled);
        Assert.False(vm.NotificationsSidebarEnabled);
        Assert.False(vm.StatisticsPanelEnabled);
    }

    [Fact]
    public void OnNotificationsPanelEnabledChanged_InvokesCallback_WithNewValue()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);
        bool? notified = null;

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            (_, _) => Task.FromResult(true), _ => { }, v => notified = v, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { });

        vm.NotificationsPanelEnabled = false;

        Assert.False(notified);
    }

    /// <summary>Los dos interruptores nuevos llegan como parámetros opcionales al final del constructor,
    /// así que el riesgo real es que nadie los conecte: se comprueba que cada uno persiste y avisa.</summary>
    [Fact]
    public void OnInboxPanelEnabledChanged_PersistsAndInvokesCallback()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);
        bool? notified = null;

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            (_, _) => Task.FromResult(true), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { },
            null, v => notified = v);

        Assert.True(vm.InboxPanelEnabled);
        vm.InboxPanelEnabled = false;

        Assert.False(notified);
        Assert.False(settingsService.Settings.InboxPanelEnabled);
    }

    [Fact]
    public void OnVaultSwitcherEnabledChanged_PersistsAndInvokesCallback()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);
        bool? notified = null;

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            (_, _) => Task.FromResult(true), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { },
            null, null, v => notified = v);

        Assert.True(vm.VaultSwitcherEnabled);
        vm.VaultSwitcherEnabled = false;

        Assert.False(notified);
        Assert.False(settingsService.Settings.VaultSwitcherEnabled);
    }

    [Fact]
    public void EditorFontSize_UpdatesEditorPreferences_ImmediatelyEvenBeforeDebouncedSave()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.EditorFontSize = 18;

        // El valor en memoria y EditorPreferences se actualizan de inmediato; solo la escritura a disco
        // se retrasa 300ms (ver DebouncedSave) — docs/analisis-mejoras-funcionalidades.md, Fase 1 §5.
        Assert.Equal(18, EditorPreferences.FontSize);
    }

    [Fact]
    public void FlushPendingSave_PersistsDebouncedSliderChange_ToDisk()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.EditorFontSize = 18;
        vm.FlushPendingSave();

        var reloaded = new SettingsService(_storageDirectory);
        Assert.Equal(18, reloaded.Settings.EditorFontSize);
    }

    [Fact]
    public void FlushPendingSave_WithNoPendingChange_DoesNothing()
    {
        var (vm, settingsService, _, _) = CreateViewModel();
        settingsService.Settings.EditorFontSize = 99; // mutación directa, fuera del debounce

        vm.FlushPendingSave();

        var reloaded = new SettingsService(_storageDirectory);
        Assert.NotEqual(99, reloaded.Settings.EditorFontSize);
    }

    [Fact]
    public void CalendarWorkingHoursStart_SetPastEnd_PushesEndForward()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.CalendarWorkingHoursStart = 9;
        vm.CalendarWorkingHoursEnd = 18;

        vm.CalendarWorkingHoursStart = 20;

        Assert.Equal(20, vm.CalendarWorkingHoursStart);
        Assert.Equal(21, vm.CalendarWorkingHoursEnd);
    }

    [Fact]
    public void CalendarWorkingHoursEnd_SetBeforeStart_PullsStartBack()
    {
        var (vm, _, _, _) = CreateViewModel();
        vm.CalendarWorkingHoursStart = 9;
        vm.CalendarWorkingHoursEnd = 18;

        vm.CalendarWorkingHoursEnd = 5;

        Assert.Equal(5, vm.CalendarWorkingHoursEnd);
        Assert.Equal(4, vm.CalendarWorkingHoursStart);
    }

    [Fact]
    public void CalendarWorkingHours_ValidRange_StaysUnchanged()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.CalendarWorkingHoursStart = 8;
        vm.CalendarWorkingHoursEnd = 17;

        Assert.Equal(8, vm.CalendarWorkingHoursStart);
        Assert.Equal(17, vm.CalendarWorkingHoursEnd);
    }

    [Fact]
    public void CalendarWorkingHours_ValidatedValues_PersistAfterFlush()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.CalendarWorkingHoursStart = 20;
        vm.FlushPendingSave();

        var reloaded = new SettingsService(_storageDirectory);
        Assert.Equal(20, reloaded.Settings.CalendarWorkingHoursStart);
        Assert.Equal(21, reloaded.Settings.CalendarWorkingHoursEnd);
    }

    [Fact]
    public void OnUiZoomPercentChanged_InvokesCallback_WithNewValue()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var (backupService, syncService, notificationService, customThemeService, brandLogoService) = CreateExtraDependencies(settingsService, vault, authService, _storageDirectory);
        int? notified = null;

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            (_, _) => Task.FromResult(true), _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, v => notified = v, () => { });

        vm.UiZoomPercent = 150;

        Assert.Equal(150, notified);
    }

    [Fact]
    public void FlushPendingSave_PersistsDebouncedZoomChange_ToTheVaultFile_NotTheGlobalOne()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.UiZoomPercent = 150;
        vm.FlushPendingSave();

        Assert.True(File.Exists(Path.Combine(_vaultPath, ".enki", "settings.json")));

        var reloadedGlobal = new SettingsService(_storageDirectory);
        var reloadedVault = new VaultSettingsService(new VaultService(reloadedGlobal));
        Assert.Equal(150, reloadedVault.Settings.UiZoomPercent);
    }

    [Fact]
    public void DashboardSections_DefaultOrder_AndAllVisible()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();

        Assert.Equal(new[] { "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits" }, vm.DashboardSections.Select(s => s.Key));
        Assert.All(vm.DashboardSections, s => Assert.True(s.IsVisible));
        Assert.Empty(vaultSettings.Settings.DashboardSectionOrder);
    }

    [Fact]
    public void ToggleDashboardSectionVisibility_FlipsIsVisible_AndPersists()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();

        vm.ToggleDashboardSectionVisibilityCommand.Execute("Vault");

        var vaultItem = vm.DashboardSections.First(s => s.Key == "Vault");
        Assert.False(vaultItem.IsVisible);
        Assert.Contains("Vault", vaultSettings.Settings.DashboardHiddenSections);
    }

    [Fact]
    public void ToggleDashboardSectionVisibility_PreventsHidingLastVisibleSection()
    {
        var (vm, _, _, _) = CreateViewModel();
        foreach (var item in vm.DashboardSections.Skip(1)) vm.ToggleDashboardSectionVisibilityCommand.Execute(item.Key);
        var last = vm.DashboardSections.First();
        Assert.True(last.IsVisible);

        vm.ToggleDashboardSectionVisibilityCommand.Execute(last.Key);

        Assert.True(last.IsVisible);
    }

    [Fact]
    public void MoveDashboardSection_ReordersCollection_AndPersists()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();

        vm.MoveDashboardSection("RecentNotes", "QuickAccess");

        Assert.Equal("RecentNotes", vm.DashboardSections[0].Key);
        Assert.Equal("QuickAccess", vm.DashboardSections[1].Key);
        Assert.Equal(vaultSettings.Settings.DashboardSectionOrder, vm.DashboardSections.Select(s => s.Key).ToList());
    }

    [Fact]
    public void DashboardSections_Position_IsOneBased_AndTracksFirstLast()
    {
        var (vm, _, _, _) = CreateViewModel();

        Assert.Equal(Enumerable.Range(1, vm.DashboardSections.Count), vm.DashboardSections.Select(s => s.Position));
        Assert.True(vm.DashboardSections.First().IsFirst);
        Assert.False(vm.DashboardSections.First().IsLast);
        Assert.True(vm.DashboardSections.Last().IsLast);
        Assert.False(vm.DashboardSections.Last().IsFirst);
    }

    [Fact]
    public void MoveDashboardSectionUpAndDown_SwapAdjacentRows_AndPersist()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();

        vm.MoveDashboardSectionDownCommand.Execute("QuickAccess");
        Assert.Equal("Today", vm.DashboardSections[0].Key);
        Assert.Equal("QuickAccess", vm.DashboardSections[1].Key);
        Assert.Equal(vaultSettings.Settings.DashboardSectionOrder, vm.DashboardSections.Select(s => s.Key).ToList());

        vm.MoveDashboardSectionUpCommand.Execute("QuickAccess");
        Assert.Equal("QuickAccess", vm.DashboardSections[0].Key);
        Assert.Equal("Today", vm.DashboardSections[1].Key);
    }

    [Fact]
    public void MoveDashboardSectionUp_AtStart_IsNoOp()
    {
        var (vm, _, _, _) = CreateViewModel();
        var originalOrder = vm.DashboardSections.Select(s => s.Key).ToList();

        vm.MoveDashboardSectionUpCommand.Execute(originalOrder[0]);

        Assert.Equal(originalOrder, vm.DashboardSections.Select(s => s.Key).ToList());
    }

    [Fact]
    public void MoveDashboardSectionDown_AtEnd_IsNoOp()
    {
        var (vm, _, _, _) = CreateViewModel();
        var originalOrder = vm.DashboardSections.Select(s => s.Key).ToList();

        vm.MoveDashboardSectionDownCommand.Execute(originalOrder[^1]);

        Assert.Equal(originalOrder, vm.DashboardSections.Select(s => s.Key).ToList());
    }

    [Fact]
    public void ResetDashboardLayout_RestoresDefaultOrder_AndVisibility()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();
        vm.MoveDashboardSection("RecentNotes", "QuickAccess");
        vm.ToggleDashboardSectionVisibilityCommand.Execute("Vault");

        vm.ResetDashboardLayoutCommand.Execute(null);

        Assert.Equal(new[] { "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits" }, vm.DashboardSections.Select(s => s.Key));
        Assert.All(vm.DashboardSections, s => Assert.True(s.IsVisible));
        Assert.Empty(vaultSettings.Settings.DashboardHiddenSections!);
    }

    [Fact]
    public void ToggleDashboardSectionVisibility_UpdatesVisibilitySummary()
    {
        var (vm, _, _, _) = CreateViewModel();
        var initialSummary = vm.DashboardVisibilitySummary;

        vm.ToggleDashboardSectionVisibilityCommand.Execute("Vault");

        Assert.NotEqual(initialSummary, vm.DashboardVisibilitySummary);
        Assert.Contains("7", vm.DashboardVisibilitySummary);
    }

    [Fact]
    public void DashboardLayoutOptions_HasOneCardPerEngineStyle_ClassicSelectedByDefault()
    {
        var (vm, _, _, _) = CreateViewModel();

        Assert.Equal(enki.Services.DashboardLayoutEngine.AllStyles, vm.DashboardLayoutOptions.Select(o => o.Key));
        Assert.Equal(enki.Services.DashboardLayoutEngine.Classic, vm.DashboardLayoutStyle);
        Assert.True(vm.DashboardLayoutOptions.Single(o => o.Key == enki.Services.DashboardLayoutEngine.Classic).IsSelected);
        Assert.All(vm.DashboardLayoutOptions.Where(o => o.Key != enki.Services.DashboardLayoutEngine.Classic), o => Assert.False(o.IsSelected));
    }

    [Fact]
    public void SelectDashboardLayoutStyle_PersistsChoice_AndUpdatesSelectedFlags()
    {
        var (vm, _, _, vaultSettings) = CreateViewModel();

        vm.SelectDashboardLayoutStyleCommand.Execute(enki.Services.DashboardLayoutEngine.Mosaic);

        Assert.Equal(enki.Services.DashboardLayoutEngine.Mosaic, vm.DashboardLayoutStyle);
        Assert.Equal(enki.Services.DashboardLayoutEngine.Mosaic, vaultSettings.Settings.DashboardLayoutStyle);
        Assert.True(vm.DashboardLayoutOptions.Single(o => o.Key == enki.Services.DashboardLayoutEngine.Mosaic).IsSelected);
        Assert.False(vm.DashboardLayoutOptions.Single(o => o.Key == enki.Services.DashboardLayoutEngine.Classic).IsSelected);
    }

    [Fact]
    public void SelectDashboardLayoutStyle_UpdatesPreviewGridSlots()
    {
        var (vm, _, _, _) = CreateViewModel();

        vm.SelectDashboardLayoutStyleCommand.Execute(enki.Services.DashboardLayoutEngine.ModularGrid);

        var quickAccess = vm.DashboardSections.First(s => s.Key == "QuickAccess");
        Assert.Equal(0, quickAccess.GridRow);
        Assert.Equal(4, quickAccess.GridColumnSpan);
    }
}
