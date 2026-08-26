using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Mapa de calor de calendario: una columna por semana, una fila por día de la semana, la
/// intensidad de cada celda proporcional a la actividad de ese día. Es la vista que responde "¿escribo
/// todos los días o a rachas?" de un vistazo, sobre un año entero, sin ningún eje numérico.
///
/// A diferencia del heatmap de hábitos que ya existía (celdas de estado 0/1/2), aquí la intensidad es
/// continua y se cuantiza a cinco escalones de la rampa de tinta: con opacidad continua, dos días con 3 y
/// 4 notas serían indistinguibles, y con dos estados se pierde toda la gradación.</summary>
public sealed class CalendarHeatmap : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<HeatCell>?> CellsProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IReadOnlyList<HeatCell>?>(nameof(Cells));

    public static readonly StyledProperty<double> CellSizeProperty =
        AvaloniaProperty.Register<CalendarHeatmap, double>(nameof(CellSize), 13d);

    public static readonly StyledProperty<double> CellGapProperty =
        AvaloniaProperty.Register<CalendarHeatmap, double>(nameof(CellGap), 3d);

    /// <summary>Etiquetas de día de la semana (lunes primero), ya localizadas por el ViewModel. Solo se
    /// pintan las filas impares para no saturar la columna.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> DayLabelsProperty =
        AvaloniaProperty.Register<CalendarHeatmap, IReadOnlyList<string>?>(nameof(DayLabels));

    static CalendarHeatmap()
    {
        AffectsRender<CalendarHeatmap>(CellsProperty, CellSizeProperty, CellGapProperty, DayLabelsProperty);
        AffectsMeasure<CalendarHeatmap>(CellsProperty, CellSizeProperty, CellGapProperty);
    }

    public IReadOnlyList<HeatCell>? Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public double CellSize { get => GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, value); }
    public double CellGap { get => GetValue(CellGapProperty); set => SetValue(CellGapProperty, value); }
    public IReadOnlyList<string>? DayLabels { get => GetValue(DayLabelsProperty); set => SetValue(DayLabelsProperty, value); }

    private double MonthRowHeight => LabelFontSize + 8;
    private double DayColumnWidth => (DayLabels is { Count: > 0 } labels ? labels.Max(l => Text(l, LabelBrush).Width) : 0) + 8;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            MonthRowHeight + 7 * (CellSize + CellGap) - CellGap);

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var cells = Cells;
        if (cells is null || cells.Count == 0) return;

        var dayColumn = DayColumnWidth;
        var gridLeft = area.X + dayColumn;
        var gridTop = area.Y + MonthRowHeight;

        // La primera celda define la semana 0; cada celda cae en la columna de su semana ISO relativa y en
        // la fila de su día (lunes = 0), así el calendario queda alineado aunque la serie empiece a mitad
        // de semana.
        var first = cells[0].Date;
        var firstMonday = first.AddDays(-DayIndex(first.DayOfWeek));

        var weeks = (int)Math.Ceiling((cells[^1].Date.DayNumber - firstMonday.DayNumber + 1) / 7.0);
        var pitch = CellSize + CellGap;

        // Con un año entero la anchura disponible manda: si no caben 53 columnas al tamaño pedido, se
        // encogen las celdas en vez de recortar el histórico.
        var available = area.Width - dayColumn;
        if (weeks > 0 && weeks * pitch > available)
        {
            pitch = available / weeks;
            if (pitch <= 1) return;
        }
        var cell = Math.Max(1, pitch - CellGap);

        DrawDayLabels(context, gridLeft, gridTop, pitch, cell);

        var hovered = -1;

        for (var i = 0; i < cells.Count; i++)
        {
            var week = (cells[i].Date.DayNumber - firstMonday.DayNumber) / 7;
            var day = DayIndex(cells[i].Date.DayOfWeek);
            var rect = new Rect(gridLeft + week * pitch, gridTop + day * pitch, cell, cell);
            if (rect.Right > area.Right + 0.5) continue;

            if (HoverPoint is { } h && rect.Inflate(CellGap / 2).Contains(h)) hovered = i;

            context.FillRectangle(IntensityBrush(cells[i].Intensity, cells[i].Count), rect);
            if (cells[i].Date == DateOnly.FromDateTime(DateTime.Today))
                context.DrawRectangle(null, new Pen(Fade(InkBrush, 0.60), 1), rect);
        }

        DrawMonthLabels(context, cells, gridLeft, area, firstMonday, pitch);

        if (hovered >= 0 && HoverPoint is { } point)
            DrawReadout(context, area, point, cells[hovered].Date.ToString("d", CultureInfo.CurrentCulture), cells[hovered].Label);
    }

    /// <summary>Cinco escalones (incluido "nada") en vez de una rampa continua — ver resumen de clase.</summary>
    private IBrush IntensityBrush(double intensity, int count)
    {
        if (count <= 0) return Fade(InkBrush, 0.05);
        return Math.Clamp(intensity, 0, 1) switch
        {
            <= 0.25 => Fade(InkBrush, 0.22),
            <= 0.50 => Fade(InkBrush, 0.42),
            <= 0.75 => Fade(InkBrush, 0.65),
            _ => Fade(InkBrush, 0.92)
        };
    }

    private void DrawDayLabels(DrawingContext context, double gridLeft, double gridTop, double pitch, double cell)
    {
        if (DayLabels is not { Count: 7 } labels) return;
        for (var day = 0; day < 7; day += 2)
        {
            var text = Text(labels[day], LabelBrush);
            context.DrawText(text, new Point(gridLeft - text.Width - 8, gridTop + day * pitch + (cell - text.Height) / 2));
        }
    }

    /// <summary>Rótulo del mes sobre la primera semana que lo estrena, saltándose el que quedaría pegado
    /// al anterior (un mes que solo aporta una columna).</summary>
    private void DrawMonthLabels(DrawingContext context, IReadOnlyList<HeatCell> cells, double gridLeft, Rect area,
        DateOnly firstMonday, double pitch)
    {
        var lastMonth = -1;
        var lastRight = double.NegativeInfinity;

        foreach (var heat in cells)
        {
            if (heat.Date.Month == lastMonth) continue;
            lastMonth = heat.Date.Month;

            var week = (heat.Date.DayNumber - firstMonday.DayNumber) / 7;
            var x = gridLeft + week * pitch;
            var text = Text(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(heat.Date.Month)
                .ToUpper(CultureInfo.CurrentCulture), LabelBrush);
            if (x < lastRight + 10 || x + text.Width > area.Right) continue;

            context.DrawText(text, new Point(x, area.Y));
            lastRight = x + text.Width;
        }
    }

    private static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;
}
