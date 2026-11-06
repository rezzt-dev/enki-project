using System;
using System.Globalization;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Un día del calendario de constancia de Documentos: una celda que dice si ese día tiene nota
/// diaria y que, al pulsarla, la abre (creándola si no existe).
///
/// <para>Mismo lenguaje visual deliberadamente monocromo que <see cref="ConsistencyDayViewModel"/> —celda
/// rellena o hueca, sin intensidades ni colores— y por el mismo motivo (propuesta 8: "sin colores
/// llamativos", nada de rachas). La diferencia con aquel es que este se pulsa y aquel no: la constancia de
/// un hábito se marca en Hábitos, mientras que la de las notas diarias <b>es</b> la propia nota, así que la
/// celda es el camino más corto para ir a escribirla.</para></summary>
public sealed class DailyConsistencyDayViewModel
{
    public required DateOnly Date { get; init; }

    /// <summary>Si ese día tiene nota diaria en el vault.</summary>
    public required bool HasNote { get; init; }

    public required bool IsToday { get; init; }

    /// <summary>Días que la rejilla dibuja solo para completar la última semana (mañana, pasado…). Se
    /// pintan apagados y no se pueden pulsar: la nota diaria de un día que aún no ha llegado se crea desde
    /// la navegación del editor, no desde un calendario que mira hacia atrás.</summary>
    public required bool IsFuture { get; init; }

    public bool IsInteractive => !IsFuture;

    /// <summary>Fecha larga en el idioma de la interfaz, sin el año cuando es el año en curso — el
    /// calendario cubre diez semanas, así que el año solo aporta ruido salvo justo al cambiar de año.</summary>
    public string DateLabel => Date.Year == DateTime.Today.Year
        ? Date.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        : Date.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);

    public string Tooltip
    {
        get
        {
            var key = IsFuture
                ? "Notes_Consistency_DayTooltip_Future"
                : HasNote
                    ? "Notes_Consistency_DayTooltip_WithNote"
                    : "Notes_Consistency_DayTooltip_WithoutNote";
            return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance[key], DateLabel);
        }
    }
}
