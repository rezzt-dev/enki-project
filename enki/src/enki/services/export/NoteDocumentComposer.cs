using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using enki.Views.Editor.Markdown;
using Material.Icons;
using Path = Avalonia.Controls.Shapes.Path;

namespace enki.Services.Export;

/// <summary>Un bloque del documento ya maquetado al ancho de la caja de texto: un párrafo, una lista, una
/// tabla, una imagen… La unidad con la que razona la paginación.</summary>
public sealed class DocumentBlock
{
    internal DocumentBlock(Control visual, double spaceBefore, bool keepWithNext, Control? repeatedHeader = null)
    {
        Visual = visual;
        SpaceBefore = spaceBefore;
        KeepWithNext = keepWithNext;
        RepeatedHeader = repeatedHeader;
    }

    public Control Visual { get; }
    public double SpaceBefore { get; }
    public bool KeepWithNext { get; }

    /// <summary>En una tabla, su fila de encabezados: se repite en lo alto de cada página por la que
    /// continúe la tabla.</summary>
    internal Control? RepeatedHeader { get; }

    /// <summary>El bloque visto desde el paginador: alto, cortes seguros y reglas de composición.</summary>
    internal PaginationBlock ToPagination()
    {
        var (breaks, fallback) = DocumentBreaks.Find(Visual);
        return new PaginationBlock(Visual.Bounds.Height, breaks, fallback, SpaceBefore, KeepWithNext,
            RepeatedHeader?.Bounds.Height ?? 0);
    }
}

/// <summary>El documento de una nota, compuesto y maquetado, a falta de paginar.</summary>
public sealed record NoteDocument(IReadOnlyList<DocumentBlock> Blocks, double Width);

/// <summary>
/// Compone, fuera del árbol visual, el documento de una nota exportada a PDF o a PNG: traduce el Markdown a
/// bloques de controles de Avalonia (texto, tablas, imágenes…) maquetados al ancho de la caja de texto.
///
/// <para><b>El editor manda.</b> Todo lo que decide qué es cada cosa sale de los mismos analizadores que
/// alimentan el live preview —<see cref="MarkdownLineParser"/>, <see cref="MarkdownTableParser"/>,
/// <see cref="MarkdownHtmlParser"/>, <see cref="MarkdownCallouts"/>, <see cref="MarkdownSpecialBlocks"/>—,
/// así que lo que se ve al editar es lo que sale en el documento: un salto de línea simple sigue siendo un
/// salto de línea, las etiquetas HTML que el editor reconoce (<c>&lt;div align="center"&gt;</c>,
/// <c>&lt;u&gt;</c>, <c>&lt;br&gt;</c>…) alinean y dan formato igual, una imagen con
/// <c>![alt|300](…)</c> mide lo mismo, los avisos llevan su icono y su tono, y el código se colorea con las
/// mismas gramáticas. Aquí no hay una segunda idea de qué es un encabezado.</para>
///
/// <para>Cada línea de texto es su propio <see cref="TextBlock"/>, como en el editor —donde cada línea del
/// documento es una línea visual— y así la paginación sabe dónde empieza y acaba cada línea maquetada para
/// no cortar nunca ninguna por la mitad (ver <see cref="DocumentBreaks"/> y
/// <see cref="DocumentPaginator"/>).</para>
///
/// <para>Todo el estilo se declara aquí explícitamente (<see cref="NoteDocumentStyle"/>): el documento se
/// mide y se dispone sin estar unido a ninguna ventana, donde los estilos de <c>Styles.axaml</c> no
/// llegan.</para>
/// </summary>
internal sealed class NoteDocumentComposer
{
    /// <summary>Tope de líneas maquetadas de una sola nota. Una nota de más de 20.000 líneas no es un
    /// documento que exportar, y componer un control por línea sin tope dejaría la app colgada sin salida —
    /// mismo criterio de "acotar el bucle" que <c>PolarTaskService.MaxRolloverIterations</c>.</summary>
    internal const int MaxLines = 20_000;

    /// <summary>Clase que marca un control cuyo contenido no se debe repartir entre dos páginas (una fila
    /// de tabla): <see cref="DocumentBreaks"/> la trata como una sola unidad.</summary>
    internal const string KeepTogetherClass = "export-keep-together";

    private static readonly FontFeatureCollection NoLigatures = [FontFeature.Parse("-liga")];

    private readonly NoteDocumentStyle _s;
    private readonly NoteDocumentAssets _assets;
    private readonly NoteExportSource _source;
    private readonly double _maxImageHeight;

    private NoteDocumentComposer(NoteDocumentStyle style, NoteDocumentAssets assets, NoteExportSource source,
        double maxImageHeight)
    {
        _s = style;
        _assets = assets;
        _source = source;
        _maxImageHeight = maxImageHeight;
    }

    /// <param name="maxImageHeight">Alto máximo de una imagen: el de la caja de texto de la página, para que
    /// ninguna imagen tenga que partirse entre dos páginas.</param>
    public static NoteDocument Compose(NoteExportSource source, NoteDocumentStyle style, NoteDocumentAssets assets,
        double width, double maxImageHeight)
    {
        var composer = new NoteDocumentComposer(style, assets, source, maxImageHeight);
        var scope = new Scope(source.NoteDirectory,
            source.NotePath is null ? Array.Empty<string>() : [source.NotePath], width);

        var markdown = NoteTagHelper.StripFrontMatter(source.Markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var flow = new BlockFlow(style);
        composer.AddTitle(flow, source, markdown, width);
        composer.ComposeMarkdown(flow, markdown, scope);

        foreach (var block in flow.Blocks) Layout(block.Visual, width);
        return new NoteDocument(flow.Blocks, width);
    }

    /// <summary>Mide y dispone un bloque suelto al ancho dado. Se dispone a ese ancho exacto (no al que
    /// pida) para que una imagen centrada o un párrafo alineado a la derecha tengan dónde colocarse.</summary>
    private static void Layout(Control visual, double width)
    {
        // Ancho fijo y no solo el disponible: al dibujar, el bloque se coloca dentro de un Canvas (que mide
        // a sus hijos con ancho infinito) y tiene que maquetarse exactamente igual que al paginarlo.
        visual.Width = width;
        visual.Measure(new Size(width, double.PositiveInfinity));
        visual.Arrange(new Rect(0, 0, width, visual.DesiredSize.Height));
    }

    // ===================== Flujo de bloques y espaciado =====================

    private enum BlockKind { Title, Paragraph, Heading, List, Quote, Callout, Code, Table, Image, Rule, Math, Embed }

    /// <summary>Lista de bloques en construcción más el espaciado entre ellos. El aire entre dos bloques
    /// depende de qué son y de cuántas líneas en blanco los separan en la nota: una línea en blanco es el
    /// aire normal entre párrafos; las demás, una línea vacía más cada una (en el editor también se ven);
    /// sin línea en blanco, el mínimo.</summary>
    private sealed class BlockFlow(NoteDocumentStyle style)
    {
        private const int MaxExtraBlankLines = 4;

        private BlockKind? _previous;
        private int _blankLines;

        public List<DocumentBlock> Blocks { get; } = new();

        public void BlankLine() => _blankLines++;

        public void Add(Control visual, BlockKind kind, int headingLevel = 0, Control? repeatedHeader = null)
        {
            var s = style.FontSize;
            double gap = 0;
            if (_previous is { } previous)
            {
                gap = _blankLines > 0 ? s * 0.75 : s * 0.3;
                if (previous == BlockKind.Heading) gap = s * 0.4;
                if (previous == BlockKind.Title) gap = s * 1.4;
                if (kind == BlockKind.Heading) gap = Math.Max(gap, s * (headingLevel <= 2 ? 1.35 : 1.0));
                if (kind == BlockKind.Rule || previous == BlockKind.Rule) gap = Math.Max(gap, s * 0.8);
                gap += Math.Min(Math.Max(0, _blankLines - 1), MaxExtraBlankLines) * style.LineHeight;
            }

            Blocks.Add(new DocumentBlock(visual, gap, kind == BlockKind.Heading, repeatedHeader));
            _previous = kind;
            _blankLines = 0;
        }
    }

    /// <summary>Contexto de la nota que se está componiendo: la propia o una transcluida dentro de ella. De
    /// él salen la carpeta contra la que se resuelven sus imágenes, la cadena de notas que la contienen
    /// (para no transcluir en bucle) y el ancho disponible.</summary>
    private sealed record Scope(string? NoteDirectory, IReadOnlyList<string> Ancestry, double Width);

    // ===================== Título =====================

    /// <summary>Cabecera del documento: el título de la nota, sus etiquetas y una regla, como la
    /// exportación a HTML. Si la nota ya empieza por un encabezado de nivel 1 con su mismo título (lo
    /// habitual en notas traídas de Obsidian), no se repite.</summary>
    private void AddTitle(BlockFlow flow, NoteExportSource source, string markdown, double width)
    {
        if (string.IsNullOrWhiteSpace(source.Title) || StartsWithOwnTitle(markdown, source.Title)) return;

        var header = new StackPanel { Orientation = Orientation.Vertical };
        var title = Text(_s.HeadingSize(1) * 1.1, _s.Ink, Math.Round(_s.HeadingSize(1) * 1.1 * 1.25, 2), FontWeight.Bold);
        title.Inlines!.Add(new Run(source.Title));
        header.Children.Add(title);

        if (source.Tags.Count > 0)
        {
            var tags = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, _s.FontSize * 0.6, 0, 0) };
            foreach (var tag in source.Tags)
            {
                // En la tipografía del cuerpo y no en la de código: Skia no recorta las fuentes que incrusta,
                // y la de código del editor (con todos sus iconos) añadiría más de un mega a cualquier PDF
                // con etiquetas aunque la nota no tenga ni una línea de código.
                var label = new TextBlock
                {
                    Text = "#" + tag.ToUpperInvariant(),
                    FontFamily = _s.BodyFont,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = Math.Round(_s.FontSize * 0.68, 2),
                    Foreground = _s.InkSecondary,
                    FontFeatures = NoLigatures,
                    LetterSpacing = 0.8
                };
                tags.Children.Add(new Border
                {
                    BorderBrush = _s.Rule,
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(_s.FontSize * 0.45, _s.FontSize * 0.12),
                    Margin = new Thickness(0, 0, _s.FontSize * 0.4, _s.FontSize * 0.35),
                    Child = label
                });
            }
            header.Children.Add(tags);
        }

        header.Children.Add(new Border
        {
            Height = 0.9,
            Background = _s.Ink,
            Margin = new Thickness(0, _s.FontSize * (source.Tags.Count > 0 ? 0.45 : 0.8), 0, 0)
        });
        flow.Add(header, BlockKind.Title);
    }

    private static bool StartsWithOwnTitle(string markdown, string title)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = MarkdownLineParser.Parse(raw, 0, default);
            return line.Kind == MdBlockKind.Heading && line.HeadingLevel == 1
                   && string.Equals(raw[line.ContentStart..].Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ===================== Bloques =====================

    private void ComposeMarkdown(BlockFlow flow, string text, Scope scope)
    {
        if (text.Length == 0) return;

        var lines = text.Split('\n');
        var lineCount = Math.Min(lines.Length, MaxLines);
        var fences = MarkdownLineParser.ComputeFences(text);
        var tables = MarkdownTableParser.Scan(text, fences);
        var htmlStates = MarkdownHtmlParser.ComputeLineStates(text, fences);
        var callouts = MarkdownCallouts.Scan(text, fences);
        var mathBlocks = MarkdownSpecialBlocks.Scan(text, fences)
            .Where(b => b.Kind == MarkdownSpecialBlockKind.Math)
            .ToDictionary(b => b.StartLine);

        // Todas las consultas por línea van con índice de base 0; los escaneos de documento indexan por
        // número de línea (base 1), igual que en el editor.
        MdLine Parse(int i) => MarkdownLineParser.Parse(lines[i], 0, fences.TryGetValue(i + 1, out var fence) ? fence : default);
        bool IsCode(int i) => fences.TryGetValue(i + 1, out var fence) && fence.IsCode;
        HtmlLineState Html(int i) => htmlStates.TryGetValue(i + 1, out var state) ? state : HtmlLineState.Empty;

        var i = 0;
        while (i < lineCount)
        {
            if (IsCode(i))
            {
                i = AddCodeBlock(flow, lines, fences, i, lineCount);
                continue;
            }

            if (mathBlocks.TryGetValue(i + 1, out var math))
            {
                AddDisplayMath(flow, math.Source);
                i = math.EndLine;
                continue;
            }

            if (tables.TryGetValue(i + 1, out var tableRow) && tableRow.Kind == MdTableRowKind.Header)
            {
                var table = BuildTable(lines, Parse, Html, tableRow.Table, scope);
                flow.Add(table.Visual, BlockKind.Table, repeatedHeader: table.Header);
                i = tableRow.Table.EndLine;
                continue;
            }

            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                flow.BlankLine();
                i++;
                continue;
            }

            var parsed = Parse(i);
            if (IsMarkupOnly(raw, parsed))
            {
                // Una línea que solo abre o cierra un contenedor (<div align="center">) no se ve en el
                // editor y aquí tampoco ocupa sitio; un <br> suelto sí es una línea vacía.
                if (parsed.HtmlTags.Any(t => t.Name == "br")) flow.BlankLine();
                i++;
                continue;
            }

            if (callouts.TryGetValue(i + 1, out var callout) && callout.IsHeader)
            {
                flow.Add(BuildCallout(lines, Parse, Html, callout, scope), BlockKind.Callout);
                i = callout.EndLine;
                continue;
            }

            switch (parsed.Kind)
            {
                case MdBlockKind.Heading:
                    flow.Add(BuildHeading(raw, parsed, Html(i), scope), BlockKind.Heading, parsed.HeadingLevel);
                    i++;
                    break;

                case MdBlockKind.HorizontalRule:
                    flow.Add(new Border { Height = 0.75, Background = _s.Rule }, BlockKind.Rule);
                    i++;
                    break;

                case MdBlockKind.Blockquote:
                    i = AddQuote(flow, lines, Parse, Html, IsCode, callouts, i, lineCount, scope);
                    break;

                case MdBlockKind.BulletList:
                case MdBlockKind.OrderedList:
                case MdBlockKind.TaskList:
                    i = AddList(flow, lines, Parse, Html, IsCode, i, lineCount, scope);
                    break;

                default:
                    if (TryAddLoneImage(flow, raw, parsed, Html(i), scope) || TryAddTransclusion(flow, raw, parsed, scope))
                    {
                        i++;
                        break;
                    }
                    i = AddParagraph(flow, lines, Parse, Html, IsCode, tables, mathBlocks, callouts, i, lineCount, scope);
                    break;
            }
        }

        if (lines.Length > MaxLines)
        {
            var more = Text(_s.FontSize, _s.InkMuted, _s.LineHeight);
            more.Inlines!.Add(new Run("…"));
            flow.Add(more, BlockKind.Paragraph);
        }
    }

    /// <summary>Párrafo: las líneas de texto consecutivas, una debajo de otra. Cada línea es un
    /// <see cref="TextBlock"/> propio porque en el editor cada línea del documento es una línea visible —
    /// un salto simple no se funde con la línea siguiente como haría CommonMark estricto.</summary>
    private int AddParagraph(BlockFlow flow, string[] lines, Func<int, MdLine> parse, Func<int, HtmlLineState> html,
        Func<int, bool> isCode, Dictionary<int, MdTableRow> tables, Dictionary<int, MarkdownSpecialBlock> mathBlocks,
        Dictionary<int, MdCalloutLine> callouts, int start, int count, Scope scope)
    {
        var paragraph = new StackPanel { Orientation = Orientation.Vertical };
        var i = start;
        while (i < count && !isCode(i) && !string.IsNullOrWhiteSpace(lines[i]))
        {
            if (i > start && (tables.ContainsKey(i + 1) || mathBlocks.ContainsKey(i + 1) || callouts.ContainsKey(i + 1))) break;

            var line = parse(i);
            if (line.Kind != MdBlockKind.Paragraph) break;
            if (i > start && (IsMarkupOnly(lines[i], line) || IsLoneImage(lines[i], line) || IsLoneTransclusion(lines[i], line))) break;

            var state = html(i);
            var block = Text(_s.FontSize, _s.Ink, _s.LineHeight, align: BodyAlignment(state.Align, lines[i], line));
            AppendInlines(block, lines[i], line, line.ContentStart, lines[i].Length, state.OpenInlineTags, scope, scope.Width);
            paragraph.Children.Add(block);
            i++;
        }

        if (paragraph.Children.Count > 0) flow.Add(paragraph, BlockKind.Paragraph);
        return Math.Max(i, start + 1);
    }

    private TextBlock BuildHeading(string raw, MdLine parsed, HtmlLineState state, Scope scope)
    {
        var size = _s.HeadingSize(parsed.HeadingLevel);
        var heading = Text(size, _s.Ink, Math.Round(size * 1.3, 2), FontWeight.Bold, Alignment(state.Align));
        AppendInlines(heading, raw, parsed, parsed.ContentStart, raw.Length, state.OpenInlineTags, scope, scope.Width);
        return heading;
    }

    // ----- Listas -----

    /// <summary>Lista: los elementos consecutivos de viñetas, numerados o de tareas, anidados por su sangría
    /// (una sangría mayor que la del elemento anterior abre un nivel; una menor vuelve al nivel que tenía esa
    /// sangría). Los números son los que escribió el usuario, igual que en el editor.</summary>
    private int AddList(BlockFlow flow, string[] lines, Func<int, MdLine> parse, Func<int, HtmlLineState> html,
        Func<int, bool> isCode, int start, int count, Scope scope)
    {
        var list = new StackPanel { Orientation = Orientation.Vertical, Spacing = Math.Round(_s.FontSize * 0.12, 2) };
        var indents = new List<int>();
        var i = start;

        while (i < count && !isCode(i) && !string.IsNullOrWhiteSpace(lines[i]))
        {
            var line = parse(i);
            if (line.Kind is not (MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList)) break;

            var column = IndentColumns(lines[i]);
            if (indents.Count == 0 || column > indents[^1]) indents.Add(column);
            else
                while (indents.Count > 1 && column < indents[^1]) indents.RemoveAt(indents.Count - 1);
            var level = indents.Count - 1;

            list.Children.Add(BuildListItem(lines[i], line, html(i), level, scope));
            i++;
        }

        flow.Add(list, BlockKind.List);
        return Math.Max(i, start + 1);
    }

    private Grid BuildListItem(string raw, MdLine line, HtmlLineState state, int level, Scope scope)
    {
        var s = _s.FontSize;
        var indent = Math.Min(level, 8) * s * 1.5;

        Control marker;
        double markerWidth;
        if (line.Kind == MdBlockKind.OrderedList)
        {
            var number = raw[Math.Clamp(line.MarkerStart, 0, raw.Length)..Math.Clamp(line.MarkerEnd, 0, raw.Length)].Trim();
            var label = Text(s, _s.InkSecondary, _s.LineHeight, align: TextAlignment.Right);
            label.TextWrapping = TextWrapping.NoWrap;
            label.Inlines!.Add(new Run(number));
            label.Measure(Size.Infinity);
            markerWidth = Math.Max(s * 1.6, label.DesiredSize.Width + s * 0.5);
            label.Margin = new Thickness(0, 0, s * 0.45, 0);
            marker = label;
        }
        else
        {
            markerWidth = s * 1.35;
            marker = line.Kind == MdBlockKind.TaskList ? Checkbox(line.TaskChecked) : Bullet(level);
        }

        var content = Text(s, line.TaskChecked ? _s.InkMuted : _s.Ink, _s.LineHeight,
            align: BodyAlignment(state.Align, raw, line));
        if (line.TaskChecked) content.TextDecorations = TextDecorations.Strikethrough;
        AppendInlines(content, raw, line, line.ContentStart, raw.Length, state.OpenInlineTags, scope,
            scope.Width - indent - markerWidth);

        var item = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(indent + markerWidth)),
                new ColumnDefinition(GridLength.Star)
            }
        };
        marker.Margin = new Thickness(indent, marker.Margin.Top, marker.Margin.Right, 0);
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(content, 1);
        item.Children.Add(marker);
        item.Children.Add(content);
        return item;
    }

    /// <summary>Viñeta dibujada como forma, no como carácter: no depende de que la tipografía elegida en el
    /// editor tenga el glifo, y sale nítida a cualquier tamaño. Círculo, circunferencia y cuadrado según el
    /// nivel, como hacen los procesadores de texto.</summary>
    private Shape Bullet(int level)
    {
        var size = Math.Round(_s.FontSize * 0.34, 2);
        Shape shape = (level % 3) switch
        {
            0 => new Ellipse { Fill = _s.Ink },
            1 => new Ellipse { Stroke = _s.Ink, StrokeThickness = 0.8 },
            _ => new Rectangle { Fill = _s.Ink }
        };
        shape.Width = size;
        shape.Height = size;
        shape.HorizontalAlignment = HorizontalAlignment.Left;
        shape.VerticalAlignment = VerticalAlignment.Top;
        shape.Margin = new Thickness(0, (_s.LineHeight - size) / 2, 0, 0);
        return shape;
    }

    /// <summary>Casilla de una tarea: cuadrada (sin esquinas redondeadas, como todo el sistema de diseño) y,
    /// marcada, rellena de tinta con la marca en el color del papel.</summary>
    private Border Checkbox(bool isChecked)
    {
        var size = Math.Round(_s.FontSize * 0.8, 2);
        var box = new Border
        {
            Width = size,
            Height = size,
            BorderBrush = isChecked ? _s.Ink : _s.InkSecondary,
            BorderThickness = new Thickness(0.9),
            Background = isChecked ? _s.Ink : null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, (_s.LineHeight - size) / 2, 0, 0)
        };
        if (isChecked)
            box.Child = new Path
            {
                Data = Geometry.Parse("M 1.5,5 L 4,7.5 L 8.5,2.5"),
                Stroke = _s.Paper,
                StrokeThickness = 1.4,
                StrokeLineCap = PenLineCap.Square,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(size * 0.16)
            };
        return box;
    }

    private static int IndentColumns(string raw)
    {
        var columns = 0;
        foreach (var c in raw)
        {
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4;
            else break;
        }
        return columns;
    }

    // ----- Citas y avisos -----

    private double QuoteLevelWidth => Math.Round(_s.FontSize * 1.05, 2);

    /// <summary>Cita: una fila por línea, con una barra vertical por cada nivel de anidamiento — la misma
    /// geometría que dibuja el editor (<see cref="MarkdownQuoteLayout"/>), pasada a puntos. Las filas van
    /// pegadas para que las barras sean continuas.</summary>
    private int AddQuote(BlockFlow flow, string[] lines, Func<int, MdLine> parse, Func<int, HtmlLineState> html,
        Func<int, bool> isCode, Dictionary<int, MdCalloutLine> callouts, int start, int count, Scope scope)
    {
        var quote = new StackPanel { Orientation = Orientation.Vertical };
        var i = start;
        while (i < count && !isCode(i))
        {
            if (i > start && callouts.TryGetValue(i + 1, out var c) && c.IsHeader) break;
            var line = parse(i);
            if (line.Kind != MdBlockKind.Blockquote) break;

            var depth = Math.Clamp(line.QuoteDepth, 1, 8);
            var row = new Panel();
            for (var level = 0; level < depth; level++)
                row.Children.Add(new Rectangle
                {
                    Width = Math.Round(_s.FontSize * 0.17, 2),
                    Fill = _s.InkMuted,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(level * QuoteLevelWidth, 0, 0, 0)
                });

            var state = html(i);
            var content = Text(_s.FontSize, _s.InkSecondary, _s.LineHeight, align: BodyAlignment(state.Align, lines[i], line));
            content.Margin = new Thickness(depth * QuoteLevelWidth, 0, 0, 0);
            AppendInlines(content, lines[i], line, line.ContentStart, lines[i].Length, state.OpenInlineTags, scope,
                scope.Width - depth * QuoteLevelWidth);
            row.Children.Add(content);
            quote.Children.Add(row);
            i++;
        }

        flow.Add(quote, BlockKind.Quote);
        return Math.Max(i, start + 1);
    }

    /// <summary>Aviso (<c>&gt; [!warning] Título</c>): caja con la barra y un velo del color de su tono, su
    /// icono y su título, igual que en el editor.</summary>
    private Border BuildCallout(string[] lines, Func<int, MdLine> parse, Func<int, HtmlLineState> html,
        MdCalloutLine callout, Scope scope)
    {
        var s = _s.FontSize;
        var tone = callout.Tone switch
        {
            MdCalloutTone.Success => _s.Success,
            MdCalloutTone.Warning => _s.Warning,
            MdCalloutTone.Danger => _s.Danger,
            _ => _s.InkSecondary
        };
        var padding = new Thickness(s * 0.95, s * 0.55, s * 0.95, s * 0.6);
        var innerWidth = scope.Width - padding.Left - padding.Right - 2.25;
        var body = new StackPanel { Orientation = Orientation.Vertical };

        var headerIndex = callout.StartLine - 1;
        var header = parse(headerIndex);
        var iconSize = Math.Round(s * 1.05, 2);
        var icon = new Path
        {
            Data = Geometry.Parse(MaterialIconDataProvider.GetData(MarkdownCallouts.IconOf(callout.Kind))),
            Fill = tone,
            Stretch = Stretch.Uniform,
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, (_s.LineHeight - iconSize) / 2, s * 0.5, 0)
        };
        var title = Text(s, tone, _s.LineHeight, FontWeight.Bold);
        var raw = lines[headerIndex];
        if (header.CalloutTitleStart < raw.Length && !string.IsNullOrWhiteSpace(raw[header.CalloutTitleStart..]))
            AppendInlines(title, raw, header, header.CalloutTitleStart, raw.Length, html(headerIndex).OpenInlineTags,
                scope, innerWidth);
        else
            title.Inlines!.Add(new Run(MarkdownCallouts.DefaultTitle(callout.Kind)));

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions { new(GridLength.Auto), new(GridLength.Star) }
        };
        Grid.SetColumn(title, 1);
        headerRow.Children.Add(icon);
        headerRow.Children.Add(title);
        body.Children.Add(headerRow);

        for (var index = callout.StartLine; index < callout.EndLine && index < lines.Length; index++)
        {
            var line = parse(index);
            var text = Text(s, _s.Ink, _s.LineHeight, align: BodyAlignment(html(index).Align, lines[index], line));
            if (string.IsNullOrWhiteSpace(lines[index][Math.Min(line.ContentStart, lines[index].Length)..]))
                text.Inlines!.Add(new Run(" "));
            else
                AppendInlines(text, lines[index], line, line.ContentStart, lines[index].Length,
                    html(index).OpenInlineTags, scope, innerWidth);
            body.Children.Add(text);
        }

        return new Border
        {
            BorderBrush = tone,
            BorderThickness = new Thickness(2.25, 0, 0, 0),
            Background = Tint(tone, 0.08),
            Padding = padding,
            Child = body
        };
    }

    // ----- Código y fórmulas -----

    /// <summary>Bloque de código: caja con fondo y borde, una línea por <see cref="TextBlock"/> en la
    /// tipografía de código del editor y coloreado con las gramáticas TextMate del editor (tema claro sobre
    /// papel). Las líneas largas se parten dentro de la caja en vez de salirse del margen.</summary>
    private int AddCodeBlock(BlockFlow flow, string[] lines, Dictionary<int, FenceInfo> fences, int start, int count)
    {
        var i = start;
        string? language = null;
        if (fences.TryGetValue(i + 1, out var opening) && opening.IsDelimiter)
        {
            language = opening.Language;
            i++;
        }

        var code = new List<string>();
        while (i < count && fences.TryGetValue(i + 1, out var fence) && fence.IsInside)
        {
            code.Add(lines[i].Replace("\t", new string(' ', Math.Clamp(EditorPreferences.TabSize, 1, 8)), StringComparison.Ordinal));
            i++;
        }
        if (i < count && fences.TryGetValue(i + 1, out var closing) && closing.IsDelimiter) i++;

        flow.Add(BuildCodeBox(code, language), BlockKind.Code);
        return Math.Max(i, start + 1);
    }

    private Border BuildCodeBox(List<string> code, string? language)
    {
        var tokens = _assets.Code(code, language);
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (var index = 0; index < Math.Max(1, code.Count); index++)
        {
            var line = index < code.Count ? code[index] : string.Empty;
            var block = new TextBlock
            {
                FontFamily = _s.MonoFont,
                FontSize = _s.CodeFontSize,
                LineHeight = _s.CodeLineHeight,
                Foreground = _s.Ink,
                TextWrapping = TextWrapping.Wrap,
                FontFeatures = NoLigatures
            };

            if (line.Length == 0) block.Inlines!.Add(new Run(" "));
            else if (tokens is not null && index < tokens.Count) AppendCodeTokens(block, line, tokens[index]);
            else block.Inlines!.Add(new Run(line));
            panel.Children.Add(block);
        }

        return new Border
        {
            Background = _s.CodeBackground,
            BorderBrush = _s.CodeBorder,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(_s.FontSize * 0.85, _s.FontSize * 0.6),
            Child = panel
        };
    }

    private static void AppendCodeTokens(TextBlock block, string line, IReadOnlyList<CodeToken> tokens)
    {
        var cursor = 0;
        foreach (var token in tokens)
        {
            if (token.Start > cursor) block.Inlines!.Add(new Run(line[cursor..token.Start]));
            var start = Math.Max(cursor, token.Start);
            var end = Math.Min(line.Length, token.End);
            if (end <= start) continue;

            var run = new Run(line[start..end]) { FontStyle = token.Style, FontWeight = token.Weight };
            if (token.Foreground is { } foreground) run.Foreground = foreground;
            if (token.Underline) run.TextDecorations = TextDecorations.Underline;
            block.Inlines!.Add(run);
            cursor = end;
        }
        if (cursor < line.Length) block.Inlines!.Add(new Run(line[cursor..]));
    }

    private void AddDisplayMath(BlockFlow flow, string latex)
    {
        if (_assets.Formula(latex, display: true) is { } formula)
        {
            var image = FittedImage(formula, formula.NaturalWidth, formula.NaturalHeight, double.PositiveInfinity);
            image.HorizontalAlignment = HorizontalAlignment.Center;
            flow.Add(new Panel { Children = { image } }, BlockKind.Math);
            return;
        }

        // LaTeX que CSharpMath no sabe componer: se enseña su código, que es legible, en vez de nada.
        flow.Add(BuildCodeBox(["$$", .. latex.Split('\n'), "$$"], null), BlockKind.Code);
    }

    // ----- Tablas -----

    /// <summary>Tabla con la rejilla del editor: anchos de columna calculados como los de un navegador
    /// (cada columna pide su contenido sin partir; si no cabe, se reparte el ancho a partir de su palabra
    /// más larga) y llenando el ancho de la caja de texto, que es como la dibujan el editor y la exportación
    /// a HTML. Cada fila es un control propio para que la paginación pueda cortar entre filas y repetir la
    /// de encabezados.</summary>
    private (Control Visual, Control Header) BuildTable(string[] lines, Func<int, MdLine> parse,
        Func<int, HtmlLineState> html, MdTable table, Scope scope)
    {
        var s = _s.FontSize;
        var columns = table.ColumnCount;
        var paddingX = Math.Round(s * 0.55, 2);
        var paddingY = Math.Round(s * 0.32, 2);
        const double border = 0.75;
        var cellImageMax = Math.Min(MarkdownTableLayout.CellImageMaxWidth * _s.PointsPerPixel, scope.Width / 2);

        var rowLines = new List<int> { table.StartLine - 1 };
        for (var line = table.DelimiterLine + 1; line <= table.EndLine; line++) rowLines.Add(line - 1);

        var cells = new TextBlock[rowLines.Count, columns];
        var natural = new double[columns];
        var minimum = new double[columns];
        for (var row = 0; row < rowLines.Count; row++)
        {
            var index = rowLines[row];
            var raw = lines[index];
            var model = parse(index);
            var split = MarkdownTableParser.SplitCells(raw, 0);
            var header = row == 0;

            for (var column = 0; column < columns; column++)
            {
                var cell = Text(s, _s.Ink, _s.LineHeight, header ? FontWeight.Bold : FontWeight.Normal,
                    CellAlignment(table.Alignments[column]));
                if (column < split.Count)
                    AppendInlines(cell, raw, model, split[column].Start, split[column].End,
                        html(index).OpenInlineTags, scope, cellImageMax);
                cells[row, column] = cell;

                cell.Measure(Size.Infinity);
                natural[column] = Math.Max(natural[column], cell.DesiredSize.Width);
                minimum[column] = Math.Max(minimum[column], MinContentWidth(cell));
            }
        }

        var chrome = 2 * paddingX + border;
        var widths = ColumnWidths(natural, minimum, chrome, scope.Width - border);

        var container = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left };
        Control? headerRow = null;
        for (var row = 0; row < rowLines.Count; row++)
        {
            var grid = new Grid();
            grid.Classes.Add(KeepTogetherClass);
            for (var column = 0; column < columns; column++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[column] + (column == 0 ? border : 0))));
                var cell = new Border
                {
                    BorderBrush = _s.TableBorder,
                    BorderThickness = new Thickness(column == 0 ? border : 0, row == 0 ? border : 0, border, border),
                    Background = row == 0 ? _s.TableHeaderBackground : null,
                    Padding = new Thickness(paddingX, paddingY),
                    Child = cells[row, column]
                };
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
            container.Children.Add(grid);
            headerRow ??= grid;
        }

        return (container, headerRow!);
    }

    /// <summary>Reparto del ancho entre columnas. <paramref name="natural"/> es lo que mide cada columna
    /// sin partir líneas; <paramref name="minimum"/>, su palabra (o imagen) más ancha. Mismo criterio que el
    /// diseño automático de tablas de un navegador: si todo cabe sin partir, cada columna recibe lo suyo más
    /// una parte del sobrante proporcional a lo que pide; si no, cada una parte de su mínimo y el resto se
    /// reparte según cuánto más querría; y si ni los mínimos caben, se reparte en proporción a ellos.</summary>
    internal static double[] ColumnWidths(double[] natural, double[] minimum, double chrome, double available)
    {
        var count = natural.Length;
        var widths = new double[count];
        if (count == 0) return widths;

        var want = natural.Select(n => n + chrome).ToArray();
        var least = minimum.Select((m, i) => Math.Min(m, natural[i]) + chrome).ToArray();
        var totalWant = want.Sum();
        var totalLeast = least.Sum();

        if (totalWant <= available)
        {
            for (var i = 0; i < count; i++) widths[i] = want[i] + (available - totalWant) * (want[i] / totalWant);
        }
        else if (totalLeast <= available)
        {
            var flexible = want.Select((w, i) => w - least[i]).ToArray();
            var totalFlexible = flexible.Sum();
            for (var i = 0; i < count; i++)
                widths[i] = least[i] + (totalFlexible > 0 ? (available - totalLeast) * flexible[i] / totalFlexible : 0);
        }
        else
        {
            for (var i = 0; i < count; i++) widths[i] = available * least[i] / totalLeast;
        }

        // El relleno y el borde van dentro del ancho de la columna; lo que se devuelve es la columna entera.
        return widths;
    }

    /// <summary>Lo mínimo que puede estrecharse una celda sin partir una palabra: su palabra más ancha (o
    /// su imagen, que no se puede estrechar).</summary>
    private static double MinContentWidth(TextBlock cell)
    {
        double widest = 0;
        foreach (var inline in cell.Inlines!)
        {
            switch (inline)
            {
                case Run { Text: { Length: > 0 } text } run:
                    var typeface = new Typeface(run.IsSet(TextElement.FontFamilyProperty) ? run.FontFamily : cell.FontFamily,
                        run.IsSet(TextElement.FontStyleProperty) ? run.FontStyle : cell.FontStyle,
                        run.IsSet(TextElement.FontWeightProperty) ? run.FontWeight : cell.FontWeight);
                    var size = run.IsSet(TextElement.FontSizeProperty) ? run.FontSize : cell.FontSize;
                    foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                                 .OrderByDescending(w => w.Length).Take(3))
                    {
                        using var layout = new TextLayout(word, typeface, size, null);
                        widest = Math.Max(widest, layout.WidthIncludingTrailingWhitespace);
                    }
                    break;
                case InlineUIContainer { Child: { } child }:
                    widest = Math.Max(widest, double.IsNaN(child.Width) ? 0 : child.Width);
                    break;
            }
        }
        return widest;
    }

    private static TextAlignment CellAlignment(MdTableAlign align) => align switch
    {
        MdTableAlign.Center => TextAlignment.Center,
        MdTableAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    // ----- Imágenes y transclusiones -----

    /// <summary>Una línea que no tiene más que una imagen (y, como mucho, etiquetas HTML que la alinean) es
    /// una imagen de bloque: se dibuja sola, con la alineación de su línea, y nunca se parte entre
    /// páginas.</summary>
    private bool TryAddLoneImage(BlockFlow flow, string raw, MdLine parsed, HtmlLineState state, Scope scope)
    {
        if (!IsLoneImage(raw, parsed)) return false;
        var span = parsed.Spans.First(s => s.Kind == MdSpanKind.Image);
        var image = BuildImage(span, raw, scope, scope.Width);
        image.HorizontalAlignment = state.Align switch
        {
            MdAlign.Center => HorizontalAlignment.Center,
            MdAlign.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
        flow.Add(new Panel { Children = { image } }, BlockKind.Image);
        return true;
    }

    private static bool IsLoneImage(string raw, MdLine parsed)
    {
        var images = parsed.Spans.Where(s => s.Kind == MdSpanKind.Image).ToList();
        return images.Count == 1
               && !NoteAttachmentService.IsAttachmentTarget(images[0].LinkTarget ?? string.Empty)
               && OnlyWhitespaceOutside(raw, parsed, MdSpanKind.Image);
    }

    private static bool IsLoneTransclusion(string raw, MdLine parsed) =>
        parsed.Spans.Count(s => s.Kind == MdSpanKind.Transclusion) == 1
        && OnlyWhitespaceOutside(raw, parsed, MdSpanKind.Transclusion);

    /// <summary>Si fuera de los tramos de tipo <paramref name="kind"/> y de las etiquetas HTML la línea no
    /// tiene más que espacios.</summary>
    private static bool OnlyWhitespaceOutside(string raw, MdLine parsed, MdSpanKind kind)
    {
        var covered = new bool[raw.Length];
        foreach (var span in parsed.Spans)
        {
            if (span.Kind != kind && span.Kind != MdSpanKind.HtmlTag) continue;
            for (var i = Math.Max(0, span.Start); i < Math.Min(raw.Length, span.End); i++) covered[i] = true;
        }
        for (var i = 0; i < raw.Length; i++)
        {
            if (!covered[i] && !char.IsWhiteSpace(raw[i])) return false;
        }
        return true;
    }

    /// <summary>Línea con etiquetas HTML y nada más que se vea.</summary>
    private static bool IsMarkupOnly(string raw, MdLine parsed) =>
        parsed.Kind == MdBlockKind.Paragraph && parsed.HtmlTags.Count > 0
        && OnlyWhitespaceOutside(raw, parsed, MdSpanKind.HtmlTag);

    /// <summary>Imagen a tamaño de documento: el pedido en la nota (<c>|300</c>, en píxeles del editor) o el
    /// natural, nunca más ancha que <paramref name="maxWidth"/> ni más alta que una página. Si no se pudo
    /// cargar, un hueco con su nombre en vez de nada — mismo criterio que el editor.</summary>
    private Control BuildImage(MdSpan span, string raw, Scope scope, double maxWidth)
    {
        var src = span.LinkTarget ?? string.Empty;
        var image = _assets.Image(_source, scope.NoteDirectory, src, span.ImageWidth);
        if (image is null)
        {
            var label = Text(_s.FontSize * 0.85, _s.InkMuted, _s.LineHeight * 0.85);
            label.FontStyle = FontStyle.Italic;
            label.TextWrapping = TextWrapping.Wrap;
            var alt = raw[Math.Clamp(span.ContentStart, 0, raw.Length)..Math.Clamp(span.ContentEnd, 0, raw.Length)];
            label.Inlines!.Add(new Run(string.IsNullOrWhiteSpace(alt) ? MarkdownImageResolver.ShortLabel(src) : alt));
            return new Border
            {
                BorderBrush = _s.Rule,
                BorderThickness = new Thickness(0.75),
                Background = _s.CodeBackground,
                Padding = new Thickness(_s.FontSize * 0.7, _s.FontSize * 0.45),
                MaxWidth = Math.Min(maxWidth, _s.FontSize * 22),
                Child = label
            };
        }

        var ppp = _s.PointsPerPixel;
        var width = span.ImageWidth > 0 ? span.ImageWidth * ppp : image.NaturalWidth * ppp;
        var height = span.ImageHeight > 0 ? span.ImageHeight * ppp : width * image.NaturalHeight / image.NaturalWidth;
        return FittedImage(image, width, height, maxWidth);
    }

    private Image FittedImage(DocumentImage image, double width, double height, double maxWidth)
    {
        if (width > maxWidth)
        {
            height *= maxWidth / width;
            width = maxWidth;
        }
        if (height > _maxImageHeight)
        {
            width *= _maxImageHeight / height;
            height = _maxImageHeight;
        }

        var control = new Image
        {
            Source = image.Bitmap,
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapInterpolationMode(control, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return control;
    }

    /// <summary><c>![[Nota]]</c> solo en su línea: el contenido de la nota embebida, compuesto aquí mismo
    /// con las mismas reglas, tras una barra lateral y el nombre de la nota — lo que enseña el editor. Se
    /// resuelve con el mismo servicio que el live preview (<see cref="NoteTransclusionService"/>), con su
    /// tope de profundidad y su detección de ciclos. Si no se puede resolver, la referencia sigue la vía
    /// normal y sale como texto, igual que en la exportación a HTML.</summary>
    private bool TryAddTransclusion(BlockFlow flow, string raw, MdLine parsed, Scope scope)
    {
        if (_source.ResolveTransclusion is not { } resolve || !IsLoneTransclusion(raw, parsed)) return false;

        var span = parsed.Spans.First(s => s.Kind == MdSpanKind.Transclusion);
        NoteTransclusionResult result;
        try
        {
            result = resolve(span.LinkTarget ?? string.Empty, scope.Ancestry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Warning($"No se pudo resolver '{span.LinkTarget}' al exportar la nota", ex);
            return false;
        }
        if (result.Status != NoteTransclusionStatus.Resolved || result.Note is not { } note) return false;

        var s = _s.FontSize;
        var inset = Math.Round(s * 1.0, 2);
        var nested = new BlockFlow(_s);
        var nestedScope = new Scope(System.IO.Path.GetDirectoryName(note.FullPath),
            [.. scope.Ancestry, note.FullPath], scope.Width - inset);
        ComposeMarkdown(nested, NoteTagHelper.StripFrontMatter(result.Markdown).Replace("\r\n", "\n", StringComparison.Ordinal), nestedScope);
        if (nested.Blocks.Count == 0) return false;

        var content = new StackPanel { Orientation = Orientation.Vertical };
        var caption = Text(Math.Round(s * 0.78, 2), _s.InkMuted, Math.Round(s * 1.2, 2), FontWeight.Bold);
        caption.Inlines!.Add(new Run(note.Title.ToUpperInvariant()));
        caption.LetterSpacing = 0.5;
        caption.Margin = new Thickness(0, 0, 0, s * 0.35);
        content.Children.Add(caption);
        for (var index = 0; index < nested.Blocks.Count; index++)
        {
            var block = nested.Blocks[index];
            block.Visual.Margin = new Thickness(0, index == 0 ? 0 : block.SpaceBefore, 0, 0);
            content.Children.Add(block.Visual);
        }

        flow.Add(new Border
        {
            BorderBrush = _s.Rule,
            BorderThickness = new Thickness(1.5, 0, 0, 0),
            Padding = new Thickness(inset - 1.5, s * 0.1, 0, s * 0.1),
            Child = content
        }, BlockKind.Embed);
        return true;
    }

    // ===================== Texto en línea =====================

    [Flags]
    private enum InlineStyle
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Strike = 4,
        Code = 8,
        Highlight = 16,
        Underline = 32,
        Link = 64,
        WikiLink = 128
    }

    /// <summary>
    /// Vuelca el rango [<paramref name="from"/>, <paramref name="to"/>) de una línea como <see cref="Inline"/>s:
    /// oculta los marcadores (Markdown y etiquetas HTML) exactamente como el live preview, aplica los estilos
    /// de los tramos y de las etiquetas en línea (también las que una línea anterior dejó abiertas), resuelve
    /// los escapes con barra invertida y sustituye imágenes y fórmulas por el objeto que dibujan.
    ///
    /// <para>Se trabaja carácter a carácter (qué estilo tiene, si se oculta) en vez de tramo a tramo para que
    /// los estilos se combinen: <c>&lt;b&gt;</c> envolviendo un <c>*cursiva*</c> sale en negrita cursiva, como
    /// en el editor. Los enlaces seguros se registran con su posición en el texto
    /// (<see cref="DocumentLinks"/>) para que el PDF los haga clicables.</para>
    /// </summary>
    private void AppendInlines(TextBlock target, string text, MdLine model, int from, int to,
        IReadOnlyList<string> openTags, Scope scope, double imageMaxWidth)
    {
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, from, text.Length);
        var length = to - from;
        if (length == 0) return;

        var styles = new InlineStyle[length];
        var hidden = new bool[length];
        var verbatim = new bool[length];
        var urls = new string?[length];
        var atoms = new Dictionary<int, MdSpan>();

        void Hide(int start, int end)
        {
            for (var i = Math.Max(start, from); i < Math.Min(end, to); i++) hidden[i - from] = true;
        }

        void Mark(int start, int end, InlineStyle style)
        {
            for (var i = Math.Max(start, from); i < Math.Min(end, to); i++) styles[i - from] |= style;
        }

        void Wrap(MdSpan span, InlineStyle style)
        {
            Hide(span.Start, span.ContentStart);
            Hide(span.ContentEnd, span.End);
            Mark(span.ContentStart, span.ContentEnd, style);
        }

        foreach (var span in model.Spans)
        {
            // Un tramo que se sale del rango (un `**` abierto en una celda y cerrado en la siguiente) no es
            // de este rango: se deja como texto literal en vez de aplicar un estilo a medias.
            if (span.Start < from || span.End > to) continue;

            switch (span.Kind)
            {
                case MdSpanKind.Bold: Wrap(span, InlineStyle.Bold); break;
                case MdSpanKind.Italic: Wrap(span, InlineStyle.Italic); break;
                case MdSpanKind.BoldItalic: Wrap(span, InlineStyle.Bold | InlineStyle.Italic); break;
                case MdSpanKind.Strikethrough: Wrap(span, InlineStyle.Strike); break;
                case MdSpanKind.Highlight: Wrap(span, InlineStyle.Highlight); break;
                case MdSpanKind.Underline: Wrap(span, InlineStyle.Underline); break;
                case MdSpanKind.WikiLink: Wrap(span, InlineStyle.WikiLink); break;
                case MdSpanKind.Transclusion: Wrap(span, InlineStyle.WikiLink); break;
                case MdSpanKind.InlineCode:
                    Wrap(span, InlineStyle.Code);
                    for (var i = span.ContentStart; i < span.ContentEnd; i++) verbatim[i - from] = true;
                    break;
                case MdSpanKind.Link:
                    Wrap(span, InlineStyle.Link);
                    if (span.LinkTarget is { } url && ExternalLinkPolicy.IsSafeUrl(url))
                        for (var i = span.ContentStart; i < span.ContentEnd; i++) urls[i - from] = url;
                    break;
                case MdSpanKind.HtmlTag:
                    Hide(span.Start, span.End);
                    if (model.HtmlTags.Any(t => t.Start == span.Start && t.Name == "br")) atoms[span.Start] = span;
                    break;
                case MdSpanKind.Math:
                case MdSpanKind.Image:
                    atoms[span.Start] = span;
                    break;
            }
        }

        // Solo las etiquetas de este rango: en una fila de tabla, un <b> de otra celda no es de esta.
        var tags = model.HtmlTags.Where(t => t.Start >= from && t.End <= to).ToList();
        foreach (var range in MarkdownHtmlParser.BuildStyleRanges(tags, from, to, openTags))
        {
            Mark(range.Start, range.End, range.Kind switch
            {
                MdSpanKind.Bold => InlineStyle.Bold,
                MdSpanKind.Italic => InlineStyle.Italic,
                MdSpanKind.BoldItalic => InlineStyle.Bold | InlineStyle.Italic,
                MdSpanKind.Strikethrough => InlineStyle.Strike,
                MdSpanKind.Highlight => InlineStyle.Highlight,
                MdSpanKind.Underline => InlineStyle.Underline,
                MdSpanKind.InlineCode => InlineStyle.Code,
                _ => InlineStyle.None
            });
        }

        // Escapes con barra invertida (\*, \_, \<): la barra no se ve y el carácter escapado sale tal cual.
        // Dentro del código en línea no hay escapes — es literal, igual que en el editor.
        var insideAtom = new bool[length];
        foreach (var atom in atoms.Values)
            for (var i = Math.Max(atom.Start, from); i < Math.Min(atom.End, to); i++) insideAtom[i - from] = true;
        for (var i = from; i < to - 1; i++)
        {
            var at = i - from;
            if (text[i] != '\\' || hidden[at] || verbatim[at] || insideAtom[at]) continue;
            if (IsEscapable(text[i + 1]))
            {
                hidden[at] = true;
                i++;
            }
        }

        var offset = TextLength(target);
        var position = from;
        var buffer = new StringBuilder();
        while (position < to)
        {
            if (atoms.TryGetValue(position, out var atom))
            {
                if (BuildAtom(atom, text, scope, imageMaxWidth) is { } inline)
                {
                    target.Inlines!.Add(inline);
                    offset += inline is Run run ? run.Text?.Length ?? 0 : 1;
                }
                position = Math.Max(atom.End, position + 1);
                continue;
            }

            if (hidden[position - from])
            {
                position++;
                continue;
            }

            var style = styles[position - from];
            var url = urls[position - from];
            buffer.Clear();
            while (position < to && !hidden[position - from] && !atoms.ContainsKey(position)
                   && styles[position - from] == style && urls[position - from] == url)
            {
                buffer.Append(text[position]);
                position++;
            }

            var styled = StyledRun(buffer.ToString(), style);
            if (url is not null) DocumentLinks.Register(target, offset, buffer.Length, url);
            offset += buffer.Length;
            target.Inlines!.Add(styled);
        }
    }

    /// <summary>Caracteres que ya ocupan los inlines de <paramref name="block"/>, con la misma cuenta que
    /// hace el <c>TextLayout</c> de Avalonia: un objeto incrustado o un salto de línea cuentan uno.</summary>
    private static int TextLength(TextBlock block)
    {
        var total = 0;
        foreach (var inline in block.Inlines!)
            total += inline is Run run ? run.Text?.Length ?? 0 : 1;
        return total;
    }

    private Inline? BuildAtom(MdSpan span, string text, Scope scope, double imageMaxWidth)
    {
        switch (span.Kind)
        {
            case MdSpanKind.HtmlTag:
                return new LineBreak();

            case MdSpanKind.Math:
            {
                var latex = text[span.ContentStart..span.ContentEnd];
                if (_assets.Formula(latex, span.IsDisplayMath) is { } formula)
                    return new InlineUIContainer(FittedImage(formula, formula.NaturalWidth, formula.NaturalHeight, imageMaxWidth))
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    };
                var delimiter = span.IsDisplayMath ? "$$" : "$";
                return StyledRun(delimiter + latex + delimiter, InlineStyle.Code);
            }

            case MdSpanKind.Image:
            {
                var src = span.LinkTarget ?? string.Empty;
                if (NoteAttachmentService.IsAttachmentTarget(src))
                {
                    // Un adjunto (PDF, audio…) no se puede incrustar en el documento: queda su nombre, como
                    // referencia, con el aspecto de un enlace interno.
                    var alt = text[span.ContentStart..span.ContentEnd];
                    return StyledRun(string.IsNullOrWhiteSpace(alt) ? System.IO.Path.GetFileName(src) : alt, InlineStyle.WikiLink);
                }
                return new InlineUIContainer(BuildImage(span, text, scope, imageMaxWidth))
                {
                    BaselineAlignment = BaselineAlignment.Bottom
                };
            }

            default:
                return null;
        }
    }

    /// <summary>Lo que Markdown permite escapar con barra invertida: la puntuación ASCII.</summary>
    private static bool IsEscapable(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private Run StyledRun(string text, InlineStyle style)
    {
        var run = new Run(text);
        if (style == InlineStyle.None) return run;

        if (style.HasFlag(InlineStyle.Bold)) run.FontWeight = FontWeight.Bold;
        if (style.HasFlag(InlineStyle.Italic)) run.FontStyle = FontStyle.Italic;
        if (style.HasFlag(InlineStyle.Code))
        {
            run.FontFamily = _s.MonoFont;
            run.FontSize = _s.CodeFontSize;
            run.Background = _s.InlineCodeBackground;
        }
        if (style.HasFlag(InlineStyle.Highlight)) run.Background = _s.Highlight;

        var decorations = new TextDecorationCollection();
        if (style.HasFlag(InlineStyle.Strike)) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        if (style.HasFlag(InlineStyle.Underline) || style.HasFlag(InlineStyle.Link))
            decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (style.HasFlag(InlineStyle.WikiLink))
        {
            // Enlace interno: no lleva a ninguna parte fuera del vault, así que se distingue del texto con
            // un subrayado discontinuo, el mismo recurso que la exportación a HTML.
            run.FontWeight = FontWeight.SemiBold;
            decorations.Add(new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Stroke = _s.InkMuted,
                StrokeDashArray = [2, 2]
            });
        }
        if (decorations.Count > 0) run.TextDecorations = decorations;
        return run;
    }

    // ===================== Utilidades =====================

    private TextBlock Text(double size, IBrush foreground, double lineHeight, FontWeight? weight = null,
        TextAlignment align = TextAlignment.Left) => new()
    {
        FontFamily = _s.BodyFont,
        FontSize = size,
        LineHeight = lineHeight,
        Foreground = foreground,
        FontWeight = weight ?? FontWeight.Normal,
        TextAlignment = align,
        TextWrapping = TextWrapping.Wrap,
        // Sin ligaduras: en un PDF, "fi" compuesto como un solo glifo se copia y se busca como «ﬁ», y la
        // palabra deja de encontrarse con Ctrl+F.
        FontFeatures = NoLigatures
    };

    private static TextAlignment Alignment(MdAlign align) => align switch
    {
        MdAlign.Center => TextAlignment.Center,
        MdAlign.Right => TextAlignment.Right,
        MdAlign.Justify => TextAlignment.Justify,
        _ => TextAlignment.Left
    };

    /// <summary>Alineación del texto corrido: la que declare una etiqueta HTML y, si no declara ninguna, la
    /// justificación de Ajustes › Editor con las mismas excepciones que el editor
    /// (<c>MarkdownBlockTextFormatter.IsJustifiable</c>): ni líneas con tabuladores, ni líneas con
    /// imágenes, ni párrafos sangrados como código.</summary>
    private TextAlignment BodyAlignment(MdAlign align, string raw, MdLine line)
    {
        if (align != MdAlign.None) return Alignment(align);
        if (!_s.Justify || raw.Contains('\t') || line.Spans.Exists(s => s.Kind == MdSpanKind.Image)) return TextAlignment.Left;
        if (line.Kind == MdBlockKind.Paragraph && IndentColumns(raw) >= 4) return TextAlignment.Left;
        return TextAlignment.Justify;
    }

    private static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;
}

/// <summary>Enlaces externos de cada <see cref="TextBlock"/> del documento: su posición en el texto del
/// bloque y su destino. El renderizador de PDF los convierte en zonas clicables con
/// <c>SKCanvas.DrawUrlAnnotation</c> sobre el rectángulo que ocupa cada uno una vez maquetado.</summary>
internal static class DocumentLinks
{
    private static readonly ConditionalWeakTable<TextBlock, List<DocumentLink>> Links = new();

    public static void Register(TextBlock block, int start, int length, string url)
    {
        if (length <= 0) return;
        Links.GetOrCreateValue(block).Add(new DocumentLink(start, length, url));
    }

    public static IReadOnlyList<DocumentLink> Of(TextBlock block) =>
        Links.TryGetValue(block, out var links) ? links : Array.Empty<DocumentLink>();
}

internal readonly record struct DocumentLink(int Start, int Length, string Url);

/// <summary>
/// Dónde se puede cortar un bloque ya maquetado sin partir nada por la mitad. Recorre su árbol visual y
/// reúne los intervalos verticales que no admiten un corte por dentro: cada línea maquetada de cada
/// <see cref="TextBlock"/>, cada imagen y cada control marcado con
/// <see cref="NoteDocumentComposer.KeepTogetherClass"/> (una fila de tabla, con todas las líneas de todas
/// sus celdas). Los intervalos que se solapan forman una unidad; los cortes son el principio de cada
/// unidad. Los cortes de segunda categoría ignoran las filas y solo respetan líneas e imágenes, para el
/// caso raro de una fila más alta que una página.
/// </summary>
internal static class DocumentBreaks
{
    private const double Epsilon = 0.01;

    public static (IReadOnlyList<double> Breaks, IReadOnlyList<double> Fallback) Find(Control root)
    {
        var leaves = new List<(double Top, double Bottom)>();
        var groups = new List<(double Top, double Bottom)>();
        Collect(root, -root.Bounds.Y, leaves, groups);

        var height = root.Bounds.Height;
        return (Cuts([.. leaves, .. groups], height), Cuts(leaves, height));
    }

    private static void Collect(Visual visual, double parentTop, List<(double, double)> leaves,
        List<(double, double)> groups)
    {
        var top = parentTop + visual.Bounds.Y;
        var bottom = top + visual.Bounds.Height;

        switch (visual)
        {
            case TextBlock text:
            {
                var y = top + text.Padding.Top;
                foreach (var line in text.TextLayout.TextLines)
                {
                    leaves.Add((y, y + line.Height));
                    y += line.Height;
                }
                // Las imágenes y fórmulas en línea son parte de su línea: no hace falta mirar dentro.
                return;
            }
            case Image:
                leaves.Add((top, bottom));
                return;
            case Control control when control.Classes.Contains(NoteDocumentComposer.KeepTogetherClass):
                groups.Add((top, bottom));
                break;
        }

        foreach (var child in visual.GetVisualChildren()) Collect(child, top, leaves, groups);
    }

    private static List<double> Cuts(List<(double Top, double Bottom)> intervals, double height)
    {
        var cuts = new List<double>();
        if (intervals.Count == 0) return cuts;

        intervals.Sort((a, b) => a.Top.CompareTo(b.Top));
        var unitBottom = intervals[0].Bottom;
        foreach (var (top, bottom) in intervals.Skip(1))
        {
            if (top < unitBottom - Epsilon)
            {
                unitBottom = Math.Max(unitBottom, bottom);
                continue;
            }
            if (top > Epsilon && top < height - Epsilon && (cuts.Count == 0 || top > cuts[^1] + Epsilon)) cuts.Add(top);
            unitBottom = Math.Max(unitBottom, bottom);
        }
        return cuts;
    }
}
