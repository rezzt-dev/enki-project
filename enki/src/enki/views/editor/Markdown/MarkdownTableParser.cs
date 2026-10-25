using System;
using System.Collections.Generic;

namespace enki.Views.Editor.Markdown;

/// <summary>Alineación de una columna, tal y como la declara la fila separadora de la tabla
/// (<c>| :--- | :---: | ---: |</c>). <see cref="Default"/> es la ausencia de dos puntos: se dibuja y se
/// exporta como izquierda, pero se distingue para que el HTML exportado no lleve un
/// <c>text-align</c> que la nota nunca pidió.</summary>
public enum MdTableAlign
{
    Default,
    Left,
    Center,
    Right
}

/// <summary>Papel de una línea dentro de su tabla: la fila de encabezados, la fila separadora que declara
/// las alineaciones, o una fila de datos.</summary>
public enum MdTableRowKind
{
    Header,
    Delimiter,
    Body
}

/// <summary>Una tabla completa localizada en el documento, en números de línea (1-based) — la unidad con
/// la que razonan los tres renderizadores del live preview: una tabla se dibuja o se muestra en crudo
/// entera, nunca a medias.</summary>
public sealed class MdTable
{
    /// <summary>Línea de la fila de encabezados; <see cref="DelimiterLine"/> es siempre la siguiente.</summary>
    public required int StartLine { get; init; }

    /// <summary>Última fila de datos (o la separadora, si la tabla no tiene ninguna).</summary>
    public required int EndLine { get; init; }

    public int DelimiterLine => StartLine + 1;

    /// <summary>Alineación de cada columna. Su longitud es el número de columnas de la tabla, que es el
    /// máximo de celdas que tiene cualquiera de sus filas: una fila con celdas de más no se recorta (se
    /// perdería contenido que el usuario escribió), sino que ensancha la tabla, y esas columnas extra
    /// quedan con <see cref="MdTableAlign.Default"/>.</summary>
    public required IReadOnlyList<MdTableAlign> Alignments { get; init; }

    public int ColumnCount => Alignments.Count;

    /// <summary>Número de filas de datos — 0 en una tabla recién insertada que solo tiene encabezado y
    /// separadora.</summary>
    public int BodyRowCount => EndLine - StartLine - 1;
}

/// <summary>Lo que una línea del documento es dentro de su tabla.</summary>
public readonly record struct MdTableRow(MdTable Table, MdTableRowKind Kind, int LineNumber);

/// <summary>Contenido de una celda en offsets absolutos de documento, ya sin los espacios que la separan
/// de sus barras verticales: es exactamente el rango de texto que el editor dibuja dentro de la columna
/// (y el que el exportador vuelca dentro del <c>&lt;td&gt;</c>).</summary>
public readonly record struct MdTableCell(int Column, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Reconoce tablas Markdown al estilo GFM (<c>| a | b |</c> + <c>| --- | --- |</c>). Está separado de
/// <see cref="MarkdownLineParser"/> porque una fila de tabla no se puede reconocer mirando su propia
/// línea: <c>| a | b |</c> es texto normal si la línea siguiente no es una separadora válida. Igual que
/// <see cref="MarkdownLineParser.ComputeFences"/>, el escaneo recorre el documento entero una vez y deja
/// dicho qué papel juega cada línea; el troceado en celdas (<see cref="SplitCells"/>) se rehace en cambio
/// por línea en cada repintado, para que lo que se dibuja salga siempre del texto actual y no de un
/// escaneo que puede ir un par de pulsaciones por detrás.
/// </summary>
public static class MarkdownTableParser
{
    /// <summary>Recorre el documento y devuelve, por número de línea (1-based), a qué tabla pertenece esa
    /// línea y con qué papel. Las líneas que no son tabla no aparecen en el diccionario, así que un
    /// documento sin tablas cuesta una búsqueda fallida por línea y nada más.</summary>
    public static Dictionary<int, MdTableRow> Scan(string documentText, IReadOnlyDictionary<int, FenceInfo> fences)
    {
        var result = new Dictionary<int, MdTableRow>();
        if (string.IsNullOrEmpty(documentText)) return result;

        var lines = documentText.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (IsCode(fences, i + 1) || IsCode(fences, i + 2)) continue;
            if (!CanBeRow(lines[i])) continue;

            var alignments = ParseDelimiterRow(lines[i + 1]);
            if (alignments is null) continue;

            // La regla que separa una tabla de dos líneas de texto que casualmente llevan barras: GFM
            // exige que la separadora declare exactamente tantas columnas como celdas tiene el
            // encabezado. Sin ella, un "a | b" seguido de "--- | --- | ---" sería tabla.
            var columns = CountCells(lines[i]);
            if (columns == 0 || columns != alignments.Count) continue;

            var endLine = i + 1;
            for (var j = i + 2; j < lines.Length; j++)
            {
                if (IsCode(fences, j + 1) || !CanBeRow(lines[j])) break;
                columns = Math.Max(columns, CountCells(lines[j]));
                endLine = j;
            }

            while (alignments.Count < columns) alignments.Add(MdTableAlign.Default);

            var table = new MdTable
            {
                StartLine = i + 1,
                EndLine = endLine + 1,
                Alignments = alignments
            };

            result[table.StartLine] = new MdTableRow(table, MdTableRowKind.Header, table.StartLine);
            result[table.DelimiterLine] = new MdTableRow(table, MdTableRowKind.Delimiter, table.DelimiterLine);
            for (var line = table.DelimiterLine + 1; line <= table.EndLine; line++)
                result[line] = new MdTableRow(table, MdTableRowKind.Body, line);

            i = endLine;
        }

        return result;
    }

    /// <summary>Trocea una fila en las celdas que la componen, devolviendo el contenido de cada una ya
    /// recortado de espacios y en offsets absolutos (<paramref name="lineOffset"/> es el del principio de
    /// la línea). Las barras exteriores son opcionales, como en GFM — <c>a | b</c> y <c>| a | b |</c> son
    /// la misma fila de dos celdas—, y una barra escapada (<c>\|</c>) es contenido, no un separador.</summary>
    public static List<MdTableCell> SplitCells(string lineText, int lineOffset)
    {
        var cells = new List<MdTableCell>();
        var start = 0;
        var end = lineText.Length;
        while (start < end && char.IsWhiteSpace(lineText[start])) start++;
        while (end > start && char.IsWhiteSpace(lineText[end - 1])) end--;
        if (start >= end) return cells;

        if (lineText[start] == '|') start++;
        // La barra final solo es "exterior" si no es la misma que ya se ha consumido como barra de
        // apertura: en una fila que es solo "|" una barra no puede abrir y cerrar a la vez.
        if (end - 1 >= start && lineText[end - 1] == '|' && !IsEscaped(lineText, end - 1)) end--;

        var column = 0;
        var cellStart = start;
        for (var i = start; i <= end; i++)
        {
            if (i < end && (lineText[i] != '|' || IsEscaped(lineText, i))) continue;

            var contentStart = cellStart;
            var contentEnd = i;
            while (contentStart < contentEnd && char.IsWhiteSpace(lineText[contentStart])) contentStart++;
            while (contentEnd > contentStart && char.IsWhiteSpace(lineText[contentEnd - 1])) contentEnd--;

            cells.Add(new MdTableCell(column++, lineOffset + contentStart, lineOffset + contentEnd));
            cellStart = i + 1;
        }

        return cells;
    }

    /// <summary>Alineaciones declaradas por una fila separadora (<c>| :--- | ---: |</c>), o null si la
    /// línea no lo es. Cada celda tiene que ser exactamente guiones con dos puntos opcionales a los
    /// lados: cualquier otra cosa (una celda vacía, texto) descarta la fila entera, que es lo que impide
    /// que una fila de datos con guiones se confunda con la separadora.</summary>
    public static List<MdTableAlign>? ParseDelimiterRow(string lineText)
    {
        if (lineText.IndexOf('-') < 0) return null;

        var cells = SplitCells(lineText, 0);
        if (cells.Count == 0) return null;

        var alignments = new List<MdTableAlign>(cells.Count);
        foreach (var cell in cells)
        {
            var align = ParseDelimiterCell(lineText, cell.Start, cell.End);
            if (align is null) return null;
            alignments.Add(align.Value);
        }
        return alignments;
    }

    private static MdTableAlign? ParseDelimiterCell(string lineText, int start, int end)
    {
        if (end <= start) return null;

        var left = lineText[start] == ':';
        var right = lineText[end - 1] == ':';
        var dashStart = left ? start + 1 : start;
        var dashEnd = right ? end - 1 : end;
        if (dashEnd <= dashStart) return null;

        for (var i = dashStart; i < dashEnd; i++)
        {
            if (lineText[i] != '-') return null;
        }

        return (left, right) switch
        {
            (true, true) => MdTableAlign.Center,
            (true, false) => MdTableAlign.Left,
            (false, true) => MdTableAlign.Right,
            _ => MdTableAlign.Default
        };
    }

    /// <summary>Si esta línea puede ser una fila de tabla. Aparte de llevar al menos una barra sin
    /// escapar, no puede empezar por el marcador de otro bloque: un elemento de lista o una cita que
    /// lleven una barra dentro siguen siendo lista y cita, y una tabla nunca continúa a través de una
    /// línea en blanco.</summary>
    private static bool CanBeRow(string lineText)
    {
        var i = 0;
        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t')) i++;
        if (i >= lineText.Length) return false;

        var c = lineText[i];
        if (c is '#' or '>') return false;
        if ((c is '-' or '*' or '+') && i + 1 < lineText.Length && (lineText[i + 1] == ' ' || lineText[i + 1] == '\t')) return false;

        var digits = i;
        while (digits < lineText.Length && char.IsDigit(lineText[digits])) digits++;
        if (digits > i && digits + 1 < lineText.Length && lineText[digits] is '.' or ')' && lineText[digits + 1] == ' ') return false;

        return HasUnescapedPipe(lineText, i);
    }

    private static bool HasUnescapedPipe(string lineText, int from)
    {
        for (var i = from; i < lineText.Length; i++)
        {
            if (lineText[i] == '|' && !IsEscaped(lineText, i)) return true;
        }
        return false;
    }

    private static int CountCells(string lineText) => SplitCells(lineText, 0).Count;

    /// <summary>Si el carácter en <paramref name="index"/> es una barra que separa dos celdas — y no una
    /// barra escapada, que es contenido. Lo usa el coloreado para apagar la sintaxis de la tabla mientras
    /// se edita en crudo, sin tener que rehacer el troceado en celdas.</summary>
    public static bool IsCellSeparator(string lineText, int index) =>
        index >= 0 && index < lineText.Length && lineText[index] == '|' && !IsEscaped(lineText, index);

    /// <summary>Si el carácter en <paramref name="index"/> va precedido de un número impar de barras
    /// invertidas — es decir, si está escapado. Contarlas (en vez de mirar solo la anterior) es lo que
    /// hace que <c>\\|</c> sea una barra invertida literal seguida de un separador de verdad.</summary>
    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }

    private static bool IsCode(IReadOnlyDictionary<int, FenceInfo> fences, int lineNumber) =>
        fences.TryGetValue(lineNumber, out var fence) && fence.IsCode;
}
