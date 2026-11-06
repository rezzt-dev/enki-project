using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace enki.Controls.Charts;

/// <summary>Micro-gráfico sin ejes ni etiquetas para meter dentro de una ficha de KPI: da la forma de la
/// tendencia (subiendo, plana, a picos) junto al número, que por sí solo no dice si es una buena o mala
/// semana. Marca el último punto con un cuadrado sólido para anclar "aquí estás hoy".</summary>
public sealed class Sparkline : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>Dibuja barras en vez de línea — mejor para series muy cortas o muy discretas (7 días).</summary>
    public static readonly StyledProperty<bool> UseBarsProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(UseBars));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, UseBarsProperty);
    }

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public bool UseBars { get => GetValue(UseBarsProperty); set => SetValue(UseBarsProperty, value); }

    public Sparkline() => ShowHoverReadout = false;

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var values = Values;
        if (values is null || values.Count < 2) return;

        var max = values.Max();
        var min = Math.Min(0, values.Min());
        var span = max - min;
        if (span <= 0) span = 1;

        double Y(double value) => area.Bottom - (area.Height - 2) * ((value - min) / span) - 1;

        if (UseBars)
        {
            var slot = area.Width / values.Count;
            var width = Math.Max(1.5, slot - 2);
            for (var i = 0; i < values.Count; i++)
            {
                var y = Y(values[i]);
                var height = Math.Max(values[i] > 0 ? 1.5 : 0, area.Bottom - y);
                if (height <= 0) continue;
                context.FillRectangle(Fade(InkBrush, i == values.Count - 1 ? 0.95 : 0.35),
                    new Rect(area.X + slot * i + (slot - width) / 2, area.Bottom - height, width, height));
            }
            return;
        }

        var stepX = area.Width / (values.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(area.X, Y(values[0])), isFilled: false);
            for (var i = 1; i < values.Count; i++) ctx.LineTo(new Point(area.X + stepX * i, Y(values[i])));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Fade(InkBrush, 0.55), 1.2), geometry);

        var last = new Point(area.Right, Y(values[^1]));
        context.FillRectangle(InkBrush, new Rect(last.X - 3, last.Y - 3, 3, 6));
    }
}
