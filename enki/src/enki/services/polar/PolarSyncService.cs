using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>The offline-first sync engine described in docs/supabase-sync/04-estrategia-sincronizacion.md,
/// adapted to Enki: push every locally-dirty row, then pull everything changed remotely since the last
/// cursor, for all 4 tables (task_lists/tasks/subtasks/reminders). Runs on a periodic timer from
/// <c>MainWindowViewModel</c> (the same polling pattern Enki already uses for due reminders) as the
/// offline-first safety net, and is now also triggered immediately on a remote change via
/// <see cref="PolarRealtimeService"/>'s Postgres Realtime subscription — the polling timer still runs
/// regardless, since a websocket can drop silently. Everything else from the offline-first design is here:
/// dirty-flag queueing (writes work fully offline, queued for the next successful sync),
/// server-authoritative Last-Write-Wins via the <c>updated_at</c> the trigger in docs/supabase-sync/03
/// resolves, tombstone soft-deletes, and the recurrence-rollover fallback.</summary>
/// <summary>Coarse sync state a UI status indicator can render directly, derived from
/// <see cref="PolarSyncService"/>'s existing <c>IsSyncing</c>/<c>LastError</c>/<c>LastErrorWasNetwork</c>
/// fields instead of each of the 3 consuming view models (Recordatorios, Gestión de tareas, Ajustes ›
/// Cuenta) re-deriving the same branching independently (10.2 §8).</summary>
public enum PolarSyncStatus
{
    NotLinked,
    Synced,
    Syncing,
    /// <summary>Last attempt never reached the server — see <see cref="PolarSyncService.LastErrorWasNetwork"/>.</summary>
    Offline,
    /// <summary>The server responded but rejected or failed the request (401/403/429/5xx/unexpected).</summary>
    Error
}

/// <summary>Result of a single <see cref="PolarSyncService.SyncNowAsync"/> call — lets a manual "Sync now"
/// button (Recordatorios, Gestión de tareas, Ajustes › Cuenta) tell a user "nothing happened because a sync
/// was already running" apart from "a sync cycle actually ran" (Fase 9, docs/analisis-proyecto/roadmap.md);
/// before this, both cases looked identical from the caller's side — a quick, silent flip of
/// <c>IsSyncing</c> with no visible outcome either way.</summary>
public enum PolarSyncOutcome
{
    /// <summary>A sync attempt actually ran to completion, successfully or not — see
    /// <see cref="PolarSyncService.LastError"/> for whether it succeeded.</summary>
    Completed,
    /// <summary>Skipped because another <see cref="PolarSyncService.SyncNowAsync"/> call already held the
    /// gate — this attempt never even reached the network.</summary>
    SkippedAlreadyRunning,
    /// <summary>Skipped because no Polar account is linked — <see cref="PolarSyncService.SyncNowAsync"/> is
    /// a no-op in that state.</summary>
    NotLinked
}

// CA1001: _gate (SemaphoreSlim) is disposable, but this service is an app-lifetime singleton constructed
// once in MainWindowViewModel.Composition.cs and never explicitly torn down (Fase 10, docs/analisis-proyecto/
// roadmap.md) — same lifetime as every other composed service in the shell. Process exit reclaims the
// handle; there is no shutdown sequence anywhere in the app for this class of resource to hook into.
#pragma warning disable CA1001
public sealed class PolarSyncService
#pragma warning restore CA1001
{
    private readonly PolarAuthService _auth;
    private readonly SupabaseRestClient _rest;
    private readonly PolarTaskService _taskService;
    private readonly ReminderService _reminderService;
    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsSyncing { get; private set; }
    public LocalizedText? LocalizedError { get; private set; }
    public string? LastError => LocalizedError is null ? null : LocalizedTextFormatter.Resolve(LocalizedError);

    public PolarSyncStatus Status =>
        !_auth.IsLinked ? PolarSyncStatus.NotLinked :
        IsSyncing ? PolarSyncStatus.Syncing :
        LastErrorWasNetwork ? PolarSyncStatus.Offline :
        LastError is not null ? PolarSyncStatus.Error :
        PolarSyncStatus.Synced;

    /// <summary>When <see cref="Status"/> is <see cref="PolarSyncStatus.Offline"/>, the UTC instant the
    /// background timer expects to retry — set by <c>MainWindowViewModel</c> right after it computes the
    /// next exponential-backoff interval, so every "reintentando en X" label reads the same value the
    /// timer actually honors instead of guessing. Null whenever no retry is scheduled (synced, syncing, or
    /// a non-network error where backoff doesn't apply).</summary>
    public DateTime? NextSyncAttemptUtc { get; set; }

    /// <summary>True cuando el último intento falló por falta de red (<see cref="HttpRequestException"/>/
    /// <see cref="TaskCanceledException"/>) en vez de por un error real del servidor o de los datos — el
    /// compositor (<see cref="ViewModels.MainWindowViewModel"/>) lo usa para aplicar backoff exponencial
    /// al temporizador de sync solo mientras el dispositivo está realmente offline, no ante cualquier
    /// fallo (03-problemas-y-deuda-tecnica.md §4).</summary>
    public bool LastErrorWasNetwork { get; private set; }

    public DateTime? LastSyncedUtc => _settingsService.Settings.PolarLastSyncAtUtcMs > 0
        ? PolarWire.FromEpochMs(_settingsService.Settings.PolarLastSyncAtUtcMs)
        : null;

    /// <summary>Raised after every sync attempt (success or failure) — the Kanban task-management view and
    /// the Recordatorios page subscribe to refresh live when a pull brings in remote changes. The two flags
    /// report whether the task domain (lists/tasks/subtasks) or reminders actually changed, so a listener
    /// can skip rebuilding its list when a cycle brought in nothing new.</summary>
    public event Action<bool, bool>? SyncCompleted;

    /// <summary>Raised once per <see cref="SyncNowAsync"/> call (never mid-call, and never at all if nothing
    /// lost a conflict) with the title of every dirty local row whose edit was discarded this cycle because
    /// a newer remote write won the Last-Write-Wins race — see the "Lost the LWW conflict" branches below.
    /// Before Fase 9 (docs/analisis-proyecto/roadmap.md) this was invisible: the local edit vanished with no
    /// trace, and the user's only clue was the value silently reverting the next time they looked at it.
    /// Batched into one call per sync cycle (one title, or several) rather than one event per row, matching
    /// how <c>KanbanViewModel.DeleteColumn</c> already reports "N cards moved to trash" as a single
    /// notification instead of N separate ones.</summary>
    public event Action<IReadOnlyList<string>>? LocalEditsOverwritten;

    public PolarSyncService(
        PolarAuthService auth, SupabaseRestClient rest, PolarTaskService taskService,
        ReminderService reminderService, SettingsService settingsService)
    {
        _auth = auth;
        _rest = rest;
        _taskService = taskService;
        _reminderService = reminderService;
        _settingsService = settingsService;
    }

    public async Task<PolarSyncOutcome> SyncNowAsync(CancellationToken ct = default)
    {
        if (!_auth.IsLinked || string.IsNullOrEmpty(_auth.UserId)) return PolarSyncOutcome.NotLinked;
        if (!await _gate.WaitAsync(0, ct)) return PolarSyncOutcome.SkippedAlreadyRunning; // a sync is already running — skip, don't queue up

        IsSyncing = true;
        LocalizedError = null;
        LastErrorWasNetwork = false;
        var tasksChanged = false;
        var remindersChanged = false;
        var overwrittenTitles = new List<string>();
        try
        {
            var userId = _auth.UserId!;
            var cursor = _settingsService.Settings.PolarLastSyncAtUtcMs;

            tasksChanged |= await PushTaskListsAsync(userId, overwrittenTitles, ct);
            tasksChanged |= await PushTasksAsync(userId, overwrittenTitles, ct);
            tasksChanged |= await PushSubtasksAsync(userId, overwrittenTitles, ct);
            remindersChanged |= await PushRemindersAsync(userId, overwrittenTitles, ct);

            tasksChanged |= await PullTaskListsAsync(cursor, ct);
            tasksChanged |= await PullTasksAsync(cursor, ct);
            tasksChanged |= await PullSubtasksAsync(cursor, ct);
            remindersChanged |= await PullRemindersAsync(cursor, ct);

            // Runs after push/pull, never before: rolling a completed recurring task forward marks it
            // dirty again for the *next* cycle, so running this first (as it used to) would flip
            // Completed back to false and advance DueDateUtc locally before this cycle's push ever sent
            // "completed=true" — the server (and every other linked device) would never learn the task
            // was completed at all, just that its due date silently jumped forward. Since a recurring
            // task's due date is usually today or earlier by the time someone checks it off, this hit the
            // common case, not an edge case.
            if (_taskService.RollRecurringTasks()) tasksChanged = true;

            _settingsService.Settings.PolarLastSyncAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _settingsService.Save();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Sin red — comportamiento offline-first correcto, ver EnsureFreshTokenAsync. El backoff
            // exponencial vive en el temporizador que llama a este método, no aquí, y solo debe aplicar
            // mientras el dispositivo esté realmente sin conexión: un HttpRequestException con StatusCode
            // (401/403/429/500...) significa que el servidor sí respondió, así que no es un fallo de red.
            LocalizedError = TranslateSyncError(ex);
            LastErrorWasNetwork = ex is HttpRequestException { StatusCode: null } or TaskCanceledException;
        }
        catch (Exception ex)
        {
            LogService.Warning("Sincronización de Polar fallida", ex);
            LocalizedError = TranslateSyncError(ex);
        }
        finally
        {
            IsSyncing = false;
            _gate.Release();
            SyncCompleted?.Invoke(tasksChanged, remindersChanged);
            if (overwrittenTitles.Count > 0) LocalEditsOverwritten?.Invoke(overwrittenTitles);
        }

        return PolarSyncOutcome.Completed;
    }

    /// <summary>Traduce el fallo crudo de sync a un mensaje accionable para el usuario — antes
    /// <see cref="LastError"/> mostraba <c>ex.Message</c> tal cual (p. ej. "Response status code does not
    /// indicate success: 401 (Unauthorized)."), indistinguible entre "reinicia sesión", "espera y
    /// reintenta" o "problema del servidor" (04-oportunidades-de-integracion.md, mejora básica de sync
    /// #4).</summary>
    private static LocalizedText TranslateSyncError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_Unauthorized"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_Forbidden"),
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_TooManyRequests"),
        HttpRequestException { StatusCode: not null } httpEx when (int)httpEx.StatusCode!.Value >= 500 =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_ServerError"),
        HttpRequestException { StatusCode: null } =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_NoConnection"),
        TaskCanceledException =>
            LocalizedTextFormatter.Format("Polar_Sync_Error_Timeout"),
        // Los fallos inesperados tampoco exponen mensajes del sistema operativo o del servidor.
        _ => LocalizedTextFormatter.Format("Common_Error_Unexpected")
    };

    // ===================== Push =====================

    /// <summary>Fase 4 (docs/analisis-proyecto/roadmap.md): a <c>Dirty</c> row only pushes under the
    /// currently linked account if it's unowned (never claimed, or predates the <c>UserId</c> field — see
    /// <c>PolarTaskService.Touch</c>) or already owned by that same account. A row stamped with a
    /// *different* account (created/edited while that other account was linked, never pushed before an
    /// unlink+relink to a different account) is skipped entirely — it stays <c>Dirty</c> forever until the
    /// correct account reconnects, rather than leaking into the wrong Supabase project. Unowned rows found
    /// here are claimed for <paramref name="userId"/> right before being sent, since we're about to
    /// associate them with that account anyway.</summary>
    private static List<T> SelectOwnedDirty<T>(IEnumerable<T> rows, string userId, Func<T, bool> isDirty,
        Func<T, string?> getUserId, Action<T, string> setUserId)
    {
        var owned = rows.Where(r => isDirty(r) && (getUserId(r) is null || getUserId(r) == userId)).ToList();
        foreach (var row in owned.Where(r => getUserId(r) is null)) setUserId(row, userId);
        return owned;
    }

    private async Task<bool> PushTaskListsAsync(string userId, List<string> overwrittenTitles, CancellationToken ct)
    {
        var dirty = SelectOwnedDirty(_taskService.Store.Lists, userId, l => l.Dirty, l => l.UserId, (l, id) => l.UserId = id);
        if (dirty.Count == 0) return false;

        var dtos = dirty.Select(l => new TaskListDto
        {
            Id = l.Id,
            UserId = userId,
            Title = l.Title,
            Icon = l.Icon,
            Color = l.Color,
            OrderIndex = l.OrderIndex,
            HomeOrderIndex = l.HomeOrderIndex,
            IsDependencyChain = l.IsDependencyChain,
            IsDeleted = l.IsDeleted,
            DeletedAt = PolarWire.ToEpochMsOrNull(l.DeletedAtUtc),
            CreatedAt = PolarWire.ToEpochMs(l.CreatedAtUtc),
            UpdatedAt = PolarWire.ToEpochMs(l.UpdatedAtUtc)
        }).ToList();

        var sentUpdatedAt = dtos.ToDictionary(d => d.Id, d => d.UpdatedAt);
        var results = await _rest.UpsertAsync("task_lists", dtos, ct);
        var changed = false;

        foreach (var result in results)
        {
            var local = dirty.FirstOrDefault(l => l.Id == result.Id);
            if (local is null || !sentUpdatedAt.TryGetValue(result.Id, out var sent)) continue;

            // If the user edited this row again while the push was in flight, local.UpdatedAtUtc has
            // already moved past what we sent — leave Dirty alone so the newer edit gets pushed on the
            // next cycle instead of being silently swallowed by clearing the flag here.
            if (PolarWire.ToEpochMs(local.UpdatedAtUtc) != sent) continue;

            if (result.UpdatedAt == sent)
            {
                local.Dirty = false;
            }
            else
            {
                // Lost the LWW conflict — someone else's write is newer; adopt it, but tell the user their
                // edit to local.Title (captured before it's overwritten below) was discarded (Fase 9,
                // docs/analisis-proyecto/roadmap.md) instead of silently vanishing.
                overwrittenTitles.Add(local.Title);
                ApplyTaskListDto(local, result);
                local.Dirty = false;
            }
            changed = true;
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PushTasksAsync(string userId, List<string> overwrittenTitles, CancellationToken ct)
    {
        var dirty = SelectOwnedDirty(_taskService.Store.Tasks, userId, t => t.Dirty, t => t.UserId, (t, id) => t.UserId = id);
        if (dirty.Count == 0) return false;

        var dtos = dirty.Select(t => new TaskDto
        {
            Id = t.Id,
            UserId = userId,
            ListId = t.ListId,
            Title = t.Title,
            Description = t.Description,
            Completed = t.Completed,
            Tags = t.Tags,
            DueDate = PolarWire.ToEpochMsOrNull(t.DueDateUtc),
            OrderIndex = t.OrderIndex,
            Recurrence = PolarWire.RecurrenceToWire(t.Recurrence),
            Priority = t.Priority,
            ImagePath = t.ImagePath,
            TimeEstimate = t.TimeEstimate,
            IsDeleted = t.IsDeleted,
            DeletedAt = PolarWire.ToEpochMsOrNull(t.DeletedAtUtc),
            CreatedAt = PolarWire.ToEpochMs(t.CreatedAtUtc),
            UpdatedAt = PolarWire.ToEpochMs(t.UpdatedAtUtc)
        }).ToList();

        var sentUpdatedAt = dtos.ToDictionary(d => d.Id, d => d.UpdatedAt);
        var results = await _rest.UpsertAsync("tasks", dtos, ct);
        var changed = false;

        foreach (var result in results)
        {
            var local = dirty.FirstOrDefault(t => t.Id == result.Id);
            if (local is null || !sentUpdatedAt.TryGetValue(result.Id, out var sent)) continue;

            // See PushTaskListsAsync — skip if a newer local edit landed while this push was in flight.
            if (PolarWire.ToEpochMs(local.UpdatedAtUtc) != sent) continue;

            if (result.UpdatedAt == sent) local.Dirty = false;
            else { overwrittenTitles.Add(local.Title); ApplyTaskDto(local, result); local.Dirty = false; }
            changed = true;
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PushSubtasksAsync(string userId, List<string> overwrittenTitles, CancellationToken ct)
    {
        var dirty = SelectOwnedDirty(_taskService.Store.Subtasks, userId, s => s.Dirty, s => s.UserId, (s, id) => s.UserId = id);
        if (dirty.Count == 0) return false;

        var dtos = dirty.Select(s => new SubtaskDto
        {
            Id = s.Id,
            UserId = userId,
            TaskId = s.TaskId,
            Title = s.Title,
            Completed = s.Completed,
            DueDate = PolarWire.ToEpochMsOrNull(s.DueDateUtc),
            OrderIndex = s.OrderIndex,
            IsDeleted = s.IsDeleted,
            DeletedAt = PolarWire.ToEpochMsOrNull(s.DeletedAtUtc),
            CreatedAt = PolarWire.ToEpochMs(s.CreatedAtUtc),
            UpdatedAt = PolarWire.ToEpochMs(s.UpdatedAtUtc)
        }).ToList();

        var sentUpdatedAt = dtos.ToDictionary(d => d.Id, d => d.UpdatedAt);
        var results = await _rest.UpsertAsync("subtasks", dtos, ct);
        var changed = false;

        foreach (var result in results)
        {
            var local = dirty.FirstOrDefault(s => s.Id == result.Id);
            if (local is null || !sentUpdatedAt.TryGetValue(result.Id, out var sent)) continue;

            // See PushTaskListsAsync — skip if a newer local edit landed while this push was in flight.
            if (PolarWire.ToEpochMs(local.UpdatedAtUtc) != sent) continue;

            if (result.UpdatedAt == sent) local.Dirty = false;
            else { overwrittenTitles.Add(local.Title); ApplySubtaskDto(local, result); local.Dirty = false; }
            changed = true;
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PushRemindersAsync(string userId, List<string> overwrittenTitles, CancellationToken ct)
    {
        var dirty = SelectOwnedDirty(_reminderService.Reminders, userId, r => r.Dirty, r => r.UserId, (r, id) => r.UserId = id);
        if (dirty.Count == 0) return false;

        var dtos = dirty.Select(r => new ReminderDto
        {
            Id = r.Id,
            UserId = userId,
            Title = r.Title,
            Description = r.Description,
            DateTime = PolarWire.ToEpochMs(r.NotifyAtUtc),
            IsCompleted = r.IsCompleted,
            Latitude = r.Latitude,
            Longitude = r.Longitude,
            Radius = r.Radius,
            LocationName = r.LocationName,
            IsDeleted = r.IsDeleted,
            DeletedAt = PolarWire.ToEpochMsOrNull(r.DeletedAtUtc),
            CreatedAt = PolarWire.ToEpochMs(r.CreatedUtc),
            UpdatedAt = PolarWire.ToEpochMs(r.UpdatedAtUtc)
        }).ToList();

        var sentUpdatedAt = dtos.ToDictionary(d => d.Id, d => d.UpdatedAt);
        var results = await _rest.UpsertAsync("reminders", dtos, ct);
        var changed = false;

        foreach (var result in results)
        {
            var local = dirty.FirstOrDefault(r => r.Id == result.Id);
            if (local is null || !sentUpdatedAt.TryGetValue(result.Id, out var sent)) continue;

            // See PushTaskListsAsync — skip if a newer local edit landed while this push was in flight.
            if (PolarWire.ToEpochMs(local.UpdatedAtUtc) != sent) continue;

            if (result.UpdatedAt == sent) local.Dirty = false;
            else { overwrittenTitles.Add(local.Title); ApplyReminderDto(local, result); local.Dirty = false; }
            changed = true;
        }

        if (changed) _reminderService.Save();
        return changed;
    }

    // ===================== Pull =====================
    // Order matters for a fresh local install: lists before tasks before subtasks, so a task pulled in
    // already has its parent list on disk (docs/supabase-sync/04). Reminders are independent, pulled last.

    private async Task<bool> PullTaskListsAsync(long cursor, CancellationToken ct)
    {
        var remote = await _rest.SelectUpdatedSinceAsync<TaskListDto>("task_lists", cursor, ct);
        if (remote.Count == 0) return false;

        var changed = false;
        foreach (var dto in remote)
        {
            var local = _taskService.Store.Lists.FirstOrDefault(l => l.Id == dto.Id);
            if (local is null)
            {
                var created = NewTaskListFromDto(dto);
                created.UserId = _auth.UserId;
                _taskService.Store.Lists.Add(created);
                changed = true;
            }
            else if (dto.UpdatedAt > PolarWire.ToEpochMs(local.UpdatedAtUtc))
            {
                ApplyTaskListDto(local, dto);
                local.Dirty = false;
                // A row Enki can see via RLS necessarily belongs to the linked account — stamping it here
                // also self-heals any legacy row that predates the UserId field (docs/analisis-proyecto/
                // roadmap.md, Fase 4).
                local.UserId = _auth.UserId;
                changed = true;
            }
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PullTasksAsync(long cursor, CancellationToken ct)
    {
        var remote = await _rest.SelectUpdatedSinceAsync<TaskDto>("tasks", cursor, ct);
        if (remote.Count == 0) return false;

        var changed = false;
        foreach (var dto in remote)
        {
            var local = _taskService.Store.Tasks.FirstOrDefault(t => t.Id == dto.Id);
            if (local is null)
            {
                var created = NewTaskFromDto(dto);
                created.UserId = _auth.UserId;
                _taskService.Store.Tasks.Add(created);
                changed = true;
            }
            else if (dto.UpdatedAt > PolarWire.ToEpochMs(local.UpdatedAtUtc))
            {
                ApplyTaskDto(local, dto);
                local.Dirty = false;
                local.UserId = _auth.UserId;
                changed = true;
            }
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PullSubtasksAsync(long cursor, CancellationToken ct)
    {
        var remote = await _rest.SelectUpdatedSinceAsync<SubtaskDto>("subtasks", cursor, ct);
        if (remote.Count == 0) return false;

        var changed = false;
        foreach (var dto in remote)
        {
            var local = _taskService.Store.Subtasks.FirstOrDefault(s => s.Id == dto.Id);
            if (local is null)
            {
                var created = NewSubtaskFromDto(dto);
                created.UserId = _auth.UserId;
                _taskService.Store.Subtasks.Add(created);
                changed = true;
            }
            else if (dto.UpdatedAt > PolarWire.ToEpochMs(local.UpdatedAtUtc))
            {
                ApplySubtaskDto(local, dto);
                local.Dirty = false;
                local.UserId = _auth.UserId;
                changed = true;
            }
        }

        if (changed) _taskService.Save();
        return changed;
    }

    private async Task<bool> PullRemindersAsync(long cursor, CancellationToken ct)
    {
        var remote = await _rest.SelectUpdatedSinceAsync<ReminderDto>("reminders", cursor, ct);
        if (remote.Count == 0) return false;

        var changed = false;
        foreach (var dto in remote)
        {
            var local = _reminderService.Reminders.FirstOrDefault(r => r.Id == dto.Id);
            if (local is null)
            {
                var created = NewReminderFromDto(dto);
                created.UserId = _auth.UserId;
                _reminderService.Reminders.Insert(0, created);
                changed = true;
            }
            else if (dto.UpdatedAt > PolarWire.ToEpochMs(local.UpdatedAtUtc))
            {
                ApplyReminderDto(local, dto);
                local.Dirty = false;
                local.UserId = _auth.UserId;
                changed = true;
            }
        }

        if (changed) _reminderService.Save();
        return changed;
    }

    // ===================== Dto <-> local model mapping =====================

    internal static PolarTaskList NewTaskListFromDto(TaskListDto dto)
    {
        var list = new PolarTaskList { Id = dto.Id, Title = dto.Title };
        ApplyTaskListDto(list, dto);
        list.Dirty = false;
        return list;
    }

    internal static void ApplyTaskListDto(PolarTaskList list, TaskListDto dto)
    {
        list.Title = dto.Title;
        list.Icon = dto.Icon;
        list.Color = dto.Color;
        list.OrderIndex = dto.OrderIndex;
        list.HomeOrderIndex = dto.HomeOrderIndex;
        list.IsDependencyChain = dto.IsDependencyChain;
        list.IsDeleted = dto.IsDeleted;
        list.DeletedAtUtc = PolarWire.FromEpochMsOrNull(dto.DeletedAt);
        list.CreatedAtUtc = PolarWire.FromEpochMs(dto.CreatedAt);
        list.UpdatedAtUtc = PolarWire.FromEpochMs(dto.UpdatedAt);
    }

    internal static PolarTask NewTaskFromDto(TaskDto dto)
    {
        var task = new PolarTask { Id = dto.Id, ListId = dto.ListId, Title = dto.Title };
        ApplyTaskDto(task, dto);
        task.Dirty = false;
        return task;
    }

    internal static void ApplyTaskDto(PolarTask task, TaskDto dto)
    {
        task.ListId = dto.ListId;
        task.Title = dto.Title;
        task.Description = dto.Description;
        task.Completed = dto.Completed;
        task.Tags = dto.Tags.ToList();
        task.DueDateUtc = PolarWire.FromEpochMsOrNull(dto.DueDate);
        task.OrderIndex = dto.OrderIndex;
        task.Recurrence = PolarWire.RecurrenceFromWire(dto.Recurrence);
        task.Priority = dto.Priority;
        task.ImagePath = dto.ImagePath;
        task.TimeEstimate = dto.TimeEstimate;
        task.IsDeleted = dto.IsDeleted;
        task.DeletedAtUtc = PolarWire.FromEpochMsOrNull(dto.DeletedAt);
        task.CreatedAtUtc = PolarWire.FromEpochMs(dto.CreatedAt);
        task.UpdatedAtUtc = PolarWire.FromEpochMs(dto.UpdatedAt);
    }

    internal static PolarSubtask NewSubtaskFromDto(SubtaskDto dto)
    {
        var subtask = new PolarSubtask { Id = dto.Id, TaskId = dto.TaskId, Title = dto.Title };
        ApplySubtaskDto(subtask, dto);
        subtask.Dirty = false;
        return subtask;
    }

    internal static void ApplySubtaskDto(PolarSubtask subtask, SubtaskDto dto)
    {
        subtask.TaskId = dto.TaskId;
        subtask.Title = dto.Title;
        subtask.Completed = dto.Completed;
        subtask.DueDateUtc = PolarWire.FromEpochMsOrNull(dto.DueDate);
        subtask.OrderIndex = dto.OrderIndex;
        subtask.IsDeleted = dto.IsDeleted;
        subtask.DeletedAtUtc = PolarWire.FromEpochMsOrNull(dto.DeletedAt);
        subtask.CreatedAtUtc = PolarWire.FromEpochMs(dto.CreatedAt);
        subtask.UpdatedAtUtc = PolarWire.FromEpochMs(dto.UpdatedAt);
    }

    internal static Reminder NewReminderFromDto(ReminderDto dto)
    {
        var reminder = new Reminder { Id = dto.Id, Title = dto.Title, NotifyAtUtc = PolarWire.FromEpochMs(dto.DateTime) };
        ApplyReminderDto(reminder, dto);
        reminder.Dirty = false;
        return reminder;
    }

    internal static void ApplyReminderDto(Reminder reminder, ReminderDto dto)
    {
        var notifyAtUtc = PolarWire.FromEpochMs(dto.DateTime);
        // Si el servidor mueve la fecha (reprogramación desde Polar/Android), el recordatorio tiene que
        // rearmarse: IsTriggered es un flag local de "ya notificado" que nunca viaja por el wire, y dejarlo
        // en true con una fecha nueva haría que no volviera a notificar jamás pese a mostrarse como
        // "Programado" (docs/analisis-proyecto/roadmap.md, Fase 2). Solo se rearma cuando la fecha cambia
        // de verdad — una edición de título/nota del servidor no debe provocar una segunda notificación.
        if (reminder.NotifyAtUtc != notifyAtUtc) reminder.IsTriggered = false;

        reminder.Title = dto.Title;
        reminder.Description = dto.Description;
        reminder.NotifyAtUtc = notifyAtUtc;
        reminder.IsCompleted = dto.IsCompleted;
        reminder.Latitude = dto.Latitude;
        reminder.Longitude = dto.Longitude;
        reminder.Radius = dto.Radius;
        reminder.LocationName = dto.LocationName;
        reminder.IsDeleted = dto.IsDeleted;
        reminder.DeletedAtUtc = PolarWire.FromEpochMsOrNull(dto.DeletedAt);
        reminder.CreatedUtc = PolarWire.FromEpochMs(dto.CreatedAt);
        reminder.UpdatedAtUtc = PolarWire.FromEpochMs(dto.UpdatedAt);
    }
}
