using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Un día del rango con todo lo que pasó en él — la unidad mínima de la que se derivan la curva
/// de actividad, el mapa de calor, las rachas y el reparto por día de la semana. Se calcula una sola vez
/// por recarga y se reutiliza en todos esos gráficos en vez de recorrer las notas una vez por gráfico.</summary>
public sealed record DailyStatsPoint(
    DateOnly Date,
    int NotesCreated,
    int NotesEdited,
    int TasksCompleted,
    int HabitsCompleted,
    int HabitsApplicable)
{
    public int Total => NotesCreated + NotesEdited + TasksCompleted + HabitsCompleted;
}

/// <summary>Salud del tablero de tareas: no solo cuántas hay en cada columna (eso ya lo da
/// <see cref="KanbanFlowSnapshot"/>), sino cuántas están fuera de plazo, cuántas vencen pronto y cuánto
/// se ha avanzado en las subtareas — las tres cosas que uno mira antes de decidir qué hacer hoy.</summary>
public sealed record TaskHealth(
    int Total,
    int Overdue,
    int DueToday,
    int DueNext7Days,
    int NoDueDate,
    int Subtasks,
    int SubtasksDone)
{
    public double SubtaskCompletionPercent => Subtasks == 0 ? 0 : SubtasksDone * 100.0 / Subtasks;
}

/// <summary>Estado de la bandeja de recordatorios, con el mismo criterio de "vencido" que usa el resto de
/// la app (<see cref="KanbanCard.IsPastDue"/>).</summary>
public sealed record ReminderHealth(int Active, int Upcoming, int Overdue, int Completed, int Recurring);

/// <summary>Composición del vault: el "qué tengo" frente al "qué he hecho" del resto del informe.</summary>
public sealed record VaultComposition(
    int Notes,
    int Folders,
    int Files,
    int Sessions,
    int DistinctTags,
    int TotalWords,
    int LongestNoteWords,
    string LongestNoteTitle,
    long NotesBytes,
    long FilesBytes)
{
    public double AverageWords => Notes == 0 ? 0 : (double)TotalWords / Notes;
    public long TotalBytes => NotesBytes + FilesBytes;
}

/// <summary>Racha actual y mejor racha histórica, en días.</summary>
public sealed record StreakInfo(int Current, int Best);

/// <summary>Todo lo que la página de Estadísticas necesita, calculado en una sola pasada sobre el vault,
/// el tablero, las tareas Polar, los hábitos, los recordatorios y las sesiones.
///
/// Se devuelve como un único informe (y no como quince métodos públicos independientes) por una razón muy
/// concreta: cada gráfico necesita cruzar los mismos datos con distinta agrupación, y un método por
/// gráfico significaría releer y reagrupar el vault entero una vez por tarjeta de la página. Las
/// etiquetas que son *dato* (nombres de etiqueta, carpetas, títulos, extensiones, fechas) vienen ya
/// puestas; las que son *interfaz* (prioridades, estados, tipos) llegan como cifras y las rotula
/// <see cref="ViewModels.StatisticsViewModel"/> con los recursos localizados — el servicio no traduce.</summary>
public sealed record StatisticsReport(
    IReadOnlyList<DailyStatsPoint> Daily,
    IReadOnlyList<WeeklyStatsPoint> Weekly,
    IReadOnlyList<HeatCell> ActivityHeatmap,
    IReadOnlyList<HeatCell> HabitHeatmap,
    IReadOnlyList<double> PunchCard,
    IReadOnlyList<ChartDatum> WeekdayActivity,
    IReadOnlyList<ChartDatum> Growth,
    IReadOnlyList<ChartDatum> TopTags,
    IReadOnlyList<ChartDatum> TopFolders,
    IReadOnlyList<ChartDatum> LongestNotes,
    IReadOnlyList<ChartDatum> NoteLengthBuckets,
    IReadOnlyList<ChartDatum> FileTypes,
    IReadOnlyList<ChartDatum> SessionNotes,
    IReadOnlyList<ChartDatum> HabitConsistency,
    IReadOnlyList<ChartDatum> ColumnDistribution,
    IReadOnlyList<int> PriorityCounts,
    KanbanFlowSnapshot Flow,
    TaskHealth Tasks,
    ReminderHealth Reminders,
    VaultComposition Vault,
    StreakInfo ActivityStreak,
    StreakInfo HabitStreak,
    double? HabitCompletionPercent,
    int NotesCreatedInRange,
    int NotesEditedInRange,
    int TasksCompletedInRange,
    int WordsWrittenInRange,
    int ActiveDaysInRange,
    int RangeDays);

public sealed partial class StatisticsAggregationService
{
    /// <summary>Ventana fija del mapa de calor anual — 53 columnas para que siempre entre un año completo
    /// aunque el primer día caiga a mitad de semana.</summary>
    private const int HeatmapWeeks = 53;

    private const int TopListSize = 8;

    /// <summary>Construye el informe completo para el rango [<paramref name="rangeStartUtc"/>,
    /// <paramref name="rangeEndUtc"/>). El mapa de calor anual y la composición del vault son
    /// deliberadamente independientes del rango: uno es un histórico fijo y la otra es una foto del
    /// estado actual, y ninguno de los dos significaría nada recortado a "esta semana".</summary>
    public StatisticsReport BuildReport(DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rangeStart = DateOnly.FromDateTime(rangeStartUtc.ToLocalTime());
        var rangeEnd = DateOnly.FromDateTime(rangeEndUtc.ToLocalTime()).AddDays(-1);
        if (rangeEnd > today) rangeEnd = today;
        if (rangeStart > rangeEnd) rangeStart = rangeEnd;

        var notes = vaultService.VaultPath is { Length: > 0 } vaultPath && Directory.Exists(vaultPath)
            ? vaultService.GetAllNotesRecursive(vaultPath)
            : [];

        var noteTimes = notes
            .Select(n => (Note: n, Created: SafeCreationTimeUtc(n).ToLocalTime(), Modified: n.ModifiedUtc.ToLocalTime()))
            .ToList();

        var cards = kanbanService.Board.Columns.SelectMany(c => c.Cards).ToList();
        var polarTasks = polarAuthService.IsLinked
            ? polarTaskService.Store.Tasks.Where(t => !t.IsDeleted).ToList()
            : [];
        var activeHabits = habitService.Store.Habits.Where(h => !h.Archived).ToList();

        var completions = cards.Where(c => c.CompletedAtUtc is not null)
            .Select(c => c.CompletedAtUtc!.Value.ToLocalTime())
            .Concat(polarTasks.Where(t => t.Completed && t.CompletedAtUtc is not null)
                .Select(t => t.CompletedAtUtc!.Value.ToLocalTime()))
            .ToList();

        var daily = BuildDailySeries(rangeStart, rangeEnd, noteTimes, completions, activeHabits, today);
        var weekly = BuildWeeklySeries(rangeStartUtc, rangeEndUtc);

        return new StatisticsReport(
            Daily: daily,
            Weekly: weekly,
            ActivityHeatmap: BuildActivityHeatmap(noteTimes, completions, today),
            HabitHeatmap: BuildHabitHeatCells(activeHabits, today),
            PunchCard: BuildPunchCard(noteTimes, completions, rangeStart, rangeEnd),
            WeekdayActivity: BuildWeekdayActivity(daily),
            Growth: BuildGrowth(noteTimes, rangeStart, rangeEnd),
            TopTags: BuildTopTags(notes, cards, polarTasks),
            TopFolders: BuildTopFolders(notes),
            LongestNotes: BuildLongestNotes(notes),
            NoteLengthBuckets: BuildNoteLengthBuckets(notes),
            FileTypes: BuildFileTypes(out var filesCount, out var filesBytes),
            SessionNotes: BuildSessionNotes(),
            HabitConsistency: BuildHabitConsistency(activeHabits, rangeStart, rangeEnd, today),
            ColumnDistribution: BuildColumnDistribution(),
            PriorityCounts: BuildPriorityCounts(cards),
            Flow: BuildKanbanFlow(),
            Tasks: BuildTaskHealth(cards, polarTasks),
            Reminders: BuildReminderHealth(),
            Vault: BuildVaultComposition(notes, filesCount, filesBytes),
            ActivityStreak: BuildActivityStreak(noteTimes, completions, today),
            HabitStreak: BuildHabitStreak(activeHabits, today),
            HabitCompletionPercent: RangeHabitPercent(daily),
            NotesCreatedInRange: daily.Sum(d => d.NotesCreated),
            NotesEditedInRange: daily.Sum(d => d.NotesEdited),
            TasksCompletedInRange: daily.Sum(d => d.TasksCompleted),
            WordsWrittenInRange: noteTimes
                .Where(n => InRange(DateOnly.FromDateTime(n.Created), rangeStart, rangeEnd))
                .Sum(n => n.Note.WordCount),
            ActiveDaysInRange: daily.Count(d => d.Total > 0),
            RangeDays: daily.Count);
    }

    private static bool InRange(DateOnly date, DateOnly start, DateOnly end) => date >= start && date <= end;

    // ===================== Series por día =====================

    private static List<DailyStatsPoint> BuildDailySeries(
        DateOnly start, DateOnly end,
        List<(NoteFile Note, DateTime Created, DateTime Modified)> noteTimes,
        List<DateTime> completions,
        List<Habit> activeHabits,
        DateOnly today)
    {
        var created = CountByDay(noteTimes.Select(n => DateOnly.FromDateTime(n.Created)));
        var edited = CountByDay(noteTimes.Select(n => DateOnly.FromDateTime(n.Modified)));
        var done = CountByDay(completions.Select(DateOnly.FromDateTime));

        var points = new List<DailyStatsPoint>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var (applicable, completed) = HabitDayTotals(activeHabits, date, today);
            points.Add(new DailyStatsPoint(
                date,
                created.GetValueOrDefault(date),
                // Una nota creada hoy también se ha "editado" hoy (mismo timestamp): descontarla evita
                // contar dos veces el mismo trabajo en el total del día.
                Math.Max(0, edited.GetValueOrDefault(date) - created.GetValueOrDefault(date)),
                done.GetValueOrDefault(date),
                completed,
                applicable));
        }
        return points;
    }

    private static Dictionary<DateOnly, int> CountByDay(IEnumerable<DateOnly> dates)
    {
        var map = new Dictionary<DateOnly, int>();
        foreach (var date in dates) map[date] = map.GetValueOrDefault(date) + 1;
        return map;
    }

    private static (int Applicable, int Completed) HabitDayTotals(List<Habit> habits, DateOnly date, DateOnly today)
    {
        if (date > today) return (0, 0);
        var applicable = 0;
        var completed = 0;
        foreach (var habit in habits)
        {
            if (!HabitService.IsApplicable(habit, date)) continue;
            applicable++;
            if (HabitService.IsCompleted(habit, date)) completed++;
        }
        return (applicable, completed);
    }

    private static double? RangeHabitPercent(List<DailyStatsPoint> daily)
    {
        var applicable = daily.Sum(d => d.HabitsApplicable);
        return applicable == 0 ? null : daily.Sum(d => d.HabitsCompleted) * 100.0 / applicable;
    }

    // ===================== Mapas de calor =====================

    /// <summary>Un año de actividad diaria (notas creadas/editadas + tareas completadas) normalizada
    /// contra el percentil 90 y no contra el máximo: un único día atípico de 40 notas dejaría todos los
    /// demás en el escalón más bajo y el mapa se vería vacío.</summary>
    private static List<HeatCell> BuildActivityHeatmap(
        List<(NoteFile Note, DateTime Created, DateTime Modified)> noteTimes,
        List<DateTime> completions,
        DateOnly today)
    {
        var lastMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = lastMonday.AddDays(-7 * (HeatmapWeeks - 1));

        var counts = new Dictionary<DateOnly, int>();
        void Add(DateOnly date)
        {
            if (date < start || date > today) return;
            counts[date] = counts.GetValueOrDefault(date) + 1;
        }

        foreach (var note in noteTimes)
        {
            Add(DateOnly.FromDateTime(note.Created));
            if (DateOnly.FromDateTime(note.Modified) != DateOnly.FromDateTime(note.Created))
                Add(DateOnly.FromDateTime(note.Modified));
        }
        foreach (var completion in completions) Add(DateOnly.FromDateTime(completion));

        var reference = Percentile([.. counts.Values.Where(v => v > 0)], 0.90);
        if (reference <= 0) reference = 1;

        var cells = new List<HeatCell>();
        for (var date = start; date <= today; date = date.AddDays(1))
        {
            var count = counts.GetValueOrDefault(date);
            cells.Add(new HeatCell(date, count, Math.Min(1, count / reference),
                count.ToString(CultureInfo.CurrentCulture)));
        }
        return cells;
    }

    /// <summary>Mismo formato de celda que el mapa anual, pero la intensidad es la fracción de hábitos
    /// aplicables cumplidos ese día — así un día con 3 de 4 hábitos ya no se ve igual que uno en blanco,
    /// que era la limitación del heatmap de tres estados anterior.</summary>
    private static List<HeatCell> BuildHabitHeatCells(List<Habit> activeHabits, DateOnly today)
    {
        if (activeHabits.Count == 0) return [];

        var lastMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = lastMonday.AddDays(-7 * (HeatmapWeeks - 1));

        var cells = new List<HeatCell>();
        for (var date = start; date <= today; date = date.AddDays(1))
        {
            var (applicable, completed) = HabitDayTotals(activeHabits, date, today);
            var intensity = applicable == 0 ? 0 : (double)completed / applicable;
            cells.Add(new HeatCell(date, applicable == 0 ? 0 : completed, intensity,
                applicable == 0 ? "—" : $"{completed}/{applicable}"));
        }
        return cells;
    }

    private static double Percentile(List<int> values, double percentile)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var index = (int)Math.Clamp(Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1);
        return values[index];
    }

    // ===================== Ritmo (día × hora) =====================

    /// <summary>168 casillas (7 días × 24 horas) con la actividad del rango. Las marcas de tiempo son las
    /// que el vault guarda de verdad: creación y última modificación de cada nota, más el instante en que
    /// se completó cada tarea. No hay un historial de ediciones por nota, así que una nota tocada diez
    /// veces solo aporta su última hora — el patrón semanal sigue siendo válido, el volumen absoluto no.</summary>
    private static List<double> BuildPunchCard(
        List<(NoteFile Note, DateTime Created, DateTime Modified)> noteTimes,
        List<DateTime> completions,
        DateOnly start, DateOnly end)
    {
        var slots = new double[7 * 24];

        void Add(DateTime stamp)
        {
            var date = DateOnly.FromDateTime(stamp);
            if (!InRange(date, start, end)) return;
            slots[((int)stamp.DayOfWeek + 6) % 7 * 24 + stamp.Hour]++;
        }

        foreach (var note in noteTimes)
        {
            Add(note.Created);
            if (note.Modified != note.Created) Add(note.Modified);
        }
        foreach (var completion in completions) Add(completion);

        return [.. slots];
    }

    private static List<ChartDatum> BuildWeekdayActivity(List<DailyStatsPoint> daily)
    {
        var totals = new int[7];
        var notes = new int[7];
        var tasks = new int[7];
        foreach (var point in daily)
        {
            var index = ((int)point.Date.DayOfWeek + 6) % 7;
            totals[index] += point.Total;
            notes[index] += point.NotesCreated + point.NotesEdited;
            tasks[index] += point.TasksCompleted;
        }

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        return [.. Enumerable.Range(0, 7).Select(i =>
        {
            var dayOfWeek = (DayOfWeek)((i + 1) % 7);
            return new ChartDatum(
                format.GetDayName(dayOfWeek).ToUpper(CultureInfo.CurrentCulture),
                totals[i],
                $"{notes[i]} · {tasks[i]}")
            {
                ShortLabel = format.GetShortestDayName(dayOfWeek).ToUpper(CultureInfo.CurrentCulture)
            };
        })];
    }

    // ===================== Crecimiento acumulado =====================

    /// <summary>Curva acumulada de notas del vault a lo largo del rango, arrancando en las que ya existían
    /// antes del primer día — sin ese arranque, un vault de 400 notas parecería empezar de cero cada vez
    /// que se cambia de rango.</summary>
    private static List<ChartDatum> BuildGrowth(
        List<(NoteFile Note, DateTime Created, DateTime Modified)> noteTimes,
        DateOnly start, DateOnly end)
    {
        var created = CountByDay(noteTimes.Select(n => DateOnly.FromDateTime(n.Created)));
        var running = noteTimes.Count(n => DateOnly.FromDateTime(n.Created) < start);

        var points = new List<ChartDatum>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            running += created.GetValueOrDefault(date);
            points.Add(new ChartDatum(date.ToString("d", CultureInfo.CurrentCulture), running)
            {
                ShortLabel = date.ToString("d MMM", CultureInfo.CurrentCulture)
            });
        }
        return points;
    }

    // ===================== Tops del vault =====================

    private static List<ChartDatum> BuildTopTags(List<NoteFile> notes, List<KanbanCard> cards, List<PolarTask> tasks)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> tags)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        foreach (var note in notes) Add(note.Tags);
        foreach (var card in cards) Add(card.Tags);
        foreach (var task in tasks) Add(task.Tags);

        return [.. counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopListSize)
            .Select(kv => new ChartDatum("#" + kv.Key, kv.Value))];
    }

    private List<ChartDatum> BuildTopFolders(List<NoteFile> notes)
    {
        var root = vaultService.VaultPath;
        if (string.IsNullOrEmpty(root)) return [];

        var counts = new Dictionary<string, (int Notes, int Words)>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var directory = Path.GetDirectoryName(note.FullPath);
            if (directory is null) continue;
            var relative = Path.GetRelativePath(root, directory);
            var name = relative is "." or "" ? "/" : relative;
            var current = counts.GetValueOrDefault(name);
            counts[name] = (current.Notes + 1, current.Words + note.WordCount);
        }

        return [.. counts.OrderByDescending(kv => kv.Value.Notes).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(TopListSize)
            .Select(kv => new ChartDatum(kv.Key, kv.Value.Notes,
                kv.Value.Words.ToString("N0", CultureInfo.CurrentCulture)))];
    }

    private static List<ChartDatum> BuildLongestNotes(List<NoteFile> notes) =>
        [.. notes.Where(n => n.WordCount > 0)
            .OrderByDescending(n => n.WordCount)
            .Take(TopListSize)
            .Select(n => new ChartDatum(n.Title, n.WordCount))];

    /// <summary>Histograma de longitud. Los tramos están elegidos sobre cómo se usa una app de notas de
    /// verdad (un apunte suelto, una nota de trabajo, un documento largo), no como una escala matemática
    /// regular que dejaría el 90% de las notas en el primer tramo.</summary>
    private static List<ChartDatum> BuildNoteLengthBuckets(List<NoteFile> notes)
    {
        int[] limits = [50, 150, 400, 1000, 2500];
        string[] labels = ["<50", "50–149", "150–399", "400–999", "1000–2499", "2500+"];
        var counts = new int[labels.Length];

        foreach (var note in notes)
        {
            var index = limits.Length;
            for (var i = 0; i < limits.Length; i++)
            {
                if (note.WordCount < limits[i]) { index = i; break; }
            }
            counts[index]++;
        }

        return [.. labels.Select((label, i) => new ChartDatum(label, counts[i]) { ShortLabel = label })];
    }

    /// <summary>Reparto de los adjuntos por extensión. Recorre el vault por su cuenta en vez de usar
    /// <c>VaultService.GetFiles</c> porque ese solo devuelve una carpeta (es el que alimenta la rejilla de
    /// Archivos) y aquí interesa el vault entero, saltándose las carpetas ocultas igual que hace el resto
    /// de la app.</summary>
    private List<ChartDatum> BuildFileTypes(out int totalFiles, out long totalBytes)
    {
        totalFiles = 0;
        totalBytes = 0;

        var root = vaultService.VaultPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return [];

        var counts = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(root, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.StartsWith('.'))) continue;

                long length;
                try { length = new FileInfo(path).Length; }
                catch (IOException ex)
                {
                    LogService.Debug($"No se pudo medir '{path}' para las estadísticas: {ex}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogService.Debug($"No se pudo medir '{path}' para las estadísticas: {ex}");
                    continue;
                }

                var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                if (extension.Length == 0) extension = "—";
                var current = counts.GetValueOrDefault(extension);
                counts[extension] = (current.Count + 1, current.Bytes + length);
                totalFiles++;
                totalBytes += length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudieron enumerar los archivos del vault para las estadísticas", ex);
        }

        return [.. counts.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(TopListSize)
            .Select(kv => new ChartDatum(kv.Key, kv.Value.Count, FormatBytes(kv.Value.Bytes)))];
    }

    private List<ChartDatum> BuildSessionNotes() =>
        sessionService is null
            ? []
            : [.. sessionService.List()
                .OrderByDescending(s => s.NoteCount)
                .Take(TopListSize)
                .Select(s => new ChartDatum(s.Name, s.NoteCount))];

    /// <summary>Reparto real de tarjetas entre las columnas tal y como el usuario las ha nombrado — el
    /// flujo de tres estados (<see cref="BuildKanbanFlow"/>) colapsa las columnas intermedias, y quien ha
    /// montado un tablero de seis columnas quiere ver las seis.</summary>
    private List<ChartDatum> BuildColumnDistribution() =>
        [.. kanbanService.Board.Columns.Select(c => new ChartDatum(c.Title, c.Cards.Count))];

    private static List<int> BuildPriorityCounts(List<KanbanCard> cards) =>
        [cards.Count(c => c.Priority == KanbanPriority.High),
         cards.Count(c => c.Priority == KanbanPriority.Medium),
         cards.Count(c => c.Priority == KanbanPriority.Low)];

    private static List<ChartDatum> BuildHabitConsistency(List<Habit> habits, DateOnly start, DateOnly end, DateOnly today)
    {
        if (habits.Count == 0) return [];
        var last = end > today ? today : end;

        var result = new List<ChartDatum>();
        foreach (var habit in habits)
        {
            var applicable = 0;
            var completed = 0;
            for (var date = start; date <= last; date = date.AddDays(1))
            {
                if (!HabitService.IsApplicable(habit, date)) continue;
                applicable++;
                if (HabitService.IsCompleted(habit, date)) completed++;
            }
            if (applicable == 0) continue;
            result.Add(new ChartDatum(habit.Name, Math.Round(completed * 100.0 / applicable),
                $"{completed}/{applicable}"));
        }

        return [.. result.OrderByDescending(d => d.Value).ThenBy(d => d.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    // ===================== Salud de tareas / recordatorios =====================

    private static TaskHealth BuildTaskHealth(List<KanbanCard> cards, List<PolarTask> tasks)
    {
        var dueDates = cards.Where(c => c.CompletedAtUtc is null).Select(c => c.DueDateUtc)
            .Concat(tasks.Where(t => !t.Completed).Select(t => t.DueDateUtc))
            .ToList();

        var todayLocal = DateTime.Now.Date;
        var overdue = dueDates.Count(d => d is not null && KanbanCard.IsPastDue(d.Value));
        var dueToday = dueDates.Count(d => d is not null && !KanbanCard.IsPastDue(d.Value) && d.Value.ToLocalTime().Date == todayLocal);
        var dueWeek = dueDates.Count(d => d is not null && !KanbanCard.IsPastDue(d.Value)
            && d.Value.ToLocalTime().Date > todayLocal && d.Value.ToLocalTime().Date <= todayLocal.AddDays(7));

        var subtasks = cards.SelectMany(c => c.Subtasks).ToList();

        return new TaskHealth(
            Total: dueDates.Count,
            Overdue: overdue,
            DueToday: dueToday,
            DueNext7Days: dueWeek,
            NoDueDate: dueDates.Count(d => d is null),
            Subtasks: subtasks.Count,
            SubtasksDone: subtasks.Count(s => s.IsDone));
    }

    private ReminderHealth BuildReminderHealth()
    {
        if (reminderService is null) return new ReminderHealth(0, 0, 0, 0, 0);

        var active = reminderService.ActiveReminders.ToList();
        return new ReminderHealth(
            Active: active.Count,
            Upcoming: active.Count(r => !r.IsCompleted && !KanbanCard.IsPastDue(r.NotifyAtUtc)),
            Overdue: active.Count(r => !r.IsCompleted && KanbanCard.IsPastDue(r.NotifyAtUtc)),
            Completed: active.Count(r => r.IsCompleted),
            Recurring: active.Count(r => r.Recurrence != PolarRecurrence.None));
    }

    // ===================== Composición del vault =====================

    private VaultComposition BuildVaultComposition(List<NoteFile> notes, int files, long filesBytes)
    {
        long notesBytes = 0;
        foreach (var note in notes)
        {
            try
            {
                var info = new FileInfo(note.FullPath);
                if (info.Exists) notesBytes += info.Length;
            }
            catch (IOException ex) { LogService.Debug($"No se pudo medir la nota '{note.FullPath}': {ex}"); }
            catch (UnauthorizedAccessException ex) { LogService.Debug($"No se pudo medir la nota '{note.FullPath}': {ex}"); }
        }

        var longest = notes.OrderByDescending(n => n.WordCount).FirstOrDefault();
        var distinctTags = notes.SelectMany(n => n.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new VaultComposition(
            Notes: notes.Count,
            Folders: CountFolders(vaultService.VaultPath),
            Files: files,
            Sessions: sessionService?.List().Count ?? CountSessionDirectories(),
            DistinctTags: distinctTags,
            TotalWords: notes.Sum(n => n.WordCount),
            LongestNoteWords: longest?.WordCount ?? 0,
            LongestNoteTitle: longest?.Title ?? string.Empty,
            NotesBytes: notesBytes,
            FilesBytes: filesBytes);
    }

    private static int CountFolders(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Count(d => !Path.GetRelativePath(root, d)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(s => s.StartsWith('.')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudieron contar las carpetas del vault para las estadísticas", ex);
            return 0;
        }
    }

    private int CountSessionDirectories()
    {
        var root = Path.Combine(vaultService.VaultPath ?? string.Empty, ".sessions");
        try { return Directory.Exists(root) ? Directory.GetDirectories(root).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudieron contar las carpetas de sesión", ex);
            return 0;
        }
    }

    // ===================== Rachas =====================

    /// <summary>Racha de días consecutivos con alguna actividad. La de hoy no se rompe hasta mañana: si
    /// hoy todavía no has tocado nada pero ayer sí, la racha sigue viva — penalizar el día en curso
    /// convertiría el contador en un reproche a media mañana.</summary>
    private static StreakInfo BuildActivityStreak(
        List<(NoteFile Note, DateTime Created, DateTime Modified)> noteTimes,
        List<DateTime> completions,
        DateOnly today)
    {
        var active = new HashSet<DateOnly>();
        foreach (var note in noteTimes)
        {
            active.Add(DateOnly.FromDateTime(note.Created));
            active.Add(DateOnly.FromDateTime(note.Modified));
        }
        foreach (var completion in completions) active.Add(DateOnly.FromDateTime(completion));

        return BuildStreak(active.Contains, today);
    }

    private static StreakInfo BuildHabitStreak(List<Habit> habits, DateOnly today)
    {
        if (habits.Count == 0) return new StreakInfo(0, 0);

        return BuildStreak(date =>
        {
            var (applicable, completed) = HabitDayTotals(habits, date, today);
            // Un día sin ningún hábito aplicable no suma racha, pero tampoco la rompe (ver bucle abajo).
            return applicable > 0 && completed == applicable;
        }, today, neutral: date => HabitDayTotals(habits, date, today).Applicable == 0);
    }

    /// <summary>Recorre hacia atrás un año desde hoy contando días que cumplen <paramref name="hit"/>.
    /// Un día "neutro" (sin nada que cumplir) se salta sin cortar ni sumar.</summary>
    private static StreakInfo BuildStreak(Func<DateOnly, bool> hit, DateOnly today, Func<DateOnly, bool>? neutral = null)
    {
        const int lookbackDays = 366;

        var current = 0;
        var counting = true;
        var best = 0;
        var run = 0;

        for (var offset = 0; offset < lookbackDays; offset++)
        {
            var date = today.AddDays(-offset);
            if (neutral?.Invoke(date) == true) continue;

            if (hit(date))
            {
                run++;
                if (counting) current = run;
                best = Math.Max(best, run);
                continue;
            }

            // El día en curso sin actividad no rompe la racha (ver resumen); cualquier otro sí.
            if (offset == 0) continue;
            counting = false;
            run = 0;
        }

        return new StreakInfo(current, best);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
