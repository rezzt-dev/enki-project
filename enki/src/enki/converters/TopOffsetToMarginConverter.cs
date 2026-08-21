using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace enki.Converters;

/// <summary>Converts a pixel offset (<see cref="ViewModels.CalendarTimedItemViewModel.TopOffset"/> or
/// <see cref="ViewModels.CalendarWeekColumnViewModel.NowOffsetY"/>) into a top-only <see cref="Thickness"/>,
/// so a Semana/Día hour-grid item can be absolutely positioned inside its column's plain <c>Panel</c>
/// (which stretches every child to the same rect) using nothing but <c>Margin</c> + <c>VerticalAlignment="Top"</c>
/// — see docs/calendar-feature/04-vistas-y-navegacion.md §5.</summary>
public sealed class TopOffsetToMarginConverter : IValueConverter
{
    public static readonly TopOffsetToMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double top ? new Thickness(0, top, 0, 0) : new Thickness(0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
