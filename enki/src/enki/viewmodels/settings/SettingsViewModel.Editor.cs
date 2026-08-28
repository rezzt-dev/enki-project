using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Editor: tamaño de fuente, ancho y alineación de columna, barra de desplazamiento,
/// tamaño de tabulación, numeración de líneas, ajuste de línea, panel lateral y fuente del editor de notas —
/// split out of <c>SettingsViewModel.cs</c> so every editor-specific property/handler lives together instead
/// of interleaved with Apariencia/Notificaciones/Calendario. Still the very same
/// <see cref="SettingsViewModel"/> class — partial types share every field/property regardless of which
/// file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
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

    /// <summary>Ajustes › Editor › "Imágenes remotas" — ver
    /// <see cref="EditorPreferences.RemoteImages"/> para por qué esto es un interruptor y no un
    /// comportamiento fijo.</summary>
    [ObservableProperty]
    private bool _editorRemoteImages;

    [ObservableProperty]
    private bool _editorSidePanelVisible;

    [ObservableProperty]
    private string _editorFontFamily;

    /// <summary>Vista simplificada de la página de Notas: árbol de carpetas y notas a la izquierda,
    /// pestañas y editor a la derecha, sin barra de formato ni panel lateral de esquema/enlaces/
    /// estadísticas. False deja la vista completa de siempre. Se guarda como el string
    /// "Simple"/"Full" en <see cref="Models.AppSettings.NotesViewMode"/> —mismo criterio que
    /// <see cref="Models.AppSettings.EditorColumnPosition"/>— y se refleja al vuelo en las notas ya
    /// abiertas a través de <see cref="EditorPreferences.SimplifiedNotesView"/>.</summary>
    [ObservableProperty]
    private bool _isSimplifiedNotesView;

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

    partial void OnEditorRemoteImagesChanged(bool value)
    {
        _settingsService.Settings.EditorRemoteImages = value;
        _settingsService.Save();
        EditorPreferences.RemoteImages = value;
    }

    partial void OnEditorSidePanelVisibleChanged(bool value)
    {
        _settingsService.Settings.EditorSidePanelVisible = value;
        _settingsService.Save();
        EditorPreferences.SidePanelVisible = value;
    }

    partial void OnIsSimplifiedNotesViewChanged(bool value)
    {
        _settingsService.Settings.NotesViewMode = value ? "Simple" : "Full";
        _settingsService.Save();
        EditorPreferences.SimplifiedNotesView = value;
    }

    partial void OnEditorFontFamilyChanged(string value)
    {
        var stored = FromLabel(value);
        _settingsService.Settings.EditorFontFamily = stored;
        _settingsService.Save();
        EditorPreferences.FontFamily = stored;
    }
}
