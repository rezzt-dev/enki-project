using System;

namespace enki.ViewModels;

/// <summary>One cell of the discrete, monochrome "consistency grid" — propuesta 13's deliberate
/// alternative to a colorful GitHub-style heatmap or any kind of streak counter (docs/
/// propuesta-nuevas-funcionalidades.md §13/§8: "un cuadrito relleno u hueco por día, sin colores
/// llamativos"). Shared by <see cref="HabitItemViewModel"/> (one grid per habit), the Dashboard's compact
/// "Habits" card, and the habit-consistency block of <see cref="StatisticsViewModel"/>, so the same visual
/// language never has to be rebuilt three times.</summary>
public sealed class ConsistencyDayViewModel
{
    public required DateOnly Date { get; init; }

    /// <summary>Whether the habit was marked done on <see cref="Date"/>.</summary>
    public required bool IsDone { get; init; }

    /// <summary>Whether <see cref="Date"/> is even one of this habit's expected days (see
    /// <c>Services.HabitService.IsApplicable</c>) — <see langword="false"/> renders as a faint, borderless
    /// "not applicable" cell instead of reading as a missed day.</summary>
    public required bool IsApplicable { get; init; }

    /// <summary>Texto del tooltip de la celda cuando quien la construye puede decir algo más útil que la
    /// fecha a secas —el calendario de constancia de Documentos dice además cuántos hábitos tocaban ese día
    /// y cuántos se marcaron—. Null deja que cada vista siga mostrando <see cref="Date"/>, que es lo que
    /// hacen la cuadrícula de Hábitos y la de Estadísticas.</summary>
    public string? Tooltip { get; init; }
}
