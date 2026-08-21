using System;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Local persistence + CRUD for the Polar-compatible "Gestión de tareas" domain
/// (<see cref="PolarTaskList"/>/<see cref="PolarTask"/>/<see cref="PolarSubtask"/>) — same
/// load/save-a-single-JSON-file pattern as <see cref="KanbanService"/>, stored separately at
/// <c>%AppData%/Enki/polar-tasks.json</c> since this is an entirely separate data domain from the local
/// Kanban board (docs/supabase-sync/). Every mutation here marks the row <c>Dirty</c> and stamps
/// <c>UpdatedAtUtc</c> so <c>PolarSyncService</c> knows what to push next.</summary>
public sealed class PolarTaskService
{
    private readonly string _path;
    private readonly Func<string?> _currentUserIdProvider;

    public PolarTaskStore Store { get; private set; }

    /// <summary>Raised after every <see cref="Save"/> — lets an open "Gestión de tareas" view refresh
    /// live when a background sync pull brings in remote changes, same role as
    /// <see cref="KanbanService.BoardChanged"/>.</summary>
    public event Action? DataChanged;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, lets tests point persistence at a throwaway directory.</param>
    /// <param name="currentUserIdProvider">Returns the Supabase user id of the currently linked Polar
    /// account (or <see langword="null"/> if none is linked) — the composition root wires this to
    /// <c>PolarAuthService.UserId</c>. Used to stamp <c>UserId</c> on every row the moment it first becomes
    /// dirty, so <see cref="Services.PolarSyncService"/> can later refuse to push a row under a different
    /// account than the one linked when it was created/edited (docs/analisis-proyecto/roadmap.md, Fase 4).
    /// Defaults to "always null" so existing callers that don't care about account isolation (most tests)
    /// keep working unchanged.</param>
    public PolarTaskService(string? storageDirectory = null, Func<string?>? currentUserIdProvider = null)
    {
        var configDir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enki");
        Directory.CreateDirectory(configDir);
        _path = Path.Combine(configDir, "polar-tasks.json");
        _currentUserIdProvider = currentUserIdProvider ?? (() => null);
        Store = Load();
    }

    private PolarTaskStore Load() =>
        JsonFileStore.TryLoad<PolarTaskStore>(_path, out var loaded) ? loaded! : new PolarTaskStore();

    public void Save()
    {
        JsonFileStore.Save(_path, Store);
        DataChanged?.Invoke();
    }

    /// <summary>Empties every list/task/subtask and persists the empty store — called when the Polar
    /// account is unlinked (docs/analisis-proyecto/roadmap.md, Fase 4): unlike Recordatorios, this domain
    /// has no standalone local-only use case (see <see cref="Models.PolarTaskList.UserId"/>), so without a
    /// linked account its data has no valid owner left and would otherwise sit on disk ready to be pushed
    /// under whichever different account links next on the same device.</summary>
    public void ClearAll()
    {
        Store = new PolarTaskStore();
        Save();
    }

    // ===================== Listas =====================

    public PolarTaskList CreateList(string title, string icon, string color, bool isDependencyChain)
    {
        var list = new PolarTaskList
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Icon = icon,
            Color = color,
            IsDependencyChain = isDependencyChain,
            OrderIndex = Store.Lists.Count(l => !l.IsDeleted),
            UserId = _currentUserIdProvider()
        };
        Store.Lists.Add(list);
        Save();
        return list;
    }

    public void UpdateList(PolarTaskList list, string title, string icon, string color, bool isDependencyChain)
    {
        list.Title = title;
        list.Icon = icon;
        list.Color = color;
        list.IsDependencyChain = isDependencyChain;
        Touch(list);
        Save();
    }

    /// <summary>Soft-deletes the list and cascades the soft-delete to every task/subtask under it — a
    /// Postgres <c>UPDATE</c> (soft-delete) never cascades on its own the way a real <c>DELETE</c> does,
    /// so the client has to do it explicitly (docs/supabase-sync/04, "Manejo de borrado en cascada").</summary>
    public void DeleteList(PolarTaskList list)
    {
        Touch(list);
        list.IsDeleted = true;
        list.DeletedAtUtc = list.UpdatedAtUtc;

        foreach (var task in Store.Tasks.Where(t => t.ListId == list.Id && !t.IsDeleted))
        {
            SoftDeleteTaskCore(task, list.UpdatedAtUtc);
        }

        Save();
    }

    // ===================== Tareas =====================

    public PolarTask CreateTask(
        PolarTaskList list, string title, string description, int priority, DateTime? dueDateUtc,
        PolarRecurrence recurrence, int timeEstimate, string[] tags, int? durationMinutes = null)
    {
        var task = new PolarTask
        {
            Id = Guid.NewGuid().ToString(),
            ListId = list.Id,
            Title = title,
            Description = description,
            Priority = priority,
            DueDateUtc = dueDateUtc,
            Recurrence = recurrence,
            TimeEstimate = timeEstimate,
            DurationMinutes = durationMinutes,
            Tags = tags.ToList(),
            OrderIndex = Store.Tasks.Count(t => t.ListId == list.Id && !t.IsDeleted),
            UserId = _currentUserIdProvider()
        };
        Store.Tasks.Add(task);
        Save();
        return task;
    }

    public void UpdateTask(
        PolarTask task, string title, string description, int priority, DateTime? dueDateUtc,
        PolarRecurrence recurrence, int timeEstimate, string[] tags, int? durationMinutes = null)
    {
        task.Title = title;
        task.Description = description;
        task.Priority = priority;
        task.DueDateUtc = dueDateUtc;
        task.Recurrence = recurrence;
        task.TimeEstimate = timeEstimate;
        task.DurationMinutes = durationMinutes;
        task.Tags = tags.ToList();
        Touch(task);
        Save();
    }

    /// <summary>Actualiza solo la duración de la franja horaria en el calendario. Usado por el gesto de
    /// redimensionado de <see cref="ViewModels.CalendarViewModel"/>, que no debe reescribir el resto de
    /// campos de la tarea.</summary>
    public void UpdateTaskDuration(PolarTask task, int? durationMinutes)
    {
        task.DurationMinutes = durationMinutes;
        Touch(task);
        Save();
    }

    /// <summary>Completing/uncompleting a task cascades the same state to its subtasks in one shot —
    /// same rule Polar's own UI already applies (docs/supabase-sync/05, "Regla de negocio a preservar"
    /// under <c>subtasks</c>).</summary>
    public void SetTaskCompleted(PolarTask task, bool completed)
    {
        task.Completed = completed;
        Touch(task);

        foreach (var subtask in Store.Subtasks.Where(s => s.TaskId == task.Id && !s.IsDeleted))
        {
            subtask.Completed = completed;
            Touch(subtask);
        }

        Save();
    }

    public void DeleteTask(PolarTask task)
    {
        SoftDeleteTaskCore(task, DateTime.UtcNow);
        Save();
    }

    private void SoftDeleteTaskCore(PolarTask task, DateTime whenUtc)
    {
        task.UpdatedAtUtc = whenUtc;
        task.Dirty = true;
        task.UserId ??= _currentUserIdProvider();
        task.IsDeleted = true;
        task.DeletedAtUtc = whenUtc;

        foreach (var subtask in Store.Subtasks.Where(s => s.TaskId == task.Id && !s.IsDeleted))
        {
            subtask.UpdatedAtUtc = whenUtc;
            subtask.Dirty = true;
            subtask.UserId ??= _currentUserIdProvider();
            subtask.IsDeleted = true;
            subtask.DeletedAtUtc = whenUtc;
        }
    }

    // ===================== Subtareas =====================

    public PolarSubtask AddSubtask(PolarTask task, string title)
    {
        var subtask = new PolarSubtask
        {
            Id = Guid.NewGuid().ToString(),
            TaskId = task.Id,
            Title = title,
            OrderIndex = Store.Subtasks.Count(s => s.TaskId == task.Id && !s.IsDeleted),
            UserId = _currentUserIdProvider()
        };
        Store.Subtasks.Add(subtask);
        Save();
        return subtask;
    }

    public void SetSubtaskCompleted(PolarSubtask subtask, bool completed)
    {
        subtask.Completed = completed;
        Touch(subtask);
        Save();
    }

    /// <summary>Writes a new <see cref="PolarSubtask.DueDateUtc"/> — the path the calendar's drag &amp;
    /// drop (docs/calendar-feature/05-interacciones-y-microinteracciones.md §2) uses to reschedule a
    /// subtask occurrence, since no editor surface exists yet that edits a subtask's own due date.</summary>
    public void UpdateSubtaskDueDate(PolarSubtask subtask, DateTime? dueDateUtc)
    {
        subtask.DueDateUtc = dueDateUtc;
        Touch(subtask);
        Save();
    }

    public void DeleteSubtask(PolarSubtask subtask)
    {
        subtask.IsDeleted = true;
        subtask.DeletedAtUtc = DateTime.UtcNow;
        Touch(subtask);
        Save();
    }

    // ===================== Cadena de dependencias (docs/supabase-sync/02) =====================

    /// <summary>True when <paramref name="task"/> belongs to a dependency-chain list and an earlier,
    /// still-incomplete task precedes it in <c>OrderIndex</c> — mirrors Polar's own
    /// <c>isBlocked</c> rule exactly (only the first incomplete task in the chain is unlocked).</summary>
    public bool IsTaskBlocked(PolarTask task)
    {
        var list = Store.Lists.FirstOrDefault(l => l.Id == task.ListId);
        if (list is null || !list.IsDependencyChain || task.Completed) return false;

        return Store.Tasks.Any(t =>
            t.ListId == task.ListId && !t.IsDeleted && !t.Completed && t.OrderIndex < task.OrderIndex);
    }

    // ===================== Motor de recurrencia (fallback local, docs/supabase-sync/02) =====================

    /// <summary>Local fallback for the "roll a completed recurring task to its next due date" engine that
    /// docs/supabase-sync/03 centralizes server-side via <c>pg_cron</c>. Runs the exact same algorithm as
    /// Polar's <c>RecurrenceWorker.calculateNextDueDate</c> so both apps compute identical results whether
    /// or not the server-side job is also configured. Called from <c>PolarSyncService</c> — deliberately
    /// after that cycle's push/pull, not before, so a task completed this cycle is actually sent to
    /// Supabase before being locally advanced to its next (incomplete) occurrence; see the call site.
    /// Returns whether anything rolled, so the caller can fold it into its own "did this cycle change
    /// anything" flag.</summary>
    public bool RollRecurringTasks()
    {
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var task in Store.Tasks.Where(t =>
                     t.Completed && t.Recurrence != PolarRecurrence.None && t.DueDateUtc is not null && !t.IsDeleted))
        {
            var nextDue = task.DueDateUtc!.Value;
            var advanced = false;
            while (nextDue <= now)
            {
                nextDue = CalculateNextDueDate(nextDue, task.Recurrence);
                advanced = true;
            }
            if (!advanced) continue;

            task.Completed = false;
            task.DueDateUtc = nextDue;
            Touch(task);
            changed = true;

            foreach (var subtask in Store.Subtasks.Where(s => s.TaskId == task.Id && !s.IsDeleted))
            {
                subtask.Completed = false;
                Touch(subtask);
            }
        }

        if (changed) Save();
        return changed;
    }

    /// <summary>Exact algorithm from docs/supabase-sync/02-logica-de-negocio.md#motor-de-recurrencia —
    /// always relative to the previous due date (never to "now"), preserving time-of-day. La aritmética se
    /// hace sobre la hora de pared local (no sobre el UTC almacenado) para que la hora elegida por el
    /// usuario sobreviva a los cambios de horario de verano/invierno: +1 día sobre las 09:00 locales sigue
    /// siendo 09:00 locales aunque entre medias haya una transición DST — operar en UTC puro la desplazaba
    /// ~1 h (a veces de día) dos veces al año (docs/analisis-proyecto/roadmap.md, Fase 2).</summary>
    /// <param name="timeZone">Zona cuya hora de pared se preserva — la local del sistema por defecto;
    /// inyectable solo para que los tests de DST sean deterministas en cualquier máquina.</param>
    public static DateTime CalculateNextDueDate(DateTime currentDueDate, PolarRecurrence recurrence, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(currentDueDate, DateTimeKind.Utc), zone);

        var nextLocal = recurrence switch
        {
            PolarRecurrence.Daily => local.AddDays(1),
            PolarRecurrence.Weekly => local.AddDays(7),
            PolarRecurrence.Monthly => local.AddMonths(1),
            PolarRecurrence.MonWed => NextMondayOrWednesday(local),
            PolarRecurrence.FirstDayMonth => new DateTime(local.Year, local.Month, 1, local.Hour, local.Minute, local.Second).AddMonths(1),
            _ => local
        };

        // Si la hora local calculada cae en el hueco inexistente del salto de primavera (p. ej. 02:30 la
        // noche del cambio), ConvertTimeToUtc la interpreta como hora estándar — desvío acotado de ±1 h
        // para una única ocurrencia, aceptable para un motor de recurrencia.
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal, zone);
    }

    private static DateTime NextMondayOrWednesday(DateTime from)
    {
        var candidate = from.AddDays(1);
        while (candidate.DayOfWeek != DayOfWeek.Monday && candidate.DayOfWeek != DayOfWeek.Wednesday)
            candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>Marks a row dirty and, if it isn't owned yet (never edited before, or predates the
    /// <c>UserId</c> field), claims it for whichever account is currently linked. Deliberately never
    /// reassigns an already-owned row: an edit made while a *different* account is linked leaves that row
    /// dirty but still tagged with its original owner, so it simply won't push under the wrong session
    /// until the right account reconnects (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    private void Touch(PolarTaskList list)
    {
        list.UpdatedAtUtc = DateTime.UtcNow;
        list.Dirty = true;
        list.UserId ??= _currentUserIdProvider();
    }

    private void Touch(PolarTask task)
    {
        task.UpdatedAtUtc = DateTime.UtcNow;
        task.Dirty = true;
        task.UserId ??= _currentUserIdProvider();
    }

    private void Touch(PolarSubtask subtask)
    {
        subtask.UpdatedAtUtc = DateTime.UtcNow;
        subtask.Dirty = true;
        subtask.UserId ??= _currentUserIdProvider();
    }
}
