using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace enki.Converters;

/// <summary>Converts a "#RRGGBB" hex string (e.g. <see cref="enki.Models.PolarTaskList.Color"/>) into a
/// brush, for spots — like the "Gestión de tareas" home feed's left accent bar — that need a per-item
/// color pulled straight from data rather than a fixed theme resource. An optional numeric
/// <c>ConverterParameter</c> (e.g. <c>0.2</c>) sets the brush's own opacity, used by the calendar's
/// chips (docs/calendar-feature/10-diseno-visual-y-componentes.md §4) to render a saturated
/// <c>LayerColor</c> at low opacity instead of a solid fill.</summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || !Color.TryParse(hex, out var color)) return Brushes.Transparent;

        var brush = new SolidColorBrush(color);
        if (parameter is string opacityText && double.TryParse(opacityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
            brush.Opacity = opacity;

        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
