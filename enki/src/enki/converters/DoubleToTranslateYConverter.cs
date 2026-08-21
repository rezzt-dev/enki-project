using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;

namespace enki.Converters;

/// <summary>Converts a pixel offset (<see cref="ViewModels.CalendarViewModel.RevealOffsetY"/>) into a
/// vertical-only <see cref="TransformOperations"/>, so the reveal animation of docs/calendar-feature/
/// 10-diseno-visual-y-componentes.md §8 can bind <c>RenderTransform</c> to a plain data-bound double the
/// same way <see cref="TopOffsetToMarginConverter"/> already does for <c>Margin</c> — a
/// <c>TransformOperationsTransition</c> only interpolates smoothly between two <see cref="TransformOperations"/>
/// values, so this can't just be a raw <c>TranslateTransform.Y</c> binding.</summary>
public sealed class DoubleToTranslateYConverter : IValueConverter
{
    public static readonly DoubleToTranslateYConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        TransformOperations.Parse(value is double y ? $"translateY({y.ToString(CultureInfo.InvariantCulture)}px)" : "translateY(0px)");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
