using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace enki.Controls.Charts;

/// <summary>Base común de todos los gráficos de la página de Estadísticas. Son controles que pintan
/// directamente sobre el <see cref="DrawingContext"/> (no hay librería de charting en el proyecto, ni la
/// habrá: el sistema de diseño es demasiado específico — cero radio, rampa de opacidades sobre la tinta,
/// micro-etiquetas mono en mayúsculas — para reutilizar un tema ajeno).
///
/// Todo el color entra por propiedades enlazables (<see cref="Ink"/>, <see cref="GridLine"/>,
/// <see cref="LabelInk"/>, <see cref="Surface"/>) en vez de resolverse desde código: así el XAML las
/// alimenta con <c>{DynamicResource InkBrush}</c>/<c>{DynamicResource Ink10}</c>… y el gráfico sigue
/// automáticamente el cambio de tema (incluidos los temas personalizados del usuario) igual que cualquier
/// otro control de la app, sin que este fichero conozca ni un solo valor hexadecimal.</summary>
public abstract class ChartBase : Control
{
    /// <summary>Tinta principal: la serie destacada, el trazo del área, el segmento activo.</summary>
    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<ChartBase, IBrush?>(nameof(Ink));

    /// <summary>Serie secundaria — normalmente la misma tinta a menos opacidad (Ink30).</summary>
    public static readonly StyledProperty<IBrush?> SecondaryInkProperty =
        AvaloniaProperty.Register<ChartBase, IBrush?>(nameof(SecondaryInk));

    /// <summary>Rejilla y ejes: líneas de 1px, típicamente Ink10.</summary>
    public static readonly StyledProperty<IBrush?> GridLineProperty =
        AvaloniaProperty.Register<ChartBase, IBrush?>(nameof(GridLine));

    /// <summary>Micro-etiquetas de ejes y leyendas (Ink40).</summary>
    public static readonly StyledProperty<IBrush?> LabelInkProperty =
        AvaloniaProperty.Register<ChartBase, IBrush?>(nameof(LabelInk));

    /// <summary>Fondo de los tooltips que dibuja el propio gráfico.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceProperty =
        AvaloniaProperty.Register<ChartBase, IBrush?>(nameof(Surface));

    public static readonly StyledProperty<FontFamily?> LabelFontFamilyProperty =
        AvaloniaProperty.Register<ChartBase, FontFamily?>(nameof(LabelFontFamily));

    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<ChartBase, double>(nameof(LabelFontSize), 9d);

    /// <summary>Deja de reaccionar al puntero (y de repintar en cada movimiento) en los gráficos que no
    /// tienen nada que revelar al pasar por encima.</summary>
    public static readonly StyledProperty<bool> ShowHoverReadoutProperty =
        AvaloniaProperty.Register<ChartBase, bool>(nameof(ShowHoverReadout), true);

    static ChartBase()
    {
        AffectsRender<ChartBase>(InkProperty, SecondaryInkProperty, GridLineProperty, LabelInkProperty,
            SurfaceProperty, LabelFontFamilyProperty, LabelFontSizeProperty);
    }

    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public IBrush? SecondaryInk { get => GetValue(SecondaryInkProperty); set => SetValue(SecondaryInkProperty, value); }
    public IBrush? GridLine { get => GetValue(GridLineProperty); set => SetValue(GridLineProperty, value); }
    public IBrush? LabelInk { get => GetValue(LabelInkProperty); set => SetValue(LabelInkProperty, value); }
    public IBrush? Surface { get => GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }
    public FontFamily? LabelFontFamily { get => GetValue(LabelFontFamilyProperty); set => SetValue(LabelFontFamilyProperty, value); }
    public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }
    public bool ShowHoverReadout { get => GetValue(ShowHoverReadoutProperty); set => SetValue(ShowHoverReadoutProperty, value); }

    /// <summary>Posición del puntero dentro del control, o <c>null</c> si está fuera. Cada gráfico decide
    /// qué resaltar con ella; la base solo se encarga de mantenerla al día y repintar.</summary>
    protected Point? HoverPoint { get; private set; }

    protected IBrush InkBrush => Ink ?? Brushes.White;
    protected IBrush SecondaryInkBrush => SecondaryInk ?? Fade(InkBrush, 0.35);
    protected IBrush GridBrush => GridLine ?? Fade(InkBrush, 0.10);
    protected IBrush LabelBrush => LabelInk ?? Fade(InkBrush, 0.40);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ShowHoverReadout) return;
        HoverPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoverPoint is null) return;
        HoverPoint = null;
        InvalidateVisual();
    }

    /// <summary>Un <see cref="Control"/> sin fondo no recibe eventos de puntero (el hit-test de Avalonia
    /// solo acierta sobre lo que se ha pintado), así que la base rellena todo el área con transparente
    /// antes de ceder el turno a <see cref="RenderChart"/>. Es el mismo truco que usa cualquier control
    /// "hueco" que quiera hover.</summary>
    public sealed override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        if (area.Width <= 0 || area.Height <= 0) return;
        context.FillRectangle(Brushes.Transparent, area);
        RenderChart(context, area);
    }

    protected abstract void RenderChart(DrawingContext context, Rect area);

    // ===================== Utilidades compartidas =====================

    /// <summary>Multiplica la opacidad de un brush conservando su color — así un gráfico puede derivar
    /// toda su rampa de una sola tinta enlazada, en vez de necesitar un brush por cada matiz.</summary>
    protected static IBrush Fade(IBrush? brush, double opacity) =>
        brush is ISolidColorBrush solid
            ? new SolidColorBrush(solid.Color, Math.Clamp(solid.Opacity * opacity, 0, 1))
            : brush ?? Brushes.Transparent;

    protected FormattedText Text(string text, IBrush brush, double? size = null, bool bold = false) =>
        new(text ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(LabelFontFamily ?? FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal),
            size ?? LabelFontSize, brush);

    /// <summary>Recorta un texto al ancho disponible añadiendo "…", para que una etiqueta larga (una ruta
    /// de carpeta, el título de una nota) nunca se salga del gráfico ni se solape con la barra.</summary>
    protected FormattedText TextClamped(string text, IBrush brush, double maxWidth, double? size = null, bool bold = false)
    {
        var formatted = Text(text, brush, size, bold);
        if (formatted.Width <= maxWidth || text.Length == 0) return formatted;

        var candidate = text;
        while (candidate.Length > 1 && formatted.Width > maxWidth)
        {
            candidate = candidate[..(candidate.Length - 1)];
            formatted = Text(candidate + "…", brush, size, bold);
        }
        return formatted;
    }

    /// <summary>Cartel flotante que sigue al puntero: fondo de superficie, borde de 1px, texto mono.
    /// Se auto-coloca para no salirse del control.</summary>
    protected void DrawReadout(DrawingContext context, Rect area, Point anchor, params string[] lines)
    {
        if (lines.Length == 0) return;

        const double padX = 8, padY = 6, lineGap = 3;
        var formatted = new FormattedText[lines.Length];
        double width = 0, height = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            formatted[i] = Text(lines[i], i == 0 ? InkBrush : LabelBrush, LabelFontSize + (i == 0 ? 0.5 : 0), i == 0);
            width = Math.Max(width, formatted[i].Width);
            height += formatted[i].Height + (i > 0 ? lineGap : 0);
        }

        var boxWidth = width + padX * 2;
        var boxHeight = height + padY * 2;
        var x = Math.Clamp(anchor.X + 12, area.X, Math.Max(area.X, area.Right - boxWidth));
        var y = Math.Clamp(anchor.Y - boxHeight - 8, area.Y, Math.Max(area.Y, area.Bottom - boxHeight));
        var box = new Rect(x, y, boxWidth, boxHeight);

        context.DrawRectangle(Surface ?? Brushes.Black, new Pen(Fade(InkBrush, 0.20), 1), box);

        var cursorY = box.Y + padY;
        foreach (var line in formatted)
        {
            context.DrawText(line, new Point(box.X + padX, cursorY));
            cursorY += line.Height + lineGap;
        }
    }
}
