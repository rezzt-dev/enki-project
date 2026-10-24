using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace enki.Controls;

/// <summary>Panel de la rejilla del Dashboard: N columnas de igual ancho y tantas filas como haga falta,
/// donde cada hijo declara su celda y su tamaño con las adjuntas <c>Grid.Row</c>/<c>Grid.Column</c>/
/// <c>Grid.RowSpan</c>/<c>Grid.ColumnSpan</c> de siempre.
///
/// Sustituye al <c>Grid ColumnDefinitions="*,*,*,*,*,*" RowDefinitions="Auto,…"</c> escrito a mano que
/// tenían <c>DashboardView.axaml</c> y la vista previa de Ajustes, por tres motivos que el Grid no podía
/// dar una vez la organización pasó a ser configurable de verdad:
///
/// - El número de columnas y de filas ahora lo elige el usuario. Un <c>Grid</c> necesita sus
///   <c>ColumnDefinitions</c> en XAML (o reconstruirlas a mano desde code-behind en cada cambio); aquí es
///   una propiedad más, <see cref="Columns"/>, que se enlaza al ajuste del vault.
/// - Las filas se miden por su contenido igual que <c>Auto</c>, pero un hijo con <c>RowSpan &gt; 1</c>
///   solo engorda la última fila que ocupa, así que una ficha alta nunca estira las filas de sus vecinas.
/// - Con <see cref="RowHeight"/> las filas pasan a medir todas exactamente lo mismo, que es lo que
///   necesita el editor de Ajustes: superpone dos de estos paneles (celdas vacías debajo, fichas encima)
///   y solo cuadran al píxel si la altura de fila no depende del contenido de cada capa. El Dashboard
///   real lo deja a 0 y cada fila se ajusta a lo suyo.
///
/// Reutiliza a propósito las adjuntas de <c>Grid</c> en vez de declarar unas propias: los
/// <c>ItemContainerTheme</c> que ya posicionaban los <c>ContentPresenter</c> siguen valiendo tal cual, y
/// cualquier ficha suelta escrita en XAML se coloca con la sintaxis que cualquiera espera.</summary>
public sealed class DashboardGridPanel : Panel
{
    /// <summary>Número de columnas de igual ancho. Se recorta a 1 como mínimo.</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<DashboardGridPanel, int>(nameof(Columns), 6);

    /// <summary>Número mínimo de filas a reservar. El panel siempre pinta al menos tantas como necesiten
    /// sus hijos; esto solo fuerza filas vacías de más — el editor de Ajustes las necesita para poder
    /// soltar una ficha en una fila todavía vacía, el Dashboard real lo deja a 0.</summary>
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<DashboardGridPanel, int>(nameof(Rows));

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<DashboardGridPanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<DashboardGridPanel, double>(nameof(RowSpacing));

    /// <summary>Alto mínimo de cada fila. En el editor mantiene visibles las celdas vacías (sin esto, una
    /// fila sin fichas mediría 0 y no habría dónde soltar).</summary>
    public static readonly StyledProperty<double> MinRowHeightProperty =
        AvaloniaProperty.Register<DashboardGridPanel, double>(nameof(MinRowHeight));

    /// <summary>Alto fijo de fila, en píxeles. Con 0 (lo normal) cada fila se mide por su contenido.</summary>
    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DashboardGridPanel, double>(nameof(RowHeight));

    static DashboardGridPanel()
    {
        AffectsMeasure<DashboardGridPanel>(
            ColumnsProperty, RowsProperty, ColumnSpacingProperty, RowSpacingProperty,
            MinRowHeightProperty, RowHeightProperty);

        // Mover o redimensionar una ficha cambia una adjunta del hijo, no una propiedad del panel: sin
        // esto el panel no se enteraría y la ficha se quedaría donde estaba hasta el siguiente relayout
        // por otro motivo. Grid registra lo mismo para sí, pero depender de que su constructor estático
        // haya corrido ya sería depender de un detalle ajeno.
        AffectsParentMeasure<DashboardGridPanel>(Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double MinRowHeight
    {
        get => GetValue(MinRowHeightProperty);
        set => SetValue(MinRowHeightProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    private double[] _rowHeights = [];
    private double _columnWidth;
    private double _layoutWidth = double.NaN;

    /// <summary>Filas que el panel está pintando ahora mismo — <see cref="Rows"/> o las que exijan los
    /// hijos, lo que sea mayor. El editor la usa para saber hasta dónde llega la rejilla al convertir la
    /// posición del puntero en celda.</summary>
    public int EffectiveRowCount => _rowHeights.Length;

    protected override Size MeasureOverride(Size availableSize)
    {
        ComputeLayout(availableSize.Width, measureChildren: true);

        var height = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++)
            height += _rowHeights[i];
        if (_rowHeights.Length > 1)
            height += (_rowHeights.Length - 1) * RowSpacing;

        var cols = Math.Max(1, Columns);
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : _columnWidth * cols + (cols - 1) * ColumnSpacing;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // El alto de cada fila se midió contra el ancho disponible en MeasureOverride; si el arrange
        // llega con otro (el panel se estiró dentro de su contenedor), esas alturas ya no valen y hay
        // que rehacer el reparto — si no, una tarjeta con texto que ajusta a varias líneas se recorta.
        if (!double.IsFinite(_layoutWidth) || Math.Abs(_layoutWidth - finalSize.Width) > 0.5)
            ComputeLayout(finalSize.Width, measureChildren: true);

        var cols = Math.Max(1, Columns);

        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            var (row, column, rowSpan, columnSpan) = GetPlacement(child, cols);
            child.Arrange(GetCellBounds(row, column, rowSpan, columnSpan));
        }

        var height = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++)
            height += _rowHeights[i];
        if (_rowHeights.Length > 1)
            height += (_rowHeights.Length - 1) * RowSpacing;

        return new Size(finalSize.Width, Math.Max(finalSize.Height, height));
    }

    /// <summary>Rectángulo exacto que ocupa una colocación dentro del panel, en sus propias coordenadas.
    /// Público porque el editor de Ajustes dibuja con él la silueta de destino mientras se arrastra una
    /// ficha, y así esa silueta no puede desalinearse de dónde acabará la ficha de verdad.</summary>
    public Rect GetCellBounds(int row, int column, int rowSpan, int columnSpan)
    {
        var cols = Math.Max(1, Columns);
        var colSpacing = ColumnSpacing;
        var rowSpacing = RowSpacing;

        var c = Math.Clamp(column, 0, cols - 1);
        var cs = Math.Clamp(columnSpan, 1, cols - c);
        var r = Math.Clamp(row, 0, Math.Max(0, _rowHeights.Length - 1));
        var rs = Math.Clamp(rowSpan, 1, Math.Max(1, _rowHeights.Length - r));

        var x = c * (_columnWidth + colSpacing);
        var width = cs * _columnWidth + (cs - 1) * colSpacing;

        var y = 0.0;
        for (var i = 0; i < r && i < _rowHeights.Length; i++)
            y += _rowHeights[i] + rowSpacing;

        var height = 0.0;
        for (var i = r; i < r + rs && i < _rowHeights.Length; i++)
            height += _rowHeights[i];
        height += Math.Max(0, rs - 1) * rowSpacing;

        return new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>Celda bajo un punto del panel. Devuelve false fuera de la rejilla; el editor lo trata como
    /// "soltar aquí no vale" en vez de recortar al borde, para que arrastrar una ficha fuera del lienzo no
    /// la teletransporte a una esquina.</summary>
    public bool TryGetCell(Point point, out int row, out int column)
    {
        row = 0;
        column = 0;

        var cols = Math.Max(1, Columns);
        if (_rowHeights.Length == 0 || _columnWidth <= 0) return false;
        if (point.X < 0 || point.Y < 0) return false;

        var pitchX = _columnWidth + ColumnSpacing;
        var col = pitchX > 0 ? (int)(point.X / pitchX) : 0;
        if (col < 0 || col >= cols) return false;
        column = col;

        var y = 0.0;
        for (var i = 0; i < _rowHeights.Length; i++)
        {
            var bottom = y + _rowHeights[i] + (i < _rowHeights.Length - 1 ? RowSpacing : 0);
            if (point.Y < bottom)
            {
                row = i;
                return true;
            }
            y = bottom;
        }

        return false;
    }

    /// <summary>Reparte ancho de columna y alto de fila para un ancho dado. Se llama en la medición y, si
    /// el arrange llega con otro ancho, otra vez allí.</summary>
    private void ComputeLayout(double availableWidth, bool measureChildren)
    {
        var cols = Math.Max(1, Columns);
        var colSpacing = ColumnSpacing;
        var rowSpacing = RowSpacing;

        _layoutWidth = availableWidth;
        _columnWidth = double.IsFinite(availableWidth)
            ? Math.Max(0, (availableWidth - (cols - 1) * colSpacing) / cols)
            : 0;

        // Cuántas filas: las que pida el ajuste del usuario y, por encima, las que exija cualquier ficha
        // que se salga (el normalizador del motor puede haber tenido que desbordar hacia abajo).
        var rowCount = Math.Max(0, Rows);
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var (row, _, rowSpan, _) = GetPlacement(child, cols);
            rowCount = Math.Max(rowCount, row + rowSpan);
        }
        rowCount = Math.Max(rowCount, 1);

        // Sin ancho acotado (el panel dentro de algo que mide a lo infinito) el ancho de columna sale de
        // lo que pidan los propios hijos, para no colapsar la rejilla a 0.
        if (!double.IsFinite(availableWidth) && measureChildren)
        {
            foreach (var child in Children)
            {
                if (!child.IsVisible) continue;
                child.Measure(Size.Infinity);
                var (_, _, _, columnSpan) = GetPlacement(child, cols);
                var perColumn = (child.DesiredSize.Width - (columnSpan - 1) * colSpacing) / columnSpan;
                _columnWidth = Math.Max(_columnWidth, perColumn);
            }
        }

        var heights = new double[rowCount];
        var spanning = new List<(Control Child, int Row, int RowSpan)>();

        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            var (row, _, rowSpan, columnSpan) = GetPlacement(child, cols);
            var width = columnSpan * _columnWidth + (columnSpan - 1) * colSpacing;

            if (measureChildren)
                child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));

            if (rowSpan <= 1)
                heights[row] = Math.Max(heights[row], child.DesiredSize.Height);
            else
                spanning.Add((child, row, rowSpan));
        }

        // Una ficha que ocupa varias filas solo empuja la última que ocupa: así una tarjeta alta no
        // infla las filas de arriba, donde puede haber vecinas de una sola fila que se verían con un
        // hueco muerto debajo.
        spanning.Sort((a, b) => a.RowSpan.CompareTo(b.RowSpan));
        foreach (var (child, row, rowSpan) in spanning)
        {
            var available = 0.0;
            for (var i = row; i < row + rowSpan && i < heights.Length; i++)
                available += heights[i];
            available += (rowSpan - 1) * rowSpacing;

            var deficit = child.DesiredSize.Height - available;
            if (deficit > 0)
            {
                var last = Math.Min(row + rowSpan - 1, heights.Length - 1);
                heights[last] += deficit;
            }
        }

        var minRowHeight = MinRowHeight;
        var fixedRowHeight = RowHeight;
        if (fixedRowHeight > 0)
        {
            for (var i = 0; i < heights.Length; i++)
                heights[i] = fixedRowHeight;
        }
        else if (minRowHeight > 0)
        {
            for (var i = 0; i < heights.Length; i++)
                heights[i] = Math.Max(heights[i], minRowHeight);
        }

        _rowHeights = heights;
    }

    /// <summary>Lee la colocación declarada por un hijo y la recorta a la rejilla. Un valor imposible
    /// (columna fuera de rango tras reducir columnas, span de 0) se recorta en vez de tirar la excepción
    /// que lanzaría <c>Grid</c>: la geometría puede venir de un settings.json de otra versión o editado a
    /// mano, y una ficha mal colocada nunca debe impedir que el Dashboard se pinte.</summary>
    private static (int Row, int Column, int RowSpan, int ColumnSpan) GetPlacement(Control child, int columns)
    {
        var column = Math.Clamp(Grid.GetColumn(child), 0, columns - 1);
        var columnSpan = Math.Clamp(Grid.GetColumnSpan(child), 1, columns - column);
        var row = Math.Max(0, Grid.GetRow(child));
        var rowSpan = Math.Max(1, Grid.GetRowSpan(child));
        return (row, column, rowSpan, columnSpan);
    }
}
