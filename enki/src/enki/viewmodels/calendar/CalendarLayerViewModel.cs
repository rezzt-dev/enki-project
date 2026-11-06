using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;

namespace enki.ViewModels;

/// <summary>One row of the sidebar "capas" panel (docs/calendar-feature/07-filtros-busqueda-y-capas.md
/// §1): a single Kanban/Recordatorios toggle, or one per <c>PolarTaskList</c>. <see cref="Key"/> matches
/// <see cref="CalendarItemViewModel.LayerKey"/> so <see cref="CalendarViewModel"/> can filter by simple set
/// membership. Toggling calls back into <see cref="CalendarViewModel"/> (rather than the view model
/// listening to its own <c>PropertyChanged</c>) so persistence to <c>AppSettings.CalendarHiddenLayerKeys</c>
/// and the resulting rebuild both happen from one place, same shape as <c>CalendarWeekColumnViewModel</c>'s
/// constructor-supplied dependencies elsewhere in this feature.
///
/// Desde §1b la fila también es el sitio donde se elige el color con el que el calendario identifica ese
/// origen: <see cref="ColorKey"/> (que no siempre es <see cref="Key"/> — las columnas de Kanban comparten
/// una sola capa pero tienen un color cada una) y el emergente que abren
/// <see cref="OpenColorPickerCommand"/> y sus hermanos, con el mismo patrón de callback que el
/// interruptor.</summary>
public sealed partial class CalendarLayerViewModel : ObservableObject
{
    private readonly Action<CalendarLayerViewModel>? _onToggled;
    private readonly Action<CalendarLayerViewModel, string?>? _onColorChanged;

    /// <summary>Clave de capa (mostrar/ocultar). Las sub-filas de columnas de Kanban comparten la clave
    /// <c>"kanban"</c> de su fila madre: se ven en el panel para poder darles color, pero no son un
    /// filtro aparte.</summary>
    public string Key { get; }

    /// <summary>Clave de color de esta fila (ver <see cref="CalendarLayerColorService"/>) — vacía en una
    /// fila cuyo color no se puede tocar.</summary>
    public string ColorKey { get; }

    /// <summary>Sub-fila de columna de Kanban: se dibuja indentada bajo la fila KANBAN y sin interruptor
    /// propio (la visibilidad sigue siendo de todo Kanban a la vez).</summary>
    public bool IsChildRow { get; }

    public bool ShowToggle => !IsChildRow;

    /// <summary>Sangría del punto de color: las sub-filas de columnas cuelgan visualmente de la fila
    /// KANBAN.</summary>
    public Thickness IndentMargin => IsChildRow ? new Thickness(12, 0, 0, 0) : default;

    /// <summary>Las sub-filas se leen como detalle de la fila madre, no como capas de primer nivel.</summary>
    public double LabelOpacity => IsChildRow ? 0.75 : 1.0;

    /// <summary>Las filas que participan en el filtrado por capas — ver <see cref="Key"/>.</summary>
    public bool IsToggleable => !IsChildRow;

    /// <summary>Los 15 colores sugeridos de la rejilla del emergente. Propiedad de instancia (y no
    /// <c>x:Static</c>) para que la plantilla de la fila la enlace como cualquier otra cosa.</summary>
    public IReadOnlyList<string> PaletteSwatches { get; } = CalendarLayerPalette.PickerSwatches;

    [ObservableProperty]
    private string _label;

    /// <summary>Color actual con el que el calendario pinta este origen. Se actualiza en sitio (en vez de
    /// reconstruir la colección) para no cerrar el emergente mientras se está eligiendo.</summary>
    [ObservableProperty]
    private string _colorHex;

    /// <summary><c>false</c> en la fila madre KANBAN: su punto solo marca el grupo, el color real vive en
    /// las columnas de debajo.</summary>
    [ObservableProperty]
    private bool _canEditColor;

    /// <summary><c>true</c> mientras el color de esta capa esté elegido a mano — habilita "automático".</summary>
    [ObservableProperty]
    private bool _hasCustomColor;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isColorPickerOpen;

    /// <summary>Texto del campo hex del emergente. Se confirma al pulsar Intro o Guardar
    /// (<see cref="ApplyDraftColorCommand"/>) y no en cada tecla, para no guardar los estados a medio
    /// escribir de "#1A2B3C".</summary>
    [ObservableProperty]
    private string _colorDraftHex = string.Empty;

    /// <summary>Color del selector visual, sincronizado con el borrador hexadecimal sin persistirlo.</summary>
    [ObservableProperty]
    private Color _colorDraft;

    partial void OnIsColorPickerOpenChanged(bool value)
    {
        if (value) ColorDraftHex = ColorHex;
    }

    partial void OnColorDraftHexChanged(string value)
    {
        if (HexColorUtility.NormalizeHex(value) is { } hex)
            ColorDraft = Color.Parse(hex);
    }

    partial void OnColorDraftChanged(Color value)
    {
        var hex = HexColorUtility.ColorToHex(value);
        if (HexColorUtility.NormalizeHex(ColorDraftHex) != hex)
            ColorDraftHex = hex;
    }

    public CalendarLayerViewModel(
        string key, string label, string colorHex, bool isActive,
        Action<CalendarLayerViewModel>? onToggled,
        string colorKey = "",
        Action<CalendarLayerViewModel, string?>? onColorChanged = null,
        bool canEditColor = false,
        bool hasCustomColor = false,
        bool isChildRow = false)
    {
        Key = key;
        ColorKey = colorKey;
        IsChildRow = isChildRow;
        _label = label;
        _colorHex = colorHex;
        _isActive = isActive;
        _canEditColor = canEditColor;
        _hasCustomColor = hasCustomColor;
        _onToggled = onToggled;
        _onColorChanged = onColorChanged;
    }

    partial void OnIsActiveChanged(bool value) => _onToggled?.Invoke(this);

    [RelayCommand]
    private void OpenColorPicker()
    {
        if (!CanEditColor) return;
        ColorDraftHex = ColorHex;
        IsColorPickerOpen = true;
    }

    [RelayCommand]
    private void CloseColorPicker() => IsColorPickerOpen = false;

    /// <summary>Un clic en un color de la rejilla lo aplica y cierra directamente. El borrador del selector
    /// visual y del campo hexadecimal se confirma por separado con Guardar.</summary>
    [RelayCommand]
    private void SelectSwatch(string hex)
    {
        _onColorChanged?.Invoke(this, hex);
        IsColorPickerOpen = false;
    }

    /// <summary>Confirma lo escrito en el campo hex. Un valor inválido no se guarda y el campo vuelve al
    /// color actual, mismo criterio que <see cref="HexColorUtility.NormalizeHex"/> en el editor de temas
    /// (no forzar un color por defecto a espaldas del usuario).</summary>
    [RelayCommand]
    private void ApplyDraftColor()
    {
        if (HexColorUtility.NormalizeHex(ColorDraftHex) is { } normalized)
        {
            _onColorChanged?.Invoke(this, normalized);
            IsColorPickerOpen = false;
        }
        else
        {
            ColorDraftHex = ColorHex;
        }
    }

    /// <summary>Devuelve la capa al color que le reparte la paleta.</summary>
    [RelayCommand]
    private void UseAutomaticColor()
    {
        _onColorChanged?.Invoke(this, null);
        IsColorPickerOpen = false;
    }
}
