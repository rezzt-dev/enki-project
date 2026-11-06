using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Persists user-created reminders to <c>&lt;vault&gt;/.tasks/reminders.json</c> — same load/save shape
/// as <see cref="NotificationService"/>. Kept deliberately "dumb": it doesn't know about
/// <see cref="NotificationService"/> or toasts, it just tracks which reminders exist and which of them have
/// come due (see <see cref="PopDueReminders"/>) — the shell (<c>MainWindowViewModel</c>) is the one that
/// wires a due reminder into a real notification, same orchestration role it already plays for every other
/// cross-service interaction in the app.</summary>
public sealed class ReminderService
{
    private readonly string? _storageDirectory;
    private readonly VaultService? _vaultService;
    private string _path => _vaultService is not null
        ? VaultDataStorage.FilePath(_vaultService, "reminders.json")
        : Path.Combine(_storageDirectory!, "reminders.json");
    private readonly Func<string?> _currentUserIdProvider;

    public List<Reminder> Reminders { get; private set; }

    /// <summary>True cuando <c>reminders.json</c> existía pero no se pudo leer o deserializar — la lista se
    /// reemplazó por una vacía. Mismo patrón que <see cref="KanbanService.LoadedFromCorruptData"/>
    /// (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    public bool LoadedFromCorruptData { get; private set; }

    /// <summary>Raised after every successful <see cref="Save"/> — same role as
    /// <see cref="KanbanService.BoardChanged"/>/<see cref="PolarTaskService.DataChanged"/>, added so the
    /// calendar (docs/calendar-feature/11-arquitectura-tecnica.md §5.1) can stay live when a reminder is
    /// created/edited/completed/deleted from any screen, including the calendar itself.</summary>
    public event Action? RemindersChanged;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, lets tests point persistence at a throwaway directory (see enki.Tests).</param>
    /// <param name="currentUserIdProvider">Returns the Supabase user id of the currently linked Polar
    /// account (or <see langword="null"/> if none is linked) — the composition root wires this to
    /// <c>PolarAuthService.UserId</c>. Same account-isolation stamping as <c>PolarTaskService</c>
    /// (docs/analisis-proyecto/roadmap.md, Fase 4); defaults to "always null" for callers that don't care.</param>
    public ReminderService(string? storageDirectory = null, Func<string?>? currentUserIdProvider = null)
    {
        _storageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_storageDirectory);
        _currentUserIdProvider = currentUserIdProvider ?? (() => null);
        Reminders = Load();
        PurgeOldTombstones();
    }

    public ReminderService(VaultService vaultService, Func<string?>? currentUserIdProvider = null)
    {
        _vaultService = vaultService;
        _currentUserIdProvider = currentUserIdProvider ?? (() => null);
        Reminders = Load();
        PurgeOldTombstones();
    }

    /// <summary>Recarga los recordatorios del vault activo.</summary>
    public void Reload()
    {
        LoadedFromCorruptData = false;
        Reminders = Load();
        PurgeOldTombstones();
        RemindersChanged?.Invoke();
    }

    /// <summary>Non-deleted reminders — every UI consumer (the Recordatorios page, the due-reminder
    /// poller) should read through this instead of <see cref="Reminders"/> directly, same as any other
    /// soft-deleted collection in Enki.</summary>
    public IEnumerable<Reminder> ActiveReminders => Reminders.Where(r => !r.IsDeleted);

    /// <summary>Soft-deleted reminders not yet purged by <see cref="PurgeOldTombstones"/> — read by the
    /// Papelera's "Recordatorios" category (<c>TrashViewModel</c>) so a deleted reminder is recoverable
    /// the same way a deleted note or Kanban card already is.</summary>
    public IEnumerable<Reminder> DeletedReminders => Reminders.Where(r => r.IsDeleted);

    private List<Reminder> Load()
    {
        if (JsonFileStore.TryLoad<List<Reminder>>(_path, out var loaded))
        {
            // Ver PolarTaskService.NormalizeRecurrence: un valor de enum fuera de rango en el JSON no es
            // un error de deserialización para System.Text.Json, pero sí lo es para todo lo que después
            // hace switch sobre él.
            foreach (var reminder in loaded!) reminder.Recurrence = PolarTaskService.NormalizeRecurrence(reminder.Recurrence);
            return loaded;
        }

        if (File.Exists(_path)) LoadedFromCorruptData = true;
        return [];
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        JsonFileStore.Save(_path, Reminders);
        RemindersChanged?.Invoke();
    }

    public Reminder Create(string title, string description, DateTime notifyAtUtc, string icon,
        int? durationMinutes = null, PolarRecurrence recurrence = PolarRecurrence.None)
    {
        var reminder = new Reminder
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Description = description,
            NotifyAtUtc = notifyAtUtc,
            DurationMinutes = durationMinutes,
            Icon = icon,
            Recurrence = recurrence,
            UserId = _currentUserIdProvider()
        };

        Reminders.Insert(0, reminder);
        Save();
        return reminder;
    }

    /// <summary>Crea un evento importado visible en el calendario sin programar una notificación.
    /// Se guarda una sola vez, ya marcado como atendido y sin sincronizar a Polar,
    /// para evitar avisos en cualquiera de los dos dispositivos.</summary>
    public Reminder CreateImportedEvent(string title, string description, DateTime startUtc, int? durationMinutes)
    {
        var reminder = new Reminder
        {
            Id = Guid.NewGuid().ToString(), Title = title, Description = description,
            NotifyAtUtc = startUtc, DurationMinutes = durationMinutes,
            Icon = "CalendarBlankOutline", IsTriggered = true, Dirty = false,
            IsImportedCalendarEvent = true
        };
        Reminders.Insert(0, reminder);
        try { Save(); }
        catch { Reminders.Remove(reminder); throw; }
        return reminder;
    }

    public void MarkAsRead(string id)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null || reminder.IsRead) return;

        reminder.IsRead = true;
        Save();
    }

    public void MarkAllAsRead()
    {
        var changed = false;
        foreach (var reminder in Reminders)
        {
            if (reminder.IsRead) continue;
            reminder.IsRead = true;
            changed = true;
        }

        if (changed) Save();
    }

    /// <summary>Soft-deletes (tombstone) rather than physically removing — see
    /// docs/supabase-sync/03/04. The row keeps existing in <c>reminders.json</c> until
    /// <see cref="PurgeOldTombstones"/> clears it out 30 days later, same retention Supabase's own purge
    /// job uses server-side. Every UI surface reads <see cref="ActiveReminders"/> instead of
    /// <see cref="Reminders"/>, so the visible behavior is unchanged.</summary>
    public void Delete(string id)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null || reminder.IsDeleted) return;

        SoftDelete(reminder);
        Save();
    }

    /// <summary>Undoes <see cref="Delete"/> — the Papelera's "Restaurar" action. Clears the tombstone and
    /// marks the reminder dirty again so the un-delete itself gets pushed to Polar on the next sync,
    /// same as any other field change.</summary>
    public void Restore(string id)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null || !reminder.IsDeleted) return;

        reminder.IsDeleted = false;
        reminder.DeletedAtUtc = null;
        Touch(reminder);
        Save();
    }

    public void ClearAll()
    {
        var changed = false;
        foreach (var reminder in ActiveReminders.ToList())
        {
            SoftDelete(reminder);
            changed = true;
        }

        if (changed) Save();
    }

    public void ToggleCompleted(string id)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null) return;

        reminder.IsCompleted = !reminder.IsCompleted;
        Touch(reminder);
        Save();
    }

    /// <summary>Writes a new <see cref="Reminder.NotifyAtUtc"/> for an existing reminder — the path the
    /// calendar's drag &amp; drop (docs/calendar-feature/05-interacciones-y-microinteracciones.md §2) uses
    /// to reschedule, since <see cref="ReminderEditorViewModel"/> only ever creates new reminders and has
    /// no edit mode of its own to reuse here. Reprogramar rearma el recordatorio
    /// (<see cref="Reminder.IsTriggered"/> vuelve a <c>false</c>): sin ese reset, un recordatorio ya
    /// disparado que se arrastra a otra fecha en el calendario se mostraría como "Programado" pero no
    /// volvería a notificar jamás (docs/analisis-proyecto/roadmap.md, Fase 2).</summary>
    public void Reschedule(string id, DateTime notifyAtUtc)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null) return;

        reminder.NotifyAtUtc = notifyAtUtc;
        reminder.IsTriggered = reminder.IsImportedCalendarEvent;
        Touch(reminder);
        Save();
    }

    /// <summary>Actualiza solo la duración de la franja horaria en el calendario. Usado por el gesto de
    /// redimensionado de <see cref="ViewModels.CalendarViewModel"/>, que no debe reescribir la fecha/hora
    /// del recordatorio.</summary>
    public void RescheduleDuration(string id, int? durationMinutes)
    {
        var reminder = Reminders.Find(r => r.Id == id);
        if (reminder is null) return;

        reminder.DurationMinutes = durationMinutes;
        Touch(reminder);
        Save();
    }

    /// <summary>Physically removes every soft-deleted reminder from the local store — used by the
    /// Papelera's section-specific "VACIAR" action. Because the row is dropped locally, any tombstone that
    /// has not yet been pushed to Polar will not be synchronized; the action is therefore only triggered
    /// after an explicit user confirmation.</summary>
    public void PurgeAllDeleted()
    {
        var removed = Reminders.RemoveAll(r => r.IsDeleted);
        if (removed > 0) Save();
    }

    /// <summary>Elimina definitivamente un único recordatorio de la papelera. El asistente solicita antes
    /// confirmación en el chat; si el recordatorio ya no está en la papelera, no se modifica.</summary>
    public void PurgeDeleted(string id)
    {
        if (Reminders.RemoveAll(r => r.Id == id && r.IsDeleted) > 0) Save();
    }

    private void SoftDelete(Reminder reminder)
    {
        reminder.IsDeleted = true;
        Touch(reminder);
        reminder.DeletedAtUtc = reminder.UpdatedAtUtc;
    }

    /// <summary>Marks the reminder dirty and, if unowned yet, claims it for whichever Polar account is
    /// currently linked — see <see cref="PolarTaskService"/>'s identical <c>Touch</c> for the full
    /// account-isolation rationale (docs/analisis-proyecto/roadmap.md, Fase 4). Never reassigns an
    /// already-owned reminder: editing it while a *different* account is linked leaves it dirty but still
    /// tagged with its original owner, so it won't push under the wrong session.</summary>
    private void Touch(Reminder reminder)
    {
        reminder.UpdatedAtUtc = DateTime.UtcNow;
        reminder.Dirty = true;
        reminder.UserId ??= _currentUserIdProvider();
    }

    /// <summary>Mirrors Supabase's own <c>purge_old_tombstones</c> job (docs/supabase-sync/03) locally:
    /// physically drops reminders that were soft-deleted more than 30 days ago and are already confirmed
    /// synced (<c>!Dirty</c>) — never purges a tombstone that hasn't been pushed yet, so an offline delete
    /// always gets at least one chance to sync before disappearing for good.</summary>
    private void PurgeOldTombstones()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var removed = Reminders.RemoveAll(r => r.IsDeleted && !r.Dirty && r.DeletedAtUtc is { } deletedAt && deletedAt < cutoff);
        if (removed > 0) Save();
    }

    /// <summary>Marks and persists every active reminder whose <see cref="Reminder.NotifyAtUtc"/> has
    /// passed and hasn't fired yet, returning them so the caller can turn each into a notification exactly
    /// once.</summary>
    public List<Reminder> PopDueReminders() => PopDueReminders(DateTime.UtcNow);

    /// <summary>Umbral de hueco entre dos comprobaciones a partir del cual se considera que el reloj ha
    /// dado un salto hacia adelante (cambio manual de hora, vuelta de una suspensión larga, depurador
    /// pausado...). El poller del shell llama cada 30 s, así que cualquier hueco mayor es anómalo por
    /// definición.</summary>
    internal static readonly TimeSpan MaxReasonableClockGap = TimeSpan.FromMinutes(15);

    /// <summary>Instante de la última comprobación, solo en memoria: no se persiste a propósito, porque
    /// pescar al vuelo los recordatorios vencidos mientras la app estaba cerrada sí es el comportamiento
    /// deseado (ver el doc de <c>MainWindowViewModel.CheckDueReminders</c>) — la guarda de
    /// <see cref="PopDueReminders(DateTime)"/> solo actúa sobre saltos detectados con la app viva.</summary>
    private DateTime? _lastPopCheckUtc;

    /// <summary>Núcleo de <see cref="PopDueReminders"/> con el instante inyectado — <c>internal</c> para
    /// que los tests puedan simular saltos de reloj de forma determinista (mismo patrón de seam de
    /// testeo que el <c>storageDirectory</c> del constructor). Guarda contra saltos de reloj
    /// hacia adelante (docs/analisis-proyecto/roadmap.md, Fase 2): si desde la última comprobación ha
    /// pasado más de <see cref="MaxReasonableClockGap"/>, no dispara en bloque todo lo que el salto ha
    /// dejado "vencido" — dispara solo lo que ya estaba vencido en la última comprobación (genuinamente
    /// perdido con el scheduler vivo) y deja el resto armado, de modo que cada uno disparará en un ciclo
    /// posterior normal en vez de en una tormenta de avisos, y ninguno queda marcado como disparado por
    /// una hora de sistema que puede no ser de fiar. Sin esa marca, un recordatorio cuya hora real aún no
    /// ha llegado sigue pudiendo notificar si el reloj se corrige de vuelta.</summary>
    internal List<Reminder> PopDueReminders(DateTime nowUtc)
    {
        if (_lastPopCheckUtc is { } lastCheck && nowUtc - lastCheck > MaxReasonableClockGap)
        {
            LogService.Warning(
                $"ReminderService: salto de reloj detectado ({nowUtc - lastCheck:g} desde la última comprobación); " +
                "no se disparan en bloque los recordatorios vencidos durante el hueco.");
            var missed = ActiveReminders.Where(r => !r.IsImportedCalendarEvent && !r.IsTriggered && r.NotifyAtUtc <= lastCheck).ToList();
            _lastPopCheckUtc = nowUtc;
            if (missed.Count == 0) return missed;

            foreach (var reminder in missed) Fire(reminder, nowUtc);
            Save();
            return missed;
        }

        _lastPopCheckUtc = nowUtc;
        var due = ActiveReminders.Where(r => !r.IsImportedCalendarEvent && !r.IsTriggered && r.NotifyAtUtc <= nowUtc).ToList();
        if (due.Count == 0) return due;

        foreach (var reminder in due) Fire(reminder, nowUtc);
        Save();
        return due;
    }

    /// <summary>Un solo recordatorio que acaba de vencer: para uno normal, se marca disparado (comportamiento
    /// preexistente). Para uno recurrente (Fase 10, docs/analisis-proyecto/roadmap.md — "recurrencia nativa
    /// a Reminder"), se reprograma en su lugar a la siguiente ocurrencia y se deja armado
    /// (<see cref="Reminder.IsTriggered"/> en <see langword="false"/>), igual que <see cref="Reschedule"/> ya
    /// rearma un recordatorio arrastrado a otra fecha — así no hace falta recrearlo a mano cada vez.
    /// <c>PolarTaskService.AdvanceUntilAfter</c> avanza más de una ocurrencia si hiciera falta (p. ej. la
    /// app estuvo cerrada varios días), mismo criterio y misma aritmética de avance (con DST) que
    /// <c>PolarTaskService.RollRecurringTasks</c>, en vez de duplicarla.
    ///
    /// <para>Si el avance no es posible —recurrencia desconocida por un <c>reminders.json</c> dañado, o
    /// fecha desbordada— el recordatorio se marca disparado y se comporta como uno normal. Degradar a "una
    /// sola vez" es lo peor que puede pasar aquí; el bucle sin tope que había antes colgaba la app entera
    /// (.docs/auditoria-seguridad-y-fallos-criticos.md, BUG-01).</para></summary>
    private void Fire(Reminder reminder, DateTime nowUtc)
    {
        if (reminder.Recurrence == PolarRecurrence.None ||
            PolarTaskService.AdvanceUntilAfter(reminder.NotifyAtUtc, reminder.Recurrence, nowUtc) is not { } next)
        {
            reminder.IsTriggered = true;
            return;
        }

        reminder.NotifyAtUtc = next;
        Touch(reminder);
    }
}
