using System;
using System.Globalization;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Hábitos (<see cref="HabitService"/>): consultarlos y marcar o desmarcar un día. Viven en el vault
/// (<c>.enki/habits.json</c>), así que cuelgan de él como Kanban o los recordatorios y no de una sesión. No hay
/// rachas ni puntuaciones que mantener: una marca es una fecha en la lista del hábito y nada más.</summary>
public sealed partial class AiVaultToolbox
{
    private const int DefaultHabitDays = 7;

    /// <summary>Las mismas diez semanas que enseña la cuadrícula de constancia de la app.</summary>
    private const int MaxHabitDays = 70;

    private void RegisterHabitTools()
    {
        Register("list_habits", AiToolScope.Vault, AiToolEffect.Read,
            "Lists the user's habits with their frequency (daily, weekly or specific_days), whether each one applies today and is done "
            + "today, and the days it was marked done in the last 'days' days. Habits have no streaks or scores.",
            Schema(Opt("days", Number($"How many past days of marks to include (default {DefaultHabitDays}, max {MaxHabitDays}).")),
                Opt("include_archived", Flag("Include archived habits (default false)."))),
            call =>
            {
                var days = call.GetInteger("days", 1, MaxHabitDays) ?? DefaultHabitDays;
                var archived = call.GetFlag("include_archived") ?? false;
                var today = DailyNoteService.Today;
                var from = today.AddDays(1 - days);
                return new
                {
                    Today = FormatDay(today),
                    From = FormatDay(from),
                    Habits = _habits.Store.Habits.Where(h => archived || !h.Archived).Select(h => new
                    {
                        h.Id,
                        h.Name,
                        Frequency = FrequencyName(h.Frequency),
                        Days = h.Frequency == HabitFrequency.SpecificDays ? h.SpecificDays.Select(d => d.ToString().ToLowerInvariant()) : null,
                        Archived = h.Archived ? true : (bool?)null,
                        AppliesToday = HabitService.IsApplicable(h, today),
                        DoneToday = HabitService.IsCompleted(h, today),
                        DoneDays = h.CompletedDates.Where(d => d >= from && d <= today).Order().Select(FormatDay)
                    }).ToList()
                };
            });

        Register("set_habit_done", AiToolScope.Vault, AiToolEffect.Change,
            "Marks a habit as done (or not done) on a day, today by default. Use it when the user says they did, or did not do, a habit.",
            Schema(Req("habit", Text("Habit id or exact name, from list_habits.")),
                Opt("date", Text("Day as 2026-10-24 (default today). It cannot be in the future.")),
                Opt("done", Flag("true to mark it done (default), false to unmark it."))),
            call =>
            {
                var habit = Habit(call);
                var date = Day(call, "date") ?? DailyNoteService.Today;
                if (date > DailyNoteService.Today) throw AiToolCall.Invalid("'date' cannot be in the future.");
                var done = call.GetFlag("done") ?? true;
                if (HabitService.IsCompleted(habit, date) != done)
                {
                    _habits.ToggleCompletion(habit, date);
                    call.Undoable(new AiToolUndo
                    {
                        Kind = AiUndoKind.HabitMarked,
                        Target = habit.Id,
                        Previous = FormatDay(date),
                        ResultHash = done ? "done" : "pending"
                    });
                }
                return new { habit.Id, habit.Name, Date = FormatDay(date), Done = done };
            });
    }

    private Habit Habit(AiToolCall call)
    {
        var requested = call.RequireText("habit").Trim();
        var habits = _habits.Store.Habits;
        var matches = habits.Where(h => h.Id == requested).ToList();
        if (matches.Count == 0)
            matches = habits.Where(h => !h.Archived && string.Equals(h.Name, requested, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            throw AiToolCall.Invalid($"Several habits are called \"{requested}\". Use the id: " + string.Join(", ", matches.Select(h => h.Id)) + ".");
        var habit = matches.FirstOrDefault()
            ?? throw new AiToolException(AiToolErrorKind.NotFound, $"There is no habit \"{requested}\". Use list_habits.");
        call.Target(AiToolTargetKind.Habit, habit.Name, habit.Id);
        return habit;
    }

    private static string FrequencyName(HabitFrequency frequency) => frequency switch
    {
        HabitFrequency.Weekly => "weekly",
        HabitFrequency.SpecificDays => "specific_days",
        _ => "daily"
    };

    /// <summary>Un día de calendario, sin hora: <c>2026-10-24</c>.</summary>
    private static DateOnly? Day(AiToolCall call, string name)
    {
        if (call.GetText(name)?.Trim() is not { Length: > 0 } text) return null;
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw AiToolCall.Invalid($"'{name}' must be a day as 2026-10-24.");
    }

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
