using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Models;

/// <summary>Un punto de cualquier gráfico de Estadísticas: una etiqueta, un valor y (opcionalmente) una
/// línea extra de detalle para el cartel flotante que dibujan los controles al pasar el puntero.
///
/// Es deliberadamente un POCO en <c>models/</c> y no un tipo de la capa de vista: lo produce
/// <see cref="Services.StatisticsAggregationService"/> tal cual y lo consumen los controles de
/// <c>controls/charts/</c> sin ninguna capa de mapeo intermedia — mismo criterio que el resto del
/// proyecto (ver AGENTS.md, "Unificar sin duplicar").</summary>
public sealed record ChartDatum(string Label, double Value, string Detail = "")
{
    /// <summary>Etiqueta corta para el eje cuando la larga no cabe (p. ej. "L" para "lunes").</summary>
    public string ShortLabel { get; init; } = string.Empty;

    public string AxisLabel => string.IsNullOrEmpty(ShortLabel) ? Label : ShortLabel;

    /// <summary>Opacidad sobre la tinta con la que se pinta este dato en los gráficos por categorías
    /// (anillo, barra apilada). Vale 0 mientras nadie la fije, y entonces el gráfico la deriva de la
    /// posición con <see cref="ShadeFor"/>; se rellena cuando la leyenda vive fuera del control (en XAML)
    /// y necesita pintar su cuadrito exactamente del mismo tono que el segmento al que se refiere.</summary>
    public double Shade { get; init; }

    /// <summary>Escalón de la rampa de tinta para la categoría <paramref name="index"/> de
    /// <paramref name="count"/>: del 0,85 del primer segmento al 0,23 del último, con un salto siempre
    /// perceptible aunque haya seis categorías. Única definición de la rampa — la usan el anillo, la barra
    /// apilada y las leyendas del XAML.</summary>
    public static double ShadeFor(int index, int count) =>
        count <= 1 ? 0.75 : 0.85 - 0.62 * index / (count - 1);

    /// <summary>Devuelve la misma lista con <see cref="Shade"/> ya resuelto por posición.</summary>
    public static IReadOnlyList<ChartDatum> WithShades(IReadOnlyList<ChartDatum> items) =>
        [.. items.Select((item, index) => item with { Shade = ShadeFor(index, items.Count) })];
}

/// <summary>Una celda del mapa de calor de calendario: un día, cuántas cosas pasaron ese día y su
/// intensidad ya normalizada a 0..1 (el control no reescala nada, solo pinta).</summary>
public sealed record HeatCell(DateOnly Date, int Count, double Intensity, string Label);

/// <summary>Una serie con nombre para los gráficos que pintan dos a la vez (notas vs tareas).</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<ChartDatum> Points);
