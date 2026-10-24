using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace enki.Converters;

public sealed class DepthIndentConverter : IValueConverter
{
    public static readonly DepthIndentConverter Instance = new();

    // Base de -8 (no 14): la fila de carpeta ya reserva 20px para la flechita de expandir más 2px
    // del margen izquierdo del icono (22px fijos) antes de que empiece el contenido, mientras que
    // la fila "Todas las notas" no tiene esa columna y su icono arranca a 14px. Restando esos 22px
    // aquí, el icono de una carpeta de nivel 0 queda a 14px igual que "Todas las notas".
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int depth ? new Thickness(-8 + depth * 10, 0, 0, 0) : new Thickness(-8, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
