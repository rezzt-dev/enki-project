using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Apariencia: tema claro/oscuro, título de ventana personalizado, fuente de interfaz,
/// zoom de interfaz y el sistema completo de temas personalizados (crear/editar/duplicar/activar/eliminar/
/// importar/exportar) — split out of <c>SettingsViewModel.cs</c> to keep every "how the app looks" setting
/// in one file instead of interleaved with Editor/Notificaciones/Calendario. Still the very same
/// <see cref="SettingsViewModel"/> class — partial types share every field/property regardless of which
/// file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Ajustes › Apariencia › Tema personalizado — un tema importado por fila, rebuilt (not just
    /// re-labeled, unlike <see cref="ShortcutCategories"/>) by <see cref="RefreshCustomThemes"/> after every
    /// import/activate/delete so <see cref="CustomThemeRowViewModel.IsActive"/> stays correct.</summary>
    public ObservableCollection<CustomThemeRowViewModel> CustomThemes { get; }

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
    /// directly from its own toggle control, so this is the only place that needed a dedicated command.</summary>
    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

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
