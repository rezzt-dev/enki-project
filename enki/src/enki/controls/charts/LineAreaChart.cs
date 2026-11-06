using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Gráfico de líneas con relleno de área para series temporales densas (actividad por día,
/// crecimiento acumulado del vault). Pinta hasta dos series: la principal como área + trazo grueso, la
/// secundaria como trazo fino discontinuo — la jerarquía se marca con opacidad y grosor, nunca con color,
/// que es lo que exige el sistema monocromo.
///
/// Al pasar el puntero se resalta el día más cercano en horizontal (línea guía vertical + puntos) y se
/// dibuja el cartel con los valores exactos de ambas series, que es la única forma de leer un valor
/// concreto en una serie de 90 puntos donde no caben etiquetas.</summary>
public sealed class LineAreaChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> SeriesProperty =
        AvaloniaProperty.Register<LineAreaChart, IReadOnlyList<ChartDatum>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> ComparisonSeriesProperty =
        AvaloniaProperty.Register<LineAreaChart, IReadOnlyList<ChartDatum>?>(nameof(ComparisonSeries));

    /// <summary>Nombres usados solo en el cartel flotante (ya localizados por el ViewModel).</summary>
    public static readonly StyledProperty<string> SeriesNameProperty =
        AvaloniaProperty.Register<LineAreaChart, string>(nameof(SeriesName), string.Empty);

    public static readonly StyledProperty<string> ComparisonSeriesNameProperty =
        AvaloniaProperty.Register<LineAreaChart, string>(nameof(ComparisonSeriesName), string.Empty);

    /// <summary>Número de líneas horizontales de rejilla (incluye el 0 y el máximo).</summary>
    public static readonly StyledProperty<int> GridLinesProperty =
        AvaloniaProperty.Register<LineAreaChart, int>(nameof(GridLines), 4);

    static LineAreaChart()
    {
        AffectsRender<LineAreaChart>(SeriesProperty, ComparisonSeriesProperty, GridLinesProperty,
            SeriesNameProperty, ComparisonSeriesNameProperty);
    }

    public IReadOnlyList<ChartDatum>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<ChartDatum>? ComparisonSeries { get => GetValue(ComparisonSeriesProperty); set => SetValue(ComparisonSeriesProperty, value); }
    public string SeriesName { get => GetValue(SeriesNameProperty); set => SetValue(SeriesNameProperty, value); }
    public string ComparisonSeriesName { get => GetValue(ComparisonSeriesNameProperty); set => SetValue(ComparisonSeriesNameProperty, value); }
    public int GridLines { get => GetValue(GridLinesProperty); set => SetValue(GridLinesProperty, value); }

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var primary = Series;
        if (primary is null || primary.Count < 2) return;

        var comparison = ComparisonSeries is { Count: > 1 } c && c.Count == primary.Count ? c : null;

        // Reservamos el canalón izquierdo midiendo la etiqueta del máximo, no con una constante: con
        // zoom de interfaz o un vault grande ("1.240") la etiqueta crece y taparía la primera columna.
        var max = Math.Max(primary.Max(p => p.Value), comparison?.Max(p => p.Value) ?? 0);
        var niceMax = NiceScale(max, Math.Max(2, GridLines));

        var gutter = Text(FormatValue(niceMax), LabelBrush).Width + 10;
        var bottom = LabelFontSize + 12;
        var plot = new Rect(area.X + gutter, area.Y + 4, Math.Max(1, area.Width - gutter), Math.Max(1, area.Height - bottom - 4));

        DrawGrid(context, plot, niceMax);

        var stepX = plot.Width / (primary.Count - 1);
        double X(int i) => plot.X + stepX * i;
        double Y(double value) => plot.Bottom - plot.Height * (value / niceMax);

        // Área de la serie principal primero, para que ambos trazos queden por encima del relleno.
        var areaGeometry = new StreamGeometry();
        using (var ctx = areaGeometry.Open())
        {
            ctx.BeginFigure(new Point(X(0), plot.Bottom), isFilled: true);
            for (var i = 0; i < primary.Count; i++) ctx.LineTo(new Point(X(i), Y(primary[i].Value)));
            ctx.LineTo(new Point(X(primary.Count - 1), plot.Bottom));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(Fade(InkBrush, 0.10), null, areaGeometry);

        if (comparison is not null)
            DrawPolyline(context, comparison, X, Y, new Pen(SecondaryInkBrush, 1, new DashStyle([3, 3], 0)));

        DrawPolyline(context, primary, X, Y, new Pen(InkBrush, 1.6));

        DrawXAxisLabels(context, primary, plot, X);

        if (HoverPoint is { } hover && plot.Contains(hover))
            DrawHover(context, area, plot, primary, comparison, hover, stepX, X, Y);
    }

    private static void DrawPolyline(DrawingContext context, IReadOnlyList<ChartDatum> points,
        Func<int, double> x, Func<double, double> y, IPen pen)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(points[0].Value)), isFilled: false);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(new Point(x(i), y(points[i].Value)));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawGrid(DrawingContext context, Rect plot, double niceMax)
    {
        var lines = Math.Max(2, GridLines);
        var pen = new Pen(GridBrush, 1);
        for (var i = 0; i < lines; i++)
        {
            var value = niceMax * i / (lines - 1);
            var y = Math.Round(plot.Bottom - plot.Height * (value / niceMax)) + 0.5;
            context.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));

            var label = Text(FormatValue(value), LabelBrush);
            context.DrawText(label, new Point(plot.X - label.Width - 8, y - label.Height / 2));
        }
    }

    /// <summary>Etiquetas del eje X: la primera, la última y las intermedias que quepan sin solaparse —
    /// con 90 días en el rango "Todo" no cabe una por punto, y rotarlas rompería la retícula ortogonal
    /// del sistema de diseño.</summary>
    private void DrawXAxisLabels(DrawingContext context, IReadOnlyList<ChartDatum> points, Rect plot, Func<int, double> x)
    {
        var sample = Text(points[0].AxisLabel, LabelBrush);
        var slot = sample.Width + 24;
        var maxLabels = Math.Max(2, (int)(plot.Width / Math.Max(1, slot)));
        var step = Math.Max(1, (int)Math.Ceiling((double)points.Count / maxLabels));

        for (var i = 0; i < points.Count; i += step)
        {
            var label = Text(points[i].AxisLabel, LabelBrush);
            var left = Math.Clamp(x(i) - label.Width / 2, plot.X, plot.Right - label.Width);
            context.DrawText(label, new Point(left, plot.Bottom + 8));
        }
    }

    private void DrawHover(DrawingContext context, Rect area, Rect plot, IReadOnlyList<ChartDatum> primary,
        IReadOnlyList<ChartDatum>? comparison, Point hover, double stepX, Func<int, double> x, Func<double, double> y)
    {
        var index = Math.Clamp((int)Math.Round((hover.X - plot.X) / stepX), 0, primary.Count - 1);
        var px = x(index);

        context.DrawLine(new Pen(Fade(InkBrush, 0.25), 1), new Point(px, plot.Y), new Point(px, plot.Bottom));
        DrawMarker(context, new Point(px, y(primary[index].Value)), InkBrush);
        if (comparison is not null) DrawMarker(context, new Point(px, y(comparison[index].Value)), SecondaryInkBrush);

        var lines = new List<string> { primary[index].Label };
        lines.Add($"{SeriesName}  {FormatValue(primary[index].Value)}".TrimStart());
        if (comparison is not null) lines.Add($"{ComparisonSeriesName}  {FormatValue(comparison[index].Value)}".TrimStart());
        if (!string.IsNullOrEmpty(primary[index].Detail)) lines.Add(primary[index].Detail);

        DrawReadout(context, area, new Point(px, y(primary[index].Value)), [.. lines]);
    }

    private static void DrawMarker(DrawingContext context, Point center, IBrush brush) =>
        context.DrawRectangle(brush, null, new Rect(center.X - 3, center.Y - 3, 6, 6));

    /// <summary>Techo del eje que además cae justo en <paramref name="lines"/> escalones enteros: se
    /// redondea el *paso* (no el máximo) a 1/2/5 × 10ⁿ y se multiplica de vuelta. Sin esto, un máximo de
    /// 5 con cuatro líneas rotulaba el eje con 1,67 y 3,33 — cifras que nadie pide ver en un gráfico de
    /// "cuántas notas".</summary>
    internal static double NiceScale(double rawMax, int lines)
    {
        if (rawMax <= 0) return lines - 1;
        var step = NiceCeiling(rawMax / (lines - 1));
        return step * (lines - 1);
    }

    /// <summary>Redondea hacia arriba a 1/2/5 × 10ⁿ.</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var stepped = normalized switch { <= 1 => 1, <= 2 => 2, <= 5 => 5, _ => 10 };
        return stepped * magnitude;
    }

    private static string FormatValue(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "k"
            : value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
}
