using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace enki.Converters;

/// <summary>Converts a <see cref=\"ViewModels.HeatmapCellViewModel.State\"/> value (0=empty, 1=miss, 2=hit)
/// to the appropriate <see cref=\"ISolidColorBrush\"/> for the heatmap cell background.</summary>
public sealed class HeatmapStateConverter : IValueConverter
{
    public static readonly HeatmapStateConverter Instance = new();

    private static readonly ISolidColorBrush EmptyBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255), 0.05);
    private static readonly ISolidColorBrush MissBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255), 0.20);
    private static readonly ISolidColorBrush HitBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255), 1.00);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int state
            ? state switch { 2 => HitBrush, 1 => MissBrush, _ => EmptyBrush }
            : EmptyBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}