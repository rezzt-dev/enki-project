using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace enki.Converters;

public sealed class ExpandChevronConverter : IValueConverter
{
    public static readonly ExpandChevronConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MaterialIconKind.MenuDown : MaterialIconKind.MenuRight;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
