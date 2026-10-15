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
    public static string DefaultCodeFontLabel => LocalizationService.Instance["Settings_Editor_CodeFont_DefaultLabel"];

    private readonly SettingsService _settingsService;
    private readonly CustomThemeService _customThemeService;
    private readonly BrandLogoService _brandLogoService;
    private readonly VaultSettingsService _vaultSettingsService;
    private readonly VaultService _vaultService;

    /// <summary>Plantillas de notas del vault activo: aquí solo se leen —para el desplegable de la nota
    /// diaria— y se abre su carpeta; quien las crea y las aplica es Documentos
    /// (<c>NotesViewModel.Templates.cs</c>).</summary>
    private readonly NoteTemplateService _noteTemplateService;
    private readonly VaultBackupService _vaultBackupService;
    private readonly PolarAuthService _polarAuthService;
    private readonly PolarSyncService _polarSyncService;
    private readonly NotificationService _notificationService;
    /// <summary>Pide al shell abrir otra carpeta como vault activo y recargarlo todo, devolviendo si
    /// el cambio llegó a producirse — <c>MainWindowViewModel.SwitchToVaultAsync</c>. Es una petición, no
    /// un aviso: la secuencia segura (comprobar la ruta, volcar lo pendiente, cancelar si algo quedó sin
    /// guardar, bloquear sesiones, recargar) vive donde está la composición, y tanto "cambiar carpeta del
    /// vault" como "restaurar copia de seguridad" entran por aquí en vez de repetirla cada uno por su
    /// cuenta (T4, .docs/project-info/multi-vault-en-caliente.md). El segundo parámetro es el hueco entre
    /// adoptar la ruta y recargar, que solo usa la restauración para importar sus preferencias.</summary>
    private readonly Func<string, Action?, Task<bool>> _switchVault;
    private readonly Action<bool> _onTitleBarModeChanged;
    private readonly Action<bool> _onNotificationsPanelChanged;
    private readonly Action<bool> _onRemindersPanelChanged;

    /// <summary>Los dos interruptores nuevos (botón de bandeja de entrada de la barra de título y fila
    /// "Cambiar de vault" del sidebar) llegan como parámetros opcionales al final del constructor, no
    /// en su sitio "lógico" entre los demás callbacks: la docena de tests y UI-tests que construyen este
    /// viewmodel a mano no necesitan enterarse de un ajuste que no ejercitan.</summary>
    private readonly Action<bool>? _onInboxPanelChanged;

    private readonly Action<bool>? _onVaultSwitcherChanged;
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
    public ObservableCollection<string> AvailableCodeFonts { get; }

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

    /// <summary>Si cerrar la ventana principal deja Enki vivo en la bandeja. Global a la instalación y
    /// apagado por defecto para que el comportamiento de cerrar solo cambie por elección explícita.</summary>
    [ObservableProperty]
    private bool _minimizeToTray;

    public SettingsViewModel(
        SettingsService settingsService,
        CustomThemeService customThemeService,
        BrandLogoService brandLogoService,
        VaultSettingsService vaultSettingsService,
        VaultService vaultService,
        VaultBackupService vaultBackupService,
        PolarAuthService polarAuthService,
        PolarSyncService polarSyncService,
        NotificationService notificationService,
        Func<string, Action?, Task<bool>> switchVault,
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
        Action onLanguageChanged,
        NoteTemplateService? noteTemplateService = null,
        Action<bool>? onInboxPanelChanged = null,
        Action<bool>? onVaultSwitcherChanged = null,
        UpdateCheckService? updateCheckService = null,
        KanbanService? kanbanService = null,
        Action? onManageKanbanBoards = null,
        ReminderService? reminderService = null,
        Action? onExternalImportCompleted = null)
    {
        _reminderImportService = reminderService;
        _onExternalImportCompleted = onExternalImportCompleted;
        _kanbanService = kanbanService;
        _onManageKanbanBoards = onManageKanbanBoards;
        _updateCheckService = updateCheckService;
        _settingsService = settingsService;
        _customThemeService = customThemeService;
        _brandLogoService = brandLogoService;
        _vaultSettingsService = vaultSettingsService;
        _vaultService = vaultService;
        // Opcional y derivado del vault cuando no se pasa, como en NotesViewModel: el servicio no tiene
        // estado propio. La app sí pasa el que compone MainWindowViewModel, para que crear una plantilla
        // desde Documentos refresque este desplegable por TemplatesChanged sin más cableado.
        _noteTemplateService = noteTemplateService ?? new NoteTemplateService(vaultService);
        _noteTemplateService.TemplatesChanged += ReloadDailyNoteTemplates;
        _vaultBackupService = vaultBackupService;
        _polarAuthService = polarAuthService;
        _polarSyncService = polarSyncService;
        _notificationService = notificationService;
        _switchVault = switchVault;
        _onTitleBarModeChanged = onTitleBarModeChanged;
        _onNotificationsPanelChanged = onNotificationsPanelChanged;
        _onRemindersPanelChanged = onRemindersPanelChanged;
        _onInboxPanelChanged = onInboxPanelChanged;
        _onVaultSwitcherChanged = onVaultSwitcherChanged;
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
        _minimizeToTray = settingsService.Settings.MinimizeToTray;
        _useSubtleAnimations = settingsService.Settings.UseSubtleAnimations;
        _isDarkTheme = settingsService.Settings.Theme != "Light";
        _isCustomThemeActive = customThemeService.ActiveThemeId is not null;
        _editorFontSize = settingsService.Settings.EditorFontSize;
        _editorColumnWidth = settingsService.Settings.EditorColumnWidth;
        _isColumnLeftAligned = settingsService.Settings.EditorColumnPosition == "Left";
        _editorShowScrollbar = settingsService.Settings.EditorShowScrollbar;
        _editorTabSize = settingsService.Settings.EditorTabSize;
        _editorLineNumbers = settingsService.Settings.EditorLineNumbers;
        _editorWordWrap = settingsService.Settings.EditorWordWrap;
        _editorJustifyText = settingsService.Settings.EditorJustifyParagraphs;
        _editorAutoPairs = settingsService.Settings.EditorAutoPairs;
        _editorRemoteImages = settingsService.Settings.EditorRemoteImages;
        _vaultWatchExternalChanges = settingsService.Settings.VaultWatchExternalChanges;
        _vaultAutoReloadExternalChanges = settingsService.Settings.VaultAutoReloadExternalChanges;
        _editorSidePanelVisible = settingsService.Settings.EditorSidePanelVisible;
        _isSimplifiedNotesView = settingsService.Settings.NotesViewMode == "Simple";
        _useCustomTitleBar = settingsService.Settings.UseCustomTitleBar;
        _showWindowControls = settingsService.Settings.ShowWindowControls;
        _dashboardShowScrollbar = settingsService.Settings.DashboardShowScrollbar;
        _kanbanShowTagFilter = settingsService.Settings.KanbanShowTagFilter;
        // KanbanMultipleBoardsEnabled no tiene campo propio: se lee y escribe directo contra los ajustes
        // desde SettingsViewModel.Kanban.cs, para que no haya dos copias de un dato que además puede
        // cambiar solo (importar un vault con varios tableros lo activa).
        _kanbanShowHighPriorityButton = settingsService.Settings.KanbanShowHighPriorityButton;
        _kanbanShowOverdueButton = settingsService.Settings.KanbanShowOverdueButton;
        _kanbanShowArchiveButton = settingsService.Settings.KanbanShowArchiveButton;
        _kanbanShowSortButton = settingsService.Settings.KanbanShowSortButton;
        _kanbanBoardVertical = settingsService.Settings.KanbanBoardOrientation != "Horizontal";
        _useCustomBrandLogo = settingsService.Settings.UseCustomBrandLogo && brandLogoService.HasLogo;
        _isBrandLogoCircular = settingsService.Settings.BrandLogoShape == "Circle";
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
        _tagManagerPanelEnabled = settingsService.Settings.TagManagerPanelEnabled;
        _habitsPanelEnabled = settingsService.Settings.HabitsPanelEnabled;
        _statisticsPanelEnabled = settingsService.Settings.StatisticsPanelEnabled;
        _remindersSidebarEnabled = settingsService.Settings.RemindersSidebarEnabled;
        _notificationsSidebarEnabled = settingsService.Settings.NotificationsSidebarEnabled;
        _polarShowTaskTags = settingsService.Settings.PolarShowTaskTags;
        _notificationsEnabled = settingsService.Settings.NotificationsEnabled;
        _notificationsPanelEnabled = settingsService.Settings.NotificationsPanelEnabled;
        _remindersPanelEnabled = settingsService.Settings.RemindersPanelEnabled;
        _inboxPanelEnabled = settingsService.Settings.InboxPanelEnabled;
        _vaultSwitcherEnabled = settingsService.Settings.VaultSwitcherEnabled;
        _showDesktopToasts = settingsService.Settings.ShowDesktopToasts;
        _muteInfoNotifications = settingsService.Settings.MuteInfoNotifications;
        _muteSuccessNotifications = settingsService.Settings.MuteSuccessNotifications;
        _muteWarningNotifications = settingsService.Settings.MuteWarningNotifications;
        _muteErrorNotifications = settingsService.Settings.MuteErrorNotifications;
        _reminderSoonWindowDays = settingsService.Settings.ReminderSoonWindowDays;
        _checkUpdatesOnStartup = settingsService.Settings.CheckUpdatesOnStartup;
        _snoozePrimaryMinutes = settingsService.Settings.SnoozePrimaryMinutes;
        _snoozeSecondaryMinutes = settingsService.Settings.SnoozeSecondaryMinutes;
        _snoozeEveningHour = settingsService.Settings.SnoozeEveningHour;
        _snoozeTomorrowHour = settingsService.Settings.SnoozeTomorrowHour;
        _doNotDisturbEnabled = settingsService.Settings.DoNotDisturbEnabled;
        _doNotDisturbStart = settingsService.Settings.DoNotDisturbStart;
        _doNotDisturbEnd = settingsService.Settings.DoNotDisturbEnd;
        _notificationSoundEnabled = settingsService.Settings.NotificationSoundEnabled;
        _notificationSoundVolume = settingsService.Settings.NotificationSoundVolume;
        _interfaceFontFamily = ToLabel(settingsService.Settings.InterfaceFontFamily);
        _editorFontFamily = ToLabel(settingsService.Settings.EditorFontFamily);
        _codeFontFamily = ToCodeFontLabel(settingsService.Settings.CodeFontFamily);
        _selectedLanguage = SupportedLanguages.Find(settingsService.Settings.Language)
            ?? SupportedLanguages.Find(LocalizationService.Instance.CurrentCulture.Name)
            ?? SupportedLanguages.All[0];

        EditorPreferences.FontSize = _editorFontSize;
        EditorPreferences.ColumnWidth = _editorColumnWidth;
        EditorPreferences.ColumnPosition = settingsService.Settings.EditorColumnPosition;
        EditorPreferences.ShowScrollbar = _editorShowScrollbar;
        EditorPreferences.FontFamily = settingsService.Settings.EditorFontFamily;
        EditorPreferences.CodeFontFamily = settingsService.Settings.CodeFontFamily;
        EditorPreferences.TabSize = settingsService.Settings.EditorTabSize;
        EditorPreferences.LineNumbers = _editorLineNumbers;
        EditorPreferences.WordWrap = _editorWordWrap;
        EditorPreferences.JustifyParagraphs = _editorJustifyText;
        EditorPreferences.AutoPairs = _editorAutoPairs;
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
        AvailableCodeFonts = BuildAvailableFonts(DefaultCodeFontLabel);

        Sections =
        [
            new SettingsSectionViewModel { Section = SettingsSection.General, Icon = MaterialIconKind.TuneVariant, GroupKey = "Settings_Group_Application", StartsGroup = true, IsActive = true },
            new SettingsSectionViewModel { Section = SettingsSection.Appearance, Icon = MaterialIconKind.ThemeLightDark, GroupKey = "Settings_Group_Application" },
            new SettingsSectionViewModel { Section = SettingsSection.Navigation, Icon = MaterialIconKind.PageLayoutSidebarLeft, GroupKey = "Settings_Group_Application" },
            new SettingsSectionViewModel { Section = SettingsSection.Shortcuts, Icon = MaterialIconKind.KeyboardOutline, GroupKey = "Settings_Group_Application" },
            new SettingsSectionViewModel { Section = SettingsSection.Notifications, Icon = MaterialIconKind.BellRingOutline, GroupKey = "Settings_Group_Application" },
            new SettingsSectionViewModel { Section = SettingsSection.Vault, Icon = MaterialIconKind.FolderOutline, GroupKey = "Settings_Group_Data", StartsGroup = true },
            new SettingsSectionViewModel { Section = SettingsSection.Backups, Icon = MaterialIconKind.BackupRestore, GroupKey = "Settings_Group_Data" },
            new SettingsSectionViewModel { Section = SettingsSection.Account, Icon = MaterialIconKind.AccountCircleOutline, GroupKey = "Settings_Group_Data" },
            new SettingsSectionViewModel { Section = SettingsSection.Editor, Icon = MaterialIconKind.PencilOutline, GroupKey = "Settings_Group_Tools", StartsGroup = true },
            new SettingsSectionViewModel { Section = SettingsSection.Dashboard, Icon = MaterialIconKind.ViewDashboardOutline, GroupKey = "Settings_Group_Tools" },
            new SettingsSectionViewModel { Section = SettingsSection.Kanban, Icon = MaterialIconKind.ViewDashboardOutline, GroupKey = "Settings_Group_Tools" },
            new SettingsSectionViewModel { Section = SettingsSection.Calendar, Icon = MaterialIconKind.CalendarBlankOutline, GroupKey = "Settings_Group_Tools" },
            new SettingsSectionViewModel { Section = SettingsSection.Skills, Icon = MaterialIconKind.Brain, GroupKey = "Settings_Group_Tools" },
            new SettingsSectionViewModel { Section = SettingsSection.Maintenance, Icon = MaterialIconKind.Update, GroupKey = "Settings_Group_Help", StartsGroup = true },
            new SettingsSectionViewModel { Section = SettingsSection.About, Icon = MaterialIconKind.InfoOutline, GroupKey = "Settings_Group_Help" }
        ];
        RebuildSections();

        ShortcutCategories = new ObservableCollection<ShortcutCategoryViewModel>(
            ShortcutCatalog.All
                .GroupBy(d => d.CategoryKey)
                .Select(group => new ShortcutCategoryViewModel(
                    group.Key,
                    group.Select(d => new ShortcutRowViewModel(
                        d.Id, d.LabelKey, ShortcutRegistry.GetGestureDisplay(d.Id), ShortcutRegistry.IsCustomized(d.Id), group.Key)).ToList())));
        // Mantiene las filas al día si otra ventana (o un reset disparado desde aquí mismo) cambia un
        // atajo — mismo "live mirror" que EditorPreferences.Changed, sin desuscripción porque este
        // viewmodel vive tanto como la shell (igual que Notes/Kanban, nunca se reconstruye salvo reinicio).
        ShortcutRegistry.Changed += RefreshAllShortcutRows;

        CustomThemes = new ObservableCollection<CustomThemeRowViewModel>();
        BuiltInDarkThemes = new ObservableCollection<CustomThemeRowViewModel>();
        BuiltInLightThemes = new ObservableCollection<CustomThemeRowViewModel>();
        RefreshCustomThemes();
        RefreshBrandLogoPreview();

        BuildDashboardLayoutOptions();
        BuildDashboardSections();
        BuildSidebarSections();
        ReloadDailyNoteTemplates();

        // Only request a built-in variant when no custom theme is active. A custom theme overrides the
        // colors in Application.Resources and must not be clobbered by the dark/light toggle.
        if (Avalonia.Application.Current is not null && !_isCustomThemeActive)
        {
            Avalonia.Application.Current.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        ApplyInterfaceFont(settingsService.Settings.InterfaceFontFamily);
        ApplyCodeFont(settingsService.Settings.CodeFontFamily);
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

    private static string ToCodeFontLabel(string? fontFamily) =>
        string.IsNullOrWhiteSpace(fontFamily) ? DefaultCodeFontLabel : fontFamily;

    private static string? FromCodeFontLabel(string label) =>
        label == DefaultCodeFontLabel ? null : label;

    private static CalendarViewMode ParseCalendarViewMode(string value) =>
        Enum.TryParse<CalendarViewMode>(value, out var mode) ? mode : CalendarViewMode.Month;

    private static AppPage ParseAppPage(string value) =>
        Enum.TryParse<AppPage>(value, out var page) ? page : AppPage.Dashboard;

    private static ObservableCollection<string> BuildAvailableFonts(string? defaultLabel = null)
    {
        var list = new ObservableCollection<string> { defaultLabel ?? DefaultFontLabel };
        try
        {
            var names = FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names) list.Add(name);
        }
        catch (Exception ex)
        {
            // Fall back to just the bundled default if the platform can't enumerate system fonts.
            LogService.Warning("No se pudieron enumerar las fuentes del sistema", ex);
        }
        return list;
    }

    /// <summary>Claves de <c>Colors.axaml</c> que la fuente de la interfaz sustituye: la fuente de texto, la
    /// de micro-labels y la que usa FluentTheme en sus controles. <c>MonoFontFamily</c> y
    /// <c>CodeFontFamily</c> quedan fuera para preservar sus preferencias independientes.</summary>
    private static readonly string[] InterfaceFontResourceKeys =
        ["DisplayFontFamily", "InterfaceLabelFontFamily", "ContentControlThemeFontFamily"];

    private static void ApplyInterfaceFont(string? fontFamily)
    {
        if (Avalonia.Application.Current is not { } app) return;

        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            // Quitar la clave de nivel superior deja ver la de Colors.axaml (diccionario fusionado).
            foreach (var key in InterfaceFontResourceKeys) app.Resources.Remove(key);
        }
        else
        {
            var family = new FontFamily(fontFamily);
            foreach (var key in InterfaceFontResourceKeys) app.Resources[key] = family;
        }
    }

    private static void ApplyCodeFont(string? fontFamily)
    {
        if (Avalonia.Application.Current is not { } app) return;
        if (string.IsNullOrWhiteSpace(fontFamily)) app.Resources.Remove("CodeFontFamily");
        else app.Resources["CodeFontFamily"] = new FontFamily(fontFamily);
    }

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        RefreshSectionHeading();
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

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settingsService.Settings.MinimizeToTray = value;
        _settingsService.Save();
    }

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
    private void SelectSection(SettingsSection section)
    {
        SearchText = string.Empty;
        SelectedSection = section;
    }

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
        OnPropertyChanged(nameof(InstalledVersionLabel));
        OnPropertyChanged(nameof(AvailableUpdateLabel));
        OnPropertyChanged(nameof(UpdateCheckStatusLabel));
        OnPropertyChanged(nameof(SnoozePrimaryLabel));
        OnPropertyChanged(nameof(SnoozeSecondaryLabel));
        OnPropertyChanged(nameof(SnoozeEveningHourLabel));
        OnPropertyChanged(nameof(SnoozeTomorrowHourLabel));
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
        RefreshSearchTexts();
    }

    private void RebuildSections()
    {
        if (Sections is null) return;
        foreach (var section in Sections)
        {
            section.Label = LocalizationService.Instance[$"Settings_Nav_{section.Section}"];
            section.Description = LocalizationService.Instance[$"Settings_Summary_{section.Section}"];
            section.GroupLabel = LocalizationService.Instance[section.GroupKey];
        }
        RefreshSectionHeading();
    }

    private void RebuildAvailableFonts()
    {
        if (AvailableFonts is null || AvailableFonts.Count == 0) return;
        var previousDefault = AvailableFonts[0];
        var interfaceWasDefault = InterfaceFontFamily == previousDefault;
        var editorWasDefault = EditorFontFamily == previousDefault;
        var codeWasDefault = CodeFontFamily == AvailableCodeFonts[0];
        AvailableFonts[0] = DefaultFontLabel;
        AvailableCodeFonts[0] = DefaultCodeFontLabel;
        if (interfaceWasDefault) InterfaceFontFamily = DefaultFontLabel;
        if (editorWasDefault) EditorFontFamily = DefaultFontLabel;
        if (codeWasDefault) CodeFontFamily = DefaultCodeFontLabel;
    }

    /// <summary>Host legible de <see cref="SkillsViewModel.CommunitySkillsUrl"/> — ver el comentario de
    /// <see cref="SkillsViewModel.CommunitySkillsUrlDisplay"/> para por qué esto vive como propiedad en
    /// vez de un literal "skillsclaude.org" en <c>SettingsView.axaml</c>.</summary>
    public string CommunitySkillsUrlDisplay { get; } = new Uri(SkillsViewModel.CommunitySkillsUrl).Host;

    [RelayCommand]
    private static void OpenCommunitySkills() => UrlOpener.Open(SkillsViewModel.CommunitySkillsUrl);

}
