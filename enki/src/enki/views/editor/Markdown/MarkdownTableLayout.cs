using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace enki.Views.Editor.Markdown;

/// <summary>Geometría ya resuelta de una tabla: dónde empieza el texto de cada columna y cuánto mide,
/// en píxeles y relativo al borde izquierdo de la columna de texto del editor. Es la única fuente de la
/// que salen tanto los espaciadores que colocan el texto (<see cref="MarkdownElementGenerator"/>) como
/// las líneas de la rejilla (<see cref="MarkdownBackgroundRenderer"/>): si cada uno hiciera su cuenta,
/// bastaría un redondeo distinto para que el texto no cayera dentro de su celda.</summary>
internal sealed class MdTableGeometry
{
    /// <summary>Ancho de cada columna, hueco interior aparte: el de su celda más ancha, más la parte que
    /// le toca del espacio sobrante hasta llenar la columna de texto (ver <c>MarkdownTableLayout</c>).</summary>
    public required double[] ColumnWidths { get; init; }

    /// <summary>X donde empieza el texto de cada columna (ya con el hueco interior sumado).</summary>
    public required double[] ColumnX { get; init; }

    /// <summary>Hueco entre la línea vertical de la rejilla y el texto, a cada lado de la celda.</summary>
    public required double CellPadding { get; init; }

    /// <summary>Ancho total de la tabla dibujada, borde a borde.</summary>
    public required double TotalWidth { get; init; }

    /// <summary>Alto del contenido de cada fila (sin el relleno vertical), por número de línea, cuando la
    /// tabla tiene alguna celda que envuelve a varias líneas. Null cuando cada fila cabe en una línea, que
    /// es el caso corriente y el que se dibuja con espaciadores.</summary>
    public IReadOnlyDictionary<int, double>? RowHeights { get; init; }

    /// <summary>Si esta tabla se dibuja con celdas envueltas — filas altas dibujadas como un objeto en vez
    /// de texto colocado con espaciadores (ver <see cref="MarkdownTableRowVisual"/>).</summary>
    public bool Wraps => RowHeights is { Count: > 0 };

    /// <summary>Alto del contenido de la fila <paramref name="lineNumber"/>, o 0 si esta tabla no envuelve
    /// o esa línea no es una de sus filas.</summary>
    public double RowHeight(int lineNumber) =>
        RowHeights is { } heights && heights.TryGetValue(lineNumber, out var height) ? height : 0;

    /// <summary>X de la línea vertical que separa la columna <paramref name="column"/> de la anterior.</summary>
    public double SeparatorX(int column) => ColumnX[column] - CellPadding;
}

/// <summary>
/// Estado de las tablas del documento abierto: qué papel juega cada línea (lo que devuelve
/// <see cref="MarkdownTableParser.Scan"/>) y, calculada bajo demanda, la geometría de cada tabla.
///
/// <para>El cálculo de anchos es caro —hay que medir cada celda de la tabla con la tipografía real— y no
/// depende del texto solo, sino también del ancho de la columna del editor y del tamaño de letra. Por eso
/// se cachea por tabla y se tira entero en cuanto cambia cualquiera de esas tres cosas: el texto (que
/// crea instancias nuevas de <see cref="MdTable"/> al reescanear), el tamaño del editor o la
/// tipografía.</para>
/// </summary>
internal sealed class MarkdownTableLayout
{
    /// <summary>Hueco entre la línea de la rejilla y el texto de la celda, a cada lado.</summary>
    private const double DefaultCellPadding = 10;

    /// <summary>Hueco mínimo al que se aprieta una tabla que no cabe a lo ancho antes de empezar a recortar
    /// columnas.</summary>
    private const double MinimumCellPadding = 5;

    /// <summary>Ancho mínimo de una columna recortada. Por debajo de esto una celda con texto envolvería
    /// una o dos letras por línea, que es peor de leer que la tabla en Markdown crudo: si ni siquiera este
    /// mínimo cabe para todas las columnas, la tabla se deja en crudo.</summary>
    private const double MinColumnWidth = 48;

    /// <summary>Espacio en blanco que se añade encima y debajo del texto de cada fila. Sin él la rejilla
    /// quedaría pegada a las letras: una fila de tabla es una línea de texto normal, y una línea de texto
    /// no tiene relleno vertical propio.</summary>
    public const double RowVerticalPadding = 4;

    /// <summary>Alto al que se encoge la fila separadora (<c>| --- | --- |</c>) cuando la tabla se dibuja:
    /// no es contenido, es la declaración de las alineaciones, así que ocupa lo justo para hacer de línea
    /// entre el encabezado y el cuerpo. Sigue siendo una línea del documento y se puede editar — poner el
    /// cursor en la tabla la devuelve entera a su forma de texto.</summary>
    public const double DelimiterRowHeight = 7;

    /// <summary>Techo del ancho de una imagen dentro de una celda. Sin él, una imagen sin tamaño explícito
    /// se dibujaría al ancho de la columna de texto y la tabla entera dejaría de caber.</summary>
    public const double CellImageMaxWidth = 240;

    private IReadOnlyDictionary<int, MdTableRow> _rows = new Dictionary<int, MdTableRow>();
    private readonly List<MdTable> _tables = new();
    private readonly Dictionary<MdTable, MdTableGeometry?> _geometry = new();

    private double _availableWidth = double.NaN;
    private double _fontSize = double.NaN;
    private string _fontKey = string.Empty;

    /// <summary>Si el documento tiene alguna tabla — deja que los renderizadores se salten todo el trabajo
    /// de tablas en la nota corriente, que no tiene ninguna.</summary>
    public bool HasTables => _rows.Count > 0;

    /// <summary>Repone el resultado del escaneo del documento (ver <see cref="MarkdownTableParser.Scan"/>)
    /// y tira la geometría cacheada, que se calculó sobre las tablas anteriores.</summary>
    public void SetRows(IReadOnlyDictionary<int, MdTableRow> rows)
    {
        _rows = rows;
        _geometry.Clear();
        _tables.Clear();
        foreach (var row in rows.Values)
        {
            if (row.Kind == MdTableRowKind.Header) _tables.Add(row.Table);
        }
    }

    /// <summary>Las tablas del documento, una vez cada una (el diccionario de líneas repite la misma tabla
    /// en cada una de sus filas).</summary>
    public IReadOnlyList<MdTable> Tables => _tables;

    /// <summary>Olvida los anchos calculados sin tocar el escaneo — lo que hace falta cuando cambia algo
    /// que solo afecta a la medida (una imagen de una celda que acaba de cargarse).</summary>
    public void InvalidateGeometry() => _geometry.Clear();

    public MdTableRow? GetRow(int lineNumber) =>
        _rows.TryGetValue(lineNumber, out var row) ? row : null;

    /// <summary>Geometría de <paramref name="table"/> con el ancho y la tipografía actuales, o null si la
    /// tabla no cabe a lo ancho ni recortando sus columnas al mínimo — el único caso que queda en
    /// Markdown crudo, porque una columna de dos letras de ancho se lee peor que la tabla sin dibujar.
    /// Cuando hay que recortar columnas, la geometría trae además el alto de cada fila
    /// (<see cref="MdTableGeometry.RowHeights"/>): las celdas que no caben envuelven a varias líneas y la
    /// fila crece hasta la más alta.</summary>
    public MdTableGeometry? GetGeometry(MdTable table, TextView textView, MarkdownStyle style, LivePreviewState state)
    {
        var available = textView.Bounds.Width;
        var fontKey = style.BodyFontFamily.Name + "|" + style.MonoFontFamily.Name;
        if (!Same(available, _availableWidth) || !Same(style.BaseFontSize, _fontSize) || fontKey != _fontKey)
        {
            _geometry.Clear();
            _availableWidth = available;
            _fontSize = style.BaseFontSize;
            _fontKey = fontKey;
        }

        if (_geometry.TryGetValue(table, out var cached)) return cached;

        var geometry = Compute(table, textView, style, state, available);
        _geometry[table] = geometry;
        return geometry;
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 0.5 || (double.IsNaN(a) && double.IsNaN(b));

    private static MdTableGeometry? Compute(MdTable table, TextView textView, MarkdownStyle style,
        LivePreviewState state, double available)
    {
        var document = textView.Document;
        if (document is null || available <= 0) return null;

        var widths = new double[table.ColumnCount];
        for (var lineNumber = table.StartLine; lineNumber <= table.EndLine; lineNumber++)
        {
            if (lineNumber == table.DelimiterLine || lineNumber > document.LineCount) continue;

            var line = document.GetLineByNumber(lineNumber);
            var text = document.GetText(line);
            var model = MarkdownLineParser.Parse(text, line.Offset, state.GetFence(lineNumber));
            var header = lineNumber == table.StartLine;

            foreach (var cell in MarkdownTableParser.SplitCells(text, line.Offset))
            {
                if (cell.Column >= widths.Length) continue;
                widths[cell.Column] = Math.Max(widths[cell.Column],
                    MeasureCell(text, line, model, cell, header, style, state, textView));
            }
        }

        var padding = DefaultCellPadding;
        var total = TotalWidth(widths, padding);
        var wraps = false;
        if (total > available)
        {
            padding = MinimumCellPadding;
            total = TotalWidth(widths, padding);
            if (total > available)
            {
                // Ni apretando los huecos cabe una línea por fila: se recortan las columnas al ancho que
                // les toca y las celdas que se pasen envolverán dentro de la suya.
                if (!CapColumns(widths, available - 2 * padding * widths.Length)) return null;
                wraps = true;
                total = TotalWidth(widths, padding);
            }
        }

        // Una tabla ocupa siempre el ancho de la columna de texto, no el de su contenido: el sobrante se
        // reparte entre las columnas y el ancho total pasa a ser el disponible. Tras recortar no sobra
        // nada, así que ahí no hace nada.
        Stretch(widths, available - total);
        total = TotalWidth(widths, padding);

        var columnX = new double[widths.Length];
        var x = padding;
        for (var i = 0; i < widths.Length; i++)
        {
            columnX[i] = x;
            x += widths[i] + 2 * padding;
        }

        return new MdTableGeometry
        {
            ColumnWidths = widths,
            ColumnX = columnX,
            CellPadding = padding,
            TotalWidth = total,
            RowHeights = wraps ? MeasureRowHeights(table, textView, style, state, widths) : null
        };
    }

    /// <summary>Recorta los anchos de columna para que quepan en <paramref name="availableContent"/>
    /// píxeles de contenido (el disponible menos los huecos), y devuelve si ha sido posible.
    ///
    /// <para>El reparto es de "parte justa máximo-mínimo", el mismo que hace un navegador con un
    /// <c>&lt;table&gt;</c> al 100 %: una columna que ya cabe en su parte se queda con su ancho natural y
    /// lo que le sobra pasa a las demás, hasta que solo quedan columnas más anchas que la parte que les
    /// toca, que se la reparten a partes iguales. Así la columna del "sí/no" no se ensancha ni la de las
    /// notas largas se queda con la misma parte que ella.</para>
    ///
    /// <para>Devuelve false si no cabe ni <see cref="MinColumnWidth"/> por columna — el único caso en el
    /// que la tabla se queda en Markdown crudo.</para></summary>
    private static bool CapColumns(double[] widths, double availableContent)
    {
        if (widths.Length == 0 || availableContent < MinColumnWidth * widths.Length) return false;

        var settled = new bool[widths.Length];
        var remaining = availableContent;
        var free = widths.Length;

        while (free > 0)
        {
            var share = remaining / free;
            var any = false;
            for (var i = 0; i < widths.Length; i++)
            {
                if (settled[i] || widths[i] > share) continue;
                settled[i] = true;
                remaining -= widths[i];
                free--;
                any = true;
            }
            if (!any) break;
        }

        if (free > 0)
        {
            // La parte que queda nunca baja de MinColumnWidth: solo se han apartado columnas más estrechas
            // que la media, lo que solo puede subirla.
            var share = remaining / free;
            for (var i = 0; i < widths.Length; i++)
            {
                if (!settled[i]) widths[i] = share;
            }
        }

        return true;
    }

    /// <summary>Alto del contenido de cada fila de la tabla una vez envueltas sus celdas: el de la celda
    /// más alta, y nunca menos que una línea de texto. Es el número que hace que la rejilla del fondo y el
    /// objeto que dibuja la fila midan lo mismo, porque los dos salen de aquí.</summary>
    private static Dictionary<int, double> MeasureRowHeights(MdTable table, TextView textView,
        MarkdownStyle style, LivePreviewState state, double[] widths)
    {
        var heights = new Dictionary<int, double>();
        var document = textView.Document;
        if (document is null) return heights;

        for (var lineNumber = table.StartLine; lineNumber <= table.EndLine; lineNumber++)
        {
            if (lineNumber == table.DelimiterLine || lineNumber > document.LineCount) continue;

            var line = document.GetLineByNumber(lineNumber);
            var text = document.GetText(line);
            var model = MarkdownLineParser.Parse(text, line.Offset, state.GetFence(lineNumber));
            var header = lineNumber == table.StartLine;
            var height = textView.DefaultLineHeight;

            foreach (var cell in MarkdownTableParser.SplitCells(text, line.Offset))
            {
                if (cell.Column >= widths.Length || cell.Length <= 0) continue;
                var formatted = BuildCell(text, line, model, cell, header, table.Alignments[cell.Column],
                    widths[cell.Column], style, state, style.TextPrimary);
                height = Math.Max(height, formatted.Height);
            }

            heights[lineNumber] = height;
        }

        return heights;
    }

    /// <summary>Compone el contenido de una celda como un párrafo que envuelve dentro de su columna, ya
    /// alineado como pide la fila separadora. Lo usan las dos capas que tienen que coincidir al píxel: la
    /// medida del alto de la fila (arriba) y el objeto que la dibuja
    /// (<see cref="MarkdownTableRowVisual"/>).</summary>
    public static FormattedText BuildCell(string lineText, DocumentLine line, MdLine model, MdTableCell cell,
        bool header, MdTableAlign align, double columnWidth, MarkdownStyle style, LivePreviewState state,
        IBrush foreground)
    {
        var hidden = new List<(int Start, int End, string? Replacement)>();
        foreach (var span in model.Spans)
        {
            if (span.Start >= cell.End || span.End <= cell.Start) continue;

            // En una fila envuelta no hay dónde colgar el control de una imagen: la fila entera es un
            // dibujo, no una línea de texto con elementos. Se anuncia con el nombre de su archivo, que es
            // mejor que borrar sin rastro lo que la nota pedía ahí.
            if (span.Kind == MdSpanKind.Image)
            {
                hidden.Add((span.Start, span.End,
                    MarkdownImageResolver.ShortLabel(span.LinkTarget ?? string.Empty)));
                continue;
            }

            if (span.ContentStart > span.Start) hidden.Add((span.Start, span.ContentStart, null));
            if (span.End > span.ContentEnd) hidden.Add((span.ContentEnd, span.End, null));
        }

        var styles = MarkdownHtmlParser.BuildStyleRanges(
            model.HtmlTags, line.Offset, line.EndOffset, state.GetHtml(line.LineNumber).OpenInlineTags);

        return MarkdownLineMeasurer.BuildVisible(
            lineText, line.Offset, model, hidden, styles, style, foreground,
            cell.Start - line.Offset, cell.End - line.Offset,
            header ? FontWeight.SemiBold : null, columnWidth, ToTextAlignment(align));
    }

    private static TextAlignment ToTextAlignment(MdTableAlign align) => align switch
    {
        MdTableAlign.Center => TextAlignment.Center,
        MdTableAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    /// <summary>Reparte <paramref name="leftover"/> píxeles sobrantes entre las columnas, en proporción a
    /// lo que ocupa el contenido de cada una — igual que hace un navegador con un <c>&lt;table&gt;</c> al
    /// 100 % de ancho: la columna de las notas largas se lleva el hueco y la del "sí/no" se queda estrecha,
    /// que es justo lo contrario de lo que pasaría repartiendo a partes iguales. Con todas las celdas
    /// vacías (una tabla recién insertada) no hay proporción que respetar y el reparto es equitativo.
    /// El resto de la división se le da a la última columna para que el ancho total cuadre al píxel con el
    /// disponible y el borde derecho caiga exactamente donde acaba la columna de texto.</summary>
    private static void Stretch(double[] widths, double leftover)
    {
        if (leftover <= 0.5 || widths.Length == 0) return;

        var content = 0.0;
        foreach (var width in widths) content += width;

        var given = 0.0;
        for (var i = 0; i < widths.Length - 1; i++)
        {
            var share = content > 0 ? leftover * widths[i] / content : leftover / widths.Length;
            widths[i] += share;
            given += share;
        }
        widths[^1] += leftover - given;
    }

    private static double TotalWidth(double[] widths, double padding)
    {
        var total = 0.0;
        foreach (var width in widths) total += width + 2 * padding;
        return total;
    }

    /// <summary>Ancho que ocupará el contenido de una celda una vez dibujado: el texto ya sin los
    /// marcadores que el live preview oculta, con la tipografía que le va a tocar (negrita en el
    /// encabezado), más lo que ocupen las imágenes que lleve dentro.</summary>
    public static double MeasureCell(string lineText, DocumentLine line, MdLine model, MdTableCell cell,
        bool header, MarkdownStyle style, LivePreviewState state, TextView textView)
    {
        if (cell.Length <= 0) return 0;

        var hidden = new List<(int Start, int End)>();
        double images = 0;
        foreach (var span in model.Spans)
        {
            if (span.Start >= cell.End || span.End <= cell.Start) continue;

            if (span.Kind == MdSpanKind.Image)
            {
                hidden.Add((span.Start, span.End));
                images += MarkdownImageMeasurer.Measure(state, textView, span, CellImageMaxWidth).Width;
                continue;
            }

            if (span.ContentStart > span.Start) hidden.Add((span.Start, span.ContentStart));
            if (span.End > span.ContentEnd) hidden.Add((span.ContentEnd, span.End));
        }

        var styles = MarkdownHtmlParser.BuildStyleRanges(
            model.HtmlTags, line.Offset, line.EndOffset, state.GetHtml(line.LineNumber).OpenInlineTags);

        // Se mide como si el cursor estuviera fuera porque justamente eso es cuando se dibuja la tabla:
        // con el cursor dentro se muestra el Markdown en crudo y esta geometría no se usa.
        return images + MarkdownLineMeasurer.MeasureVisible(
            lineText, line.Offset, model, hidden, styles, static (_, _) => false, style,
            cell.Start - line.Offset, cell.End - line.Offset,
            header ? FontWeight.SemiBold : null);
    }
}
