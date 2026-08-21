using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace enki.Converters;

public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var a = value?.ToString() ?? string.Empty;
        var b = parameter?.ToString() ?? string.Empty;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
