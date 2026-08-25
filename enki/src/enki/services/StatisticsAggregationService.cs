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

/// <summary>A single habit-day cell in the 12-week × 7-day heatmap — <c>State</c> is 0 (no habit / not
/// applicable that day), 1 (applicable, not completed), or 2 (completed).</summary>
public sealed record HabitDayCell(DateOnly Date, int State);

/// <summary>Vault-level snapshot computed once per reload — cheaper than the weekly series since it
/// reuses data already read for the weekly buckets rather than doing its own disk scan.</summary>
public sealed record VaultSnapshot(
    int TotalNotes,
    int TotalSessions,
    long VaultSizeBytes);

/// <summary>Pending / in-progress / done counts from the kanban board, used for the flow meter.</summary>
public sealed record KanbanFlowSnapshot(
    int Pending,
    int InProgress,
    int Done);

/// <summary>Propuesta 15 (docs/propuesta-nuevas-funcionalidades.md §15) — builds weekly activity trends from
/// data that already lives in <see cref="VaultService"/>/<see cref="KanbanService"/>/
/// <see cref="PolarTaskService"/>/<see cref="HabitService"/>, exactly the same "purely a function of
/// already-loaded state, persists nothing, owns nothing" shape as <see cref="CalendarAggregationService"/>.</summary>
public sealed class StatisticsAggregationService(
    VaultService vaultService,
    KanbanService kanbanService,
    PolarTaskService polarTaskService,
    PolarAuthService polarAuthService,
    HabitService habitService)
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

        var kanbanCompletedDates = kanbanService.Board.Columns.SelectMany(c => c.Cards)
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

    /// <summary>Returns a snapshot of the whole vault — total notes, total notes across all sessions, and
    /// approximate vault size on disk.</summary>
    public VaultSnapshot BuildVaultSnapshot()
    {
        var notes = vaultService.VaultPath is { Length: > 0 } vp
            ? vaultService.GetAllNotesRecursive(vp)
            : [];
        long size = 0;
        foreach (var n in notes)
        {
            try
            {
                var fi = new System.IO.FileInfo(n.FullPath);
                if (fi.Exists) size += fi.Length;
            }
            catch { }
        }
        var sessionCount = System.IO.Directory.Exists(System.IO.Path.Combine(vaultService.VaultPath, ".sessions"))
            ? System.IO.Directory.GetDirectories(System.IO.Path.Combine(vaultService.VaultPath, ".sessions")).Length
            : 0;
        return new VaultSnapshot(notes.Count, sessionCount, size);
    }

    /// <summary>Returns pending / in-progress / done counts from the kanban board columns — the
    /// first column is always "pending," the last is always "done," and everything in between is
    /// "in progress" (same convention the rest of the app already uses).</summary>
    public KanbanFlowSnapshot BuildKanbanFlow()
    {
        var columns = kanbanService.Board.Columns;
        if (columns.Count == 0) return new KanbanFlowSnapshot(0, 0, 0);
        var pending = columns[0].Cards.Count;
        var done = columns[^1].Cards.Count;
        var inProgress = columns.Skip(1).Take(columns.Count - 2).Sum(c => c.Cards.Count);
        return new KanbanFlowSnapshot(pending, inProgress, done);
    }

    /// <summary>Builds a 12-week × 7-day heatmap matrix of habit completion, newest week first.
    /// Each cell is 0 (no habit / not applicable), 1 (applicable → not done), or 2 (done).</summary>
    public List<List<HabitDayCell>> BuildHabitHeatmap()
    {
        var activeHabits = habitService.Store.Habits.Where(h => !h.Archived).ToList();
        var result = new List<List<HabitDayCell>>();
        if (activeHabits.Count == 0) return result;

        var today = DateOnly.FromDateTime(DateTime.Today);
        // Start from the Monday of 12 weeks ago
        var cursor = MondayOnOrBefore(today.AddDays(-7 * 11));
        for (var w = 0; w < 12; w++, cursor = cursor.AddDays(7))
        {
            var week = new List<HabitDayCell>();
            for (var d = 0; d < 7; d++)
            {
                var date = cursor.AddDays(d);
                if (date > today) { week.Add(new HabitDayCell(date, 0)); continue; }

                var applicable = 0;
                var done = 0;
                foreach (var habit in activeHabits)
                {
                    if (!HabitService.IsApplicable(habit, date)) continue;
                    applicable++;
                    if (HabitService.IsCompleted(habit, date)) done++;
                }
                if (applicable == 0) week.Add(new HabitDayCell(date, 0));
                else if (done == applicable) week.Add(new HabitDayCell(date, 2));
                else week.Add(new HabitDayCell(date, 1));
            }
            result.Add(week);
        }
        return result;
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
