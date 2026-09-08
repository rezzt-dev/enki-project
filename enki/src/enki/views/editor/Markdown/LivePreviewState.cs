using System;
using System.Collections.Generic;
using System.Linq;
using AvaloniaEdit.Document;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Shared state consumed by the colorizer, element generator and background renderer.
/// Updated by the view whenever the caret moves, the selection changes, or the document
/// text changes (which also recomputes fenced-code-block ranges and table detection).
/// </summary>
public sealed class LivePreviewState
{
    private static readonly List<(int Start, int End)> Empty = new();

    public int CaretOffset { get; set; }
    public List<(int Start, int End)> Selections { get; set; } = Empty;
    public Dictionary<int, FenceInfo> Fences { get; set; } = new();

    /// <summary>HTML context of each line — the alignment of the block wrapping it and the inline tags
    /// still open when it starts (<see cref="MarkdownHtmlParser.ComputeLineStates"/>). Only lines that
    /// carry something are in the dictionary, so a note without HTML costs one empty lookup per line and
    /// nothing else.</summary>
    public Dictionary<int, HtmlLineState> HtmlLines { get; set; } = new();

    /// <summary>Tablas del documento: qué papel juega cada línea dentro de la suya y, ya medida, la
    /// geometría de cada una. Se repone en cada reescaneo del documento
    /// (<see cref="MarkdownTableParser.Scan"/>) igual que <see cref="Fences"/>, con la diferencia de que
    /// esto es un objeto vivo y no un diccionario nuevo: los tres renderizadores comparten la misma caché
    /// de anchos, que es lo que evita que cada uno mida la tabla por su cuenta.</summary>
    internal MarkdownTableLayout Tables { get; } = new();

    /// <summary>Quién pliega las filas separadoras de las tablas dibujadas. Lo pone la vista al construir
    /// el editor (necesita su <c>TextView</c>); null en un contexto sin vista, donde simplemente no hay
    /// nada plegado.</summary>
    internal MarkdownTableFolding? TableFolding { get; set; }

    /// <summary>De dónde se resuelven y leen las imágenes de la nota abierta — la carpeta de la nota, la
    /// raíz del vault y el lector de bytes que descifra solo si la nota vive en una sesión protegida. La
    /// repone <c>EditorView</c> en cada cambio de pestaña; <see cref="MarkdownImageContext.Empty"/> (sin
    /// lector) simplemente hace que ninguna imagen de archivo llegue a cargarse.</summary>
    internal MarkdownImageContext Images { get; set; } = MarkdownImageContext.Empty;

    /// <summary>Term the editor's Buscar/Reemplazar panel is currently searching for — null/empty means
    /// no highlight. Set by the view whenever the panel is open and its search text/match-case changes, so
    /// <see cref="MarkdownBackgroundRenderer"/> can shade every match, not just the one the native text
    /// selection already marks as "current" (análisis §4.2, Fase 8).</summary>
    public string? SearchHighlightText { get; set; }
    public bool SearchHighlightMatchCase { get; set; }

    public FenceInfo GetFence(int lineNumber) =>
        Fences.TryGetValue(lineNumber, out var f) ? f : default;

    public HtmlLineState GetHtml(int lineNumber) =>
        HtmlLines.TryGetValue(lineNumber, out var state) ? state : HtmlLineState.Empty;

    public MdAlign GetAlignment(int lineNumber) => GetHtml(lineNumber).Align;

    /// <summary>Si el cursor o la selección tocan la tabla <paramref name="table"/>, en cuyo caso se
    /// muestra entera en su forma de texto. Es a nivel de bloque y no de línea a propósito: una tabla con
    /// una fila en crudo y las demás dibujadas tendría esa fila fuera de la rejilla, y editar una celda
    /// exige ver las barras que la separan de la siguiente.</summary>
    internal bool IsTableActive(MdTable table, TextDocument document)
    {
        if (table.StartLine > document.LineCount) return false;
        var start = document.GetLineByNumber(table.StartLine).Offset;
        var end = document.GetLineByNumber(Math.Min(table.EndLine, document.LineCount)).EndOffset;
        return Intersects(start, end);
    }

    /// <summary>Whether the document uses any HTML at all — lets the view skip the extra redraw that
    /// keeps centred/right-aligned lines correct across a resize when nothing is aligned.</summary>
    public bool HasAlignments => HtmlLines.Count > 0;

    /// <summary>En imágenes, el extremo final queda fuera del contenido editable: permite mantener
    /// la vista previa al insertar al final de la nota sin necesitar otra línea.</summary>
    public bool IsSpanActive(MdSpan span) => span.Kind == MdSpanKind.Image
        ? (CaretOffset >= span.Start && CaretOffset < span.End)
          || Selections.Any(selection => selection.Start < span.End && selection.End > span.Start)
        : Intersects(span.Start, span.End);

    /// <summary>Whether the caret sits inside [start,end] or any selection overlaps [start,end).</summary>
    public bool Intersects(int start, int end)
    {
        if (CaretOffset >= start && CaretOffset <= end) return true;
        foreach (var (s, e) in Selections)
        {
            if (s < end && e > start) return true;
        }
        return false;
    }

}
