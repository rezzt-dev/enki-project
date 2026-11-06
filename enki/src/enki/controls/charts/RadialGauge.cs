using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace enki.Controls.Charts;

/// <summary>Medidor circular de un único porcentaje (constancia de hábitos, tasa de finalización de
/// tareas). Anillo de fondo a baja opacidad + arco de valor a tinta plena, con la cifra dentro.
///
/// Existe además del <see cref="DonutChart"/> porque el gesto es distinto: aquí no hay reparto entre
/// categorías, hay un progreso contra un 100% implícito — y ese 100% tiene que verse aunque el valor sea
/// del 4%, cosa que un donut de un solo segmento no comunica.</summary>
public sealed class RadialGauge : ChartBase
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Thickness), 12d);

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<RadialGauge, string>(nameof(Caption), string.Empty);

    /// <summary>Sufijo de la cifra central; "%" por defecto porque es el uso mayoritario.</summary>
    public static readonly StyledProperty<string> ValueSuffixProperty =
        AvaloniaProperty.Register<RadialGauge, string>(nameof(ValueSuffix), "%");

    static RadialGauge()
    {
        AffectsRender<RadialGauge>(ValueProperty, MaximumProperty, ThicknessProperty, CaptionProperty, ValueSuffixProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public string ValueSuffix { get => GetValue(ValueSuffixProperty); set => SetValue(ValueSuffixProperty, value); }

    protected override void RenderChart(DrawingContext context, Rect area)
    {
        var size = Math.Min(area.Width, area.Height);
        if (size <= 4) return;

        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        var outer = size / 2 - 1;
        var inner = Math.Max(4, outer - Thickness);

        DonutChart.DrawSegment(context, center, inner, outer, 0, 2 * Math.PI, Fade(InkBrush, 0.08));

        var fraction = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (fraction > 0)
            DonutChart.DrawSegment(context, center, inner, outer, -Math.PI / 2, 2 * Math.PI * fraction, InkBrush);

        var headline = Text(Value.ToString("0", CultureInfo.CurrentCulture) + ValueSuffix, InkBrush,
            Math.Max(14, inner * 0.55), bold: true);
        var hasCaption = !string.IsNullOrEmpty(Caption);
        context.DrawText(headline, new Point(center.X - headline.Width / 2,
            center.Y - headline.Height / 2 - (hasCaption ? headline.Height * 0.28 : 0)));

        if (hasCaption)
        {
            var caption = TextClamped(Caption, LabelBrush, inner * 1.8);
            context.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + headline.Height * 0.30));
        }
    }
}
