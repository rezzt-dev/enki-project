using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Apariencia: tema claro/oscuro, título de ventana personalizado, fuente de interfaz,
/// zoom de interfaz, logo del sidebar y el sistema completo de temas personalizados (crear/editar/duplicar/
/// activar/eliminar/importar/exportar) — split out of <c>SettingsViewModel.cs</c> to keep every "how the app looks" setting
/// in one file instead of interleaved with Editor/Notificaciones/Calendario. Still the very same
/// <see cref="SettingsViewModel"/> class — partial types share every field/property regardless of which
/// file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    public SidePanelLayoutViewModel SidePanels { get; }

    /// <summary>Ajustes › Apariencia › Tema personalizado — un tema importado por fila, rebuilt (not just
    /// re-labeled, unlike <see cref="ShortcutCategories"/>) by <see cref="RefreshCustomThemes"/> after every
    /// import/activate/delete so <see cref="CustomThemeRowViewModel.IsActive"/> stays correct.</summary>
    public ObservableCollection<CustomThemeRowViewModel> CustomThemes { get; }

    /// <summary>Ajustes › Apariencia › Temas predefinidos, sección "Oscuros" — compiled-in themes from
    /// <see cref="Services.BuiltInThemeCatalog"/>, rebuilt alongside <see cref="CustomThemes"/> by
    /// <see cref="RefreshCustomThemes"/> so their "Activo" pill stays correct too.</summary>
    public ObservableCollection<CustomThemeRowViewModel> BuiltInDarkThemes { get; }

    /// <summary>Ajustes › Apariencia › Temas predefinidos, sección "Claros" — see <see cref="BuiltInDarkThemes"/>.</summary>
    public ObservableCollection<CustomThemeRowViewModel> BuiltInLightThemes { get; }

    /// <summary>Whether the "Oscuros" built-in theme list is collapsed to reduce on-screen clutter —
    /// persisted to <see cref="Models.AppSettings.BuiltInDarkThemesCollapsed"/> so it survives an app
    /// restart.</summary>
    [ObservableProperty]
    private bool _isBuiltInDarkThemesCollapsed;

    /// <summary>See <see cref="IsBuiltInDarkThemesCollapsed"/>, same behavior for "Claros".</summary>
    [ObservableProperty]
    private bool _isBuiltInLightThemesCollapsed;

    [RelayCommand]
    private void ToggleBuiltInDarkThemesCollapsed() => IsBuiltInDarkThemesCollapsed = !IsBuiltInDarkThemesCollapsed;

    [RelayCommand]
    private void ToggleBuiltInLightThemesCollapsed() => IsBuiltInLightThemesCollapsed = !IsBuiltInLightThemesCollapsed;

    partial void OnIsBuiltInDarkThemesCollapsedChanged(bool value)
    {
        _settingsService.Settings.BuiltInDarkThemesCollapsed = value;
        _settingsService.Save();
    }

    partial void OnIsBuiltInLightThemesCollapsedChanged(bool value)
    {
        _settingsService.Settings.BuiltInLightThemesCollapsed = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private bool _useSubtleAnimations;

    partial void OnUseSubtleAnimationsChanged(bool value)
    {
        _settingsService.Settings.UseSubtleAnimations = value;
        _settingsService.Save();
        MotionService.Apply(value);
    }

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
    private string _interfaceFontFamily;

    [ObservableProperty]
    private bool _useCustomTitleBar;

    [ObservableProperty]
    private bool _showWindowControls;

    partial void OnShowWindowControlsChanged(bool value)
    {
        _settingsService.Settings.ShowWindowControls = value;
        _settingsService.Save();
    }

    /// <summary>Ajustes › Apariencia › Tablero Kanban — <c>true</c> apila las tarjetas en columnas lado a
    /// lado (disposición por defecto), <c>false</c> gira cada columna 90° en una franja horizontal. Bool en
    /// vez del enum <c>KanbanBoardOrientation</c> de <c>KanbanViewModel</c> porque el control es un segmento
    /// de dos botones, mismo patrón que <see cref="IsColumnLeftAligned"/>.</summary>
    [ObservableProperty]
    private bool _kanbanBoardVertical;

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
    /// directly from its own toggle control, so this is the only place that needed a dedicated command.
    /// Also deactivates an active custom theme first (análisis alfa-3 §4.5, Alta): before this, toggling
    /// while a custom theme was active silently flipped <see cref="IsDarkTheme"/> underneath it with no
    /// visible effect, since <see cref="OnIsDarkThemeChanged"/> only applies the built-in palette when no
    /// custom theme overrides it — leaving this, a keyboard-reachable action, as one of the few real escape
    /// hatches from an illegible custom theme.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        if (IsCustomThemeActive) UseSystemTheme();
        IsDarkTheme = !IsDarkTheme;
    }

    partial void OnInterfaceFontFamilyChanged(string value)
    {
        var stored = FromLabel(value);
        _settingsService.Settings.InterfaceFontFamily = stored;
        _settingsService.Save();
        ApplyInterfaceFont(stored);
    }

    partial void OnUseCustomTitleBarChanged(bool value)
    {
        _settingsService.Settings.UseCustomTitleBar = value;
        _settingsService.Save();
        _onTitleBarModeChanged(value);
    }

    [ObservableProperty]
    private bool _kanbanShowTagFilter;

    partial void OnKanbanShowTagFilterChanged(bool value)
    {
        _settingsService.Settings.KanbanShowTagFilter = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private bool _kanbanShowHighPriorityButton;

    partial void OnKanbanShowHighPriorityButtonChanged(bool value)
    {
        _settingsService.Settings.KanbanShowHighPriorityButton = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private bool _kanbanShowOverdueButton;

    partial void OnKanbanShowOverdueButtonChanged(bool value)
    {
        _settingsService.Settings.KanbanShowOverdueButton = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private bool _kanbanShowArchiveButton;

    partial void OnKanbanShowArchiveButtonChanged(bool value)
    {
        _settingsService.Settings.KanbanShowArchiveButton = value;
        _settingsService.Save();
    }

    [ObservableProperty]
    private bool _kanbanShowSortButton;

    partial void OnKanbanShowSortButtonChanged(bool value)
    {
        _settingsService.Settings.KanbanShowSortButton = value;
        _settingsService.Save();
    }

    partial void OnKanbanBoardVerticalChanged(bool value)
    {
        var orientation = value ? "Vertical" : "Horizontal";
        _settingsService.Settings.KanbanBoardOrientation = orientation;
        _settingsService.Save();
        _onKanbanBoardOrientationChanged(orientation);
    }

    // ===================== Logo del sidebar (Ajustes › Apariencia) =====================
    // El brand header de MainWindow.axaml puede llevar el icono de Enki (adaptativo al tema) o una imagen
    // del usuario. La imagen la guarda y optimiza BrandLogoService; aquí solo está la elección y el
    // diálogo. Ver docs/agent-docs/logo-personalizado.md.

    /// <summary>Segmento "Por defecto" / "Personalizada". Solo puede valer <c>true</c> mientras exista una
    /// imagen importada: la vista deshabilita "Personalizada" sin ella, el constructor lo arranca apagado
    /// si el ajuste guardado dice que sí pero el archivo ya no está (borrado desde fuera de Enki, unos
    /// ajustes editados a mano), y <see cref="OnUseCustomBrandLogoChanged"/> lo vuelve a apagar si aun así
    /// alguien lo enciende. El sidebar aplica la misma regla por su cuenta
    /// (<c>MainWindowViewModel.ApplyBrandLogo</c>), así que ninguno de los dos depende de que el otro
    /// corrija el ajuste en disco.</summary>
    [ObservableProperty]
    private bool _useCustomBrandLogo;

    /// <summary>Segmento "Cuadrada" / "Circular". La forma circular es la única excepción admitida al
    /// <c>CornerRadius="0"</c> del sistema de diseño, y solo llega a aplicarse al logo personalizado —
    /// el icono por defecto se dibuja siempre cuadrado (ver <see cref="Models.AppSettings.BrandLogoShape"/>).</summary>
    [ObservableProperty]
    private bool _isBrandLogoCircular;

    /// <summary>La imagen importada tal y como se verá en el sidebar — o <c>null</c> si todavía no hay
    /// ninguna. Se decodifica desde bytes en memoria (no desde la ruta) para no dejar el archivo abierto
    /// mientras Ajustes lo enseña, que es justo cuando el usuario puede reemplazarlo, y se reduce al
    /// tamaño del cuadro de 44 px con <see cref="BrandLogoImage"/>, igual que el sidebar.</summary>
    [ObservableProperty]
    private Bitmap? _brandLogoPreview;

    /// <summary>"1200 × 800 px · 1,4 MB" bajo la vista previa: lo que hay guardado, que es el archivo
    /// original tal cual. Sin traducir — son números, "px" y una unidad que ya localiza
    /// <see cref="FileSizeFormatter"/>.</summary>
    [ObservableProperty]
    private string? _brandLogoSizeLabel;

    /// <summary>Gobierna si el segmento "Personalizada" y el de forma están habilitados. Notificado a mano
    /// desde <see cref="RefreshBrandLogoPreview"/> porque se deriva del sistema de archivos, no de una
    /// propiedad observable.</summary>
    public bool HasCustomBrandLogo => _brandLogoService.HasLogo;

    /// <summary>Radio con el que se recorta la vista previa: la mitad de su lado (44 px) es un círculo
    /// exacto. Mismo criterio que <c>MainWindowViewModel.BrandLogoCornerRadius</c>, con su propio tamaño.</summary>
    public CornerRadius BrandLogoPreviewCornerRadius => new(IsBrandLogoCircular ? BrandLogoPreviewSize / 2 : 0);

    /// <summary>Una imagen que no sea cuadrada se enseña entera dentro del cuadro (<c>Uniform</c>), pero
    /// dentro de un círculo eso dejaría huecos arriba y abajo en vez de un logo redondo, así que ahí se
    /// recorta al llenarlo (<c>UniformToFill</c>) — el comportamiento de cualquier avatar. Misma regla que
    /// en el sidebar, para que la vista previa no mienta sobre lo que se va a ver.</summary>
    public Stretch BrandLogoStretch => IsBrandLogoCircular ? Stretch.UniformToFill : Stretch.Uniform;

    private const double BrandLogoPreviewSize = 44;

    partial void OnUseCustomBrandLogoChanged(bool value)
    {
        if (value && !HasCustomBrandLogo)
        {
            UseCustomBrandLogo = false;
            return;
        }

        _settingsService.Settings.UseCustomBrandLogo = value;
        _settingsService.Save();
    }

    partial void OnIsBrandLogoCircularChanged(bool value)
    {
        _settingsService.Settings.BrandLogoShape = value ? "Circle" : "Square";
        _settingsService.Save();
        OnPropertyChanged(nameof(BrandLogoPreviewCornerRadius));
        OnPropertyChanged(nameof(BrandLogoStretch));
    }

    /// <summary>"Elegir imagen…" — pide un archivo y lo deja como logo activo. Elegir una imagen implica
    /// quererla puesta, así que activa también el modo personalizado en vez de dejar al usuario delante de
    /// una vista previa que no se ve en ningún sitio.</summary>
    [RelayCommand]
    private async Task ChooseBrandLogo()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_Appearance_BrandLogo_PickerTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LocalizationService.Instance["Settings_Appearance_BrandLogo_ImageFileType"])
                {
                    Patterns = BrandLogoService.PickerPatterns
                }
            ]
        });

        var source = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(source)) return;

        ImportBrandLogo(source);
    }

    /// <summary>Todo lo que pasa con la imagen ya elegida — separado de <see cref="ChooseBrandLogo"/>, que
    /// es solo el diálogo del sistema, para que las pruebas de UI puedan recorrer el camino real (importar,
    /// refrescar la vista previa, activar el modo personalizado y notificar) sin un diálogo de archivos, que
    /// en un host headless no existe.</summary>
    internal void ImportBrandLogo(string sourcePath)
    {
        var result = _brandLogoService.Import(sourcePath);
        if (!result.Success)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_BrandLogoImportFailed_Title"),
                DescribeBrandLogoFailure(result.Failure),
                Models.NotificationSeverity.Error);
            return;
        }

        RefreshBrandLogoPreview();
        UseCustomBrandLogo = true;

        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_BrandLogoImported_Title"),
            LocalizedTextFormatter.Format("Notification_BrandLogoImported_Message",
                FileSizeFormatter.Format(result.Logo?.Bytes ?? 0)),
            Models.NotificationSeverity.Success);
    }

    /// <summary>"Quitar imagen" — borra el archivo y vuelve al icono de Enki. La forma elegida se conserva
    /// en los ajustes: si el usuario importa otra imagen después, la recupera tal cual la dejó.</summary>
    [RelayCommand]
    private void RemoveBrandLogo()
    {
        if (!_brandLogoService.Remove()) return;

        UseCustomBrandLogo = false;
        RefreshBrandLogoPreview();

        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_BrandLogoRemoved_Title"),
            LocalizedTextFormatter.Format("Notification_BrandLogoRemoved_Message"),
            Models.NotificationSeverity.Info);
    }

    private static LocalizedText DescribeBrandLogoFailure(BrandLogoImportFailure failure) => failure switch
    {
        BrandLogoImportFailure.FileNotFound => LocalizedTextFormatter.Format("Notification_BrandLogoImportFailed_NotFound"),
        BrandLogoImportFailure.TooLarge => LocalizedTextFormatter.Format("Notification_BrandLogoImportFailed_TooLarge",
            FileSizeFormatter.Format(BrandLogoService.MaxSourceBytes)),
        BrandLogoImportFailure.UnsupportedFormat => LocalizedTextFormatter.Format("Notification_BrandLogoImportFailed_Format"),
        _ => LocalizedTextFormatter.Format("Common_Error_IOFailure")
    };

    /// <summary>Relee del disco la imagen guardada y actualiza vista previa, tamaño y estado de los
    /// controles. Llamado al construir el viewmodel y después de cada importación/borrado.</summary>
    private void RefreshBrandLogoPreview()
    {
        // El <see cref="Bitmap"/> anterior no se libera a mano: el compositor de Avalonia todavía puede
        // tener referenciado el que acaba de salir de pantalla, y liberar por debajo lo que el hilo de
        // render está a punto de dibujar es un fallo mucho peor que dejar que lo recoja el GC — la copia
        // decodificada no pasa de 176 px, y esto solo corre al importar o quitar el logo.
        var info = _brandLogoService.Describe();
        // Un archivo que ya no se decodifica (borrado a mano, corrupción en disco) se trata como "no hay
        // logo" en vez de dejar Ajustes con una vista previa vacía y una etiqueta que habla de ella.
        BrandLogoPreview = BrandLogoImage.Decode(_brandLogoService.ReadLogoBytes(), BrandLogoImage.PreviewPixels);
        BrandLogoSizeLabel = BrandLogoPreview is null || info is null
            ? null
            : $"{info.PixelWidth} × {info.PixelHeight} px · {FileSizeFormatter.Format(info.Bytes)}";

        OnPropertyChanged(nameof(HasCustomBrandLogo));
        OnPropertyChanged(nameof(BrandLogoPreviewCornerRadius));
        OnPropertyChanged(nameof(BrandLogoStretch));
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
        if (row.IsBuiltIn) return; // defensa en profundidad: la vista ya oculta este botón para built-in
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
        if (row.IsBuiltIn) return; // defensa en profundidad: la vista ya oculta este botón para built-in
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
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_ThemeTemplateExported_Title"),
                LocalizedTextFormatter.Format("Notification_ThemeTemplateExported_Message", destination),
                Models.NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_ThemeTemplateExportFailed_Title"),
                LocalizedTextFormatter.Format("Common_Error_AccessDenied"),
                Models.NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_ThemeTemplateExportFailed_Title"),
                LocalizedTextFormatter.Format("Common_Error_IOFailure"),
                Models.NotificationSeverity.Error);
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
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_ThemeImportFailed_Title"),
                LocalizedTextFormatter.Join("\n", result.LocalizedErrors.ToArray()),
                Models.NotificationSeverity.Error);
            return;
        }

        RefreshCustomThemes();

        var importedName = _customThemeService.Themes.First(t => t.Id == result.ThemeId).Name;
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_ThemeImported_Title"),
            LocalizedTextFormatter.Format("Notification_ThemeImported_Message", importedName),
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
        var activeId = _customThemeService.ActiveThemeId;

        CustomThemes.Clear();
        foreach (var info in _customThemeService.Themes)
            CustomThemes.Add(new CustomThemeRowViewModel(info.Id, info.Name, info.Id == activeId));
        OnPropertyChanged(nameof(HasCustomThemes));

        BuiltInDarkThemes.Clear();
        BuiltInLightThemes.Clear();
        foreach (var theme in BuiltInThemeCatalog.All)
        {
            var row = new CustomThemeRowViewModel(
                theme.Id, theme.Name, theme.Id == activeId, isBuiltIn: true,
                previewBackgroundHex: theme.Colors["AppBackgroundColor"],
                previewAccentHex: theme.Colors["AccentColor"]);
            (theme.Appearance == ThemeAppearance.Dark ? BuiltInDarkThemes : BuiltInLightThemes).Add(row);
        }
        OnPropertyChanged(nameof(ThemeSearchText));
    }

    /// <summary>Backs the empty-state text in Ajustes › Apariencia › Tema personalizado — manually notified
    /// from <see cref="RefreshCustomThemes"/> since <see cref="CustomThemes"/> itself is a plain
    /// <see cref="ObservableCollection{T}"/> (its own <c>CollectionChanged</c> doesn't raise
    /// <c>PropertyChanged</c> for a derived property like this one).</summary>
    public bool HasCustomThemes => CustomThemes.Count > 0;
}
