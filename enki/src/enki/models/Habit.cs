using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>How often a <see cref="Habit"/> is expected to be done. Only <see cref="SpecificDays"/> reads
/// <see cref="Habit.SpecificDays"/> — <see cref="Daily"/> applies to every day and <see cref="Weekly"/>
/// only cares that it happens at least once inside a Monday-Sunday week, not which day.</summary>
public enum HabitFrequency
{
    Daily,
    Weekly,
    SpecificDays
}

/// <summary>A user-defined habit tracked by simple done/not-done marks per day — propuesta 13 (docs/
/// propuesta-nuevas-funcionalidades.md §13). Deliberately minimal: no streak count, no points, no levels.
/// Persisted per-vault (see <see cref="Services.HabitService"/>) since a habit is the user's own content,
/// the same reasoning <see cref="VaultSettings"/> already documents for why it isn't a global,
/// <c>%AppData%</c>-scoped store like <c>AppSettings</c>.</summary>
public sealed class Habit
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public HabitFrequency Frequency { get; set; } = HabitFrequency.Daily;

    /// <summary>Only meaningful when <see cref="Frequency"/> is <see cref="HabitFrequency.SpecificDays"/>.</summary>
    public List<DayOfWeek> SpecificDays { get; set; } = [];

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Hides the habit from the active list without discarding <see cref="CompletedDates"/> —
    /// same "soft hide, never silently lose history" reasoning as <see cref="KanbanTrashedCard"/>.</summary>
    public bool Archived { get; set; }

    /// <summary>Local calendar dates (no time-of-day, no timezone) marked as "done". A date's presence in
    /// this list is the only state a completion carries — no count, no note, nothing gamified.</summary>
    public List<DateOnly> CompletedDates { get; set; } = [];
}

/// <summary>Root document persisted at <c>&lt;vault&gt;/.enki/habits.json</c> by <see cref="Services.HabitService"/>.</summary>
public sealed class HabitStore
{
    public List<Habit> Habits { get; set; } = [];
}
