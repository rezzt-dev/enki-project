using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Anillo de proporciones (prioridad de las tareas, tipos de archivo, reparto de recordatorios).
/// Cada segmento usa un escalón distinto de la rampa de opacidad de la tinta en vez de un color propio,
/// que es como el sistema de diseño distingue categorías sin introducir paleta.
///
/// El centro no se desperdicia: muestra el total (o el segmento sobre el que está el puntero), que es el
/// dato que uno busca antes de mirar el reparto.</summary>
public sealed class DonutChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> ItemsProperty =
        AvaloniaProperty.Register<DonutChart, IReadOnlyList<ChartDatum>?>(nameof(Items));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(Thickness), 22d);

    /// <summary>Rótulo bajo la cifra central (ya localizado por el ViewModel).</summary>
    public static readonly StyledProperty<string> CenterCaptionProperty =
        AvaloniaProperty.Register<DonutChart, string>(nameof(CenterCaption), string.Empty);

    static DonutChart()
    {
        AffectsRender<DonutChart>(ItemsProperty, ThicknessProperty, CenterCaptionProperty);
    }

    public IReadOnlyList<ChartDatum>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public string CenterCaption { get => GetValue(CenterCaptionProperty); set => SetValue(CenterCaptionProperty, value); }

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var items = Items?.Where(i => i.Value > 0).ToList();
        if (items is null || items.Count == 0) return;

        var total = items.Sum(i => i.Value);
        if (total <= 0) return;

        var size = Math.Min(area.Width, area.Height);
        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        var outer = size / 2 - 1;
        var inner = Math.Max(6, outer - Thickness);

        var hoveredIndex = HitTestSegment(items, total, center, inner, outer);

        // -90° para arrancar arriba, como cualquier reloj: leer un reparto empezando por la derecha es
        // antinatural y obliga a girar la cabeza mentalmente.
        var angle = -Math.PI / 2;
        for (var i = 0; i < items.Count; i++)
        {
            var sweep = 2 * Math.PI * (items[i].Value / total);
            // Un hueco de 1.5px entre segmentos, igual que la costura de 1px de las rejillas del resto de
            // la app. Con un único segmento no hay costura que dibujar (dejaría una muesca sin sentido).
            var gap = items.Count > 1 ? 1.5 / outer : 0;
            DrawSegment(context, center, inner, outer, angle + gap / 2, sweep - gap,
                SegmentBrush(items[i], i, items.Count, hovered: i == hoveredIndex));
            angle += sweep;
        }

        var headline = hoveredIndex >= 0 ? items[hoveredIndex].Value : total;
        var caption = hoveredIndex >= 0 ? items[hoveredIndex].Label : CenterCaption;

        var headlineText = Text(headline.ToString("0.##", CultureInfo.CurrentCulture), InkBrush, Math.Max(16, inner * 0.62), bold: true);
        context.DrawText(headlineText, new Point(center.X - headlineText.Width / 2, center.Y - headlineText.Height * 0.62));

        if (!string.IsNullOrEmpty(caption))
        {
            var captionText = TextClamped(caption, LabelBrush, inner * 1.7);
            context.DrawText(captionText, new Point(center.X - captionText.Width / 2, center.Y + headlineText.Height * 0.30));
        }

        if (hoveredIndex >= 0 && HoverPoint is { } point)
        {
            var percent = (items[hoveredIndex].Value / total * 100).ToString("0.#", CultureInfo.CurrentCulture);
            DrawReadout(context, area, point, items[hoveredIndex].Label,
                $"{items[hoveredIndex].Value.ToString("0.##", CultureInfo.CurrentCulture)}  ·  {percent}%");
        }
    }

    private IBrush SegmentBrush(ChartDatum datum, int index, int count, bool hovered) =>
        hovered ? InkBrush : Fade(InkBrush, datum.Shade > 0 ? datum.Shade : ChartDatum.ShadeFor(index, count));

    private int HitTestSegment(List<ChartDatum> items, double total, Point center, double inner, double outer)
    {
        if (HoverPoint is not { } hover) return -1;

        var dx = hover.X - center.X;
        var dy = hover.Y - center.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < inner || distance > outer) return -1;

        // atan2 devuelve (-π, π] con 0 a la derecha; lo giramos para que 0 sea arriba y crezca en sentido
        // horario, que es el mismo origen con el que se pintan los segmentos.
        var angle = Math.Atan2(dy, dx) + Math.PI / 2;
        if (angle < 0) angle += 2 * Math.PI;

        var cursor = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            cursor += 2 * Math.PI * (items[i].Value / total);
            if (angle <= cursor) return i;
        }
        return items.Count - 1;
    }

    /// <summary>Un segmento de anillo como geometría cerrada: arco exterior, radio de cierre, arco
    /// interior de vuelta. Avalonia no tiene primitiva de "arco grueso", así que se construye a mano.</summary>
    internal static void DrawSegment(DrawingContext context, Point center, double inner, double outer,
        double startAngle, double sweep, IBrush brush)
    {
        if (sweep <= 0) return;
        sweep = Math.Min(sweep, 2 * Math.PI - 0.0001); // un círculo completo no se puede expresar con un solo ArcTo

        var endAngle = startAngle + sweep;
        var isLarge = sweep > Math.PI;

        Point At(double radius, double angle) => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(outer, startAngle), isFilled: true);
            ctx.ArcTo(At(outer, endAngle), new Size(outer, outer), 0, isLarge, SweepDirection.Clockwise);
            ctx.LineTo(At(inner, endAngle));
            ctx.ArcTo(At(inner, startAngle), new Size(inner, inner), 0, isLarge, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }
}
