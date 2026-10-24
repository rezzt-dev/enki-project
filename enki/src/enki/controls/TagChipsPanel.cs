using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace enki.Controls;

/// <summary>Panel en flujo para las filas de chips de etiqueta. Desplegado se comporta como un
/// <c>WrapPanel</c>; plegado se queda en una sola fila con, como mucho, <see cref="MaxCollapsed"/>
/// chips y publica en <see cref="HiddenCount"/> cuántos se han quedado fuera, para que la vista pueda
/// ofrecer el "mostrar todas" y decir cuántas faltan.
///
/// Los chips sobrantes se colocan fuera del recuadro —que recorta -en vez de apagarse con
/// <c>IsVisible</c>: tocar propiedades de los hijos durante la medida encadenaría una pasada de layout
/// más en cada arranque y en cada recarga del filtro.</summary>
public sealed class TagChipsPanel : Panel
{
    /// <summary>Distancia a la que se aparcan los chips que no caben. Cualquier valor mayor que el
    /// ancho de un chip sirve: el recorte del panel se encarga del resto.</summary>
    private const double OffscreenOffset = -10000;

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<TagChipsPanel, bool>(nameof(IsExpanded));

    public static readonly StyledProperty<int> MaxCollapsedProperty =
        AvaloniaProperty.Register<TagChipsPanel, int>(nameof(MaxCollapsed), 5);

    public static readonly StyledProperty<int> HiddenCountProperty =
        AvaloniaProperty.Register<TagChipsPanel, int>(nameof(HiddenCount));

    static TagChipsPanel() => AffectsMeasure<TagChipsPanel>(IsExpandedProperty, MaxCollapsedProperty);

    public TagChipsPanel() => ClipToBounds = true;

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Tope de chips de la fila plegada; el ancho real puede recortarlo aún más.</summary>
    public int MaxCollapsed
    {
        get => GetValue(MaxCollapsedProperty);
        set => SetValue(MaxCollapsedProperty, value);
    }

    /// <summary>Chips que quedan fuera de la vista plegada. No depende de <see cref="IsExpanded"/>:
    /// ya desplegado sigue contando cuántos se ocultarían, para que el botón no desaparezca justo
    /// cuando hace falta para volver a plegar.</summary>
    public int HiddenCount
    {
        get => GetValue(HiddenCountProperty);
        set => SetValue(HiddenCountProperty, value);
    }

    private int _visibleCount;

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        foreach (var child in Children)
            child.Measure(new Size(lineWidth, double.PositiveInfinity));

        var collapsedCount = Math.Min(FirstRowCount(lineWidth), Math.Max(1, MaxCollapsed));
        collapsedCount = Math.Min(collapsedCount, Children.Count);
        _visibleCount = IsExpanded ? Children.Count : collapsedCount;
        PublishHiddenCount(Children.Count - collapsedCount);

        return LayoutRows(lineWidth, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRows(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Chips que caben en la primera fila — la medida de "supera una línea". Siempre cuenta
    /// al menos uno: un chip más ancho que la fila entera se recorta, no desaparece.</summary>
    private int FirstRowCount(double lineWidth)
    {
        var count = 0;
        var used = 0d;
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            if (count > 0 && used + width > lineWidth) break;
            used += width;
            count++;
        }

        return count;
    }

    private Size LayoutRows(double lineWidth, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, widest = 0;

        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;

            if (i >= _visibleCount)
            {
                if (arrange) child.Arrange(new Rect(OffscreenOffset, OffscreenOffset, size.Width, size.Height));
                continue;
            }

            if (x > 0 && x + size.Width > lineWidth)
            {
                widest = Math.Max(widest, x);
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }

            if (arrange) child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(Math.Max(widest, x), y + rowHeight);
    }

    /// <summary>Escribe el contador fuera de la pasada de layout: el botón que lo consume vive en la
    /// misma rejilla que este panel, así que actualizarlo en plena medida reabriría el layout.</summary>
    private void PublishHiddenCount(int hidden)
    {
        if (hidden == HiddenCount) return;
        Dispatcher.UIThread.Post(() => SetCurrentValue(HiddenCountProperty, hidden), DispatcherPriority.Loaded);
    }
}
