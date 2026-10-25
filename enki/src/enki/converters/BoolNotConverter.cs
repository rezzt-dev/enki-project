using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace enki.Converters;

public sealed class BoolNotConverter : IValueConverter
{
    public static readonly BoolNotConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}