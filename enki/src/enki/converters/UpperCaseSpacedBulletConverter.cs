using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;

namespace enki.Converters;

// Igual que UpperCaseConverter, pero además abre hueco alrededor de los separadores "•" que
// puede traer el propio nombre (p.ej. "Main • To do list"). page-title usa LetterSpacing="-1"
// para titulares grandes y compactos, y ese ajuste negativo se aplica también al espacio pegado
// al "•", así que a simple vista el separador queda encajado contra las palabras vecinas.
public sealed class UpperCaseSpacedBulletConverter : IValueConverter
{
    public static readonly UpperCaseSpacedBulletConverter Instance = new();

    private static readonly Regex BulletWithSpacing = new(@"\s*•\s*", RegexOptions.Compiled);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? BulletWithSpacing.Replace(s.ToUpper(culture), "   •   ") : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
