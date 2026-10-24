using Avalonia;
using Avalonia.Controls;
using enki.ViewModels;

namespace enki.Controls;

/// <summary>Horizontal-only layout panel for the Semana/Día hour grid's timed items (análisis §4.4, Fase
/// 8). Vertical position/height stay exactly as before — each item's own root <c>Panel</c> (in
/// <c>CalendarView.axaml</c>'s <c>CalendarTimedChipTemplate</c>) positions itself via a top-only
/// <c>Margin</c> plus <c>VerticalAlignment="Top"</c>, same as the plain <see cref="Panel"/> this replaces.
/// The only difference is the X-axis: each child's slice is read straight from its DataContext's
/// <see cref="CalendarTimedItemViewModel.LeftFraction"/>/<see cref="CalendarTimedItemViewModel.WidthFraction"/>
/// (0..1, set once per column by <see cref="CalendarTimedItemLayout.Apply"/>) instead of every child
/// stretching to the column's full width and drawing completely on top of its overlapping siblings.</summary>
public sealed class CalendarTimedItemsPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = default(Size);
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            desired = new Size(
                System.Math.Max(desired.Width, child.DesiredSize.Width),
                System.Math.Max(desired.Height, child.DesiredSize.Height));
        }
        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var (left, width) = child.DataContext is CalendarTimedItemViewModel item
                ? (item.LeftFraction, item.WidthFraction)
                : (0.0, 1.0);
            child.Arrange(new Rect(left * finalSize.Width, 0, width * finalSize.Width, finalSize.Height));
        }
        return finalSize;
    }
}
