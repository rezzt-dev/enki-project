using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Barras verticales de una o dos series (notas vs tareas por semana, actividad por día de la
/// semana, histograma de longitud de notas). Las barras son rectángulos macizos sin radio, con la serie
/// secundaria a menor opacidad y pegada a la primera — la separación entre grupos es lo que hace de
/// agrupación visual, sin necesidad de contornos.
///
/// El resaltado al pasar el puntero ilumina el grupo completo y saca el cartel con los valores; sin él,
/// una barra corta en una serie de 12 semanas es ilegible salvo que se etiquete cada una, que es
/// exactamente el ruido que el sistema de diseño evita.</summary>
public sealed class BarSeriesChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> SeriesProperty =
        AvaloniaProperty.Register<BarSeriesChart, IReadOnlyList<ChartDatum>?>(nameof(Series));

    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> ComparisonSeriesProperty =
        AvaloniaProperty.Register<BarSeriesChart, IReadOnlyList<ChartDatum>?>(nameof(ComparisonSeries));

    public static readonly StyledProperty<string> SeriesNameProperty =
        AvaloniaProperty.Register<BarSeriesChart, string>(nameof(SeriesName), string.Empty);

    public static readonly StyledProperty<string> ComparisonSeriesNameProperty =
        AvaloniaProperty.Register<BarSeriesChart, string>(nameof(ComparisonSeriesName), string.Empty);

    /// <summary>Dibuja el valor encima de cada barra. Solo tiene sentido con pocas categorías (7 días de
    /// la semana, 3 prioridades); con 12 semanas se apaga y el valor se lee en el cartel flotante.</summary>
    public static readonly StyledProperty<bool> ShowValueLabelsProperty =
        AvaloniaProperty.Register<BarSeriesChart, bool>(nameof(ShowValueLabels));

    /// <summary>Línea horizontal punteada en la media de la serie principal — el "¿estoy por encima o por
    /// debajo de lo normal?" que una barra suelta no responde.</summary>
    public static readonly StyledProperty<bool> ShowAverageLineProperty =
        AvaloniaProperty.Register<BarSeriesChart, bool>(nameof(ShowAverageLine));

    public static readonly StyledProperty<double> MaxBarWidthProperty =
        AvaloniaProperty.Register<BarSeriesChart, double>(nameof(MaxBarWidth), 26d);

    static BarSeriesChart()
    {
        AffectsRender<BarSeriesChart>(SeriesProperty, ComparisonSeriesProperty, ShowValueLabelsProperty,
            ShowAverageLineProperty, MaxBarWidthProperty, SeriesNameProperty, ComparisonSeriesNameProperty);
    }

    public IReadOnlyList<ChartDatum>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<ChartDatum>? ComparisonSeries { get => GetValue(ComparisonSeriesProperty); set => SetValue(ComparisonSeriesProperty, value); }
    public string SeriesName { get => GetValue(SeriesNameProperty); set => SetValue(SeriesNameProperty, value); }
    public string ComparisonSeriesName { get => GetValue(ComparisonSeriesNameProperty); set => SetValue(ComparisonSeriesNameProperty, value); }
    public bool ShowValueLabels { get => GetValue(ShowValueLabelsProperty); set => SetValue(ShowValueLabelsProperty, value); }
    public bool ShowAverageLine { get => GetValue(ShowAverageLineProperty); set => SetValue(ShowAverageLineProperty, value); }
    public double MaxBarWidth { get => GetValue(MaxBarWidthProperty); set => SetValue(MaxBarWidthProperty, value); }

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var primary = Series;
        if (primary is null || primary.Count == 0) return;
        var comparison = ComparisonSeries is { Count: > 0 } c && c.Count == primary.Count ? c : null;

        var max = Math.Max(primary.Max(p => p.Value), comparison?.Max(p => p.Value) ?? 0);
        var niceMax = LineAreaChart.NiceCeiling(max);

        var labelHeight = LabelFontSize + 10;
        var valueHeadroom = ShowValueLabels ? LabelFontSize + 6 : 2;
        var plot = new Rect(area.X, area.Y + valueHeadroom,
            Math.Max(1, area.Width), Math.Max(1, area.Height - labelHeight - valueHeadroom));

        // Línea base: el suelo del gráfico siempre visible aunque todas las barras valgan 0.
        context.DrawLine(new Pen(GridBrush, 1),
            new Point(plot.X, Math.Round(plot.Bottom) + 0.5), new Point(plot.Right, Math.Round(plot.Bottom) + 0.5));

        var slot = plot.Width / primary.Count;
        var groupWidth = Math.Min(MaxBarWidth * (comparison is null ? 1 : 2), slot * 0.68);
        var barWidth = comparison is null ? groupWidth : (groupWidth - 2) / 2;

        var hoveredIndex = HoverPoint is { } h && plot.Contains(new Point(h.X, Math.Clamp(h.Y, plot.Y, plot.Bottom)))
            ? Math.Clamp((int)((h.X - plot.X) / slot), 0, primary.Count - 1)
            : -1;

        for (var i = 0; i < primary.Count; i++)
        {
            var groupLeft = plot.X + slot * i + (slot - groupWidth) / 2;

            if (i == hoveredIndex)
                context.FillRectangle(Fade(InkBrush, 0.05), new Rect(plot.X + slot * i, plot.Y - valueHeadroom, slot, plot.Height + valueHeadroom));

            DrawBar(context, groupLeft, barWidth, primary[i].Value, niceMax, plot,
                i == hoveredIndex ? InkBrush : Fade(InkBrush, 0.85));

            if (comparison is not null)
                DrawBar(context, groupLeft + barWidth + 2, barWidth, comparison[i].Value, niceMax, plot,
                    i == hoveredIndex ? Fade(InkBrush, 0.55) : SecondaryInkBrush);

            if (ShowValueLabels && primary[i].Value > 0)
            {
                var value = Text(FormatValue(primary[i].Value), i == hoveredIndex ? InkBrush : LabelBrush);
                var barTop = plot.Bottom - plot.Height * (primary[i].Value / niceMax);
                context.DrawText(value, new Point(groupLeft + (groupWidth - value.Width) / 2, barTop - value.Height - 3));
            }

            var axis = TextClamped(primary[i].AxisLabel, i == hoveredIndex ? InkBrush : LabelBrush, slot - 4);
            context.DrawText(axis, new Point(plot.X + slot * i + (slot - axis.Width) / 2, plot.Bottom + 7));
        }

        if (ShowAverageLine && primary.Count > 1)
        {
            var average = primary.Average(p => p.Value);
            if (average > 0)
            {
                var y = Math.Round(plot.Bottom - plot.Height * (average / niceMax)) + 0.5;
                context.DrawLine(new Pen(Fade(InkBrush, 0.30), 1, new DashStyle([2, 3], 0)),
                    new Point(plot.X, y), new Point(plot.Right, y));
            }
        }

        if (hoveredIndex >= 0 && HoverPoint is { } point)
        {
            var lines = new List<string> { primary[hoveredIndex].Label, $"{SeriesName}  {FormatValue(primary[hoveredIndex].Value)}".TrimStart() };
            if (comparison is not null) lines.Add($"{ComparisonSeriesName}  {FormatValue(comparison[hoveredIndex].Value)}".TrimStart());
            if (!string.IsNullOrEmpty(primary[hoveredIndex].Detail)) lines.Add(primary[hoveredIndex].Detail);
            DrawReadout(context, area, point, [.. lines]);
        }
    }

    private static void DrawBar(DrawingContext context, double left, double width, double value, double niceMax, Rect plot, IBrush brush)
    {
        if (value <= 0 || width <= 0) return;
        var height = Math.Max(1.5, plot.Height * (value / niceMax));
        context.FillRectangle(brush, new Rect(left, plot.Bottom - height, width, height));
    }

    private static string FormatValue(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k"
            : value.ToString("0.##", CultureInfo.CurrentCulture);
}
