using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Matriz de 7 días × 24 horas donde el tamaño (y la opacidad) de cada marca es proporcional a
/// la actividad de esa franja: el clásico "punch card" que revela el ritmo real de trabajo — si escribes
/// de madrugada, si el domingo existe, si la tarde del viernes está muerta.
///
/// Se codifica por tamaño y no solo por opacidad porque en monocromo la opacidad sola satura enseguida:
/// con 168 celdas, la diferencia entre 0,3 y 0,5 de opacidad sobre fondo casi negro es indistinguible,
/// mientras que la diferencia de área se ve al instante.</summary>
public sealed class PunchCard : ChartBase
{
    /// <summary>168 valores, fila mayor: índice = díaDeLaSemana(lunes=0) * 24 + hora.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<PunchCard, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<string>?> DayLabelsProperty =
        AvaloniaProperty.Register<PunchCard, IReadOnlyList<string>?>(nameof(DayLabels));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<PunchCard, double>(nameof(RowHeight), 20d);

    static PunchCard()
    {
        AffectsRender<PunchCard>(ValuesProperty, DayLabelsProperty, RowHeightProperty);
        AffectsMeasure<PunchCard>(RowHeightProperty);
    }

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<string>? DayLabels { get => GetValue(DayLabelsProperty); set => SetValue(DayLabelsProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 7 * RowHeight + LabelFontSize + 10);

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var values = Values;
        if (values is null || values.Count < 7 * 24) return;

        var dayColumn = (DayLabels is { Count: > 0 } labels ? labels.Max(l => Text(l, LabelBrush).Width) : 0) + 10;
        var gridLeft = area.X + dayColumn;
        var gridWidth = area.Right - gridLeft;
        if (gridWidth <= 24) return;

        var columnWidth = gridWidth / 24;
        var max = values.Max();

        for (var day = 0; day < 7; day++)
        {
            var rowTop = area.Y + day * RowHeight;
            var rowMiddle = rowTop + RowHeight / 2;

            if (DayLabels is { Count: 7 } names)
            {
                var label = Text(names[day], LabelBrush);
                context.DrawText(label, new Point(gridLeft - label.Width - 10, rowMiddle - label.Height / 2));
            }

            // Carril tenue por fila: sin él, una fila entera vacía (nunca trabajas los domingos) se
            // confundiría con "aquí no hay gráfico".
            context.FillRectangle(Fade(InkBrush, 0.03), new Rect(gridLeft, rowTop + 1, gridWidth, RowHeight - 2));

            for (var hour = 0; hour < 24; hour++)
            {
                var value = values[day * 24 + hour];
                if (value <= 0) continue;

                var fraction = max <= 0 ? 0 : value / max;
                var maxRadius = Math.Min(columnWidth, RowHeight) / 2 - 1.5;
                // Raíz cuadrada: el ojo compara áreas, no lados — escalar el lado linealmente exagera los
                // valores altos y aplasta los bajos.
                var side = Math.Max(2.5, 2 * maxRadius * Math.Sqrt(fraction));
                var center = new Point(gridLeft + columnWidth * (hour + 0.5), rowMiddle);
                context.FillRectangle(Fade(InkBrush, 0.35 + 0.6 * fraction),
                    new Rect(center.X - side / 2, center.Y - side / 2, side, side));
            }
        }

        DrawHourAxis(context, gridLeft, area.Y + 7 * RowHeight + 6, columnWidth);

        if (HoverPoint is { } hover) DrawHover(context, area, hover, gridLeft, columnWidth);
    }

    private void DrawHourAxis(DrawingContext context, double gridLeft, double top, double columnWidth)
    {
        for (var hour = 0; hour < 24; hour += 3)
        {
            var text = Text(hour.ToString("00", CultureInfo.CurrentCulture), LabelBrush);
            context.DrawText(text, new Point(gridLeft + columnWidth * (hour + 0.5) - text.Width / 2, top));
        }
    }

    private void DrawHover(DrawingContext context, Rect area, Point hover, double gridLeft, double columnWidth)
    {
        var day = (int)((hover.Y - area.Y) / RowHeight);
        var hour = (int)((hover.X - gridLeft) / columnWidth);
        if (day is < 0 or > 6 || hour is < 0 or > 23) return;

        var value = Values![day * 24 + hour];
        if (value <= 0) return;

        var dayName = DayLabels is { Count: 7 } names ? names[day] : string.Empty;
        DrawReadout(context, area, hover,
            $"{dayName} · {hour:00}:00",
            value.ToString("0.##", CultureInfo.CurrentCulture));
    }
}
