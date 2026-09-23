using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using enki.Models;

namespace enki.Controls.Charts;

/// <summary>Ranking horizontal: etiqueta a la izquierda, barra proporcional y valor alineado a la derecha.
/// Es el formato correcto para los "tops" (etiquetas más usadas, carpetas con más notas, notas más largas,
/// hábitos más constantes) porque las etiquetas son texto libre de longitud variable — en vertical habría
/// que rotarlas o recortarlas hasta hacerlas inútiles.
///
/// La barra se pinta detrás del texto a baja opacidad además de como barra sólida, un patrón de "fila de
/// tabla con relleno" que encaja con la rejilla de líneas de 1px del resto de la app.</summary>
public sealed class RankingBars : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartDatum>?> ItemsProperty =
        AvaloniaProperty.Register<RankingBars, IReadOnlyList<ChartDatum>?>(nameof(Items));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<RankingBars, double>(nameof(RowHeight), 26d);

    /// <summary>Ancho reservado para la etiqueta. Si es 0 se calcula como el 42% del ancho total.</summary>
    public static readonly StyledProperty<double> LabelWidthProperty =
        AvaloniaProperty.Register<RankingBars, double>(nameof(LabelWidth));

    /// <summary>Sufijo del valor ("%", " palabras"…). El ViewModel lo pasa ya localizado.</summary>
    public static readonly StyledProperty<string> ValueSuffixProperty =
        AvaloniaProperty.Register<RankingBars, string>(nameof(ValueSuffix), string.Empty);

    /// <summary>Numera las filas (01, 02, 03…) al estilo editorial del resto de la app.</summary>
    public static readonly StyledProperty<bool> ShowRankProperty =
        AvaloniaProperty.Register<RankingBars, bool>(nameof(ShowRank), true);

    static RankingBars()
    {
        AffectsRender<RankingBars>(ItemsProperty, RowHeightProperty, LabelWidthProperty, ValueSuffixProperty, ShowRankProperty);
        AffectsMeasure<RankingBars>(ItemsProperty, RowHeightProperty);
    }

    public IReadOnlyList<ChartDatum>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public string ValueSuffix { get => GetValue(ValueSuffixProperty); set => SetValue(ValueSuffixProperty, value); }
    public bool ShowRank { get => GetValue(ShowRankProperty); set => SetValue(ShowRankProperty, value); }

    /// <summary>El control mide exactamente lo que ocupan sus filas — así una tarjeta con 4 etiquetas no
    /// deja el mismo hueco muerto que una con 10, sin que el XAML tenga que fijar alturas a mano.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Items?.Count ?? 0;
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, count * RowHeight);
    }

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var items = Items;
        if (items is null || items.Count == 0) return;

        var max = items.Max(i => i.Value);
        if (max <= 0) max = 1;

        var rankWidth = ShowRank ? Text("00", LabelBrush).Width + 10 : 0;
        var labelWidth = LabelWidth > 0 ? LabelWidth : Math.Max(60, area.Width * 0.42);
        var valueWidth = items.Max(i => Text(FormatValue(i.Value) + ValueSuffix, LabelBrush, LabelFontSize + 1).Width) + 10;
        var barLeft = area.X + rankWidth + labelWidth + 8;
        var barWidth = Math.Max(8, area.Right - valueWidth - barLeft);

        var hoveredIndex = HoverPoint is { } h
            ? (int)Math.Floor((h.Y - area.Y) / RowHeight)
            : -1;

        for (var i = 0; i < items.Count; i++)
        {
            var top = area.Y + RowHeight * i;
            if (top + RowHeight > area.Bottom + 0.5) break;
            var rowMiddle = top + RowHeight / 2;
            var hovered = i == hoveredIndex;

            if (hovered) context.FillRectangle(Fade(InkBrush, 0.04), new Rect(area.X, top, area.Width, RowHeight));

            if (ShowRank)
            {
                var rank = Text((i + 1).ToString("00", CultureInfo.CurrentCulture), Fade(InkBrush, hovered ? 0.55 : 0.28));
                context.DrawText(rank, new Point(area.X, rowMiddle - rank.Height / 2));
            }

            var label = TextClamped(items[i].Label, hovered ? InkBrush : Fade(InkBrush, 0.75), labelWidth, LabelFontSize + 1.5);
            context.DrawText(label, new Point(area.X + rankWidth, rowMiddle - label.Height / 2));

            // Carril de la barra: la referencia del 100% siempre visible, para que una barra corta se lea
            // como "poco de mucho" y no como "el gráfico está vacío".
            var railHeight = 8.0;
            var railTop = rowMiddle - railHeight / 2;
            context.FillRectangle(Fade(InkBrush, 0.06), new Rect(barLeft, railTop, barWidth, railHeight));

            var filled = Math.Max(2, barWidth * (items[i].Value / max));
            context.FillRectangle(hovered ? InkBrush : Fade(InkBrush, 0.55 + 0.30 * (items[i].Value / max)),
                new Rect(barLeft, railTop, filled, railHeight));

            var value = Text(FormatValue(items[i].Value) + ValueSuffix, hovered ? InkBrush : Fade(InkBrush, 0.60), LabelFontSize + 1);
            context.DrawText(value, new Point(area.Right - value.Width, rowMiddle - value.Height / 2));
        }

        if (hoveredIndex >= 0 && hoveredIndex < items.Count && HoverPoint is { } point
            && !string.IsNullOrEmpty(items[hoveredIndex].Detail))
        {
            DrawReadout(context, area, point, items[hoveredIndex].Label, items[hoveredIndex].Detail);
        }
    }

    private static string FormatValue(double value) =>
        value >= 10000
            ? (value / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "k"
            : value.ToString("0.##", CultureInfo.CurrentCulture);
}
