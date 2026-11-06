using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace enki.Converters;

/// <summary>Compares a bound enum value against a string <c>ConverterParameter</c> (the member
/// name), for driving section visibility from a single "selected section" property.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
