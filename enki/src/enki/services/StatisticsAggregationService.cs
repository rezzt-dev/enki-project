using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>One Monday-Sunday week's worth of aggregated activity — the row <see cref="StatisticsViewModel"/>
/// turns into a bar-chart column. <see cref="HabitCompletionPercent"/> is <c>null</c> when the vault has no
/// non-archived habits, so the view can hide that row entirely instead of showing a misleading 0%.</summary>
public sealed record WeeklyStatsPoint(
    DateOnly WeekStart,
    int NotesCreated,
    int NotesEdited,
    int TasksCompleted,
    double? HabitCompletionPercent);

/// <summary>Pending / in-progress / done counts from the kanban board, used for the flow meter.</summary>
public sealed record KanbanFlowSnapshot(
    int Pending,
    int InProgress,
    int Done);

/// <summary>Propuesta 15 (docs/propuesta-nuevas-funcionalidades.md §15) — builds weekly activity trends from
/// data that already lives in <see cref="VaultService"/>/<see cref="KanbanService"/>/
/// <see cref="PolarTaskService"/>/<see cref="HabitService"/>, exactly the same "purely a function of
/// already-loaded state, persists nothing, owns nothing" shape as <see cref="CalendarAggregationService"/>.</summary>
public sealed partial class StatisticsAggregationService(
    VaultService vaultService,
    KanbanService kanbanService,
    PolarTaskService polarTaskService,
    PolarAuthService polarAuthService,
    HabitService habitService,
    ReminderService? reminderService = null,
    SessionService? sessionService = null)
{
    /// <summary>Every Monday-Sunday week's aggregated activity whose week overlaps
    /// [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>), oldest first — mirrors
    /// <see cref="CalendarAggregationService.BuildItems"/>'s half-open UTC range convention. Bucketing itself
    /// happens on local dates (not UTC) so a week reads the same "Monday to Sunday" the rest of the app's
    /// calendar already uses (<c>AppSettings.CalendarWeekStartsOnMonday</c>'s default).</summary>
    public List<WeeklyStatsPoint> BuildWeeklySeries(DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var rangeStartLocal = DateOnly.FromDateTime(rangeStartUtc.ToLocalTime());
        var rangeEndLocal = DateOnly.FromDateTime(rangeEndUtc.ToLocalTime());
        var today = DateOnly.FromDateTime(DateTime.Today);

        var weekStarts = BuildWeekStarts(rangeStartLocal, rangeEndLocal);

        var notes = vaultService.VaultPath is { Length: > 0 } vaultPath
            ? vaultService.GetAllNotesRecursive(vaultPath)
            : [];
        var noteCreatedDates = notes.Select(n => DateOnly.FromDateTime(SafeCreationTimeUtc(n).ToLocalTime())).ToList();
        var noteEditedDates = notes.Select(n => DateOnly.FromDateTime(n.ModifiedUtc.ToLocalTime())).ToList();

        var kanbanCompletedDates = kanbanService.AllColumns.SelectMany(c => c.Cards)
            .Where(c => c.CompletedAtUtc is not null)
            .Select(c => DateOnly.FromDateTime(c.CompletedAtUtc!.Value.ToLocalTime()))
            .ToList();

        // Cero fricción para quien no usa Polar — mismo gate que CalendarAggregationService.BuildItems.
        var polarCompletedDates = polarAuthService.IsLinked
            ? polarTaskService.Store.Tasks.Where(t => t.Completed && t.CompletedAtUtc is not null)
                .Select(t => DateOnly.FromDateTime(t.CompletedAtUtc!.Value.ToLocalTime()))
                .ToList()
            : [];

        var activeHabits = habitService.Store.Habits.Where(h => !h.Archived).ToList();

        var points = new List<WeeklyStatsPoint>();
        foreach (var weekStart in weekStarts)
        {
            var weekEndExclusive = weekStart.AddDays(7);

            var notesCreated = noteCreatedDates.Count(d => d >= weekStart && d < weekEndExclusive);
            var notesEdited = noteEditedDates.Count(d => d >= weekStart && d < weekEndExclusive);
            var tasksCompleted = kanbanCompletedDates.Count(d => d >= weekStart && d < weekEndExclusive)
                + polarCompletedDates.Count(d => d >= weekStart && d < weekEndExclusive);

            points.Add(new WeeklyStatsPoint(
                weekStart, notesCreated, notesEdited, tasksCompleted,
                BuildHabitCompletionPercent(activeHabits, weekStart, weekEndExclusive, today)));
        }

        return points;
    }

    /// <summary>Agrega el histórico de todos los tableros por rol; las columnas personalizadas cuentan
    /// como trabajo en curso. No depende del orden ni de la selección de la interfaz.</summary>
    public KanbanFlowSnapshot BuildKanbanFlow()
    {
        var columns = kanbanService.AllColumns.ToList();
        return new KanbanFlowSnapshot(
            columns.Where(c => c.Role == CoreColumnRole.Backlog).Sum(c => c.Cards.Count),
            columns.Where(c => c.Role is CoreColumnRole.InProgress or CoreColumnRole.None).Sum(c => c.Cards.Count),
            columns.Where(c => c.Role == CoreColumnRole.Done).Sum(c => c.Cards.Count));
    }

    /// <summary>% of (hábito, día aplicable) pairs marked done within the week, capped at
    /// <paramref name="today"/> so a week still in progress isn't penalized for days that haven't happened
    /// yet. <c>null</c> when there are no active habits or no applicable day has occurred yet.</summary>
    private static double? BuildHabitCompletionPercent(List<Habit> activeHabits, DateOnly weekStart, DateOnly weekEndExclusive, DateOnly today)
    {
        if (activeHabits.Count == 0) return null;

        var lastDayToCount = weekEndExclusive.AddDays(-1);
        if (lastDayToCount > today) lastDayToCount = today;
        if (lastDayToCount < weekStart) return null;

        var applicableCount = 0;
        var doneCount = 0;
        for (var date = weekStart; date <= lastDayToCount; date = date.AddDays(1))
        {
            foreach (var habit in activeHabits)
            {
                if (!HabitService.IsApplicable(habit, date)) continue;
                applicableCount++;
                if (HabitService.IsCompleted(habit, date)) doneCount++;
            }
        }

        return applicableCount == 0 ? null : doneCount * 100.0 / applicableCount;
    }

    private static List<DateOnly> BuildWeekStarts(DateOnly rangeStartLocal, DateOnly rangeEndLocalExclusive)
    {
        var weekStarts = new List<DateOnly>();
        var cursor = MondayOnOrBefore(rangeStartLocal);
        while (cursor < rangeEndLocalExclusive)
        {
            weekStarts.Add(cursor);
            cursor = cursor.AddDays(7);
        }
        return weekStarts;
    }

    private static DateOnly MondayOnOrBefore(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // Sunday=0..Saturday=6 -> Monday=0
        return date.AddDays(-offset);
    }

    /// <summary>The vault doesn't persist a note's creation time itself (only <c>ModifiedUtc</c>, read from
    /// <c>File.LastWriteTimeUtc</c> — see <see cref="VaultService.GetNotes"/>), so this reads the
    /// filesystem's own creation timestamp directly, same source <c>EditorStatsService</c> already uses for
    /// a single open note's stats panel. Falls back to <see cref="NoteFile.ModifiedUtc"/> if the file
    /// vanished between the vault scan and this read (e.g. deleted mid-aggregation).</summary>
    private static DateTime SafeCreationTimeUtc(NoteFile note)
    {
        try
        {
            return System.IO.File.Exists(note.FullPath) ? new System.IO.FileInfo(note.FullPath).CreationTimeUtc : note.ModifiedUtc;
        }
        catch (System.IO.IOException)
        {
            return note.ModifiedUtc;
        }
        catch (UnauthorizedAccessException)
        {
            return note.ModifiedUtc;
        }
    }
}
