using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>Sentinel shown in the font pickers for "use the bundled Space Grotesk" — stored as
    /// null/empty in <see cref="Models.AppSettings"/> and <see cref="EditorPreferences"/>. Was a
    /// compile-time <c>const</c> before i18n; now resolved from <see cref="LocalizationService"/> so it
    /// tracks the active language, at the cost of no longer being usable in a <c>const</c>/<c>case</c>
    /// context (no callers needed that).</summary>
    public static string DefaultFontLabel => LocalizationService.Instance["Settings_Appearance_Font_DefaultLabel"];

    private readonly SettingsService _settingsService;
    private readonly CustomThemeService _customThemeService;
    private readonly VaultSettingsService _vaultSettingsService;
    private readonly VaultService _vaultService;
    private readonly VaultBackupService _vaultBackupService;
    private readonly PolarAuthService _polarAuthService;
    private readonly PolarSyncService _polarSyncService;
    private readonly NotificationService _notificationService;
    private readonly Action _onVaultChanged;
    private readonly Action<bool> _onTitleBarModeChanged;
    private readonly Action<bool> _onNotificationsPanelChanged;
    private readonly Action<bool> _onRemindersPanelChanged;
    private readonly Action<bool> _onCalendarShowSidebarScrollbarChanged;
    private readonly Action<bool> _onDashboardShowScrollbarChanged;
    private readonly Action<string> _onKanbanBoardOrientationChanged;

    /// <summary>Single callback covering every sidebar-section visibility toggle (Ajustes › General ›
    /// Secciones de la barra lateral) — replaces what used to be one <c>Action&lt;bool&gt;</c> per section
    /// (<c>onCalendarPanelChanged</c>, <c>onSkillsPanelModeChanged</c>) now that there are eight of them
    /// instead of two; <see cref="MainWindowViewModel.OnSidebarSectionChanged"/> switches on the
    /// <see cref="AppPage"/> to apply whatever page-specific side effect that section needs.</summary>
    private readonly Action<AppPage, bool> _onSidebarSectionChanged;

    /// <summary>Fired after every drag/pin reorder in <see cref="MainSidebarSections"/>/
    /// <see cref="BottomSidebarSections"/> that changes membership or order but not any single section's
    /// visibility (so <see cref="_onSidebarSectionChanged"/>'s own rebuild wouldn't otherwise fire) —
    /// <see cref="MainWindowViewModel"/> wires this straight to its own <c>RebuildNavItems</c> so the real
    /// sidebar picks up the new order immediately.</summary>
    private readonly Action _onSidebarSectionOrderChanged;

    private readonly Action<bool> _onPolarShowTaskTagsChanged;
    private readonly Action<int> _onUiZoomChanged;
    private readonly Action _onLanguageChanged;

    /// <summary>Same debounce pattern as <c>EditorViewModel</c>'s autosave timer — dragging a
    /// <c>Slider</c> (tamaño de fuente, ancho de columna, ventana de "próximo" recordatorio, horario
    /// laboral) fires dozens of value changes per second, and writing <c>settings.json</c> to disk on
    /// every one of them was both wasted I/O and a source of micro-stutter while dragging. Only the
    /// disk write is delayed — the
    /// in-memory <see cref="SettingsService.Settings"/> field and any live callback
    /// (<see cref="_onCalendarSettingsChanged"/>, <see cref="EditorPreferences"/>) still update
    /// immediately, so the rest of the app never lags behind what's on screen.</summary>
    private static readonly TimeSpan SettingsSaveDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _settingsSaveTimer;

    /// <summary>Same debounce as <see cref="_settingsSaveTimer"/>, but for <see cref="Models.VaultSettings"/>
    /// (docs/funcionalidad-zoom-interfaz.md §2) — a separate timer instead of sharing the one above
    /// because they persist to two different files (<c>%AppData%/Enki/settings.json</c> vs.
    /// <c>&lt;vault&gt;/.enki/settings.json</c>) and there's no reason a slider drag on one should force
    /// a write of the other.</summary>
    private readonly DispatcherTimer _vaultSettingsSaveTimer;

    /// <summary>Single callback for every other Calendario setting (docs/calendar-feature/
    /// 09-configuracion-y-ajustes.md §1) that the already-open <c>CalendarViewModel</c> needs to react to
    /// live — one callback covering the whole group rather than one per property (unlike
    /// <see cref="_onCalendarShowSidebarScrollbarChanged"/>/<see cref="_onSidebarSectionChanged"/>, which
    /// are their own concerns: sidebar chrome and nav visibility, not "how the grid itself is laid out").
    /// Not fired for <c>CalendarDefaultView</c>, which only matters the next time the page opens.</summary>
    private readonly Action _onCalendarSettingsChanged;

    public ObservableCollection<SettingsSectionViewModel> Sections { get; }
    public ObservableCollection<string> AvailableFonts { get; }

    /// <summary>Idiomas disponibles para la UI. La lista es estática, pero el seleccionado se enlaza a
    /// <see cref="SelectedLanguage"/>. CA1822 sugiere marcar el getter <c>static</c> porque no toca estado
    /// de instancia — pero <c>{Binding AvailableLanguages}</c> en SettingsView.axaml resuelve contra la
    /// instancia de <c>DataContext</c>: una propiedad estática dejaría de encontrarse por binding, así que
    /// se suprime en vez de "arreglarse" (Fase 10, docs/analisis-proyecto/roadmap.md).</summary>
#pragma warning disable CA1822
    public IReadOnlyList<SupportedLanguageInfo> AvailableLanguages => SupportedLanguages.All;
#pragma warning restore CA1822

    [ObservableProperty]
    private SupportedLanguageInfo? _selectedLanguage;

    public string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0";

    /// <summary>"VERSION {0}" in Ajustes › Acerca de — a computed property instead of an XAML
    /// <c>StringFormat</c> so the word order can be adapted per language (§1.3 of the i18n doc).</summary>
    public string AppVersionLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_About_VersionLabel"], AppVersion);

    [ObservableProperty]
    private SettingsSection _selectedSection = SettingsSection.General;

    /// <summary>"Preguntar antes de borrar" (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3) —
    /// consultado por <c>KanbanViewModel</c>/<c>CalendarViewModel</c>/<c>PolarTaskManagementViewModel</c>/
    /// <c>SkillsViewModel</c> en sus respectivos <c>RequestDelete*</c> para saltar directo al borrado cuando
    /// está desactivado. No afecta al borrado permanente desde la Papelera, que siempre confirma.</summary>
    [ObservableProperty]
    private bool _confirmBeforeDelete;

    /// <summary>"Registro de depuración" (Ajustes › General › Diagnóstico, Fase 0 de
    /// docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2) — baja <c>LogService</c> de su nivel
    /// mínimo por defecto (<c>Warning</c>) a <c>Debug</c>. Apagado por defecto.</summary>
    [ObservableProperty]
    private bool _debugLoggingEnabled;

    /// <summary>Página de inicio (Ajustes › General) — con qué página arranca la app justo después de
    /// cargar el vault. Reutiliza <c>AppPage</c> (el mismo enum de la navegación principal) en vez de uno
    /// propio, igual que <see cref="CalendarDefaultView"/> reutiliza <c>CalendarViewMode</c>.</summary>
    [ObservableProperty]
    private AppPage _startupPage;

    public SettingsViewModel(
        SettingsService settingsService,
        CustomThemeService customThemeService,
        VaultSettingsService vaultSettingsService,
        VaultService vaultService,
        VaultBackupService vaultBackupService,
        PolarAuthService polarAuthService,
        PolarSyncService polarSyncService,
        NotificationService notificationService,
        Action onVaultChanged,
        Action<bool> onTitleBarModeChanged,
        Action<bool> onNotificationsPanelChanged,
        Action<bool> onRemindersPanelChanged,
        Action<bool> onCalendarShowSidebarScrollbarChanged,
        Action<AppPage, bool> onSidebarSectionChanged,
        Action onSidebarSectionOrderChanged,
        Action onCalendarSettingsChanged,
        Action<bool> onDashboardShowScrollbarChanged,
        Action<string> onKanbanBoardOrientationChanged,
        Action<bool> onPolarShowTaskTagsChanged,
        Action<int> onUiZoomChanged,
        Action onLanguageChanged)
    {
        _settingsService = settingsService;
        _customThemeService = customThemeService;
        _vaultSettingsService = vaultSettingsService;
        _vaultService = vaultService;
        _vaultBackupService = vaultBackupService;
        _polarAuthService = polarAuthService;
        _polarSyncService = polarSyncService;
        _notificationService = notificationService;
        _onVaultChanged = onVaultChanged;
        _onTitleBarModeChanged = onTitleBarModeChanged;
        _onNotificationsPanelChanged = onNotificationsPanelChanged;
        _onRemindersPanelChanged = onRemindersPanelChanged;
        _onCalendarShowSidebarScrollbarChanged = onCalendarShowSidebarScrollbarChanged;
        _onSidebarSectionChanged = onSidebarSectionChanged;
        _onSidebarSectionOrderChanged = onSidebarSectionOrderChanged;
        _onCalendarSettingsChanged = onCalendarSettingsChanged;
        _onDashboardShowScrollbarChanged = onDashboardShowScrollbarChanged;
        _onKanbanBoardOrientationChanged = onKanbanBoardOrientationChanged;
        _onPolarShowTaskTagsChanged = onPolarShowTaskTagsChanged;
        _onUiZoomChanged = onUiZoomChanged;
        _onLanguageChanged = onLanguageChanged;

        _settingsSaveTimer = new DispatcherTimer { Interval = SettingsSaveDebounceDelay };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            _settingsService.Save();
        };

        _vaultSettingsSaveTimer = new DispatcherTimer { Interval = SettingsSaveDebounceDelay };
        _vaultSettingsSaveTimer.Tick += (_, _) =>
        {
            _vaultSettingsSaveTimer.Stop();
            _vaultSettingsService.Save();
        };

        _isPolarLinked = polarAuthService.IsLinked;
        _polarLinkedEmail = polarAuthService.LinkedEmail;
        polarAuthService.LinkStateChanged += () =>
        {
            IsPolarLinked = polarAuthService.IsLinked;
            PolarLinkedEmail = polarAuthService.LinkedEmail;
        };
        RefreshLastSyncedLabel();
        polarSyncService.SyncCompleted += (_, _) =>
        {
            RefreshLastSyncedLabel();
            LastSyncNowWasSkipped = false;
        };
        _vaultPath = vaultService.VaultPath;
        RefreshVaultSizeLabel();
        // Re-reads the label once VaultService's own background recomputation lands
        // (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3) — fired from a background thread, so the
        // property update is marshaled onto the UI thread here rather than in VaultService itself.
        vaultService.VaultSizeChanged += () => Dispatcher.UIThread.Post(RefreshVaultSizeLabel);
        _startupPage = ParseAppPage(settingsService.Settings.StartupPage);
        _isDarkTheme = settingsService.Settings.Theme != "Light";
        _isCustomThemeActive = customThemeService.ActiveThemeId is not null;
        _editorFontSize = settingsService.Settings.EditorFontSize;
        _editorColumnWidth = settingsService.Settings.EditorColumnWidth;
        _isColumnLeftAligned = settingsService.Settings.EditorColumnPosition == "Left";
        _editorShowScrollbar = settingsService.Settings.EditorShowScrollbar;
        _editorTabSize = settingsService.Settings.EditorTabSize;
        _editorLineNumbers = settingsService.Settings.EditorLineNumbers;
        _editorWordWrap = settingsService.Settings.EditorWordWrap;
        _editorRemoteImages = settingsService.Settings.EditorRemoteImages;
        _editorSidePanelVisible = settingsService.Settings.EditorSidePanelVisible;
        _isSimplifiedNotesView = settingsService.Settings.NotesViewMode == "Simple";
        _useCustomTitleBar = settingsService.Settings.UseCustomTitleBar;
        _dashboardShowScrollbar = settingsService.Settings.DashboardShowScrollbar;
        _kanbanBoardVertical = settingsService.Settings.KanbanBoardOrientation != "Horizontal";
        _isBuiltInDarkThemesCollapsed = settingsService.Settings.BuiltInDarkThemesCollapsed;
        _isBuiltInLightThemesCollapsed = settingsService.Settings.BuiltInLightThemesCollapsed;
        _confirmBeforeDelete = settingsService.Settings.ConfirmBeforeDelete;
        _debugLoggingEnabled = settingsService.Settings.DebugLoggingEnabled;
        _autoBackupEnabled = settingsService.Settings.AutoBackupEnabled;
        _autoBackupIntervalDays = settingsService.Settings.AutoBackupIntervalDays;
        _autoBackupRetentionCount = settingsService.Settings.AutoBackupRetentionCount;
        _uiZoomPercent = vaultSettingsService.Settings.UiZoomPercent;
        _calendarShowSidebarScrollbar = settingsService.Settings.CalendarShowSidebarScrollbar;
        _calendarDefaultView = ParseCalendarViewMode(settingsService.Settings.CalendarDefaultView);
        _calendarWeekStartsOnMonday = settingsService.Settings.CalendarWeekStartsOnMonday;
        _calendarShowWeekends = settingsService.Settings.CalendarShowWeekends;
        _calendarWorkingHoursStart = settingsService.Settings.CalendarWorkingHoursStart;
        _calendarWorkingHoursEnd = settingsService.Settings.CalendarWorkingHoursEnd;
        _calendarTimeFormat24h = settingsService.Settings.CalendarTimeFormat24h;
        _calendarPanelEnabled = settingsService.Settings.CalendarPanelEnabled;
        _skillsPanelEnabled = settingsService.Settings.SkillsPanelEnabled;
        _dashboardPanelEnabled = settingsService.Settings.DashboardPanelEnabled;
        _kanbanPanelEnabled = settingsService.Settings.KanbanPanelEnabled;
        _notesPanelEnabled = settingsService.Settings.NotesPanelEnabled;
        _filesPanelEnabled = settingsService.Settings.FilesPanelEnabled;
        _sessionsPanelEnabled = settingsService.Settings.SessionsPanelEnabled;
        _trashPanelEnabled = settingsService.Settings.TrashPanelEnabled;
        _habitsPanelEnabled = settingsService.Settings.HabitsPanelEnabled;
        _statisticsPanelEnabled = settingsService.Settings.StatisticsPanelEnabled;
        _remindersSidebarEnabled = settingsService.Settings.RemindersSidebarEnabled;
        _notificationsSidebarEnabled = settingsService.Settings.NotificationsSidebarEnabled;
        _polarShowTaskTags = settingsService.Settings.PolarShowTaskTags;
        _notificationsPanelEnabled = settingsService.Settings.NotificationsPanelEnabled;
        _remindersPanelEnabled = settingsService.Settings.RemindersPanelEnabled;
        _showDesktopToasts = settingsService.Settings.ShowDesktopToasts;
        _muteInfoNotifications = settingsService.Settings.MuteInfoNotifications;
        _muteSuccessNotifications = settingsService.Settings.MuteSuccessNotifications;
        _muteWarningNotifications = settingsService.Settings.MuteWarningNotifications;
        _muteErrorNotifications = settingsService.Settings.MuteErrorNotifications;
        _reminderSoonWindowDays = settingsService.Settings.ReminderSoonWindowDays;
        _doNotDisturbEnabled = settingsService.Settings.DoNotDisturbEnabled;
        _doNotDisturbStart = settingsService.Settings.DoNotDisturbStart;
        _doNotDisturbEnd = settingsService.Settings.DoNotDisturbEnd;
        _notificationSoundEnabled = settingsService.Settings.NotificationSoundEnabled;
        _notificationSoundVolume = settingsService.Settings.NotificationSoundVolume;
        _interfaceFontFamily = ToLabel(settingsService.Settings.InterfaceFontFamily);
        _editorFontFamily = ToLabel(settingsService.Settings.EditorFontFamily);
        _selectedLanguage = SupportedLanguages.Find(settingsService.Settings.Language)
            ?? SupportedLanguages.Find(LocalizationService.Instance.CurrentCulture.Name)
            ?? SupportedLanguages.All[0];

        EditorPreferences.FontSize = _editorFontSize;
        EditorPreferences.ColumnWidth = _editorColumnWidth;
        EditorPreferences.ColumnPosition = settingsService.Settings.EditorColumnPosition;
        EditorPreferences.ShowScrollbar = _editorShowScrollbar;
        EditorPreferences.FontFamily = settingsService.Settings.EditorFontFamily;
        EditorPreferences.TabSize = settingsService.Settings.EditorTabSize;
        EditorPreferences.LineNumbers = _editorLineNumbers;
        EditorPreferences.WordWrap = _editorWordWrap;
        EditorPreferences.RemoteImages = _editorRemoteImages;
        EditorPreferences.SidePanelVisible = _editorSidePanelVisible;
        EditorPreferences.ToolbarVisible = settingsService.Settings.EditorToolbarVisible;
        EditorPreferences.ToolbarTextStyleExpanded = settingsService.Settings.EditorToolbarTextStyleExpanded;
        EditorPreferences.ToolbarHeadingsExpanded = settingsService.Settings.EditorToolbarHeadingsExpanded;
        EditorPreferences.ToolbarListsExpanded = settingsService.Settings.EditorToolbarListsExpanded;
        EditorPreferences.ToolbarInsertExpanded = settingsService.Settings.EditorToolbarInsertExpanded;
        EditorPreferences.ToolbarCodeExpanded = settingsService.Settings.EditorToolbarCodeExpanded;
        EditorPreferences.SimplifiedNotesView = _isSimplifiedNotesView;

        AvailableFonts = BuildAvailableFonts();

        Sections =
        [
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_General"], Icon = MaterialIconKind.TuneVariant, Section = SettingsSection.General, IsActive = true },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Appearance"], Icon = MaterialIconKind.ThemeLightDark, Section = SettingsSection.Appearance },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Editor"], Icon = MaterialIconKind.PencilOutline, Section = SettingsSection.Editor },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Notifications"], Icon = MaterialIconKind.BellRingOutline, Section = SettingsSection.Notifications },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Calendar"], Icon = MaterialIconKind.CalendarBlankOutline, Section = SettingsSection.Calendar },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Account"], Icon = MaterialIconKind.AccountCircleOutline, Section = SettingsSection.Account },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Skills"], Icon = MaterialIconKind.Brain, Section = SettingsSection.Skills },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Shortcuts"], Icon = MaterialIconKind.KeyboardOutline, Section = SettingsSection.Shortcuts },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_Dashboard"], Icon = MaterialIconKind.ViewDashboardOutline, Section = SettingsSection.Dashboard },
            new SettingsSectionViewModel { Label = LocalizationService.Instance["Settings_Nav_About"], Icon = MaterialIconKind.InfoOutline, Section = SettingsSection.About }
        ];

        ShortcutCategories = new ObservableCollection<ShortcutCategoryViewModel>(
            ShortcutCatalog.All
                .GroupBy(d => d.CategoryKey)
                .Select(group => new ShortcutCategoryViewModel(
                    group.Key,
                    group.Select(d => new ShortcutRowViewModel(
                        d.Id, d.LabelKey, ShortcutRegistry.GetGestureDisplay(d.Id), ShortcutRegistry.IsCustomized(d.Id))).ToList())));
        // Mantiene las filas al día si otra ventana (o un reset disparado desde aquí mismo) cambia un
        // atajo — mismo "live mirror" que EditorPreferences.Changed, sin desuscripción porque este
        // viewmodel vive tanto como la shell (igual que Notes/Kanban, nunca se reconstruye salvo reinicio).
        ShortcutRegistry.Changed += RefreshAllShortcutRows;

        CustomThemes = new ObservableCollection<CustomThemeRowViewModel>();
        BuiltInDarkThemes = new ObservableCollection<CustomThemeRowViewModel>();
        BuiltInLightThemes = new ObservableCollection<CustomThemeRowViewModel>();
        RefreshCustomThemes();

        BuildDashboardLayoutOptions();
        BuildDashboardSections();
        BuildSidebarSections();

        // Only request a built-in variant when no custom theme is active. A custom theme overrides the
        // colors in Application.Resources and must not be clobbered by the dark/light toggle.
        if (Avalonia.Application.Current is not null && !_isCustomThemeActive)
        {
            Avalonia.Application.Current.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        ApplyInterfaceFont(settingsService.Settings.InterfaceFontFamily);
    }

    private void DebouncedSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void DebouncedVaultSave()
    {
        _vaultSettingsSaveTimer.Stop();
        _vaultSettingsSaveTimer.Start();
    }

    /// <summary>Forces any debounced slider change to disk immediately — called when the window is
    /// closing (same reasoning as <c>EditorViewModel.FlushPendingSave</c>: a pending
    /// <see cref="_settingsSaveTimer"/> tick that never fires because the app closed first would silently
    /// drop the user's last drag of a slider). Also flushes <see cref="_vaultSettingsSaveTimer"/>, so a
    /// zoom change made right before quitting isn't lost either.</summary>
    public void FlushPendingSave()
    {
        if (_settingsSaveTimer.IsEnabled)
        {
            _settingsSaveTimer.Stop();
            _settingsService.Save();
        }

        if (_vaultSettingsSaveTimer.IsEnabled)
        {
            _vaultSettingsSaveTimer.Stop();
            _vaultSettingsService.Save();
        }
    }

    private static string ToLabel(string? fontFamily) =>
        string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontLabel : fontFamily;

    private static string? FromLabel(string label) =>
        label == DefaultFontLabel ? null : label;

    private static CalendarViewMode ParseCalendarViewMode(string value) =>
        Enum.TryParse<CalendarViewMode>(value, out var mode) ? mode : CalendarViewMode.Month;

    private static AppPage ParseAppPage(string value) =>
        Enum.TryParse<AppPage>(value, out var page) ? page : AppPage.Dashboard;

    private static ObservableCollection<string> BuildAvailableFonts()
    {
        var list = new ObservableCollection<string> { DefaultFontLabel };
        try
        {
            var names = FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names) list.Add(name);
        }
        catch
        {
            // Fall back to just the bundled default if the platform can't enumerate system fonts.
        }
        return list;
    }

    private static void ApplyInterfaceFont(string? fontFamily)
    {
        if (Avalonia.Application.Current is not { } app) return;

        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            app.Resources.Remove("DisplayFontFamily");
        }
        else
        {
            app.Resources["DisplayFontFamily"] = new FontFamily(fontFamily);
        }
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        foreach (var section in Sections) section.IsActive = section.Section == value;
    }

    /// <summary>Only ever changed from <see cref="SetStartupPage"/> below (segmented control, no live
    /// callback needed — a página de inicio solo importa la próxima vez que arranque la app).</summary>
    partial void OnStartupPageChanged(AppPage value)
    {
        _settingsService.Settings.StartupPage = value.ToString();
        _settingsService.Save();
    }

    [RelayCommand]
    private void SetStartupPage(AppPage page) => StartupPage = page;

    partial void OnConfirmBeforeDeleteChanged(bool value)
    {
        _settingsService.Settings.ConfirmBeforeDelete = value;
        _settingsService.Save();
    }

    partial void OnDebugLoggingEnabledChanged(bool value)
    {
        _settingsService.Settings.DebugLoggingEnabled = value;
        _settingsService.Save();
        LogService.SetDebugEnabled(value);
    }

    /// <summary>Abre <c>%AppData%/Enki/logs</c> en el explorador de archivos — mismo patrón que
    /// <see cref="OpenVaultFolder"/>. <see cref="LogService.LogDirectory"/> es null solo si ni siquiera se
    /// pudo crear la carpeta al arrancar (ver <see cref="LogService.Initialize"/>), caso en el que no hay
    /// nada que abrir.</summary>
    [RelayCommand]
    private static void OpenLogsFolder()
    {
        if (LogService.LogDirectory is { } logDirectory) FolderOpener.Open(logDirectory);
    }

    [RelayCommand]
    private void SelectSection(SettingsSection section) => SelectedSection = section;

    partial void OnSelectedLanguageChanged(SupportedLanguageInfo? value)
    {
        if (value is null) return;

        var previousLanguage = _settingsService.Settings.Language;
        _settingsService.Settings.Language = value.CultureCode;
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _settingsService.Settings.Language = previousLanguage;
            _selectedLanguage = SupportedLanguages.Find(previousLanguage)
                ?? SupportedLanguages.Find(LocalizationService.Instance.CurrentCulture.Name);
            OnPropertyChanged(nameof(SelectedLanguage));
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_General_Language_SaveFailed"),
                VaultFailureReasons.DescribeLocalized(ex), Models.NotificationSeverity.Error);
            return;
        }

        LocalizationService.Instance.SetLanguage(value.CultureCode);

        // El shell se encarga de coordinar el refresco de etiquetas en todos los viewmodels para evitar
        // suscripciones directas al singleton de localización desde cada uno de ellos (i18n Fase 4).
        _onLanguageChanged?.Invoke();
    }

    /// <summary>Refresca todas las propiedades computadas que dependen de recursos localizados cuando
    /// <see cref="MainWindowViewModel"/> coordina un cambio de idioma en Ajustes › General —
    /// docs/agent-docs/internacionalizacion-i18n.md §3.3.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(AppVersionLabel));
        OnPropertyChanged(nameof(EditorFontSizeLabel));
        OnPropertyChanged(nameof(EditorColumnWidthLabel));
        OnPropertyChanged(nameof(EditorTabSizeLabel));
        OnPropertyChanged(nameof(UiZoomPercentLabel));
        OnPropertyChanged(nameof(ReminderSoonWindowDaysLabel));
        OnPropertyChanged(nameof(SyncNowButtonLabel));
        OnPropertyChanged(nameof(LinkButtonLabel));
        // Las dos dependían de que otra cosa cambiara para refrescarse: la del volumen, de que se moviera
        // el deslizador (hoy su unidad es "{0}%" en los seis idiomas, pero en alemán lo correcto es
        // "{0} %" con espacio fino, y ese día la etiqueta se quedaría atrás); la del error de sync, de que
        // el texto reasignado abajo fuera distinto al que ya había. Anunciarlas no depende de ninguna de
        // las dos coincidencias.
        OnPropertyChanged(nameof(NotificationSoundVolumeLabel));
        OnPropertyChanged(nameof(SyncErrorLabel));

        RefreshLastSyncedLabel();
        SyncError = _polarSyncService.LastError;
        RebuildSections();
        BuildDashboardLayoutOptions();
        BuildDashboardSections();
        BuildSidebarSections();
        RebuildAvailableFonts();
        RebuildShortcutLabels();
        ThemeEditor?.RefreshLabels();
    }

    private void RebuildSections()
    {
        if (Sections is null || Sections.Count < 10) return;
        Sections[0].Label = LocalizationService.Instance["Settings_Nav_General"];
        Sections[1].Label = LocalizationService.Instance["Settings_Nav_Appearance"];
        Sections[2].Label = LocalizationService.Instance["Settings_Nav_Editor"];
        Sections[3].Label = LocalizationService.Instance["Settings_Nav_Notifications"];
        Sections[4].Label = LocalizationService.Instance["Settings_Nav_Calendar"];
        Sections[5].Label = LocalizationService.Instance["Settings_Nav_Account"];
        Sections[6].Label = LocalizationService.Instance["Settings_Nav_Skills"];
        Sections[7].Label = LocalizationService.Instance["Settings_Nav_Shortcuts"];
        Sections[8].Label = LocalizationService.Instance["Settings_Nav_Dashboard"];
        Sections[9].Label = LocalizationService.Instance["Settings_Nav_About"];
    }

    private void RebuildAvailableFonts()
    {
        if (AvailableFonts is null || AvailableFonts.Count == 0) return;
        var previousDefault = AvailableFonts[0];
        var interfaceWasDefault = InterfaceFontFamily == previousDefault;
        var editorWasDefault = EditorFontFamily == previousDefault;
        AvailableFonts[0] = DefaultFontLabel;
        if (interfaceWasDefault) InterfaceFontFamily = DefaultFontLabel;
        if (editorWasDefault) EditorFontFamily = DefaultFontLabel;
    }

    /// <summary>Host legible de <see cref="SkillsViewModel.CommunitySkillsUrl"/> — ver el comentario de
    /// <see cref="SkillsViewModel.CommunitySkillsUrlDisplay"/> para por qué esto vive como propiedad en
    /// vez de un literal "skillsclaude.org" en <c>SettingsView.axaml</c>.</summary>
    public string CommunitySkillsUrlDisplay { get; } = new Uri(SkillsViewModel.CommunitySkillsUrl).Host;

    [RelayCommand]
    private static void OpenCommunitySkills() => UrlOpener.Open(SkillsViewModel.CommunitySkillsUrl);

}
