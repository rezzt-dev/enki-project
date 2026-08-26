using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Barra apilada horizontal con costura de 1px entre segmentos y las etiquetas debajo, alineadas
/// con el segmento al que pertenecen. Es el "reparto de un todo" cuando lo que importa es el flujo de
/// izquierda a derecha (pendiente → en curso → hecho) y no la comparación entre porciones, que es lo que
/// resuelve el donut.
///
/// La costura no se dibuja: se deja ver el fondo entre segmentos, exactamente el mismo truco de
/// <c>gap-[1px]</c> que documenta §3.2 del sistema de diseño para las rejillas.</summary>
public sealed class SegmentedBar : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> ItemsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<ChartDatum>?>(nameof(Items));

    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(BarHeight), 30d);

    /// <summary>Escribe el porcentaje dentro de cada segmento cuando cabe.</summary>
    public static readonly StyledProperty<bool> ShowPercentInsideProperty =
        AvaloniaProperty.Register<SegmentedBar, bool>(nameof(ShowPercentInside), true);

    /// <summary>Cifra y etiqueta bajo cada segmento. Se apaga cuando la tarjeta ya dice lo mismo justo
    /// encima de la barra y repetirlo solo añade ruido.</summary>
    public static readonly StyledProperty<bool> ShowLegendProperty =
        AvaloniaProperty.Register<SegmentedBar, bool>(nameof(ShowLegend), true);

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(ItemsProperty, BarHeightProperty, ShowPercentInsideProperty, ShowLegendProperty);
        AffectsMeasure<SegmentedBar>(BarHeightProperty, ShowLegendProperty);
    }

    public IReadOnlyList<ChartDatum>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double BarHeight { get => GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }
    public bool ShowPercentInside { get => GetValue(ShowPercentInsideProperty); set => SetValue(ShowPercentInsideProperty, value); }
    public bool ShowLegend { get => GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            BarHeight + (ShowLegend ? LabelFontSize * 2 + 16 : 0));

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var items = Items;
        if (items is null || items.Count == 0) return;

        var total = items.Sum(i => i.Value);
        var bar = new Rect(area.X, area.Y, area.Width, BarHeight);

        if (total <= 0)
        {
            context.FillRectangle(Fade(InkBrush, 0.06), bar);
            return;
        }

        const double seam = 1.0;
        var usable = area.Width - seam * (items.Count - 1);
        var hoveredIndex = -1;
        var cursor = area.X;
        var widths = new double[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            // Un segmento con valor > 0 nunca desaparece: por debajo de 2px dejaría de leerse como
            // "queda algo aquí", que es justo lo que un flujo de trabajo necesita comunicar.
            widths[i] = items[i].Value <= 0 ? 0 : Math.Max(2, usable * (items[i].Value / total));
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (widths[i] <= 0) continue;
            var segment = new Rect(cursor, bar.Y, widths[i], bar.Height);
            if (HoverPoint is { } h && segment.Contains(h)) hoveredIndex = i;
            cursor += widths[i] + seam;
        }

        cursor = area.X;
        for (var i = 0; i < items.Count; i++)
        {
            if (widths[i] <= 0) continue;
            var segment = new Rect(cursor, bar.Y, widths[i], bar.Height);
            var shade = items[i].Shade > 0 ? items[i].Shade : ChartDatum.ShadeFor(i, items.Count);
            context.FillRectangle(i == hoveredIndex ? InkBrush : Fade(InkBrush, shade), segment);

            if (ShowPercentInside)
            {
                var percent = (items[i].Value / total * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
                // Sobre un segmento a tinta plena el texto va del color del fondo; sobre uno tenue (que
                // apenas se despega de la tarjeta) va de tinta, o se volvería ilegible en ambos temas.
                var textBrush = shade >= 0.5 ? Surface ?? Brushes.Black : Fade(InkBrush, 0.85);
                var text = Text(percent, textBrush, LabelFontSize + 1, bold: true);
                if (text.Width + 10 < segment.Width)
                    context.DrawText(text, new Point(segment.X + (segment.Width - text.Width) / 2, segment.Y + (segment.Height - text.Height) / 2));
            }

            cursor += widths[i] + seam;
        }

        if (ShowLegend) DrawLegend(context, area, items, widths, hoveredIndex);

        if (hoveredIndex >= 0 && HoverPoint is { } point && !string.IsNullOrEmpty(items[hoveredIndex].Detail))
            DrawReadout(context, area, point, items[hoveredIndex].Label, items[hoveredIndex].Detail);
    }

    /// <summary>Etiqueta + cifra bajo cada segmento, ancladas a su borde izquierdo mientras quepan; el
    /// último se ancla a la derecha para que la línea de leyenda cierre a ras con la barra.</summary>
    private void DrawLegend(DrawingContext context, Rect area, IReadOnlyList<ChartDatum> items, double[] widths, int hoveredIndex)
    {
        var top = area.Y + BarHeight + 10;
        var cursor = area.X;
        var lastDrawnRight = double.NegativeInfinity;

        for (var i = 0; i < items.Count; i++)
        {
            var segmentLeft = cursor;
            cursor += widths[i] + 1;
            if (widths[i] <= 0) continue;

            var value = Text(items[i].Value.ToString("0", CultureInfo.CurrentCulture),
                i == hoveredIndex ? InkBrush : Fade(InkBrush, 0.85), LabelFontSize + 5, bold: true);
            var label = TextClamped(items[i].Label, LabelBrush, Math.Max(60, area.Width / items.Count));

            var left = i == items.Count - 1
                ? Math.Min(segmentLeft, area.Right - Math.Max(value.Width, label.Width))
                : segmentLeft;
            if (left < lastDrawnRight + 8) continue;

            context.DrawText(value, new Point(left, top));
            context.DrawText(label, new Point(left, top + value.Height + 2));
            lastDrawnRight = left + Math.Max(value.Width, label.Width);
        }
    }
}
