using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace enki.Converters;

public sealed class OutlineIndentConverter : IValueConverter
{
    public static readonly OutlineIndentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 1;
        var indent = Math.Max(0, level - 1) * 12;
        return new Thickness(indent, 0, 0, 0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
