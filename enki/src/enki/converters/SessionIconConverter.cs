using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace enki.Converters;

/// <summary>Maps a <c>SessionOptionViewModel.IsVault</c> flag to the icon shown next to it in the
/// workspace selector — the vault root reads as a folder, a session as its own small network.</summary>
public sealed class SessionIconConverter : IValueConverter
{
    public static readonly SessionIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? MaterialIconKind.FolderOutline : MaterialIconKind.SitemapOutline;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
