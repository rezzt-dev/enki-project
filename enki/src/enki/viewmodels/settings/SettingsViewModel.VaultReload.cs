using enki.Localization;
using enki.Services;

#pragma warning disable MVVMTK0034 // La recarga asigna campos sin disparar los callbacks de guardado.

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    /// <summary>Recarga los controles al abrir otro vault sin ejecutar los comandos de guardado.</summary>
    public void ReloadForVault()
    {
        _startupPage = ParseAppPage(_settingsService.Settings.StartupPage);
        _isDarkTheme = _settingsService.Settings.Theme != "Light";
        _isCustomThemeActive = _customThemeService.ActiveThemeId is not null;
        _editorFontSize = _settingsService.Settings.EditorFontSize;
        _editorColumnWidth = _settingsService.Settings.EditorColumnWidth;
        _isColumnLeftAligned = _settingsService.Settings.EditorColumnPosition == "Left";
        _editorShowScrollbar = _settingsService.Settings.EditorShowScrollbar;
        _editorTabSize = _settingsService.Settings.EditorTabSize;
        _editorLineNumbers = _settingsService.Settings.EditorLineNumbers;
        _editorWordWrap = _settingsService.Settings.EditorWordWrap;
        _editorRemoteImages = _settingsService.Settings.EditorRemoteImages;
        _editorSidePanelVisible = _settingsService.Settings.EditorSidePanelVisible;
        _isSimplifiedNotesView = _settingsService.Settings.NotesViewMode == "Simple";
        _useCustomTitleBar = _settingsService.Settings.UseCustomTitleBar;
        _dashboardShowScrollbar = _settingsService.Settings.DashboardShowScrollbar;
        _kanbanShowTagFilter = _settingsService.Settings.KanbanShowTagFilter;
        _kanbanBoardVertical = _settingsService.Settings.KanbanBoardOrientation != "Horizontal";
        _useCustomBrandLogo = _settingsService.Settings.UseCustomBrandLogo && _brandLogoService.HasLogo;
        _isBrandLogoCircular = _settingsService.Settings.BrandLogoShape == "Circle";
        _isBuiltInDarkThemesCollapsed = _settingsService.Settings.BuiltInDarkThemesCollapsed;
        _isBuiltInLightThemesCollapsed = _settingsService.Settings.BuiltInLightThemesCollapsed;
        _confirmBeforeDelete = _settingsService.Settings.ConfirmBeforeDelete;
        _debugLoggingEnabled = _settingsService.Settings.DebugLoggingEnabled;
        _autoBackupEnabled = _settingsService.Settings.AutoBackupEnabled;
        _autoBackupIntervalDays = _settingsService.Settings.AutoBackupIntervalDays;
        _autoBackupRetentionCount = _settingsService.Settings.AutoBackupRetentionCount;
        _uiZoomPercent = _vaultSettingsService.Settings.UiZoomPercent;
        _calendarShowSidebarScrollbar = _settingsService.Settings.CalendarShowSidebarScrollbar;
        _calendarDefaultView = ParseCalendarViewMode(_settingsService.Settings.CalendarDefaultView);
        _calendarWeekStartsOnMonday = _settingsService.Settings.CalendarWeekStartsOnMonday;
        _calendarShowWeekends = _settingsService.Settings.CalendarShowWeekends;
        _calendarWorkingHoursStart = _settingsService.Settings.CalendarWorkingHoursStart;
        _calendarWorkingHoursEnd = _settingsService.Settings.CalendarWorkingHoursEnd;
        _calendarTimeFormat24h = _settingsService.Settings.CalendarTimeFormat24h;
        _calendarPanelEnabled = _settingsService.Settings.CalendarPanelEnabled;
        _skillsPanelEnabled = _settingsService.Settings.SkillsPanelEnabled;
        _dashboardPanelEnabled = _settingsService.Settings.DashboardPanelEnabled;
        _kanbanPanelEnabled = _settingsService.Settings.KanbanPanelEnabled;
        _notesPanelEnabled = _settingsService.Settings.NotesPanelEnabled;
        _filesPanelEnabled = _settingsService.Settings.FilesPanelEnabled;
        _sessionsPanelEnabled = _settingsService.Settings.SessionsPanelEnabled;
        _trashPanelEnabled = _settingsService.Settings.TrashPanelEnabled;
        _habitsPanelEnabled = _settingsService.Settings.HabitsPanelEnabled;
        _statisticsPanelEnabled = _settingsService.Settings.StatisticsPanelEnabled;
        _remindersSidebarEnabled = _settingsService.Settings.RemindersSidebarEnabled;
        _notificationsSidebarEnabled = _settingsService.Settings.NotificationsSidebarEnabled;
        _polarShowTaskTags = _settingsService.Settings.PolarShowTaskTags;
        _notificationsPanelEnabled = _settingsService.Settings.NotificationsPanelEnabled;
        _remindersPanelEnabled = _settingsService.Settings.RemindersPanelEnabled;
        _showDesktopToasts = _settingsService.Settings.ShowDesktopToasts;
        _muteInfoNotifications = _settingsService.Settings.MuteInfoNotifications;
        _muteSuccessNotifications = _settingsService.Settings.MuteSuccessNotifications;
        _muteWarningNotifications = _settingsService.Settings.MuteWarningNotifications;
        _muteErrorNotifications = _settingsService.Settings.MuteErrorNotifications;
        _reminderSoonWindowDays = _settingsService.Settings.ReminderSoonWindowDays;
        _doNotDisturbEnabled = _settingsService.Settings.DoNotDisturbEnabled;
        _doNotDisturbStart = _settingsService.Settings.DoNotDisturbStart;
        _doNotDisturbEnd = _settingsService.Settings.DoNotDisturbEnd;
        _notificationSoundEnabled = _settingsService.Settings.NotificationSoundEnabled;
        _notificationSoundVolume = _settingsService.Settings.NotificationSoundVolume;
        _interfaceFontFamily = ToLabel(_settingsService.Settings.InterfaceFontFamily);
        _editorFontFamily = ToLabel(_settingsService.Settings.EditorFontFamily);
        _selectedLanguage = SupportedLanguages.Find(_settingsService.Settings.Language)
            ?? SupportedLanguages.Find(LocalizationService.Instance.CurrentCulture.Name)
            ?? SupportedLanguages.All[0];

        EditorPreferences.FontSize = _editorFontSize;
        EditorPreferences.ColumnWidth = _editorColumnWidth;
        EditorPreferences.ColumnPosition = _settingsService.Settings.EditorColumnPosition;
        EditorPreferences.ShowScrollbar = _editorShowScrollbar;
        EditorPreferences.FontFamily = _settingsService.Settings.EditorFontFamily;
        EditorPreferences.TabSize = _settingsService.Settings.EditorTabSize;
        EditorPreferences.LineNumbers = _editorLineNumbers;
        EditorPreferences.WordWrap = _editorWordWrap;
        EditorPreferences.JustifyParagraphs = _settingsService.Settings.EditorJustifyParagraphs;
        EditorPreferences.RemoteImages = _editorRemoteImages;
        EditorPreferences.SidePanelVisible = _editorSidePanelVisible;
        EditorPreferences.ToolbarVisible = _settingsService.Settings.EditorToolbarVisible;
        EditorPreferences.ToolbarTextStyleExpanded = _settingsService.Settings.EditorToolbarTextStyleExpanded;
        EditorPreferences.ToolbarHeadingsExpanded = _settingsService.Settings.EditorToolbarHeadingsExpanded;
        EditorPreferences.ToolbarListsExpanded = _settingsService.Settings.EditorToolbarListsExpanded;
        EditorPreferences.ToolbarInsertExpanded = _settingsService.Settings.EditorToolbarInsertExpanded;
        EditorPreferences.ToolbarCodeExpanded = _settingsService.Settings.EditorToolbarCodeExpanded;
        EditorPreferences.SimplifiedNotesView = _isSimplifiedNotesView;


        OnPropertyChanged(nameof(StartupPage));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsCustomThemeActive));
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(EditorColumnWidth));
        OnPropertyChanged(nameof(IsColumnLeftAligned));
        OnPropertyChanged(nameof(EditorShowScrollbar));
        OnPropertyChanged(nameof(EditorTabSize));
        OnPropertyChanged(nameof(EditorLineNumbers));
        OnPropertyChanged(nameof(EditorWordWrap));
        OnPropertyChanged(nameof(EditorRemoteImages));
        OnPropertyChanged(nameof(EditorSidePanelVisible));
        OnPropertyChanged(nameof(IsSimplifiedNotesView));
        OnPropertyChanged(nameof(UseCustomTitleBar));
        OnPropertyChanged(nameof(DashboardShowScrollbar));
        OnPropertyChanged(nameof(KanbanShowTagFilter));
        OnPropertyChanged(nameof(KanbanBoardVertical));
        OnPropertyChanged(nameof(UseCustomBrandLogo));
        OnPropertyChanged(nameof(IsBrandLogoCircular));
        OnPropertyChanged(nameof(IsBuiltInDarkThemesCollapsed));
        OnPropertyChanged(nameof(IsBuiltInLightThemesCollapsed));
        OnPropertyChanged(nameof(ConfirmBeforeDelete));
        OnPropertyChanged(nameof(DebugLoggingEnabled));
        OnPropertyChanged(nameof(AutoBackupEnabled));
        OnPropertyChanged(nameof(AutoBackupIntervalDays));
        OnPropertyChanged(nameof(AutoBackupRetentionCount));
        OnPropertyChanged(nameof(UiZoomPercent));
        OnPropertyChanged(nameof(CalendarShowSidebarScrollbar));
        OnPropertyChanged(nameof(CalendarDefaultView));
        OnPropertyChanged(nameof(CalendarWeekStartsOnMonday));
        OnPropertyChanged(nameof(CalendarShowWeekends));
        OnPropertyChanged(nameof(CalendarWorkingHoursStart));
        OnPropertyChanged(nameof(CalendarWorkingHoursEnd));
        OnPropertyChanged(nameof(CalendarTimeFormat24h));
        OnPropertyChanged(nameof(CalendarPanelEnabled));
        OnPropertyChanged(nameof(SkillsPanelEnabled));
        OnPropertyChanged(nameof(DashboardPanelEnabled));
        OnPropertyChanged(nameof(KanbanPanelEnabled));
        OnPropertyChanged(nameof(NotesPanelEnabled));
        OnPropertyChanged(nameof(FilesPanelEnabled));
        OnPropertyChanged(nameof(SessionsPanelEnabled));
        OnPropertyChanged(nameof(TrashPanelEnabled));
        OnPropertyChanged(nameof(HabitsPanelEnabled));
        OnPropertyChanged(nameof(StatisticsPanelEnabled));
        OnPropertyChanged(nameof(RemindersSidebarEnabled));
        OnPropertyChanged(nameof(NotificationsSidebarEnabled));
        OnPropertyChanged(nameof(PolarShowTaskTags));
        OnPropertyChanged(nameof(NotificationsPanelEnabled));
        OnPropertyChanged(nameof(RemindersPanelEnabled));
        OnPropertyChanged(nameof(ShowDesktopToasts));
        OnPropertyChanged(nameof(MuteInfoNotifications));
        OnPropertyChanged(nameof(MuteSuccessNotifications));
        OnPropertyChanged(nameof(MuteWarningNotifications));
        OnPropertyChanged(nameof(MuteErrorNotifications));
        OnPropertyChanged(nameof(ReminderSoonWindowDays));
        OnPropertyChanged(nameof(DoNotDisturbEnabled));
        OnPropertyChanged(nameof(DoNotDisturbStart));
        OnPropertyChanged(nameof(DoNotDisturbEnd));
        OnPropertyChanged(nameof(NotificationSoundEnabled));
        OnPropertyChanged(nameof(NotificationSoundVolume));
        OnPropertyChanged(nameof(InterfaceFontFamily));
        OnPropertyChanged(nameof(EditorFontFamily));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(UiZoomBarFillStar));
        OnPropertyChanged(nameof(UiZoomBarRemainderStar));
        RefreshCustomThemes();
        RefreshBrandLogoPreview();
        BuildDashboardLayoutOptions();
        BuildDashboardSections();
        BuildSidebarSections();
        ApplyInterfaceFont(_settingsService.Settings.InterfaceFontFamily);
    }
}
#pragma warning restore MVVMTK0034
