using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using enki.Views.Editor.Markdown;

namespace enki.Services.Export;

/// <summary>Compone, fuera del árbol visual, la hoja maquetada de una nota para exportarla a PNG o PDF
/// (T8). El editor de notas no sirve como origen del render: <c>AvaloniaEdit</c> virtualiza las líneas, así
/// que rasterizarlo daría solo la parte visible — justamente lo contrario de lo que pide la funcionalidad.
///
/// <para>Se apoya en <see cref="MarkdownLineParser"/>, el mismo analizador por líneas que usa la vista
/// previa del editor, para no estrenar una segunda idea de qué es un encabezado o una cita: aquí solo se
/// traduce cada <see cref="MdLine"/> a controles de Avalonia. Es la única pieza de <c>services/</c> que
/// mira a <c>views/</c>, y lo hace sobre lógica pura (el analizador no toca UI); lo contrario —duplicar el
/// analizador— sería peor.</para>
///
/// <para>Todo el estilo se declara aquí explícitamente (tipografías, tamaños, colores) en vez de dejarlo a
/// los estilos de la app: la hoja se mide y dispone sin estar unida a ninguna ventana, donde los selectores
/// de <c>Styles.axaml</c> no llegan.</para></summary>
public static class NotePageComposer
{
    /// <summary>Ancho de la hoja en píxeles lógicos: A4 a 96 ppp menos los márgenes, que es lo que hace
    /// que el PDF salga con una caja de texto de ancho de lectura razonable en vez de una línea de 1800 px.</summary>
    public const double PageWidth = 794;

    /// <summary>Alto de página para el PDF (A4 a 96 ppp). El PNG no pagina: sale una sola lámina tan alta
    /// como haga falta, que es lo que espera quien pide una imagen.</summary>
    public const double PageHeight = 1123;

    private const double Margin = 56;
    private const double BodyFontSize = 13.5;
    private const double LineHeight = 21;

    /// <summary>Tope de líneas maquetadas de una sola nota. Una nota de más de 20.000 líneas no es un
    /// documento que exportar a una imagen, y componer un <c>TextBlock</c> por línea sin tope deja la app
    /// colgada sin salida — mismo criterio de "acotar el bucle" que
    /// <c>PolarTaskService.MaxRolloverIterations</c>.</summary>
    internal const int MaxLines = 20_000;

    /// <summary>Hoja completa, ya medida y dispuesta, lista para
    /// <see cref="VisualSnapshotRenderer.Capture"/>. El título va como encabezado de nivel 1 de la propia
    /// hoja, no como cabecera de lámina: en un documento de texto el título es parte del documento.</summary>
    public static Control Build(string title, string markdown, ExportAppearance appearance)
    {
        var content = BuildContent(title, markdown, appearance);
        content.Width = PageWidth;
        content.Measure(new Size(PageWidth, double.PositiveInfinity));
        content.Arrange(new Rect(0, 0, PageWidth, content.DesiredSize.Height));
        return content;
    }

    /// <summary>Trocea la hoja en páginas de <see cref="PageHeight"/> para el PDF. Cada página es un
    /// recorte del mismo contenido desplazado hacia arriba, no un re-maquetado: así una tabla o un párrafo
    /// nunca se re-parten distinto entre una página y la siguiente.
    ///
    /// <para>Devuelve un control por página, ya dispuesto; el llamador los rasteriza en orden. El corte es
    /// por altura fija y puede partir una línea por la mitad — es la concesión aceptada de no traer un
    /// motor de maquetado paginado por una exportación.</para></summary>
    public static IReadOnlyList<Control> BuildPages(string title, string markdown, ExportAppearance appearance)
    {
        var content = BuildContent(title, markdown, appearance);
        content.Width = PageWidth;
        content.Measure(new Size(PageWidth, double.PositiveInfinity));

        var totalHeight = Math.Max(content.DesiredSize.Height, PageHeight);
        var pageCount = (int)Math.Ceiling(totalHeight / PageHeight);
        var pages = new List<Control>(pageCount);

        for (var index = 0; index < pageCount; index++)
        {
            // Una instancia de contenido por página: un mismo control no puede estar en dos árboles, y
            // rasterizarlos uno a uno reusando el mismo exigiría re-disponerlo entre renders.
            var pageContent = index == 0 ? content : BuildContent(title, markdown, appearance);
            pageContent.Width = PageWidth;

            var canvas = new Canvas
            {
                Width = PageWidth,
                Height = PageHeight,
                ClipToBounds = true,
                Background = new SolidColorBrush(appearance.Background)
            };
            Canvas.SetTop(pageContent, -index * PageHeight);
            canvas.Children.Add(pageContent);

            canvas.Measure(new Size(PageWidth, PageHeight));
            canvas.Arrange(new Rect(0, 0, PageWidth, PageHeight));
            pages.Add(canvas);
        }

        return pages;
    }

    private static StackPanel BuildContent(string title, string markdown, ExportAppearance appearance)
    {
        var body = new SolidColorBrush(appearance.Body);
        var muted = new SolidColorBrush(appearance.Muted);
        var surface = new SolidColorBrush(appearance.Surface);

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(Margin, Margin, Margin, Margin),
            Background = new SolidColorBrush(appearance.Background)
        };

        if (!string.IsNullOrWhiteSpace(title))
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = appearance.FontFamily,
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(appearance.Title),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });

        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fences = MarkdownLineParser.ComputeFences(string.Join('\n', lines));

        StackPanel? codeBlock = null;
        for (var index = 0; index < lines.Length && index < MaxLines; index++)
        {
            var text = lines[index];
            // ComputeFences indexa por número de línea (base 1), no por índice de la lista.
            var parsed = MarkdownLineParser.Parse(text, 0, fences.TryGetValue(index + 1, out var fence) ? fence : default);

            if (parsed.Kind == MdBlockKind.CodeFence)
            {
                // Las dos líneas de ``` delimitan el bloque pero no forman parte de él.
                if (parsed.MarkerEnd > parsed.MarkerStart)
                {
                    codeBlock = codeBlock is null ? StartCodeBlock(stack, surface) : null;
                    continue;
                }

                codeBlock ??= StartCodeBlock(stack, surface);
                codeBlock.Children.Add(new TextBlock
                {
                    Text = text,
                    FontFamily = appearance.MonoFontFamily,
                    FontSize = BodyFontSize - 1,
                    Foreground = body,
                    TextWrapping = TextWrapping.NoWrap
                });
                continue;
            }

            codeBlock = null;
            if (BuildBlock(parsed, text, appearance, body, muted) is { } control) stack.Children.Add(control);
        }

        if (lines.Length > MaxLines)
            stack.Children.Add(new TextBlock
            {
                Text = "…",
                FontFamily = appearance.FontFamily,
                FontSize = BodyFontSize,
                Foreground = muted
            });

        return stack;
    }

    private static StackPanel StartCodeBlock(Panel parent, IBrush surface)
    {
        var block = new StackPanel { Orientation = Orientation.Vertical };
        parent.Children.Add(new Border
        {
            Background = surface,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 6, 0, 10),
            Child = block
        });
        return block;
    }

    private static Control? BuildBlock(MdLine parsed, string text, ExportAppearance appearance, IBrush body, IBrush muted)
    {
        if (parsed.Kind == MdBlockKind.HorizontalRule)
            return new Rectangle { Height = 1, Fill = muted, Margin = new Thickness(0, 12), HorizontalAlignment = HorizontalAlignment.Stretch };

        // Una línea en blanco es separación real en Markdown, así que se conserva como aire, no se come.
        if (string.IsNullOrWhiteSpace(text)) return new Border { Height = 8 };

        var content = new TextBlock
        {
            FontFamily = appearance.FontFamily,
            FontSize = BodyFontSize,
            LineHeight = LineHeight,
            Foreground = body,
            TextWrapping = TextWrapping.Wrap
        };

        var prefix = parsed.Kind switch
        {
            MdBlockKind.BulletList => "•  ",
            MdBlockKind.TaskList => parsed.TaskChecked ? "☑  " : "☐  ",
            MdBlockKind.OrderedList => text[parsed.MarkerStart..parsed.MarkerEnd].TrimStart() + "  ",
            _ => string.Empty
        };

        if (parsed.Kind == MdBlockKind.Heading)
        {
            content.FontWeight = FontWeight.Bold;
            content.FontSize = parsed.HeadingLevel switch { 1 => 22, 2 => 18.5, 3 => 16, 4 => 14.5, _ => BodyFontSize };
            content.LineHeight = content.FontSize * 1.45;
            content.Margin = new Thickness(0, 14, 0, 6);
        }
        else if (parsed.Kind is MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList)
        {
            content.Margin = new Thickness(18, 1, 0, 1);
        }

        AppendInlines(content, text, parsed, prefix, appearance, muted);

        if (parsed.Kind != MdBlockKind.Blockquote) return content;

        // Cita: barra de acento a la izquierda, mismo recurso visual que el resto de la app usa para
        // "esto es una nota al margen" (Border.trash-hint, Button.dropdown-item.reminder-item).
        content.Foreground = muted;
        content.Margin = new Thickness(0);
        return new Border
        {
            BorderBrush = muted,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Margin = new Thickness(0, 4),
            Child = content
        };
    }

    /// <summary>Vuelca el contenido de la línea como <see cref="Run"/>s, aplicando negrita, cursiva,
    /// tachado y código en línea y ocultando sus marcadores — lo mismo que hace la vista previa del editor,
    /// pero sobre <c>Inlines</c> en vez de sobre el colorizador de AvaloniaEdit. Solo se aplican los spans
    /// de primer nivel: los anidados se quedan con la tipografía del que los contiene, que es una pérdida
    /// invisible en la práctica y evita construir un árbol de rangos para una exportación.</summary>
    private static void AppendInlines(TextBlock target, string text, MdLine parsed, string prefix,
        ExportAppearance appearance, IBrush muted)
    {
        if (prefix.Length > 0) target.Inlines!.Add(new Run(prefix) { Foreground = muted });

        var start = Math.Clamp(parsed.ContentStart, 0, text.Length);
        var spans = parsed.Spans
            .Where(s => s.Start >= start && s.End <= text.Length && s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        var cursor = start;
        foreach (var span in spans)
        {
            if (span.Start < cursor) continue;   // anidado dentro del anterior: ya emitido.
            if (span.Start > cursor) target.Inlines!.Add(new Run(text[cursor..span.Start]));

            var contentStart = Math.Clamp(span.ContentStart, span.Start, span.End);
            var contentEnd = Math.Clamp(span.ContentEnd, contentStart, span.End);
            if (contentEnd > contentStart) target.Inlines!.Add(StyledRun(text[contentStart..contentEnd], span.Kind, appearance, muted));
            cursor = span.End;
        }

        if (cursor < text.Length) target.Inlines!.Add(new Run(text[cursor..]));
    }

    private static Run StyledRun(string text, MdSpanKind kind, ExportAppearance appearance, IBrush muted)
    {
        var run = new Run(text);
        switch (kind)
        {
            case MdSpanKind.Bold:
                run.FontWeight = FontWeight.Bold;
                break;
            case MdSpanKind.Italic:
                run.FontStyle = FontStyle.Italic;
                break;
            case MdSpanKind.BoldItalic:
                run.FontWeight = FontWeight.Bold;
                run.FontStyle = FontStyle.Italic;
                break;
            case MdSpanKind.Strikethrough:
                run.TextDecorations = TextDecorations.Strikethrough;
                break;
            case MdSpanKind.InlineCode:
                run.FontFamily = appearance.MonoFontFamily;
                break;
            case MdSpanKind.Link:
            case MdSpanKind.WikiLink:
                run.TextDecorations = TextDecorations.Underline;
                break;
            case MdSpanKind.HtmlTag:
                // Las etiquetas HTML se ocultan igual que en la vista previa: no son texto del documento.
                run.Text = string.Empty;
                break;
            default:
                run.Foreground = muted;
                break;
        }

        return run;
    }
}
