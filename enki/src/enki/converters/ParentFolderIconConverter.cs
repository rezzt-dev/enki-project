using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace enki.Converters;

/// <summary>Icono de una tarjeta de carpeta en la rejilla de Skills: la flecha de "subir un nivel" para la
/// entrada "..", y la carpeta normal para el resto. Va en un converter y no en dos <c>MaterialIcon</c>
/// superpuestos con <c>IsVisible</c> porque la tarjeta ya tiene dos textos alternos por el mismo motivo y
/// apilar cuatro controles para pintar uno haría la plantilla ilegible.</summary>
public sealed class ParentFolderIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MaterialIconKind.ArrowUpLeft : MaterialIconKind.FolderOutline;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
