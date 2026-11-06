using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace enki.Converters;

/// <summary>Icono de una carpeta del árbol de la vista simplificada de Notas según esté abierta o
/// cerrada. La flechita de la izquierda ya dice lo mismo, pero solo aparece en carpetas con contenido;
/// esto mantiene la señal también en las vacías, y es la convención que espera cualquiera que venga de
/// un explorador de archivos.</summary>
public sealed class FolderStateIconConverter : IValueConverter
{
    public static readonly FolderStateIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MaterialIconKind.FolderOpenOutline : MaterialIconKind.FolderOutline;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
