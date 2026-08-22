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

    /// <summary>Single callback covering every sidebar-section visibility toggle (Ajustes › General ›
    /// Secciones de la barra lateral) — replaces what used to be one <c>Action&lt;bool&gt;</c> per section
    /// (<c>onCalendarPanelChanged</c>, <c>onSkillsPanelModeChanged</c>) now that there are eight of them
    /// instead of two; <see cref="MainWindowViewModel.OnSidebarSectionChanged"/> switches on the
    /// <see cref="AppPage"/> to apply whatever page-specific side effect that section needs.</summary>
    private readonly Action<AppPage, bool> _onSidebarSectionChanged;

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

    /// <summary>Ajustes › Dashboard (propuesta 27): one row per configurable dashboard section, ordered and
    /// filtered by the per-vault settings.</summary>
    public ObservableCollection<DashboardSectionConfigViewModel> DashboardSections { get; } = [];

    /// <summary>Default order of dashboard sections, used when <see cref="Models.VaultSettings.DashboardSectionOrder"/>
    /// is empty or when a reset is requested.</summary>
    private static readonly string[] DefaultDashboardSectionOrder =
    [
        "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits"
    ];

    /// <summary>Icon shown per dashboard section in the Ajustes › Dashboard list and its live preview —
    /// the exact same glyph <see cref="Views.DashboardView"/> renders in that section's own header, so
    /// the two surfaces read as one system rather than two independent icon choices.</summary>
    private static readonly Dictionary<string, MaterialIconKind> DashboardSectionIcons = new()
    {
        ["QuickAccess"] = MaterialIconKind.ViewGridOutline,
        ["Today"] = MaterialIconKind.CalendarCheckOutline,
        ["Vault"] = MaterialIconKind.Harddisk,
        ["Activity"] = MaterialIconKind.BellRingOutline,
        ["Sessions"] = MaterialIconKind.SitemapOutline,
        ["BoardTasks"] = MaterialIconKind.ViewColumnOutline,
        ["RecentNotes"] = MaterialIconKind.NoteTextOutline,
        ["Habits"] = MaterialIconKind.ClipboardCheckOutline
    };

    /// <summary>Total number of configurable dashboard sections — the denominator in
    /// <see cref="DashboardVisibilitySummary"/> ("N of 7 visible").</summary>
    private static int DashboardSectionCount => DefaultDashboardSectionOrder.Length;

    /// <summary>"N of 7 visible" summary shown above the section list — recomputed by
    /// <see cref="RefreshDashboardSummary"/> after every build/reorder/toggle instead of a computed
    /// property, since <see cref="DashboardSections"/> mutating in place doesn't raise property-changed
    /// for anything reading its aggregate count.</summary>
    [ObservableProperty]
    private string _dashboardVisibilitySummary = string.Empty;

    /// <summary>One of <see cref="Services.DashboardLayoutEngine.AllStyles"/> — which named arrangement
    /// (Clásica, Pirámide invertida, …) the Dashboard and this page's own preview pour the visible
    /// sections into. See <see cref="SelectDashboardLayoutStyle"/>.</summary>
    [ObservableProperty]
    private string _dashboardLayoutStyle = Services.DashboardLayoutEngine.Classic;

    /// <summary>Ajustes › Dashboard › Organización: one selectable card per layout template, rebuilt by
    /// <see cref="BuildDashboardLayoutOptions"/> — same "just rebuild it, it's cheap" approach as
    /// <see cref="BuildDashboardSections"/> rather than a separate relabel-only path.</summary>
    public ObservableCollection<DashboardLayoutOptionViewModel> DashboardLayoutOptions { get; } = [];

    /// <summary>Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md), one card per
    /// <c>ShortcutDefinition.CategoryKey</c> — built once in the constructor from
    /// <see cref="Services.ShortcutCatalog.All"/> and only re-labeled (not rebuilt) afterwards, same
    /// pattern as <see cref="Sections"/>.</summary>
    public ObservableCollection<ShortcutCategoryViewModel> ShortcutCategories { get; }

    /// <summary>Ajustes › Apariencia › Tema personalizado — un tema importado por fila, rebuilt (not just
    /// re-labeled, unlike <see cref="ShortcutCategories"/>) by <see cref="RefreshCustomThemes"/> after every
    /// import/activate/delete so <see cref="CustomThemeRowViewModel.IsActive"/> stays correct.</summary>
    public ObservableCollection<CustomThemeRowViewModel> CustomThemes { get; }

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

    [ObservableProperty]
    private string _vaultPath;

    /// <summary>Tamaño total en disco del vault activo, ya formateado ("128 MB") — reutiliza
    /// <see cref="VaultService.GetVaultSizeBytes"/> (ya usado por el Dashboard), solo que aquí se muestra
    /// junto a la ruta del vault en la "zona de peligro" (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md
    /// §2). Se recalcula al construir y cada vez que cambia de carpeta.</summary>
    [ObservableProperty]
    private string _vaultSizeLabel = string.Empty;

    /// <summary>True mientras el diálogo de confirmación de "Restablecer ajustes a valores por defecto"
    /// está abierto — mismo patrón request/cancel/confirm que <c>TrashViewModel.PendingEmptyTarget</c>.</summary>
    [ObservableProperty]
    private bool _pendingResetSettings;

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

    /// <summary>"Backup automático" (Ajustes › General, zona de peligro, Fase 1 de
    /// docs/analisis-proyecto/roadmap.md) — complemento opcional al backup manual: crea uno nuevo cada
    /// <see cref="AppSettings.AutoBackupIntervalDays"/> días y conserva los últimos
    /// <see cref="AppSettings.AutoBackupRetentionCount"/> en <c>%AppData%/Enki/backups/</c>. Apagado por
    /// defecto.</summary>
    [ObservableProperty]
    private bool _autoBackupEnabled;

    /// <summary>Cada cuántos días se crea un backup automático (Fase 10, docs/analisis-proyecto/roadmap.md
    /// — antes solo el interruptor de <see cref="AutoBackupEnabled"/> tenía control en la UI, el intervalo
    /// y la retención quedaban fijos en su valor por defecto para todo el mundo). El servicio
    /// (<c>VaultBackupService.RunScheduledBackupIfDue</c>) ya acota por debajo a 1 día
    /// independientemente de este campo; el máximo de 30 del selector de la UI es solo para mantener el
    /// control usable, no una validación adicional.</summary>
    [ObservableProperty]
    private int _autoBackupIntervalDays;

    /// <summary>Cuántos backups automáticos se conservan antes de rotar los más antiguos (Fase 10, ver
    /// <see cref="AutoBackupIntervalDays"/>). Mismo criterio de acotado por la UI, no por el servicio.</summary>
    [ObservableProperty]
    private int _autoBackupRetentionCount;

    /// <summary>Página de inicio (Ajustes › General) — con qué página arranca la app justo después de
    /// cargar el vault. Reutiliza <c>AppPage</c> (el mismo enum de la navegación principal) en vez de uno
    /// propio, igual que <see cref="CalendarDefaultView"/> reutiliza <c>CalendarViewMode</c>.</summary>
    [ObservableProperty]
    private AppPage _startupPage;

    [ObservableProperty]
    private bool _isDarkTheme;

    /// <summary>Whether an imported custom theme (Ajustes › Apariencia › Tema personalizado) is currently
    /// active — mutually exclusive with <see cref="IsDarkTheme"/>: the view disables that toggle while this
    /// is true, since a custom theme's colors override the built-in Dark/Light palette regardless of which
    /// one is "requested" underneath.</summary>
    [ObservableProperty]
    private bool _isCustomThemeActive;

    [ObservableProperty]
    private bool _isEditingTheme;

    /// <summary>Current in-app theme editor, or <c>null</c> when the theme list is shown.</summary>
    [ObservableProperty]
    private ThemeEditorViewModel? _themeEditor;

    [ObservableProperty]
    private double _editorFontSize;

    public string EditorFontSizeLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Editor_FontSize_Unit"], (int)EditorFontSize);

    [ObservableProperty]
    private double _editorColumnWidth;

    public string EditorColumnWidthLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Editor_ColumnWidth_Unit"], (int)EditorColumnWidth);

    [ObservableProperty]
    private bool _isColumnLeftAligned;

    [ObservableProperty]
    private bool _editorShowScrollbar;

    /// <summary>Tamaño de tabulación (docs/ajustes-docs/04-editor-y-notificaciones.md §1) — cuántos
    /// espacios inserta una pulsación de Tab en el editor de notas.</summary>
    [ObservableProperty]
    private double _editorTabSize;

    public string EditorTabSizeLabel => ((int)EditorTabSize).ToString(CultureInfo.CurrentCulture);

    [ObservableProperty]
    private bool _editorLineNumbers;

    [ObservableProperty]
    private bool _editorWordWrap;

    [ObservableProperty]
    private bool _editorSidePanelVisible;

    [ObservableProperty]
    private string _interfaceFontFamily;

    [ObservableProperty]
    private string _editorFontFamily;

    [ObservableProperty]
    private bool _useCustomTitleBar;

    [ObservableProperty]
    private bool _dashboardShowScrollbar;

    /// <summary>Zoom de interfaz (docs/funcionalidad-zoom-interfaz.md), 50–200 en pasos de 10, 100 por
    /// defecto — persistido por vault, no globalmente (a diferencia de todo lo demás en esta clase),
    /// así que se lee/escribe contra <see cref="_vaultSettingsService"/> en vez de
    /// <see cref="_settingsService"/>. Ver <see cref="OnUiZoomPercentChanged"/>.</summary>
    [ObservableProperty]
    private int _uiZoomPercent;

    public string UiZoomPercentLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Appearance_Zoom_PercentUnit"], UiZoomPercent);

    private const int UiZoomMinPercent = 50;
    private const int UiZoomMaxPercent = 200;
    private const int UiZoomStepPercent = 10;

    /// <summary>Relleno de la barra visual de zoom en Ajustes, como ancho "Star" (proporción del ancho
    /// disponible, no píxeles fijos) para que la barra ocupe todo el ancho de la tarjeta en vez de un
    /// tamaño fijo — recalculado en <see cref="OnUiZoomPercentChanged"/> vía <c>OnPropertyChanged</c>
    /// porque no es un valor propio, se deriva de <see cref="UiZoomPercent"/>.</summary>
    public GridLength UiZoomBarFillStar => new(UiZoomPercent - UiZoomMinPercent, GridUnitType.Star);

    /// <summary>Complemento de <see cref="UiZoomBarFillStar"/> — juntas sus dos columnas "Star" suman
    /// siempre el rango completo 50–200, así que la primera representa la fracción rellena de la barra.</summary>
    public GridLength UiZoomBarRemainderStar => new(UiZoomMaxPercent - UiZoomPercent, GridUnitType.Star);

    [ObservableProperty]
    private bool _calendarShowSidebarScrollbar;

    // ===================== Calendario (docs/calendar-feature/09-configuracion-y-ajustes.md §1) =====================

    [ObservableProperty]
    private CalendarViewMode _calendarDefaultView;

    [ObservableProperty]
    private bool _calendarWeekStartsOnMonday;

    [ObservableProperty]
    private bool _calendarShowWeekends;

    [ObservableProperty]
    private double _calendarWorkingHoursStart;

    [ObservableProperty]
    private double _calendarWorkingHoursEnd;

    [ObservableProperty]
    private bool _calendarTimeFormat24h;

    [ObservableProperty]
    private bool _calendarPanelEnabled;

    [ObservableProperty]
    private bool _skillsPanelEnabled;

    // ===================== Secciones de la barra lateral (Ajustes › General) =====================

    [ObservableProperty]
    private bool _dashboardPanelEnabled;

    [ObservableProperty]
    private bool _kanbanPanelEnabled;

    [ObservableProperty]
    private bool _notesPanelEnabled;

    [ObservableProperty]
    private bool _filesPanelEnabled;

    [ObservableProperty]
    private bool _sessionsPanelEnabled;

    [ObservableProperty]
    private bool _trashPanelEnabled;

    [ObservableProperty]
    private bool _habitsPanelEnabled;

    [ObservableProperty]
    private bool _statisticsPanelEnabled;

    [ObservableProperty]
    private bool _remindersSidebarEnabled;

    [ObservableProperty]
    private bool _notificationsSidebarEnabled;

    [ObservableProperty]
    private bool _polarShowTaskTags;

    [ObservableProperty]
    private bool _notificationsPanelEnabled;

    [ObservableProperty]
    private bool _remindersPanelEnabled;

    [ObservableProperty]
    private bool _showDesktopToasts;

    [ObservableProperty]
    private bool _muteInfoNotifications;

    [ObservableProperty]
    private bool _muteSuccessNotifications;

    [ObservableProperty]
    private bool _muteWarningNotifications;

    [ObservableProperty]
    private bool _muteErrorNotifications;

    [ObservableProperty]
    private double _reminderSoonWindowDays;

    public string ReminderSoonWindowDaysLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Notifications_Reminders_SoonWindow_Unit"], (int)ReminderSoonWindowDays);

    // ===================== Horario "no molestar" y sonido (docs/ajustes-docs/04-editor-y-notificaciones.md §2) =====================

    [ObservableProperty]
    private bool _doNotDisturbEnabled;

    [ObservableProperty]
    private string _doNotDisturbStart = "22:00";

    [ObservableProperty]
    private string _doNotDisturbEnd = "08:00";

    [ObservableProperty]
    private bool _notificationSoundEnabled;

    /// <summary>0–100 (Fase 5 del roadmap, gap 11) — ver <c>AppSettings.NotificationSoundVolume</c> y
    /// <c>NotificationSoundPlayer.Play</c> para qué backends de reproducción respetan realmente la escala
    /// vs. solo el "silencio en 0".</summary>
    [ObservableProperty]
    private double _notificationSoundVolume;

    public string NotificationSoundVolumeLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Notifications_Sound_Volume_Unit"], (int)NotificationSoundVolume);

    // ===================== Privacidad › vínculo con Polar =====================

    public bool IsSupabaseConfigured => _polarAuthService.IsConfigured;

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private string? _polarLinkedEmail;

    /// <summary>"Última sincronización" en Ajustes › Cuenta (docs/ajustes-docs/03-cuenta-y-sincronizacion-polar.md) —
    /// el dato ya existía en <see cref="PolarSyncService.LastSyncedUtc"/>, solo faltaba mostrarlo aquí.
    /// Mismo formato ("dd MMM, HH:mm" / "Nunca") que <c>PolarTaskManagementViewModel.RefreshSyncStatus</c>,
    /// refrescado al construir y cada vez que <see cref="PolarSyncService.SyncCompleted"/> se dispara.</summary>
    [ObservableProperty]
    private string _lastSyncedLabel = LocalizationService.Instance["Common_Never"];

    [ObservableProperty]
    private bool _isSyncingNow;

    [ObservableProperty]
    private string? _syncError;

    /// <summary>Composed "Couldn't sync: {0}" label — same reasoning as the equivalent properties in
    /// <c>RemindersViewModel</c>/<c>PolarTaskManagementViewModel</c>: kept out of XAML so the word order can
    /// be adapted per locale instead of being fixed by a <c>StringFormat</c>. Ajustes › Cuenta previously
    /// showed no sync error at all (10.2 §8) — only "Last synced", with no way to tell a stale timestamp
    /// apart from a genuinely broken sync.</summary>
    public string? SyncErrorLabel => SyncError is { } error
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Account_SyncErrorLabel"], error)
        : null;

    partial void OnSyncErrorChanged(string? value) => OnPropertyChanged(nameof(SyncErrorLabel));

    /// <summary>Non-null only while <see cref="PolarSyncService.Status"/> is
    /// <see cref="PolarSyncStatus.Offline"/> and a retry is actually scheduled — "Retrying in 2 minutes".</summary>
    [ObservableProperty]
    private string? _nextRetryLabel;

    /// <summary>True right after this view's own "Sync now" click was discarded because another sync (the
    /// background timer, the realtime debounce, or another view's own manual sync) already held the gate —
    /// distinct from a completed cycle that simply changed nothing (Fase 9, docs/analisis-proyecto/
    /// roadmap.md). Cleared the next time any sync cycle actually completes.</summary>
    [ObservableProperty]
    private bool _lastSyncNowWasSkipped;

    public string SyncNowButtonLabel => IsSyncingNow
        ? LocalizationService.Instance["Settings_Account_SyncingButton"]
        : LocalizationService.Instance["Settings_Account_SyncNowButton"];

    partial void OnIsSyncingNowChanged(bool value) => OnPropertyChanged(nameof(SyncNowButtonLabel));

    [ObservableProperty]
    private string _polarEmailInput = string.Empty;

    [ObservableProperty]
    private string _polarPasswordInput = string.Empty;

    [ObservableProperty]
    private bool _isLinkingPolar;

    [ObservableProperty]
    private string? _polarLinkError;

    public string LinkButtonLabel => IsLinkingPolar
        ? LocalizationService.Instance["Settings_Account_LinkingButton"]
        : LocalizationService.Instance["Settings_Account_LinkPolar_Title"];

    partial void OnIsLinkingPolarChanged(bool value) => OnPropertyChanged(nameof(LinkButtonLabel));

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
        Action onCalendarSettingsChanged,
        Action<bool> onDashboardShowScrollbarChanged,
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
        _onCalendarSettingsChanged = onCalendarSettingsChanged;
        _onDashboardShowScrollbarChanged = onDashboardShowScrollbarChanged;
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
        _editorSidePanelVisible = settingsService.Settings.EditorSidePanelVisible;
        _useCustomTitleBar = settingsService.Settings.UseCustomTitleBar;
        _dashboardShowScrollbar = settingsService.Settings.DashboardShowScrollbar;
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
        _selectedLanguage = SupportedLanguages.Find(settingsService.Settings.Language) ?? SupportedLanguages.All[0];

        EditorPreferences.FontSize = _editorFontSize;
        EditorPreferences.ColumnWidth = _editorColumnWidth;
        EditorPreferences.ColumnPosition = settingsService.Settings.EditorColumnPosition;
        EditorPreferences.ShowScrollbar = _editorShowScrollbar;
        EditorPreferences.FontFamily = settingsService.Settings.EditorFontFamily;
        EditorPreferences.TabSize = settingsService.Settings.EditorTabSize;
        EditorPreferences.LineNumbers = _editorLineNumbers;
        EditorPreferences.WordWrap = _editorWordWrap;
        EditorPreferences.SidePanelVisible = _editorSidePanelVisible;
        EditorPreferences.ToolbarVisible = settingsService.Settings.EditorToolbarVisible;
        EditorPreferences.ToolbarTextStyleExpanded = settingsService.Settings.EditorToolbarTextStyleExpanded;
        EditorPreferences.ToolbarHeadingsExpanded = settingsService.Settings.EditorToolbarHeadingsExpanded;
        EditorPreferences.ToolbarListsExpanded = settingsService.Settings.EditorToolbarListsExpanded;
        EditorPreferences.ToolbarInsertExpanded = settingsService.Settings.EditorToolbarInsertExpanded;
        EditorPreferences.ToolbarCodeExpanded = settingsService.Settings.EditorToolbarCodeExpanded;

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
        RefreshCustomThemes();

        BuildDashboardLayoutOptions();
        BuildDashboardSections();

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

    partial void OnIsDarkThemeChanged(bool value)
    {
        _settingsService.Settings.Theme = value ? "Dark" : "Light";
        _settingsService.Save();
        if (Avalonia.Application.Current is not null && !_isCustomThemeActive)
        {
            Avalonia.Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    /// <summary>Entry point for the "Toggle theme" command-palette action (propuesta 25,
    /// docs/propuesta-nuevas-funcionalidades.md) — the theme switch in Ajustes flips <see cref="IsDarkTheme"/>
    /// directly from its own toggle control, so this is the only place that needed a dedicated command.</summary>
    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    /// <summary>Only ever changed from <see cref="SetStartupPage"/> below (segmented control, no live
    /// callback needed — a página de inicio solo importa la próxima vez que arranque la app).</summary>
    partial void OnStartupPageChanged(AppPage value)
    {
        _settingsService.Settings.StartupPage = value.ToString();
        _settingsService.Save();
    }

    [RelayCommand]
    private void SetStartupPage(AppPage page) => StartupPage = page;

    partial void OnEditorFontSizeChanged(double value)
    {
        _settingsService.Settings.EditorFontSize = value;
        DebouncedSave();
        EditorPreferences.FontSize = value;
        OnPropertyChanged(nameof(EditorFontSizeLabel));
    }

    partial void OnEditorColumnWidthChanged(double value)
    {
        _settingsService.Settings.EditorColumnWidth = value;
        DebouncedSave();
        EditorPreferences.ColumnWidth = value;
        OnPropertyChanged(nameof(EditorColumnWidthLabel));
    }

    partial void OnIsColumnLeftAlignedChanged(bool value)
    {
        var position = value ? "Left" : "Center";
        _settingsService.Settings.EditorColumnPosition = position;
        _settingsService.Save();
        EditorPreferences.ColumnPosition = position;
    }

    partial void OnEditorShowScrollbarChanged(bool value)
    {
        _settingsService.Settings.EditorShowScrollbar = value;
        _settingsService.Save();
        EditorPreferences.ShowScrollbar = value;
    }

    partial void OnEditorTabSizeChanged(double value)
    {
        _settingsService.Settings.EditorTabSize = (int)value;
        DebouncedSave();
        EditorPreferences.TabSize = (int)value;
        OnPropertyChanged(nameof(EditorTabSizeLabel));
    }

    partial void OnEditorLineNumbersChanged(bool value)
    {
        _settingsService.Settings.EditorLineNumbers = value;
        _settingsService.Save();
        EditorPreferences.LineNumbers = value;
    }

    partial void OnEditorWordWrapChanged(bool value)
    {
        _settingsService.Settings.EditorWordWrap = value;
        _settingsService.Save();
        EditorPreferences.WordWrap = value;
    }

    partial void OnEditorSidePanelVisibleChanged(bool value)
    {
        _settingsService.Settings.EditorSidePanelVisible = value;
        _settingsService.Save();
        EditorPreferences.SidePanelVisible = value;
    }

    partial void OnInterfaceFontFamilyChanged(string value)
    {
        var stored = FromLabel(value);
        _settingsService.Settings.InterfaceFontFamily = stored;
        _settingsService.Save();
        ApplyInterfaceFont(stored);
    }

    partial void OnEditorFontFamilyChanged(string value)
    {
        var stored = FromLabel(value);
        _settingsService.Settings.EditorFontFamily = stored;
        _settingsService.Save();
        EditorPreferences.FontFamily = stored;
    }

    partial void OnUseCustomTitleBarChanged(bool value)
    {
        _settingsService.Settings.UseCustomTitleBar = value;
        _settingsService.Save();
        _onTitleBarModeChanged(value);
    }

    partial void OnDashboardShowScrollbarChanged(bool value)
    {
        _settingsService.Settings.DashboardShowScrollbar = value;
        _settingsService.Save();
        _onDashboardShowScrollbarChanged(value);
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

    partial void OnAutoBackupEnabledChanged(bool value)
    {
        _settingsService.Settings.AutoBackupEnabled = value;
        _settingsService.Save();
    }

    partial void OnAutoBackupIntervalDaysChanged(int value)
    {
        _settingsService.Settings.AutoBackupIntervalDays = value;
        _settingsService.Save();
    }

    partial void OnAutoBackupRetentionCountChanged(int value)
    {
        _settingsService.Settings.AutoBackupRetentionCount = value;
        _settingsService.Save();
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

    /// <summary>Slider "Zoom de interfaz" en Ajustes › Apariencia, además del punto de entrada
    /// compartido por los atajos de teclado y Ctrl+rueda (<c>MainWindowViewModel.AdjustUiZoom</c>, que
    /// escribe aquí mismo en vez de duplicar guardado/debounce — ver docs/funcionalidad-zoom-interfaz.md
    /// §9 punto 5). A diferencia del resto de esta clase, persiste contra <see cref="_vaultSettingsService"/>
    /// (por-vault) en vez de <see cref="_settingsService"/> (global).</summary>
    partial void OnUiZoomPercentChanged(int value)
    {
        _vaultSettingsService.Settings.UiZoomPercent = value;
        OnPropertyChanged(nameof(UiZoomBarFillStar));
        OnPropertyChanged(nameof(UiZoomBarRemainderStar));
        OnPropertyChanged(nameof(UiZoomPercentLabel));
        DebouncedVaultSave();
        _onUiZoomChanged(value);
    }

    /// <summary>Botones "-"/"+" que sustituyen al slider arrastrable en Ajustes › Apariencia (mismo paso
    /// que los atajos de teclado, ver <c>MainWindowViewModel.UiZoomStepPercent</c>).</summary>
    [RelayCommand]
    private void ZoomIn() => UiZoomPercent = Math.Clamp(UiZoomPercent + UiZoomStepPercent, UiZoomMinPercent, UiZoomMaxPercent);

    [RelayCommand]
    private void ZoomOut() => UiZoomPercent = Math.Clamp(UiZoomPercent - UiZoomStepPercent, UiZoomMinPercent, UiZoomMaxPercent);

    partial void OnCalendarShowSidebarScrollbarChanged(bool value)
    {
        _settingsService.Settings.CalendarShowSidebarScrollbar = value;
        _settingsService.Save();
        _onCalendarShowSidebarScrollbarChanged(value);
    }

    /// <summary>Only ever changed from <see cref="SetCalendarDefaultView"/> below (a segmented control,
    /// not a two-way toggle) — doesn't call <see cref="_onCalendarSettingsChanged"/> since it only affects
    /// which view the Calendario page opens on next, never anything about an already-open page.</summary>
    partial void OnCalendarDefaultViewChanged(CalendarViewMode value)
    {
        _settingsService.Settings.CalendarDefaultView = value.ToString();
        _settingsService.Save();
    }

    [RelayCommand]
    private void SetCalendarDefaultView(CalendarViewMode mode) => CalendarDefaultView = mode;

    partial void OnCalendarWeekStartsOnMondayChanged(bool value)
    {
        _settingsService.Settings.CalendarWeekStartsOnMonday = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarShowWeekendsChanged(bool value)
    {
        _settingsService.Settings.CalendarShowWeekends = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    /// <summary>Un horario laboral invertido o vacío (inicio ≥ fin) dejaría al Calendario sin franja que
    /// resaltar, así que en vez de guardarlo tal cual, el otro extremo se adelanta/retrasa lo justo para
    /// mantener <c>fin &gt; inicio</c> — mismo criterio de "no permitir un estado absurdo" que ya aplica
    /// <c>KanbanCardEditorViewModel</c> a sus propios campos. El extremo ajustado dispara su propio
    /// <c>On*Changed</c>, que ya persiste y notifica —
    /// por eso esta rama sale con <c>return</c> sin duplicar el <c>Save()</c>.</summary>
    partial void OnCalendarWorkingHoursStartChanged(double value)
    {
        _settingsService.Settings.CalendarWorkingHoursStart = (int)value;
        if (value >= CalendarWorkingHoursEnd)
        {
            CalendarWorkingHoursEnd = Math.Min(24, value + 1);
            return;
        }
        DebouncedSave();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarWorkingHoursEndChanged(double value)
    {
        _settingsService.Settings.CalendarWorkingHoursEnd = (int)value;
        if (value <= CalendarWorkingHoursStart)
        {
            CalendarWorkingHoursStart = Math.Max(0, value - 1);
            return;
        }
        DebouncedSave();
        _onCalendarSettingsChanged();
    }

    partial void OnCalendarTimeFormat24hChanged(bool value)
    {
        _settingsService.Settings.CalendarTimeFormat24h = value;
        _settingsService.Save();
        _onCalendarSettingsChanged();
    }

    /// <summary>Minimum number of sidebar sections (Ajustes › General › Secciones de la barra lateral) that
    /// must stay visible — an app with every entry hidden would still technically work (Ajustes itself is
    /// reachable from its own title-bar button, not a <c>NavItems</c> entry), but would look broken, so each
    /// <c>OnXPanelEnabledChanged</c> below refuses to turn off the last one still on.</summary>
    private const int MinEnabledSidebarSections = 1;

    private int CountEnabledSidebarSections()
    {
        var s = _settingsService.Settings;
        var count = 0;
        if (s.DashboardPanelEnabled) count++;
        if (s.CalendarPanelEnabled) count++;
        if (s.KanbanPanelEnabled) count++;
        if (s.NotesPanelEnabled) count++;
        if (s.FilesPanelEnabled) count++;
        if (s.SkillsPanelEnabled) count++;
        if (s.SessionsPanelEnabled) count++;
        if (s.TrashPanelEnabled) count++;
        if (s.HabitsPanelEnabled) count++;
        if (s.RemindersSidebarEnabled) count++;
        if (s.NotificationsSidebarEnabled) count++;
        if (s.StatisticsPanelEnabled) count++;
        return count;
    }

    /// <summary>Shared guard for every sidebar-section toggle below: refuses to turn the last remaining one
    /// off, reverting the just-set <see cref="ObservableProperty"/> back to <c>true</c> (which re-enters this
    /// same partial method with <paramref name="value"/> true, a no-op for this check) and surfacing why via
    /// a toast, instead of silently emptying the sidebar down to nothing but the toggle the user is looking
    /// at right now.</summary>
    private bool BlockIfLastEnabledSidebarSection(bool value, Action revert)
    {
        if (value || CountEnabledSidebarSections() > MinEnabledSidebarSections) return false;

        revert();
        _notificationService.Push(
            LocalizationService.Instance["Settings_General_Sidebar_CannotHideLast_Title"],
            LocalizationService.Instance["Settings_General_Sidebar_CannotHideLast_Message"],
            Models.NotificationSeverity.Warning);
        return true;
    }

    partial void OnDashboardPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => DashboardPanelEnabled = true)) return;
        _settingsService.Settings.DashboardPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Dashboard, value);
    }

    partial void OnCalendarPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => CalendarPanelEnabled = true)) return;
        _settingsService.Settings.CalendarPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Calendar, value);
    }

    partial void OnKanbanPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => KanbanPanelEnabled = true)) return;
        _settingsService.Settings.KanbanPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Kanban, value);
    }

    partial void OnNotesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => NotesPanelEnabled = true)) return;
        _settingsService.Settings.NotesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notes, value);
    }

    partial void OnFilesPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => FilesPanelEnabled = true)) return;
        _settingsService.Settings.FilesPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Files, value);
    }

    partial void OnSkillsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => SkillsPanelEnabled = true)) return;
        _settingsService.Settings.SkillsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Skills, value);
    }

    partial void OnSessionsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => SessionsPanelEnabled = true)) return;
        _settingsService.Settings.SessionsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Sessions, value);
    }

    partial void OnTrashPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => TrashPanelEnabled = true)) return;
        _settingsService.Settings.TrashPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Trash, value);
    }

    partial void OnHabitsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => HabitsPanelEnabled = true)) return;
        _settingsService.Settings.HabitsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Habits, value);
    }

    partial void OnStatisticsPanelEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => StatisticsPanelEnabled = true)) return;
        _settingsService.Settings.StatisticsPanelEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Statistics, value);
    }

    partial void OnRemindersSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => RemindersSidebarEnabled = true)) return;
        _settingsService.Settings.RemindersSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Reminders, value);
    }

    partial void OnNotificationsSidebarEnabledChanged(bool value)
    {
        if (BlockIfLastEnabledSidebarSection(value, () => NotificationsSidebarEnabled = true)) return;
        _settingsService.Settings.NotificationsSidebarEnabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Notifications, value);
    }

    partial void OnPolarShowTaskTagsChanged(bool value)
    {
        _settingsService.Settings.PolarShowTaskTags = value;
        _settingsService.Save();
        _onPolarShowTaskTagsChanged(value);
    }

    partial void OnNotificationsPanelEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationsPanelEnabled = value;
        _settingsService.Save();
        _onNotificationsPanelChanged(value);
    }

    partial void OnRemindersPanelEnabledChanged(bool value)
    {
        _settingsService.Settings.RemindersPanelEnabled = value;
        _settingsService.Save();
        _onRemindersPanelChanged(value);
    }

    partial void OnShowDesktopToastsChanged(bool value)
    {
        _settingsService.Settings.ShowDesktopToasts = value;
        _settingsService.Save();
    }

    partial void OnMuteInfoNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteInfoNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteSuccessNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteSuccessNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteWarningNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteWarningNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteErrorNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteErrorNotifications = value;
        _settingsService.Save();
    }

    partial void OnReminderSoonWindowDaysChanged(double value)
    {
        _settingsService.Settings.ReminderSoonWindowDays = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(ReminderSoonWindowDaysLabel));
    }

    partial void OnDoNotDisturbEnabledChanged(bool value)
    {
        _settingsService.Settings.DoNotDisturbEnabled = value;
        _settingsService.Save();
    }

    partial void OnDoNotDisturbStartChanged(string value)
    {
        _settingsService.Settings.DoNotDisturbStart = value;
        _settingsService.Save();
    }

    partial void OnDoNotDisturbEndChanged(string value)
    {
        _settingsService.Settings.DoNotDisturbEnd = value;
        _settingsService.Save();
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationSoundEnabled = value;
        _settingsService.Save();
    }

    partial void OnNotificationSoundVolumeChanged(double value)
    {
        _settingsService.Settings.NotificationSoundVolume = value;
        DebouncedSave();
        OnPropertyChanged(nameof(NotificationSoundVolumeLabel));
    }

    [RelayCommand]
    private void TestNotificationSound() => NotificationSoundPlayer.Play(NotificationSoundVolume);

    [RelayCommand]
    private void SelectSection(SettingsSection section) => SelectedSection = section;

    partial void OnSelectedLanguageChanged(SupportedLanguageInfo? value)
    {
        if (value is null) return;

        LocalizationService.Instance.SetLanguage(value.CultureCode);
        _settingsService.Settings.Language = value.CultureCode;
        _settingsService.Save();

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

        RefreshLastSyncedLabel();
        RebuildSections();
        BuildDashboardLayoutOptions();
        BuildDashboardSections();
        RebuildAvailableFonts();
        RebuildShortcutLabels();
        ThemeEditor?.RefreshLabels();
    }

    private void RebuildShortcutLabels()
    {
        foreach (var category in ShortcutCategories) category.RefreshLabel();
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

    /// <summary>Builds <see cref="DashboardSections"/> from the per-vault settings. Missing sections are
    /// appended in their default order and unknown/stale keys are ignored.</summary>
    private void BuildDashboardSections()
    {
        var settings = _vaultSettingsService.Settings;
        var order = settings.DashboardSectionOrder;
        var hidden = settings.DashboardHiddenSections ?? [];

        if (order is null || order.Count == 0)
            order = DefaultDashboardSectionOrder.ToList();

        var result = order
            .Concat(DefaultDashboardSectionOrder.Except(order))
            .Where(DefaultDashboardSectionOrder.Contains)
            .ToList();

        DashboardLayoutStyle = string.IsNullOrEmpty(settings.DashboardLayoutStyle) ? Services.DashboardLayoutEngine.Classic : settings.DashboardLayoutStyle;

        DashboardSections.Clear();
        foreach (var key in result)
        {
            DashboardSections.Add(new DashboardSectionConfigViewModel
            {
                Key = key,
                Title = LocalizationService.Instance[$"DashboardSection_{key}"],
                Description = LocalizationService.Instance[$"DashboardSection_{key}_Description"],
                Icon = DashboardSectionIcons.TryGetValue(key, out var icon) ? icon : MaterialIconKind.ViewDashboardOutline,
                IsVisible = !hidden.Contains(key)
            });
        }

        RefreshDashboardSummary();
    }

    /// <summary>Recomputes <see cref="DashboardVisibilitySummary"/>, every row's <c>Position</c>/
    /// <c>IsFirst</c>/<c>IsLast</c>, and the preview's grid geometry (<c>GridRow</c>/<c>GridColumn</c>/…,
    /// via <see cref="Services.DashboardLayoutEngine"/>) — called after every build, reorder, toggle,
    /// reset and layout-style change so the "N of 7 visible" header, the "01/02/…" index badges, the
    /// up/down button states and the live preview's arrangement never fall out of sync with
    /// <see cref="DashboardSections"/>.</summary>
    private void RefreshDashboardSummary()
    {
        for (var i = 0; i < DashboardSections.Count; i++)
        {
            DashboardSections[i].Position = i + 1;
            DashboardSections[i].IsFirst = i == 0;
            DashboardSections[i].IsLast = i == DashboardSections.Count - 1;
        }

        var visibleKeys = DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList();
        var slots = Services.DashboardLayoutEngine.Compute(visibleKeys, DashboardLayoutStyle);
        foreach (var section in DashboardSections)
        {
            if (!slots.TryGetValue(section.Key, out var slot)) continue;
            section.GridRow = slot.Row;
            section.GridColumn = slot.Column;
            section.GridRowSpan = slot.RowSpan;
            section.GridColumnSpan = slot.ColumnSpan;
        }

        var visible = visibleKeys.Count;
        DashboardVisibilitySummary = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Settings_Dashboard_VisibleCount"],
            visible, DashboardSectionCount);
    }

    /// <summary>Persists the current dashboard section order and visibility to the vault settings.</summary>
    private void SaveDashboardLayout()
    {
        _vaultSettingsService.Settings.DashboardSectionOrder = DashboardSections.Select(s => s.Key).ToList();
        _vaultSettingsService.Settings.DashboardHiddenSections = DashboardSections.Where(s => !s.IsVisible).Select(s => s.Key).ToList();
        _vaultSettingsService.Save();
        RefreshDashboardSummary();
    }

    /// <summary>Moves <paramref name="sourceKey"/> to the position of <paramref name="targetKey"/>,
    /// inserting it before the target. Used by the drag-and-drop reordering in Ajustes › Dashboard.</summary>
    public void MoveDashboardSection(string sourceKey, string targetKey)
    {
        var source = DashboardSections.FirstOrDefault(s => s.Key == sourceKey);
        var target = DashboardSections.FirstOrDefault(s => s.Key == targetKey);
        if (source is null || target is null || ReferenceEquals(source, target)) return;

        var oldIndex = DashboardSections.IndexOf(source);
        var newIndex = DashboardSections.IndexOf(target);
        DashboardSections.RemoveAt(oldIndex);
        if (newIndex > oldIndex) newIndex--;
        DashboardSections.Insert(newIndex, source);
        SaveDashboardLayout();
    }

    /// <summary>Swaps a section one slot earlier in the list — the keyboard/mouse-accessible counterpart
    /// to dragging the row's handle, exposed as its own button per row.</summary>
    [RelayCommand]
    private void MoveDashboardSectionUp(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        var index = DashboardSections.IndexOf(item);
        if (index <= 0) return;

        DashboardSections.Move(index, index - 1);
        SaveDashboardLayout();
    }

    /// <summary>Swaps a section one slot later in the list — see <see cref="MoveDashboardSectionUp"/>.</summary>
    [RelayCommand]
    private void MoveDashboardSectionDown(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        var index = DashboardSections.IndexOf(item);
        if (index < 0 || index >= DashboardSections.Count - 1) return;

        DashboardSections.Move(index, index + 1);
        SaveDashboardLayout();
    }

    /// <summary>Toggles visibility of a dashboard section, preventing the user from hiding the last
    /// visible one.</summary>
    [RelayCommand]
    private void ToggleDashboardSectionVisibility(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        if (item.IsVisible && DashboardSections.Count(s => s.IsVisible) <= 1)
        {
            _notificationService.Push(
                LocalizationService.Instance["Settings_Dashboard_CannotHideLast_Title"],
                LocalizationService.Instance["Settings_Dashboard_CannotHideLast_Message"],
                Models.NotificationSeverity.Warning);
            return;
        }

        item.IsVisible = !item.IsVisible;
        SaveDashboardLayout();
    }

    /// <summary>Restores the default section order with every section visible — the one-click escape
    /// hatch once dragging/hiding has left the layout in a state the user no longer wants.</summary>
    [RelayCommand]
    private void ResetDashboardLayout()
    {
        _vaultSettingsService.Settings.DashboardSectionOrder = DefaultDashboardSectionOrder.ToList();
        _vaultSettingsService.Settings.DashboardHiddenSections = [];
        _vaultSettingsService.Save();
        BuildDashboardSections();
    }

    /// <summary>Builds <see cref="DashboardLayoutOptions"/> — one card per
    /// <see cref="Services.DashboardLayoutEngine.AllStyles"/>, each with a thumbnail diagram computed
    /// against the full default section set so every option shows its true shape even if the vault
    /// currently has some sections hidden. <see cref="DashboardLayoutOptionViewModel.IsSelected"/> is set
    /// from the vault's current choice.</summary>
    private void BuildDashboardLayoutOptions()
    {
        var currentStyle = string.IsNullOrEmpty(_vaultSettingsService.Settings.DashboardLayoutStyle)
            ? Services.DashboardLayoutEngine.Classic
            : _vaultSettingsService.Settings.DashboardLayoutStyle;

        DashboardLayoutOptions.Clear();
        foreach (var style in Services.DashboardLayoutEngine.AllStyles)
        {
            var slots = Services.DashboardLayoutEngine.Compute(DefaultDashboardSectionOrder, style);
            var blocks = new ObservableCollection<DashboardLayoutBlockViewModel>(
                DefaultDashboardSectionOrder
                    .Where(slots.ContainsKey)
                    .Select(key =>
                    {
                        var slot = slots[key];
                        return new DashboardLayoutBlockViewModel
                        {
                            GridRow = slot.Row,
                            GridColumn = slot.Column,
                            GridRowSpan = slot.RowSpan,
                            GridColumnSpan = slot.ColumnSpan,
                            IsWide = Services.DashboardLayoutEngine.IsWideSection(key)
                        };
                    }));

            DashboardLayoutOptions.Add(new DashboardLayoutOptionViewModel
            {
                Key = style,
                Title = LocalizationService.Instance[$"DashboardLayout_{style}_Title"],
                Description = LocalizationService.Instance[$"DashboardLayout_{style}_Description"],
                Blocks = blocks,
                IsSelected = style == currentStyle
            });
        }
    }

    /// <summary>Switches which named arrangement (Clásica, Pirámide invertida, …) the Dashboard and this
    /// page's preview pour the visible sections into — the section order/visibility list is untouched, so
    /// switching layouts never requires reconfiguring which sections are shown.</summary>
    [RelayCommand]
    private void SelectDashboardLayoutStyle(string style)
    {
        if (style == DashboardLayoutStyle) return;

        DashboardLayoutStyle = style;
        _vaultSettingsService.Settings.DashboardLayoutStyle = style;
        _vaultSettingsService.Save();

        foreach (var option in DashboardLayoutOptions)
            option.IsSelected = option.Key == style;

        RefreshDashboardSummary();
    }

    private void RebuildAvailableFonts()
    {
        if (AvailableFonts is null || AvailableFonts.Count == 0) return;
        AvailableFonts[0] = DefaultFontLabel;
        InterfaceFontFamily = ToLabel(FromLabel(InterfaceFontFamily));
        EditorFontFamily = ToLabel(FromLabel(EditorFontFamily));
    }

    /// <summary>Signs in with an email/password that already exists in Supabase (docs/supabase-sync/08) —
    /// Enki never creates accounts, only links to one that's already set up. Success unlocks "Gestión de
    /// tareas" in Kanban and starts background sync (both wired off <see cref="PolarAuthService.LinkStateChanged"/>
    /// by <c>MainWindowViewModel</c>/<c>KanbanViewModel</c>).</summary>
    [RelayCommand]
    private async Task LinkPolar()
    {
        if (IsLinkingPolar) return;

        IsLinkingPolar = true;
        PolarLinkError = null;

        var (success, error) = await _polarAuthService.LinkAsync(PolarEmailInput, PolarPasswordInput);

        IsLinkingPolar = false;
        if (success)
        {
            PolarEmailInput = string.Empty;
            PolarPasswordInput = string.Empty;
        }
        else
        {
            PolarLinkError = error;
        }
    }

    [RelayCommand]
    private void UnlinkPolar()
    {
        _polarAuthService.Unlink();
        PolarEmailInput = string.Empty;
        PolarPasswordInput = string.Empty;
        PolarLinkError = null;
    }

    [RelayCommand]
    private async Task ChangeVaultFolder()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_ChangeVaultFolder_DialogTitle"],
            AllowMultiple = false
        });

        var pickedFolder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(pickedFolder)) return;

        // Same "picked folder is a container, not the vault" resolution as VaultSetupViewModel — see
        // VaultService.ResolveVaultFolder.
        var folder = VaultService.ResolveVaultFolder(pickedFolder);

        _vaultService.SetVaultPath(folder);
        VaultPath = folder;
        RefreshVaultSizeLabel();
        _onVaultChanged();
    }

    /// <summary>Host legible de <see cref="SkillsViewModel.CommunitySkillsUrl"/> — ver el comentario de
    /// <see cref="SkillsViewModel.CommunitySkillsUrlDisplay"/> para por qué esto vive como propiedad en
    /// vez de un literal "skillsclaude.org" en <c>SettingsView.axaml</c>.</summary>
    public string CommunitySkillsUrlDisplay { get; } = new Uri(SkillsViewModel.CommunitySkillsUrl).Host;

    [RelayCommand]
    private static void OpenCommunitySkills() => UrlOpener.Open(SkillsViewModel.CommunitySkillsUrl);

    // ===================== Zona de peligro (Ajustes › General) =====================
    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md — tamaño del vault, abrir su carpeta,
    // copia de seguridad y restablecer ajustes por defecto.

    private void RefreshVaultSizeLabel() => VaultSizeLabel = FileSizeFormatter.Format(_vaultService.GetVaultSizeBytes());

    [RelayCommand]
    private void OpenVaultFolder() => FolderOpener.Open(_vaultService.VaultPath);

    /// <summary>Empaqueta el vault activo + los ficheros de estado de <c>%AppData%/Enki</c> en un único
    /// .zip elegido por el usuario (mismo patrón <c>StorageProvider</c> que <see cref="ChangeVaultFolder"/>
    /// para llegar a un <see cref="TopLevel"/> desde este viewmodel). Notifica éxito o fallo por
    /// <see cref="NotificationService"/> en vez de fallar en silencio.</summary>
    [RelayCommand]
    private async Task CreateBackup()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var suggestedName = $"Enki-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}.zip";
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_BackupDialogTitle"],
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_ZipFileType"]) { Patterns = ["*.zip"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            var result = _vaultBackupService.CreateBackup(destination);
            if (result.IsComplete)
            {
                _notificationService.Push(
                    LocalizationService.Instance["Settings_General_DangerZone_BackupCreatedTitle"],
                    string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_General_DangerZone_BackupCreatedMessage"], destination),
                    Models.NotificationSeverity.Success);
            }
            else
            {
                // Fase 1 (docs/analisis-proyecto/roadmap.md): antes un fichero bloqueado/ilegible se omitía
                // del .zip en silencio — el backup "tenía éxito" mostrando el mismo mensaje que uno completo.
                _notificationService.Push(
                    LocalizationService.Instance["Settings_General_DangerZone_BackupIncompleteTitle"],
                    Plural.Get("Settings_General_DangerZone_BackupIncompleteMessage", result.FailedEntries.Count,
                        result.FailedEntries.Count, destination),
                    Models.NotificationSeverity.Warning);
            }
        }
        // Specific, expected failure types get a fully-translated message (docs/agent-docs/
        // internacionalizacion-i18n.md §5.3) instead of leaking Exception.Message, which can come through
        // in the OS's own language rather than Enki's active one.
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(
                LocalizationService.Instance["Settings_General_DangerZone_BackupFailedTitle"],
                LocalizationService.Instance["Common_Error_AccessDenied"],
                Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(
                LocalizationService.Instance["Settings_General_DangerZone_BackupFailedTitle"],
                LocalizationService.Instance["Common_Error_IOFailure"],
                Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // Last-resort fallback for exception types we can't give a fully-translated message to — a
            // documented limitation (§5.3), not a bug: a platform exception's text is never guaranteed to
            // be in Enki's active language unless Enki intercepts it by type, as the cases above already
            // do for the common, expectable ones.
            _notificationService.Push(
                LocalizationService.Instance["Settings_General_DangerZone_BackupFailedTitle"],
                ex.Message,
                Models.NotificationSeverity.Error);
        }
    }

    /// <summary>Exporta <c>settings.json</c> a una ubicación elegida por el usuario — sin lógica de merge,
    /// útil para llevarse la configuración de un equipo a otro
    /// (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §1). A diferencia de una copia literal del
    /// fichero, el vínculo de sesión con Polar (<c>PolarAccessToken</c>/<c>PolarRefreshToken</c>) se redacta
    /// antes de escribir el fichero exportado — ver <see cref="SettingsService.BuildRedactedExportJson"/> —
    /// para que "Exportar ajustes" no se convierta en un vector de robo de sesión
    /// (docs/analisis-proyecto/roadmap.md, Fase 4). Reimportar el fichero exportado no restaura el vínculo
    /// con Polar; el usuario tiene que volver a iniciar sesión desde Ajustes › Cuenta.</summary>
    [RelayCommand]
    private async Task ExportSettings()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_ExportDialogTitle"],
            SuggestedFileName = "enki-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_JsonFileType"]) { Patterns = ["*.json"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            _settingsService.Save();
            System.IO.File.WriteAllText(destination, _settingsService.BuildRedactedExportJson());
            _notificationService.Push(
                LocalizationService.Instance["Settings_General_DangerZone_ExportedTitle"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_General_DangerZone_ExportedMessage"], destination),
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ExportFailedTitle"],
                LocalizationService.Instance["Common_Error_AccessDenied"], Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ExportFailedTitle"],
                LocalizationService.Instance["Common_Error_IOFailure"], Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // Last-resort fallback documented in §5.3 — see CreateBackup's catch chain above.
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ExportFailedTitle"], ex.Message, Models.NotificationSeverity.Error);
        }
    }

    /// <summary>Reemplaza <c>settings.json</c> por el fichero elegido, sin fusionar — mismo criterio simple
    /// que <see cref="ExportSettings"/>. Requiere reiniciar Enki para que se apliquen, igual que
    /// <see cref="ConfirmResetSettings"/>.</summary>
    [RelayCommand]
    private async Task ImportSettings()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_DangerZone_ImportDialogTitle"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LocalizationService.Instance["Settings_General_DangerZone_JsonFileType"]) { Patterns = ["*.json"] }]
        });

        var source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(source)) return;

        try
        {
            System.IO.File.Copy(source, _settingsService.SettingsFilePath, overwrite: true);
            _notificationService.Push(
                LocalizationService.Instance["Settings_General_DangerZone_ImportedTitle"],
                LocalizationService.Instance["Settings_General_DangerZone_ImportedMessage"],
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ImportFailedTitle"],
                LocalizationService.Instance["Common_Error_AccessDenied"], Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ImportFailedTitle"],
                LocalizationService.Instance["Common_Error_IOFailure"], Models.NotificationSeverity.Error);
        }
        catch (Exception ex)
        {
            // Last-resort fallback documented in §5.3 — see CreateBackup's catch chain above.
            _notificationService.Push(LocalizationService.Instance["Settings_General_DangerZone_ImportFailedTitle"], ex.Message, Models.NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private void RequestResetSettings() => PendingResetSettings = true;

    [RelayCommand]
    private void CancelResetSettings() => PendingResetSettings = false;

    /// <summary>Escribe <see cref="AppSettings"/> por defecto a disco (conservando la ruta del vault) y
    /// avisa que hace falta reiniciar Enki — no hay hoy ningún mecanismo de recarga en caliente para las
    /// ~50 propiedades ya cargadas en este viewmodel, así que refrescarlas en memoria una a una sería
    /// complejidad innecesaria para una acción rara.</summary>
    [RelayCommand]
    private void ConfirmResetSettings()
    {
        _settingsService.ResetToDefaults();
        PendingResetSettings = false;
        _notificationService.Push(
            LocalizationService.Instance["Settings_General_DangerZone_ResetDoneTitle"],
            LocalizationService.Instance["Settings_General_DangerZone_ResetDoneMessage"],
            Models.NotificationSeverity.Success);
    }

    // ===================== Cuenta › sincronización con Polar (Ajustes › Cuenta) =====================
    // docs/ajustes-docs/03-cuenta-y-sincronizacion-polar.md — el dato y el método ya existían en
    // PolarSyncService, solo faltaba exponerlos aquí.

    private void RefreshLastSyncedLabel()
    {
        LastSyncedLabel = _polarSyncService.LastSyncedUtc is { } utc
            ? RelativeTimeFormatter.FormatAbsoluteShort(utc)
            : LocalizationService.Instance["Common_Never"];
        SyncError = _polarSyncService.LastError;
        NextRetryLabel = _polarSyncService.Status == PolarSyncStatus.Offline
            && _polarSyncService.NextSyncAttemptUtc is { } next
                ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Sync_NextRetryLabel"], RelativeTimeFormatter.FormatUntil(next, DateTime.UtcNow))
                : null;
    }

    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsSyncingNow) return;

        IsSyncingNow = true;
        var outcome = await _polarSyncService.SyncNowAsync();
        IsSyncingNow = false;
        LastSyncNowWasSkipped = outcome == PolarSyncOutcome.SkippedAlreadyRunning;
        // RefreshLastSyncedLabel ya se dispara desde la suscripción a SyncCompleted del constructor.
    }

    // ===================== Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) =====================
    // La captura del gesto en sí (escuchar el próximo KeyDown) vive en SettingsView.axaml.cs, ya que un
    // RelayCommand no puede recibir un KeyEventArgs de forma natural — esta sección solo valida, persiste
    // y refresca el estado una vez que el code-behind ya tiene un KeyGesture concreto.

    /// <summary>Marca <paramref name="row"/> como la única fila en modo captura — llamado por
    /// <c>SettingsView.axaml.cs</c> al hacer clic en el campo de gesto de un atajo.</summary>
    public void BeginCapture(ShortcutRowViewModel row)
    {
        foreach (var category in ShortcutCategories)
            foreach (var item in category.Items)
                item.IsCapturing = ReferenceEquals(item, row);

        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
        row.PendingGesture = null;
    }

    /// <summary>Sale del modo captura sin cambiar nada — Escape, perder el foco, o cancelar un conflicto
    /// pendiente. Instance method (not static) so <c>SettingsView.axaml.cs</c> can call it the same way as
    /// every other row-scoped method on this viewmodel, even though it only touches the row itself.</summary>
#pragma warning disable CA1822
    public void CancelCapture(ShortcutRowViewModel row)
    {
        row.IsCapturing = false;
        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
        row.PendingGesture = null;
    }
#pragma warning restore CA1822

    /// <summary>A gesture without Ctrl/Alt/Meta (a bare letter, or Shift+letter) is rejected instead of
    /// applied — see <c>SettingsView.axaml.cs</c>'s capture handler for the one exception (function keys).
    /// Leaves the row in capture mode so the user can just try another combination.</summary>
#pragma warning disable CA1822
    public void RejectCapturedGesture(ShortcutRowViewModel row)
    {
        row.CaptureHintMessage = LocalizationService.Instance["Settings_Shortcuts_RequiresModifierWarning"];
    }
#pragma warning restore CA1822

    /// <summary>Aplica el gesto recién capturado — si colisiona con otro atajo, se detiene en un estado de
    /// aviso (propuesta 29 §"Detección de conflictos") en vez de guardar directamente; el usuario decide
    /// entre <see cref="ConfirmConflictingShortcut"/> y <see cref="CancelConflictingShortcut"/>.</summary>
    public void ApplyCapturedGesture(ShortcutRowViewModel row, KeyGesture gesture)
    {
        row.IsCapturing = false;
        row.CaptureHintMessage = null;

        var conflictId = ShortcutRegistry.FindConflict(row.Id, gesture);
        if (conflictId is null)
        {
            CommitGesture(row, gesture);
            return;
        }

        var conflictLabelKey = ShortcutCatalog.All.First(d => d.Id == conflictId).LabelKey;
        row.PendingGesture = gesture;
        row.ConflictMessage = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Settings_Shortcuts_ConflictMessage"],
            LocalizationService.Instance[conflictLabelKey]);
    }

    [RelayCommand]
    private void ConfirmConflictingShortcut(ShortcutRowViewModel row)
    {
        if (row.PendingGesture is { } gesture) CommitGesture(row, gesture);
    }

#pragma warning disable CA1822
    [RelayCommand]
    private void CancelConflictingShortcut(ShortcutRowViewModel row)
    {
        row.PendingGesture = null;
        row.ConflictMessage = null;
    }
#pragma warning restore CA1822

    [RelayCommand]
    private void ResetShortcut(ShortcutRowViewModel row)
    {
        ShortcutRegistry.ResetToDefault(row.Id);
        ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        ShortcutRegistry.ResetAll();
        foreach (var category in ShortcutCategories)
            foreach (var row in category.Items)
                ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    private void CommitGesture(ShortcutRowViewModel row, KeyGesture gesture)
    {
        ShortcutRegistry.SetOverride(row.Id, gesture);
        ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    private static void ClearRowValidationState(ShortcutRowViewModel row)
    {
        row.PendingGesture = null;
        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
    }

    /// <summary>Escribe el snapshot de overrides a <see cref="Models.AppSettings.ShortcutOverrides"/> y lo persiste
    /// de inmediato — a diferencia del resto de Ajustes, remapear un atajo es una acción discreta y poco
    /// frecuente (no un arrastre de slider), así que no hace falta el debounce de <see cref="DebouncedSave"/>.</summary>
    private void PersistShortcutOverrides()
    {
        _settingsService.Settings.ShortcutOverrides = new Dictionary<string, string>(ShortcutRegistry.ExportOverrides());
        _settingsService.Save();
    }

    /// <summary>Refresca <see cref="ShortcutRowViewModel.GestureDisplay"/>/<see cref="ShortcutRowViewModel.IsCustomized"/>
    /// en todas las filas — suscrito a <see cref="Services.ShortcutRegistry.Changed"/> en el constructor,
    /// así que también cubre los cambios que ya aplicó este mismo viewmodel, no solo los externos.</summary>
    private void RefreshAllShortcutRows()
    {
        foreach (var category in ShortcutCategories)
            foreach (var row in category.Items)
            {
                row.GestureDisplay = ShortcutRegistry.GetGestureDisplay(row.Id);
                row.IsCustomized = ShortcutRegistry.IsCustomized(row.Id);
            }
    }

    // ===================== Temas personalizados (Ajustes › Apariencia) =====================
    // Editor visual + import/export CSS: el usuario puede crear, editar, duplicar, activar, eliminar y
    // exportar temas desde la propia app. El servicio mantiene los colores en archivos .css validados contra
    // ThemeCatalog; este viewmodel solo orquesta la UI y notifica el resultado.

    [RelayCommand]
    private void CreateTheme()
    {
        var theme = _customThemeService.Create(LocalizationService.Instance["Settings_Appearance_Theme_NewThemeName"]);
        OpenThemeEditor(theme, isActive: false);
    }

    [RelayCommand]
    private void EditTheme(CustomThemeRowViewModel row)
    {
        var theme = _customThemeService.Load(row.Id);
        if (theme is null) return;
        OpenThemeEditor(theme, isActive: row.IsActive);
    }

    [RelayCommand]
    private void DuplicateTheme(CustomThemeRowViewModel row)
    {
        var theme = _customThemeService.Load(row.Id);
        if (theme is null) return;

        var copy = theme.Clone();
        copy.Name = $"{theme.Name} {LocalizationService.Instance["Settings_Appearance_Theme_DuplicateSuffix"]}".Trim();
        _customThemeService.Save(copy);
        OpenThemeEditor(copy, isActive: false);
    }

    [RelayCommand]
    private void ActivateCustomTheme(CustomThemeRowViewModel row)
    {
        if (!_customThemeService.Activate(row.Id)) return;
        IsCustomThemeActive = true;
        RefreshCustomThemes();
    }

    [RelayCommand]
    private void DeleteCustomTheme(CustomThemeRowViewModel row)
    {
        _customThemeService.Delete(row.Id);
        IsCustomThemeActive = _customThemeService.ActiveThemeId is not null;
        RefreshCustomThemes();
    }

    /// <summary>"Usar tema del sistema" — vuelve al tema claro/oscuro incorporado
    /// (<see cref="IsDarkTheme"/>), sin borrar ningún tema importado.</summary>
    [RelayCommand]
    private void UseSystemTheme()
    {
        _customThemeService.Deactivate();
        IsCustomThemeActive = false;
        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        RefreshCustomThemes();
    }

    /// <summary>"Descargar plantilla CSS" — escribe la plantilla por defecto en la ubicación elegida por el
    /// usuario, lista para editar (solo los colores) y reimportar.</summary>
    [RelayCommand]
    private async Task DownloadThemeTemplate()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_Appearance_CustomTheme_TemplateSaveDialog_Title"],
            SuggestedFileName = "enki-theme-template.css",
            DefaultExtension = "css",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_Appearance_CustomTheme_CssFileType"]) { Patterns = ["*.css"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            CustomThemeService.ExportDefaultTemplate(destination);
            _notificationService.Push(
                LocalizationService.Instance["Notification_ThemeTemplateExported_Title"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_ThemeTemplateExported_Message"], destination),
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(LocalizationService.Instance["Notification_ThemeTemplateExportFailed_Title"],
                LocalizationService.Instance["Common_Error_AccessDenied"], Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(LocalizationService.Instance["Notification_ThemeTemplateExportFailed_Title"],
                LocalizationService.Instance["Common_Error_IOFailure"], Models.NotificationSeverity.Error);
        }
    }

    /// <summary>"Importar tema…" — valida el archivo elegido (todos los errores encontrados a la vez, no uno
    /// por intento) y lo añade a la lista sin activarlo automáticamente.</summary>
    [RelayCommand]
    private async Task ImportCustomTheme()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_Appearance_CustomTheme_ImportDialog_Title"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LocalizationService.Instance["Settings_Appearance_CustomTheme_CssFileType"]) { Patterns = ["*.css"] }]
        });

        var source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(source)) return;

        var result = _customThemeService.Import(source);
        if (!result.Success)
        {
            _notificationService.Push(
                LocalizationService.Instance["Notification_ThemeImportFailed_Title"],
                string.Join("\n", result.Errors),
                Models.NotificationSeverity.Error);
            return;
        }

        RefreshCustomThemes();

        var importedName = _customThemeService.Themes.First(t => t.Id == result.ThemeId).Name;
        _notificationService.Push(
            LocalizationService.Instance["Notification_ThemeImported_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_ThemeImported_Message"], importedName),
            Models.NotificationSeverity.Success);
    }

    private void CloseThemeEditor()
    {
        ThemeEditor = null;
        IsEditingTheme = false;
        RefreshCustomThemes();
    }

    private void OpenThemeEditor(CustomTheme theme, bool isActive)
    {
        ThemeEditor = new ThemeEditorViewModel(
            theme,
            _customThemeService,
            _notificationService,
            LocalizationService.Instance,
            isActive,
            () =>
            {
                IsCustomThemeActive = true;
                CloseThemeEditor();
            },
            () =>
            {
                IsCustomThemeActive = _customThemeService.ActiveThemeId is not null;
                CloseThemeEditor();
            },
            copy => OpenThemeEditor(copy, isActive: false));
        IsEditingTheme = true;
    }

    private void RefreshCustomThemes()
    {
        CustomThemes.Clear();
        var activeId = _customThemeService.ActiveThemeId;
        foreach (var info in _customThemeService.Themes)
            CustomThemes.Add(new CustomThemeRowViewModel(info.Id, info.Name, info.Id == activeId));
        OnPropertyChanged(nameof(HasCustomThemes));
    }

    /// <summary>Backs the empty-state text in Ajustes › Apariencia › Tema personalizado — manually notified
    /// from <see cref="RefreshCustomThemes"/> since <see cref="CustomThemes"/> itself is a plain
    /// <see cref="ObservableCollection{T}"/> (its own <c>CollectionChanged</c> doesn't raise
    /// <c>PropertyChanged</c> for a derived property like this one).</summary>
    public bool HasCustomThemes => CustomThemes.Count > 0;
}
