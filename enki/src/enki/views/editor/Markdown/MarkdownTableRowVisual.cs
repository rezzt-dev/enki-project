using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Dibuja una fila entera de tabla cuando alguna de sus celdas envuelve a varias líneas.
///
/// <para>Existe porque el mecanismo habitual —ocultar los separadores y reservar blanco para que el texto
/// de cada celda caiga en su columna (<see cref="MarkdownElementGenerator"/>)— solo sabe colocar una
/// línea: un espaciador únicamente desplaza la primera línea visual de la suya, y el formateador de
/// Avalonia parte la fila donde le convenga a la línea entera, no dentro de cada celda (ver
/// <see cref="WhitespaceFormattedElement"/>). Con celdas de varias líneas la única salida es dejar de
/// tratar la fila como texto colocado y dibujarla: un párrafo por celda, cada uno envuelto al ancho de su
/// columna.</para>
///
/// <para>El precio es que el texto de una fila así deja de ser texto de editor: no se puede seleccionar en
/// sitio ni lo colorea el resaltador, igual que ya pasa con una imagen. No se pierde nada editable porque
/// poner el cursor dentro de la tabla la devuelve entera a su forma de Markdown crudo, y ahí se
/// selecciona y se edita con normalidad — por eso el control no responde al ratón, para que el clic llegue
/// al texto que hay detrás y active la tabla.</para>
///
/// <para>Alto y anchos salen de <see cref="MdTableGeometry"/>, la misma de la que sale la rejilla del
/// fondo (<see cref="MarkdownBackgroundRenderer"/>): las líneas verticales caen así en el hueco entre dos
/// celdas y nunca encima de una letra.</para>
/// </summary>
internal sealed class MarkdownTableRowVisual : Control
{
    private readonly List<(double X, FormattedText Text)> _cells = new();
    private readonly double _width;
    private readonly double _height;

    public MarkdownTableRowVisual(string lineText, DocumentLine line, MdLine model, MdTableRow row,
        MdTableGeometry geometry, MarkdownStyle style, LivePreviewState state, double defaultLineHeight,
        double defaultBaseline)
    {
        // Un hueco menos que el ancho de la tabla: el objeto llena la columna de texto entera, y declarar
        // exactamente el ancho disponible haría que el formateador lo tomara por un elemento que no cabe y
        // le abriera una línea visual de más. El dibujo no se recorta por medir de menos (el control no
        // recorta a sus límites) y el contenido nunca llega tan a la derecha: la última columna acaba un
        // hueco antes del borde.
        _width = Math.Max(0, geometry.TotalWidth - geometry.CellPadding);
        var content = geometry.RowHeight(line.LineNumber);
        _height = (content > 0 ? content : defaultLineHeight) + 2 * MarkdownTableLayout.RowVerticalPadding;
        // Reserva el descenso normal del editor dentro del bloque, para que la línea visual y la
        // rejilla no añadan espacio por debajo de la fila.
        TextBlock.SetBaselineOffset(this, Math.Max(0, _height - (defaultLineHeight - defaultBaseline)));

        var header = row.Kind == MdTableRowKind.Header;
        foreach (var cell in MarkdownTableParser.SplitCells(lineText, line.Offset))
        {
            if (cell.Length <= 0) continue;
            // Una fila con celdas de más no se recorta (se perdería lo que el usuario escribió): las
            // sobrantes se apilan en la última columna, igual que hace el resto del dibujado.
            var column = Math.Min(cell.Column, geometry.ColumnWidths.Length - 1);
            _cells.Add((geometry.ColumnX[column], MarkdownTableLayout.BuildCell(
                lineText, line, model, cell, header, row.Table.Alignments[column],
                geometry.ColumnWidths[column], style, state, style.TextPrimary)));
        }

        // El caret y la selección se colocan clicando el texto que hay detrás — ver el comentario de
        // arriba y el mismo criterio en MarkdownElementGenerator.BuildImage.
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize) => new(_width, _height);

    public override void Render(DrawingContext context)
    {
        foreach (var (x, text) in _cells)
            context.DrawText(text, new Point(x, Math.Max(MarkdownTableLayout.RowVerticalPadding,
                (_height - text.Height) / 2)));
    }
}
