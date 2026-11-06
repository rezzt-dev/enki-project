using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace enki.Services.Export;

/// <summary>Maqueta una región de la ventana a su tamaño completo mientras dura un render de exportación
/// (T8), y la devuelve a como estaba al liberarse.
///
/// <para>Rasterizar la región tal cual está en pantalla deja fuera todo lo que tapan sus
/// <c>ScrollViewer</c> internos: las tarjetas que no caben en una columna del Kanban vertical, las que
/// se salen por la derecha de una franja del Kanban horizontal, las filas de la cuadrícula del mes o las
/// horas de la rejilla de Semana/Día. Aquí la región se mide con alto ilimitado —cada visor recibe
/// entonces todo el alto que pide su contenido— y se dispone a <c>max(lo que pide, lo que ocupaba)</c>,
/// para que una vista que en pantalla estira sus filas (el mes) no salga más apretada en la imagen.</para>
///
/// <para>El ancho solo se libera si, con el ancho de pantalla, algún visor sigue desbordando en
/// horizontal (las franjas del Kanban horizontal). Liberarlo siempre sería peor: el tablero vertical
/// reparte el ancho disponible entre columnas y, medido con ancho infinito, cada columna pediría el ancho
/// de su título más largo (ver el comentario de <c>BoardColumnWidth</c> en <c>KanbanView.axaml</c>).</para>
///
/// <para>Todos los visores —el que contiene la región y los de dentro— se llevan al origen antes de
/// maquetar y recuperan su desplazamiento al terminar, porque un desplazamiento vivo saldría en la
/// imagen como un recorte.</para></summary>
internal sealed class FullSizeLayout : IDisposable
{
    private readonly Control _target;
    private readonly List<(ScrollViewer Scroller, Vector Offset)> _offsets;

    private FullSizeLayout(Control target, List<(ScrollViewer, Vector)> offsets)
    {
        _target = target;
        _offsets = offsets;
    }

    /// <summary>Deja <paramref name="target"/> dispuesto a su tamaño completo. Un control fuera del árbol
    /// visual (la hoja de una nota, que <see cref="NoteDocumentExporter"/> ya mide y dispone a mano) se deja
    /// como está.</summary>
    public static IDisposable Apply(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAttachedToVisualTree()) return new FullSizeLayout(target, []);

        var scrollers = target.GetVisualAncestors().OfType<ScrollViewer>().Take(1)
            .Concat(target.GetVisualDescendants().OfType<ScrollViewer>())
            .ToList();
        var offsets = scrollers.Select(s => (s, s.Offset)).ToList();
        foreach (var scroller in scrollers) scroller.Offset = default;

        var margin = target.Margin;
        var bounds = target.Bounds;
        var slot = new Rect(bounds.X - margin.Left, bounds.Y - margin.Top,
            bounds.Width + margin.Left + margin.Right, bounds.Height + margin.Top + margin.Bottom);

        LayOut(target, slot, slot.Width);
        if (OverflowsHorizontally(target)) LayOut(target, slot, double.PositiveInfinity);

        return new FullSizeLayout(target, offsets);
    }

    private static void LayOut(Control target, Rect slot, double availableWidth)
    {
        target.Measure(new Size(availableWidth, double.PositiveInfinity));
        var desired = target.DesiredSize;
        target.Arrange(new Rect(slot.X, slot.Y, Math.Max(desired.Width, slot.Width), Math.Max(desired.Height, slot.Height)));
    }

    private static bool OverflowsHorizontally(Control target) =>
        target.GetVisualDescendants().OfType<ScrollViewer>()
            .Any(s => s.IsEffectivelyVisible && s.Extent.Width > s.Viewport.Width + 0.5);

    /// <summary>Devuelve la región a la maqueta que le da su padre: se invalidan los dos para que la
    /// pasada vuelva a medir el padre con su restricción de siempre, y no a la región con la restricción
    /// ilimitada que <see cref="LayOut"/> dejó guardada como la última.</summary>
    public void Dispose()
    {
        if (!_target.IsAttachedToVisualTree()) return;

        (_target.GetVisualParent() as Layoutable)?.InvalidateMeasure();
        _target.InvalidateMeasure();
        _target.GetLayoutManager()?.ExecuteLayoutPass();

        foreach (var (scroller, offset) in _offsets) scroller.Offset = offset;
    }
}
