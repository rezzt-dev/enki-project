using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Material.Icons;
using Material.Icons.Avalonia;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Conceals Markdown syntax markers (headings, emphasis, links, blockquote/list markers) and the
/// recognized HTML tags (<c>&lt;div align="center"&gt;</c>, <c>&lt;b&gt;</c>…) wherever the
/// caret/selection isn't touching them, replaces list bullets and task checkboxes with rendered
/// widgets, and reserves the leading space that centres or right-aligns a line - the core mechanic
/// behind Obsidian's live preview.
/// </summary>
public sealed class MarkdownElementGenerator : VisualLineElementGenerator
{
    /// <summary>Checkbox box size and the gap after it, mirrored by the measurement that aligns a task
    /// line (see <see cref="CreateInertCheckbox"/>).</summary>
    private const double CheckboxSize = 16;
    private const double CheckboxSpacing = 6;

    /// <summary>Lado del icono de un aviso. Algo menor que el cuerpo de texto a propósito: el icono
    /// acompaña al título, no compite con él.</summary>
    private const double CalloutIconSize = 15;
    private const double CalloutVerticalPadding = 4;

    private enum ZoneKind { Hide, Bullet, Checkbox, Callout, Align, JustifySpace, Image, Transclusion, Math, SpecialBlock, TableRule, TableRow }

    /// <param name="Spacer">Extra blank width (px) this zone reserves before whatever it renders: the
    /// alignment spacing of the visual row starting here, or the share of leftover width a justified
    /// row hands to this space character. <c>NaN</c> means "no row plan yet, estimate it from the whole
    /// line" (see <see cref="ComputeSpacerWidth"/>), and 0 means none.
    ///
    /// The spacing rides on whatever element comes first at that offset instead of being a zone of its
    /// own: element construction asks each generator only once per offset, so a separate zero-length zone
    /// sharing another zone's offset would make that other one be skipped.</param>
    private readonly record struct Zone(int Start, int End, ZoneKind Kind, MdLine Line, double Spacer)
    {
        /// <summary>Solo para <see cref="ZoneKind.Image"/>: el span <c>![…](…)</c> que esta zona sustituye,
        /// del que salen la ruta y el tamaño pedido. El resto de zonas no necesitan mirar ningún span
        /// concreto — les basta con el rango que ocultan.</summary>
        public MdSpan? Span { get; init; }

        /// <summary>Entrada de caché ya solicitada para una fórmula. Se conserva en la zona para que el
        /// constructor del elemento no repita la búsqueda ni pueda observar otro estado a mitad del pase.</summary>
        public MarkdownFormulaEntry? Formula { get; init; }

        public MarkdownSpecialBlock? SpecialBlock { get; init; }
        public MarkdownMermaidEntry? Mermaid { get; init; }

        /// <summary>Techo del ancho de la imagen de esta zona, o 0 si no lo hay — lo pone a
        /// <see cref="MarkdownTableLayout.CellImageMaxWidth"/> una imagen dentro de una celda de tabla
        /// dibujada, que no puede ocupar la columna de texto entera.</summary>
        public double ImageMaxWidth { get; init; }

        /// <summary>Alto que esta zona impone a su línea, si impone alguno: el relleno vertical de una
        /// fila de tabla o la banda fina de su fila separadora (ver <see cref="SpacerMetrics"/>).</summary>
        public SpacerMetrics Metrics { get; init; }

        /// <summary>Solo para <see cref="ZoneKind.TableRow"/>: la fila y la geometría con las que se dibuja
        /// una fila de tabla que envuelve (ver <see cref="MarkdownTableRowVisual"/>).</summary>
        public MdTableRow? TableRow { get; init; }

        public MdTableGeometry? TableGeometry { get; init; }
    }

    private readonly LivePreviewState _state;
    private readonly TextArea _textArea;
    private readonly MarkdownStyle _style;
    private readonly MarkdownAlignmentLayout _layout = new();

    private int _cachedLineNumber = -1;
    private List<Zone> _cachedZones = new();

    public MarkdownElementGenerator(LivePreviewState state, TextArea textArea, MarkdownStyle style)
    {
        _state = state;
        _textArea = textArea;
        _style = style;

        // El centrado de imágenes, las alineaciones y las tablas dependen del ancho disponible.
        // AvaloniaEdit solo reconstruye líneas al redimensionar si el ajuste de línea está activo;
        // el redibujado explícito mantiene estas posiciones también sin ajuste de línea.
        textArea.TextView.SizeChanged += (_, _) =>
        {
            _layout.Invalidate();
            textArea.TextView.Redraw();
        };

        // Each layout pass hands back the rows the formatter actually produced, which is the only place
        // the per-row spacing can be worked out from - see MarkdownAlignmentLayout.
        textArea.TextView.VisualLinesChanged += (_, _) =>
        {
            if (_state.HasAlignments) _layout.Refresh(textArea.TextView, _state);
        };
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        MarkdownBlockTextFormatter.Register(context, _state);
        _cachedLineNumber = -1;
    }

    private List<Zone> GetZones(int lineNumber)
    {
        if (_cachedLineNumber == lineNumber) return _cachedZones;

        var line = CurrentContext.Document.GetLineByNumber(lineNumber);
        var text = CurrentContext.Document.GetText(line);
        var fence = _state.GetFence(lineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);
        var zones = new List<Zone>();
        var lineActive = _state.Intersects(line.Offset, line.EndOffset);

        if (_state.SpecialBlockStartingAt(lineNumber) is { } special
            && !_state.Intersects(special.StartOffset, special.EndOffset)
            && _state.SpecialBlockFolding?.IsCollapsed(special) == true)
        {
            var specialFormula = special.Kind == MarkdownSpecialBlockKind.Math
                ? _state.Formulas?.GetOrRequest(special.Source, display: true, _style)
                : null;
            var mermaid = special.Kind == MarkdownSpecialBlockKind.Mermaid
                ? _state.Mermaid?.GetOrRequest(special.Source, _style)
                : null;
            var canRender = special.Kind == MarkdownSpecialBlockKind.Math
                ? specialFormula is { State: not MarkdownRenderState.Failed }
                : mermaid is { State: not MarkdownRenderState.Failed };
            if (canRender)
            {
                zones.Add(new Zone(special.StartOffset, special.EndOffset, ZoneKind.SpecialBlock, model, 0)
                {
                    Formula = specialFormula,
                    Mermaid = mermaid,
                    SpecialBlock = special
                });
                _cachedLineNumber = lineNumber;
                _cachedZones = zones;
                return zones;
            }
        }

        // Tablas: una fila solo se dibuja como tal si su tabla entera está inactiva (el cursor fuera) y
        // además cabe a lo ancho de la columna. Se resuelve antes que nada porque condiciona lo demás —
        // una imagen dentro de una celda no se dibuja al mismo tamaño que suelta en el texto.
        var tableLine = _state.Tables.GetRow(lineNumber);
        MdTableRow? tableRow = null;
        MdTableGeometry? tableGeometry = null;
        if (tableLine is { } row && !_state.IsTableActive(row.Table, CurrentContext.Document))
        {
            tableGeometry = _state.Tables.GetGeometry(row.Table, _textArea.TextView, _style, _state);
            if (tableGeometry is not null) tableRow = row;
        }
        var imageMaxWidth = tableGeometry is null ? 0 : MarkdownTableLayout.CellImageMaxWidth;

        switch (model.Kind)
        {
            case MdBlockKind.Heading:
            case MdBlockKind.HorizontalRule:
                if (!lineActive && model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model, 0));
                break;
            // Code fences need no zone of their own beyond concealing the "```lang" delimiter: the
            // padding that keeps code text clear of the block's border is produced by drawing the box
            // wider than the text column (see MarkdownBackgroundRenderer), not by inserting a spacer
            // run here. A spacer only ever indents the first visual row of a wrapped line, which is
            // precisely why the box used to be overrun by every continuation row.
            //
            // Verticalmente sí manda: la línea del delimitador se queda sin nada que dibujar, así que se
            // encoge a la banda fina de MarkdownStyle.CodeFenceLineHeight y esa banda hace de relleno
            // arriba y abajo del bloque. Con el alto normal de una línea de texto quedaban ~20px de hueco
            // vacío entre el borde del recuadro y la primera/última línea de código. Solo afecta a los
            // delimitadores: las líneas de dentro del bloque no tienen marcador que ocultar
            // (MarkerStart == MarkerEnd) y conservan su alto de línea de texto.
            case MdBlockKind.CodeFence:
                if (!lineActive && model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model, 0)
                    {
                        Metrics = new SpacerMetrics(MarkdownStyle.CodeFenceLineHeight, 0)
                    });
                break;
            case MdBlockKind.Blockquote:
                // El formateador reserva el ancho de cada nivel para todas las filas visuales; el
                // marcador puede ocultarse sin alterar el documento ni falsear el hit testing.
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Hide, model, 0)
                    {
                        // El fondo del aviso ocupa toda la línea. Este relleno desplaza la línea base
                        // hacia el centro y deja el mismo aire por arriba y por abajo, también al editar.
                        Metrics = _state.GetCallout(lineNumber) is not null
                            ? new SpacerMetrics(0, CalloutVerticalPadding) : default
                    });
                // Cabecera de un aviso: el "[!warning]" se sustituye por el icono del tipo (y por su
                // nombre, si el aviso no trae título propio). Con el cursor dentro se enseña en crudo,
                // igual que cualquier otro marcador del live preview.
                if (!lineActive && model.CalloutKind is not null && model.CalloutMarkerStart < model.CalloutMarkerEnd)
                    zones.Add(new Zone(model.CalloutMarkerStart, model.CalloutMarkerEnd, ZoneKind.Callout, model, 0));
                break;
            case MdBlockKind.BulletList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Bullet, model, 0));
                break;
            case MdBlockKind.TaskList:
                if (model.MarkerStart < model.MarkerEnd)
                    zones.Add(new Zone(model.MarkerStart, model.MarkerEnd, ZoneKind.Checkbox, model, 0));
                break;
        }

        foreach (var span in model.Spans)
        {
            if (_state.IsSpanActive(span)) continue;

            // Una imagen no oculta marcadores alrededor de un contenido: se oculta entera y se dibuja la
            // imagen en su lugar. El texto alternativo tampoco se enseña — es el reemplazo de la imagen
            // cuando no hay imagen, no una leyenda que acompañe a la que sí se ve.
            if (span.Kind == MdSpanKind.Image)
            {
                zones.Add(new Zone(span.Start, span.End, ZoneKind.Image, model, 0)
                {
                    Span = span,
                    ImageMaxWidth = imageMaxWidth
                });
                continue;
            }

            if (span.Kind == MdSpanKind.Transclusion)
            {
                zones.Add(new Zone(span.Start, span.End, ZoneKind.Transclusion, model, 0) { Span = span });
                continue;
            }

            if (span.Kind == MdSpanKind.Math)
            {
                if (_state.Formulas is null) continue;
                var latex = CurrentContext.Document.GetText(span.ContentStart, span.ContentEnd - span.ContentStart);
                var entry = _state.Formulas.GetOrRequest(latex, span.IsDisplayMath, _style);
                // Fallo o timeout: no se añade ninguna zona y queda visible el LaTeX original completo.
                if (entry.State == MarkdownRenderState.Failed) continue;
                zones.Add(new Zone(span.Start, span.End, ZoneKind.Math, model, 0)
                {
                    Span = span,
                    Formula = entry
                });
                continue;
            }

            if (span.ContentStart > span.Start) zones.Add(new Zone(span.Start, span.ContentStart, ZoneKind.Hide, model, 0));
            if (span.End > span.ContentEnd) zones.Add(new Zone(span.ContentEnd, span.End, ZoneKind.Hide, model, 0));
        }

        if (tableRow is { } renderedRow && tableGeometry is { } geometry)
        {
            // Una tabla con celdas que envuelven no se puede colocar con espaciadores: la fila entera pasa
            // a ser un dibujo (ver MarkdownTableRowVisual). Su fila separadora sigue por el camino de
            // siempre, que ya la deja en una banda fina.
            if (geometry.Wraps && renderedRow.Kind != MdTableRowKind.Delimiter)
                AddWrappedTableRowZone(zones, line, model, renderedRow, geometry);
            else
                AddTableZones(zones, line, text, model, renderedRow, geometry);
        }

        if (tableLine is { Kind: MdTableRowKind.Header } headerRow)
            AddFoldedDelimiterZone(zones, line, model, headerRow.Table);

        zones.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Una imagen sola ocupa su línea y se centra incluso dentro de un bloque alineado a otro lado.
        var standaloneImage = zones.FindIndex(zone => zone.Kind == ZoneKind.Image
            && string.IsNullOrWhiteSpace(text[..(zone.Start - line.Offset)])
            && string.IsNullOrWhiteSpace(text[(zone.End - line.Offset)..]));
        if (standaloneImage >= 0 && tableLine is null)
        {
            var zone = zones[standaloneImage];
            var width = MeasureImage(zone.Span!, zone.ImageMaxWidth).Width;
            var leading = Math.Max(0, (_textArea.TextView.Bounds.Width - width) / 2);
            zones.Clear();
            zones.Add(zone with { Start = line.Offset, End = line.EndOffset, Spacer = leading });
        }
        else
        {
            AddAlignmentZones(zones, line, text, model);
        }

        _cachedLineNumber = lineNumber;
        _cachedZones = zones;
        return zones;
    }

    /// <summary>Coloca cada celda de una fila de tabla en la columna que le toca. El mecanismo es el
    /// mismo con el que se centra una línea —reservar blanco antes del texto—, aplicado ahora a cada
    /// celda: se oculta lo que separa una celda de la siguiente (los espacios y la barra vertical) y en su
    /// lugar se reserva exactamente el blanco que hace que el texto de la celda caiga en la X que
    /// <see cref="MdTableGeometry"/> ha fijado para su columna, incluida la parte que la alinea a la
    /// derecha o al centro dentro de ella.
    ///
    /// <para>La fila separadora es un caso aparte: no se dibuja nada de su texto y se encoge a una banda
    /// fina, que es donde el fondo pinta la línea que separa el encabezado del cuerpo.</para></summary>
    private void AddTableZones(List<Zone> zones, DocumentLine line, string text, MdLine model,
        MdTableRow row, MdTableGeometry geometry)
    {
        if (row.Kind == MdTableRowKind.Delimiter)
        {
            zones.Clear();
            if (line.Length > 0) zones.Add(new Zone(line.Offset, line.EndOffset, ZoneKind.TableRule, model, 0));
            return;
        }

        var cells = MarkdownTableParser.SplitCells(text, line.Offset);
        if (cells.Count == 0) return;

        var header = row.Kind == MdTableRowKind.Header;
        var rowMetrics = new SpacerMetrics(0, MarkdownTableLayout.RowVerticalPadding);
        var drawnUpTo = 0.0;
        var gapStart = line.Offset;

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var column = Math.Min(cell.Column, geometry.ColumnWidths.Length - 1);
            var width = MarkdownTableLayout.MeasureCell(text, line, model, cell, header, _style, _state, _textArea.TextView);
            var target = geometry.ColumnX[column] + AlignOffset(geometry, row.Table, column, width);

            // El relleno vertical viaja en la primera zona de la fila: basta con que un elemento de la
            // línea sea más alto para que la línea entera lo sea (ver SpacerMetrics).
            AddTableGapZone(zones, gapStart, cell.Start, model, Math.Max(0, target - drawnUpTo),
                i == 0 ? rowMetrics : default);

            drawnUpTo = target + width;
            gapStart = cell.End;
        }

        // Lo que quede tras la última celda (espacios sobrantes y la barra de cierre) se oculta sin
        // reservar nada: el borde derecho de la tabla lo dibuja el fondo, no el texto.
        AddTableGapZone(zones, gapStart, line.EndOffset, model, 0, default);
    }

    /// <summary>Sustituye la fila entera por el objeto que la dibuja con sus celdas envueltas. Se lleva por
    /// delante cualquier otra zona de la línea (los marcadores que ocultaba, la imagen que reemplazaba):
    /// nada de eso se dibuja ya como texto, porque el objeto compone él mismo el contenido de cada celda a
    /// partir del mismo modelo — ver <see cref="MarkdownTableLayout.BuildCell"/>.</summary>
    private static void AddWrappedTableRowZone(List<Zone> zones, DocumentLine line, MdLine model,
        MdTableRow row, MdTableGeometry geometry)
    {
        zones.Clear();
        if (line.Length <= 0) return;

        zones.Add(new Zone(line.Offset, line.EndOffset, ZoneKind.TableRow, model, 0)
        {
            TableRow = row,
            TableGeometry = geometry
        });
    }

    /// <summary>Oculta el texto de la fila separadora cuando está plegada. Una línea plegada pierde su
    /// línea visual propia y su texto pasa a ser la continuación de la del encabezado, así que sin esto el
    /// <c>| --- |</c> aparecería pegado al final de los encabezados. El rango incluye el salto de línea
    /// que las une y acaba exactamente al final del texto de la separadora, nunca dentro de su propio
    /// salto de línea, que es donde AvaloniaEdit rechazaría el elemento.
    ///
    /// <para>Se decide solo por si está plegada, no por si la tabla se dibuja: las dos mitades del pliegue
    /// (el alto, que quita <see cref="MarkdownTableFolding"/>, y el texto, que se quita aquí) tienen que
    /// ir siempre juntas, aunque el estado del pliegue vaya un instante por detrás de la geometría.</para></summary>
    private void AddFoldedDelimiterZone(List<Zone> zones, DocumentLine line, MdLine model, MdTable table)
    {
        if (_state.TableFolding?.IsCollapsed(table.DelimiterLine) != true) return;
        if (table.DelimiterLine > CurrentContext.Document.LineCount) return;

        var delimiter = CurrentContext.Document.GetLineByNumber(table.DelimiterLine);
        if (delimiter.EndOffset > line.EndOffset)
            zones.Add(new Zone(line.EndOffset, delimiter.EndOffset, ZoneKind.Hide, model, 0));
    }

    /// <summary>Blanco que hay que añadir dentro de la columna antes del texto de la celda para que quede
    /// alineado como pide la fila separadora de la tabla.</summary>
    private static double AlignOffset(MdTableGeometry geometry, MdTable table, int column, double width)
    {
        var free = geometry.ColumnWidths[column] - width;
        if (free <= 0) return 0;
        return table.Alignments[column] switch
        {
            MdTableAlign.Center => free / 2,
            MdTableAlign.Right => free,
            _ => 0
        };
    }

    /// <summary>Oculta [start,end) reservando <paramref name="spacer"/> píxeles en su lugar. Si ya hay una
    /// zona que empieza en ese mismo offset (el marcador de un span que abre la celda), el espacio se le
    /// cuelga a ella en vez de añadir una segunda: el generador solo construye un elemento por offset, y
    /// dos zonas compitiendo por el mismo dejarían una sin construir.</summary>
    private static void AddTableGapZone(List<Zone> zones, int start, int end, MdLine model, double spacer,
        SpacerMetrics metrics)
    {
        if (end < start) return;
        if (end == start && spacer <= 0 && metrics == default) return;

        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].Start != start) continue;
            zones[i] = zones[i] with { Spacer = spacer, Metrics = metrics };
            return;
        }

        zones.Add(new Zone(start, end, ZoneKind.Hide, model, spacer) { Metrics = metrics });
    }

    /// <summary>Injects the blank space that aligns the line: one spacer at the start of each visual row
    /// (centre / right), or a share of the row's leftover width on each of its spaces (justify). The rows
    /// come from <see cref="MarkdownAlignmentLayout"/>, which reads them back from the previous layout
    /// pass; until it has seen the line once, a single-row estimate stands in so a short centred line is
    /// never left flush left for a frame.</summary>
    private void AddAlignmentZones(List<Zone> zones, DocumentLine line, string text, MdLine model)
    {
        var align = _state.GetAlignment(line.LineNumber);
        if (align is MdAlign.None or MdAlign.Left) return;

        var plan = _layout.Get(line.LineNumber);
        if (plan is null || plan.Align != align || plan.Rows.Count == 0)
        {
            if (align is MdAlign.Center or MdAlign.Right) AddSpacerZone(zones, line, model, line.Offset, double.NaN);
            return;
        }

        for (var i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            row.Applied = 0;

            if (row.Leading > 0.5)
            {
                // A row that does not start after a space (a word split mid-way because it is longer than
                // the column) is left alone: the spacer glues itself to the following text to stay on its
                // own row, and gluing it to half a word would drag that half up to the row before.
                if (i == 0 || StartsAfterSpace(text, line.Offset, row.StartOffset))
                {
                    if (AddSpacerZone(zones, line, model, row.StartOffset, row.Leading)) row.Applied += row.Leading;
                }
            }

            if (row.Free > 0.5) row.Applied += AddJustifyZones(zones, line, text, model, row);
        }

        zones.Sort(CompareZones);
    }

    /// <summary>Shares the row's leftover width out between the spaces it can stretch, and returns how
    /// much was actually injected. Spaces sitting inside a concealed marker are skipped (stretching them
    /// would open a gap where nothing is drawn), and so is the row's trailing space, which a justified
    /// browser line does not stretch either.</summary>
    private static double AddJustifyZones(List<Zone> zones, DocumentLine line, string text, MdLine model, AlignedRow row)
    {
        var stretchable = new List<int>();
        var lastVisible = -1;
        for (var offset = row.StartOffset; offset < row.EndOffset; offset++)
        {
            var index = offset - line.Offset;
            if (index < 0 || index >= text.Length) break;
            if (IsInsideZone(zones, offset)) continue;
            if (text[index] == ' ') stretchable.Add(offset);
            else lastVisible = offset;
        }

        // Drop the trailing run of spaces - anything past the last visible character of the row.
        while (stretchable.Count > 0 && stretchable[^1] > lastVisible) stretchable.RemoveAt(stretchable.Count - 1);
        if (stretchable.Count == 0) return 0;

        // Half a pixel short of the full leftover, so rounding can never push the row past the column
        // width and make the formatter break it one word earlier than it just did.
        var extra = (row.Free - 0.5) / stretchable.Count;
        if (extra <= 0.1) return 0;

        foreach (var offset in stretchable)
            zones.Add(new Zone(offset, offset + 1, ZoneKind.JustifySpace, model, extra));

        return extra * stretchable.Count;
    }

    /// <summary>Puts <paramref name="width"/> of blank space at <paramref name="offset"/>, riding on the
    /// zone already starting there if there is one. Returns whether the spacing was placed.</summary>
    private static bool AddSpacerZone(List<Zone> zones, DocumentLine line, MdLine model, int offset, double width)
    {
        for (var i = 0; i < zones.Count; i++)
        {
            if (zones[i].Start != offset) continue;
            if (zones[i].Kind == ZoneKind.Align || zones[i].Spacer != 0) return false;
            zones[i] = zones[i] with { Spacer = width };
            return true;
        }

        zones.Add(new Zone(offset, offset, ZoneKind.Align, model, width));
        return true;
    }

    private static bool StartsAfterSpace(string text, int lineOffset, int offset)
    {
        var index = offset - lineOffset - 1;
        return index >= 0 && index < text.Length && (text[index] == ' ' || text[index] == '\t');
    }

    private static bool IsInsideZone(List<Zone> zones, int offset)
    {
        foreach (var zone in zones)
        {
            if (zone.Start <= offset && zone.End > offset) return true;
        }
        return false;
    }

    /// <summary>Zero-length alignment zones sort before anything else starting at the same offset, so the
    /// row's spacing is never hidden behind a marker that begins there.</summary>
    private static int CompareZones(Zone a, Zone b)
    {
        if (a.Start != b.Start) return a.Start.CompareTo(b.Start);
        if (a.Kind == b.Kind) return 0;
        return a.Kind == ZoneKind.Align ? -1 : b.Kind == ZoneKind.Align ? 1 : 0;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.Document.GetLineByOffset(startOffset);
        foreach (var zone in GetZones(line.LineNumber))
        {
            if (zone.Start >= startOffset) return zone.Start;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        var zones = GetZones(line.LineNumber);
        foreach (var zone in zones)
        {
            if (zone.Start != offset) continue;
            var length = zone.End - zone.Start;
            var spacer = double.IsNaN(zone.Spacer) ? ComputeSpacerWidth(line, zone.Line, zones) : zone.Spacer;
            VisualLineElement? element = zone.Kind switch
            {
                // Single trailing space matches the gap ordered lists get naturally from "1. ".
                ZoneKind.Bullet => new WhitespaceFormattedElement("• ", length, spacer),
                ZoneKind.Checkbox => new InlineObjectElement(length, BuildCheckbox(zone.Line, spacer)),
                ZoneKind.Callout => new InlineObjectElement(length, BuildCalloutBadge(line, zone.Line, spacer)),
                ZoneKind.Image => zone.Span is { } imageSpan
                    ? new InlineObjectElement(length, BuildImage(imageSpan, spacer, zone.ImageMaxWidth))
                    : null,
                ZoneKind.Transclusion => zone.Span is { LinkTarget: { } reference }
                    ? new InlineObjectElement(length, BuildTransclusion(reference, spacer))
                    : null,
                ZoneKind.Math => zone is { Span: { } mathSpan, Formula: { } formula }
                    ? new InlineObjectElement(length, BuildFormula(mathSpan, formula, spacer))
                    : null,
                ZoneKind.SpecialBlock => zone.SpecialBlock is { } block
                    ? new InlineObjectElement(length, block.Kind == MarkdownSpecialBlockKind.Math
                        && zone.Formula is { } blockFormula
                            ? BuildFormulaBlock(block, blockFormula)
                            : BuildMermaidBlock(block, zone.Mermaid))
                    : null,
                // La fila separadora de una tabla: sin texto y con el alto fijo de la banda que hace de
                // línea entre el encabezado y el cuerpo.
                ZoneKind.TableRule => new WhitespaceFormattedElement(string.Empty, length, 0,
                    new SpacerMetrics(MarkdownTableLayout.DelimiterRowHeight, 0)),
                // Una fila de tabla con celdas que envuelven: se dibuja entera en lugar de colocarse con
                // espaciadores, y su alto arrastra al de la línea igual que el de una imagen.
                ZoneKind.TableRow => zone is { TableRow: { } tableRow, TableGeometry: { } tableGeometry }
                    ? new InlineObjectElement(length, new MarkdownTableRowVisual(
                        CurrentContext.Document.GetText(line), line, zone.Line, tableRow, tableGeometry,
                        _style, _state, _textArea.TextView.DefaultLineHeight,
                        _textArea.TextView.DefaultBaseline))
                    : null,
                ZoneKind.Align => spacer > 0 ? new WhitespaceFormattedElement(string.Empty, 0, spacer) : null,
                // The space keeps its own glyph and grows by its share of the row's leftover width.
                ZoneKind.JustifySpace => new WhitespaceFormattedElement(" ", length, spacer),
                _ => spacer > 0 || zone.Metrics != default
                    ? new WhitespaceFormattedElement(string.Empty, length, spacer, zone.Metrics)
                    : new FormattedTextElement(string.Empty, length)
            };
            return element;
        }
        return null;
    }

    /// <summary>Blank space to reserve before the line so its visible text ends up centred or pushed to
    /// the right edge of the text column. AvaloniaEdit gives every visual line the same paragraph
    /// properties, with <c>TextAlignment</c> fixed to <c>Left</c>, so moving the text is the only way to
    /// align it - and moving it means measuring it first (<see cref="MarkdownLineMeasurer"/>).</summary>
    private double ComputeSpacerWidth(DocumentLine line, MdLine model, List<Zone> zones)
    {
        var align = _state.GetAlignment(line.LineNumber);
        if (align is not (MdAlign.Center or MdAlign.Right)) return 0;

        var available = CurrentContext.TextView.Bounds.Width;
        if (double.IsNaN(available) || available <= 0) return 0;

        var hidden = new List<(int Start, int End)>(zones.Count);
        double replacements = 0;
        foreach (var zone in zones)
        {
            if (zone.Kind == ZoneKind.Align) continue;
            hidden.Add((zone.Start, zone.End));
            replacements += zone.Kind switch
            {
                ZoneKind.Bullet => MarkdownLineMeasurer.MeasureGlyph("• ", model, _style),
                ZoneKind.Checkbox => CheckboxSize + CheckboxSpacing,
                ZoneKind.Callout => CalloutIconSize + CheckboxSpacing,
                // Centrar o alinear a la derecha una línea con una imagen exige contar lo que la imagen
                // ocupa, igual que se cuenta el ancho del texto: si no, la línea se centraría como si la
                // imagen no estuviera y quedaría desplazada justo su ancho.
                ZoneKind.Image => zone.Span is { } imageSpan ? MeasureImage(imageSpan, zone.ImageMaxWidth).Width : 0,
                ZoneKind.Transclusion => Math.Max(180, _textArea.TextView.Bounds.Width),
                ZoneKind.Math => zone.Formula?.Bitmap?.Size.Width ?? 40,
                ZoneKind.SpecialBlock => zone.Formula?.Bitmap?.Size.Width
                                         ?? zone.Mermaid?.Bitmap?.Size.Width ?? 80,
                _ => 0
            };
        }

        var styles = MarkdownHtmlParser.BuildStyleRanges(
            model.HtmlTags, line.Offset, line.EndOffset, _state.GetHtml(line.LineNumber).OpenInlineTags);
        var content = replacements + MarkdownLineMeasurer.MeasureVisible(
            CurrentContext.Document.GetText(line), line.Offset, model, hidden, styles, _state.Intersects, _style);

        // A line with nothing visible on it (the "<div align=\"center\">" line itself) has nothing to
        // centre: reserving half the column there would only strand the caret in the middle of a blank line.
        if (content <= 0) return 0;

        // A line too long for the column keeps its left edge: the reserved space only ever moves the
        // first visual row. La sangría de las listas se resuelve por separado en
        // MarkdownBlockTextFormatter, usando las medidas finales de sus elementos.
        var free = available - content;
        if (free <= 1) return 0;
        return align == MdAlign.Center ? free / 2 : free;
    }

    /// <summary>Tamaño al que se dibuja la imagen de <paramref name="span"/>, delegado en
    /// <see cref="MarkdownImageMeasurer"/> — la misma cuenta que hace la geometría de una tabla para
    /// dimensionar la columna de una celda con imagen dentro.</summary>
    private MarkdownImageLayout MeasureImage(MdSpan span, double maxWidth) =>
        MarkdownImageMeasurer.Measure(_state, _textArea.TextView, span,
            maxWidth > 0 ? maxWidth : double.PositiveInfinity);

    private Control BuildTransclusion(string reference, double leadingWidth)
    {
        var context = _state.Transclusions;
        var result = context.Resolve?.Invoke(reference,
            context.CurrentNotePath is { } path ? [path] : null)
            ?? new NoteTransclusionResult(NoteTransclusionStatus.Broken, reference, null, string.Empty,
                Array.Empty<string>());
        var available = Math.Max(180, _textArea.TextView.Bounds.Width - leadingWidth);
        var control = MarkdownTransclusionPresenter.Build(result, context, _style, available);
        control.Margin = new Thickness(leadingWidth, control.Margin.Top, control.Margin.Right, control.Margin.Bottom);
        return control;
    }

    private Control BuildFormula(MdSpan span, MarkdownFormulaEntry entry, double leadingWidth)
    {
        Control control;
        if (entry is { State: MarkdownRenderState.Ready, Bitmap: { } bitmap })
        {
            var available = Math.Max(40, _textArea.TextView.Bounds.Width - leadingWidth);
            var scale = bitmap.Size.Width > available ? available / bitmap.Size.Width : 1;
            control = new Image
            {
                Source = bitmap,
                Width = Math.Max(1, bitmap.Size.Width * scale),
                Height = Math.Max(1, bitmap.Size.Height * scale),
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };
            AutomationProperties.SetName(control,
                CurrentContext.Document.GetText(span.ContentStart, span.ContentEnd - span.ContentStart));
        }
        else
        {
            control = new Border
            {
                Background = _style.CodeInlineBackground,
                BorderBrush = _style.CodeBlockBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(7, 2),
                Child = new TextBlock
                {
                    Text = Localization.LocalizationService.Instance["Editor_Math_Rendering"],
                    FontFamily = _style.MonoFontFamily,
                    FontSize = Math.Max(10, _style.BaseFontSize - 3),
                    Foreground = _style.TextMuted
                },
                IsHitTestVisible = false
            };
        }

        control.Margin = new Thickness(leadingWidth, span.IsDisplayMath ? 5 : 0, 0, span.IsDisplayMath ? 5 : 0);
        control.Measure(Size.Infinity);
        var descent = _textArea.TextView.DefaultLineHeight - _textArea.TextView.DefaultBaseline;
        TextBlock.SetBaselineOffset(control, Math.Max(0, control.DesiredSize.Height - descent));
        return control;
    }

    private Control BuildFormulaBlock(MarkdownSpecialBlock block, MarkdownFormulaEntry entry)
    {
        var surrogate = new MdSpan
        {
            Kind = MdSpanKind.Math,
            Start = block.StartOffset,
            End = block.EndOffset,
            ContentStart = block.StartOffset,
            ContentEnd = block.StartOffset,
            IsDisplayMath = true
        };
        var control = BuildFormula(surrogate, entry, 0);
        AutomationProperties.SetName(control, block.Source);
        return control;
    }

    private Control BuildMermaidBlock(MarkdownSpecialBlock block, MarkdownMermaidEntry? entry)
    {
        if (entry is { State: MarkdownRenderState.Ready, Bitmap: { } bitmap })
        {
            var available = Math.Max(80, _textArea.TextView.Bounds.Width);
            var scale = bitmap.Size.Width > available ? available / bitmap.Size.Width : 1;
            var image = new Image
            {
                Source = bitmap,
                Width = Math.Max(1, bitmap.Size.Width * scale),
                Height = Math.Max(1, bitmap.Size.Height * scale),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 7),
                IsHitTestVisible = false
            };
            AutomationProperties.SetName(image, block.Source);
            SetImageBaseline(image, image.Height);
            return image;
        }

        var loading = new Border
        {
            Background = _style.CodeBlockBackground,
            BorderBrush = _style.CodeBlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 5),
            Child = new TextBlock
            {
                Text = enki.Localization.LocalizationService.Instance["Editor_Mermaid_Rendering"],
                FontFamily = _style.MonoFontFamily,
                FontSize = Math.Max(10, _style.BaseFontSize - 2),
                Foreground = _style.TextMuted
            },
            IsHitTestVisible = false
        };
        loading.Measure(Size.Infinity);
        TextBlock.SetBaselineOffset(loading, loading.DesiredSize.Height);
        return loading;
    }


    /// <summary>Texto alternativo escrito en la nota, o el nombre del archivo si no hay ninguno — algo que
    /// anunciar siempre es mejor que una imagen anónima.</summary>
    private string AltTextOf(MdSpan span)
    {
        if (span.ContentEnd > span.ContentStart)
        {
            var alt = CurrentContext.Document.GetText(span.ContentStart, span.ContentEnd - span.ContentStart);
            if (!string.IsNullOrWhiteSpace(alt)) return NoteAttachmentService.UnescapeLabel(alt);
        }
        return MarkdownImageResolver.ShortLabel(span.LinkTarget ?? string.Empty);
    }

    /// <summary>Construye el control que ocupa el sitio del <c>![…](…)</c>: la imagen ya decodificada, o
    /// un hueco con el nombre del archivo mientras carga / si no se pudo cargar. El hueco no es adorno:
    /// una imagen que falta (ruta mal escrita, adjunto borrado, imagen remota con la descarga desactivada)
    /// tiene que verse como tal, no desaparecer sin dejar rastro de que la nota pedía algo ahí.</summary>
    private Control BuildImage(MdSpan span, double leadingWidth, double maxWidth)
    {
        var layout = MeasureImage(span, maxWidth);
        var margin = new Thickness(leadingWidth, 0, 0, 0);

        if (NoteAttachmentService.IsAttachmentTarget(span.LinkTarget ?? string.Empty))
        {
            var button = new Button
            {
                Width = layout.Width, Height = layout.Height, Margin = margin,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        new MaterialIcon { Kind = MaterialIconKind.Paperclip, Width = 18, Height = 18 },
                        new TextBlock { Text = AltTextOf(span), MaxWidth = Math.Max(20, layout.Width - 70),
                            TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = _style.MonoFontFamily,
                            FontSize = Math.Max(10, _style.BaseFontSize - 2) }
                    }
                }
            };
            button.Classes.Add("tag-chip");
            var target = span.LinkTarget!;
            button.Click += (_, _) => _state.Images.OpenAttachment?.Invoke(target);
            ToolTip.SetTip(button, target);
            AutomationProperties.SetName(button, AltTextOf(span));
            SetImageBaseline(button, layout.Height);
            return button;
        }

        if (layout.Entry is { State: MarkdownImageState.Ready, Bitmap: { } bitmap })
        {
            var image = new Image
            {
                Source = bitmap,
                Width = layout.Width,
                Height = layout.Height,
                Stretch = Stretch.Uniform,
                Margin = margin,
                // El caret y la selección se colocan clicando el texto que hay detrás: la imagen es
                // decoración de una línea que se sigue editando como texto, igual que la casilla de una
                // tarea (ver CreateInertCheckbox).
                IsHitTestVisible = false
            };
            SetImageBaseline(image, layout.Height);
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            // El texto alternativo no se dibuja (no es una leyenda), pero sí es lo que un lector de
            // pantalla debe anunciar — que es exactamente para lo que el usuario lo escribió.
            AutomationProperties.SetName(image, AltTextOf(span));
            return image;
        }

        var loading = layout.Entry.State == MarkdownImageState.Loading;
        var label = new TextBlock
        {
            Text = MarkdownImageResolver.ShortLabel(span.LinkTarget ?? string.Empty),
            FontFamily = _style.MonoFontFamily,
            FontSize = Math.Max(10, _style.BaseFontSize - 3),
            Foreground = _style.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            Children =
            {
                new MaterialIcon
                {
                    Kind = loading ? MaterialIconKind.ImageOutline : MaterialIconKind.ImageBrokenVariant,
                    Width = 14,
                    Height = 14,
                    Foreground = _style.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center
                },
                label
            }
        };

        var placeholder = new Border
        {
            Width = layout.Width,
            Height = layout.Height,
            Margin = margin,
            Background = _style.CodeBlockBackground,
            BorderBrush = _style.CodeBlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = content,
            IsHitTestVisible = false
        };
        SetImageBaseline(placeholder, layout.Height);
        return placeholder;
    }

    /// <summary>Aloja las métricas del texto dentro del alto de la imagen. Sin baseline explícita,
    /// el objeto inline reserva además una franja de texto vacía que no pertenece al documento.</summary>
    private void SetImageBaseline(Control control, double height)
    {
        var textView = _textArea.TextView;
        var descent = textView.DefaultLineHeight - textView.DefaultBaseline;
        TextBlock.SetBaselineOffset(control, Math.Max(0, height - descent));
    }

    /// <summary>El distintivo que sustituye al <c>[!tipo]</c> de un aviso: su icono y, solo si el aviso no
    /// trae título propio, el nombre del tipo. Cuando sí lo trae, el título es el texto real de la línea y
    /// el distintivo se queda en el icono — así lo que se lee en grande es lo que escribió el autor.
    ///
    /// Sin hit-test: pinchar sobre el icono coloca el cursor en el documento, como pinchar sobre
    /// cualquier otro trozo de la línea.</summary>
    private Control BuildCalloutBadge(DocumentLine line, MdLine model, double leadingWidth)
    {
        var kind = model.CalloutKind ?? "note";
        var brush = MarkdownCallouts.BrushOf(MarkdownCallouts.ToneOf(kind), _style);
        var textView = _textArea.TextView;

        var icon = new MaterialIcon
        {
            Kind = MarkdownCallouts.IconOf(kind),
            Width = CalloutIconSize,
            Height = CalloutIconSize,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            // El espaciador de la cita amplía la franja unos píxeles más que la caja inline.
            // Compensa la mitad de ese hueco sin alterar el alto de la línea ni el hit testing.
            RenderTransform = new TranslateTransform(0, CalloutVerticalPadding / 2)
        };

        Control content;
        if (model.CalloutTitleStart >= line.EndOffset)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = CheckboxSpacing };
            panel.Children.Add(icon);
            panel.Children.Add(new TextBlock
            {
                Text = MarkdownCallouts.DefaultTitle(kind),
                Foreground = brush,
                FontFamily = _style.BodyFontFamily,
                FontSize = _style.BaseFontSize,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            content = panel;
        }
        else
        {
            // El contenedor inline se coloca arriba para respetar su línea base. Si el icono fuese ese
            // contenedor, esa alineación sustituiría su Center y lo dejaría pegado arriba.
            content = new Grid { Children = { icon } };
        }

        content.IsHitTestVisible = false;
        content.Margin = new Thickness(leadingWidth, 0, CheckboxSpacing, 0);
        content.VerticalAlignment = VerticalAlignment.Top;
        var height = Math.Max(CalloutIconSize, textView.DefaultLineHeight);
        content.Height = height;
        TextBlock.SetBaselineOffset(content, textView.DefaultBaseline);
        return content;
    }

    private CheckBox BuildCheckbox(MdLine model, double leadingWidth)
    {
        var checkBox = CreateInertCheckbox(model.TaskChecked, _textArea, leadingWidth);

        if (model.CheckboxCharOffset >= 0)
        {
            // Anchored rather than a plain captured int: the visual line (and this widget) can outlive a
            // document edit made elsewhere before AvaloniaEdit gets around to rebuilding it, and a raw
            // offset would then point at the wrong character on click, corrupting whatever text had shifted
            // into its place. TextAnchor tracks the "[ ]" char through any such edit automatically.
            var anchor = _textArea.Document.CreateAnchor(model.CheckboxCharOffset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;

            checkBox.IsHitTestVisible = true;
            checkBox.Click += (_, _) =>
            {
                if (anchor.IsDeleted) return;
                var doc = _textArea.Document;
                var offset = anchor.Offset;
                if (offset < 0 || offset >= doc.TextLength || !_textArea.ReadOnlySectionProvider.CanInsert(offset)) return;
                var current = doc.GetCharAt(offset);
                var next = current is 'x' or 'X' ? ' ' : 'x';
                doc.Replace(offset, 1, next.ToString());
            };
        }

        return checkBox;
    }

    /// <summary>The 16px box (see CheckBox.markdown-checkbox in Styles.axaml) sits inside an
    /// AvaloniaEdit <see cref="InlineObjectElement"/>, whose TextView.ArrangeOverride places the
    /// control at the top of the line box. To center the box with the caret — which spans the full
    /// line height — we push it down by half the difference between the editor line height and the
    /// box, and pin the inline run's baseline to the text baseline so the surrounding line height
    /// does not grow.</summary>
    private static CheckBox CreateInertCheckbox(bool isChecked, TextArea textArea, double leadingWidth)
    {
        const double boxSize = CheckboxSize;
        var textView = textArea.TextView;
        var topMargin = Math.Max(0, (textView.DefaultLineHeight - boxSize) / 2);

        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            IsHitTestVisible = false,
            Padding = default,
            Width = boxSize,
            Height = boxSize,
            MinWidth = boxSize,
            MinHeight = boxSize,
            Margin = new Avalonia.Thickness(leadingWidth, topMargin, CheckboxSpacing, 0),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        checkBox.Classes.Add("markdown-checkbox");
        TextBlock.SetBaselineOffset(checkBox, textView.DefaultBaseline);
        return checkBox;
    }
}
