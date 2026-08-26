using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace enki.Converters;

/// <summary>Sangría de una fila del árbol de la vista simplificada de Notas, a partir de su profundidad.
/// Es un converter aparte de <see cref="DepthIndentConverter"/> (el panel de carpetas de la vista
/// completa) porque aquel compensa con una base negativa la columna de 20px que reserva para la flechita;
/// aquí todas las filas — carpetas y notas — comparten esa misma columna, así que la sangría arranca
/// limpia en 0 y solo escala con el nivel.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public static readonly TreeIndentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * 14 : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
