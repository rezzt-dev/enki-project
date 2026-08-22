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
}
