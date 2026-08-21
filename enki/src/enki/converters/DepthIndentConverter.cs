using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace enki.Converters;

public sealed class DepthIndentConverter : IValueConverter
{
    public static readonly DepthIndentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int depth ? new Thickness(14 + depth * 10, 0, 0, 0) : new Thickness(14, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
