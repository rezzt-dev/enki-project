using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="ReminderService"/> — creation, read/mark-all-read, clearing, persistence
/// across instances, and <see cref="ReminderService.PopDueReminders"/> (the piece
/// <c>MainWindowViewModel</c>'s scheduler relies on to fire each due reminder exactly once). Each test
/// points storage at its own throwaway temp directory, same pattern as
/// <see cref="NotificationServiceTests"/>, instead of the developer's real <c>%AppData%/Enki</c>.</summary>
public class ReminderServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ReminderService CreateService() => new(_storageDirectory);

    /// <summary>Fase 1 (docs/analisis-proyecto/roadmap.md): a reminders.json that exists but fails to parse
    /// must be distinguishable from a fresh install, same pattern as
    /// <see cref="SettingsService.LoadedFromCorruptData"/>.</summary>
    [Fact]
    public void Load_CorruptFile_FallsBackToEmptyList_AndFlagsCorruption()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "reminders.json"), "{ not valid json ");

        var service = CreateService();

        Assert.Empty(service.Reminders);
        Assert.True(service.LoadedFromCorruptData);
    }

    [Fact]
    public void Create_InsertsAtTheFront()
    {
        var service = CreateService();

        service.Create("Primero", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        service.Create("Segundo", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        Assert.Equal("Segundo", service.Reminders[0].Title);
        Assert.Equal("Primero", service.Reminders[1].Title);
    }

    [Fact]
    public void Create_PersistsAcrossInstances()
    {
        var first = CreateService();
        var notifyAt = new DateTime(2030, 1, 1, 10, 30, 0, DateTimeKind.Utc);
        first.Create("Persistente", "Detalle", notifyAt, "FlagOutline");

        var second = CreateService();

        Assert.Single(second.Reminders);
        Assert.Equal("Persistente", second.Reminders[0].Title);
        Assert.Equal("Detalle", second.Reminders[0].Description);
        Assert.Equal(notifyAt, second.Reminders[0].NotifyAtUtc);
        Assert.Equal("FlagOutline", second.Reminders[0].Icon);
    }

    [Fact]
    public void MarkAsRead_OnlyAffectsTheGivenReminder()
    {
        var service = CreateService();
        var a = service.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        var b = service.Create("B", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        service.MarkAsRead(a.Id);

        Assert.True(service.Reminders.Find(r => r.Id == a.Id)!.IsRead);
        Assert.False(service.Reminders.Find(r => r.Id == b.Id)!.IsRead);
    }

    [Fact]
    public void MarkAllAsRead_MarksEveryReminder()
    {
        var service = CreateService();
        service.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        service.Create("B", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        service.MarkAllAsRead();

        Assert.All(service.Reminders, r => Assert.True(r.IsRead));
    }

    /// <summary>Delete is a tombstone soft-delete (docs/supabase-sync/03/04), not a physical removal — the
    /// row stays in <see cref="ReminderService.Reminders"/> (so a pending Polar sync can still push the
    /// tombstone) but disappears from <see cref="ReminderService.ActiveReminders"/>, which is what every UI
    /// surface reads.</summary>
    [Fact]
    public void Delete_SoftDeletesOnlyTheGivenReminder()
    {
        var service = CreateService();
        var a = service.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        service.Create("B", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        service.Delete(a.Id);

        Assert.Equal(2, service.Reminders.Count);
        Assert.True(service.Reminders.Find(r => r.Id == a.Id)!.IsDeleted);
        Assert.Single(service.ActiveReminders);
        Assert.Equal("B", service.ActiveReminders.Single().Title);
    }

    /// <summary>The Papelera's "Restaurar" action (docs/analisis-proyecto/05-roadmap.md Fase 6) — undoes a
    /// soft-delete and marks the reminder dirty again so the un-delete itself gets pushed on the next
    /// sync, same as any other field change.</summary>
    [Fact]
    public void Restore_ClearsTombstoneAndMarksDirty()
    {
        var service = CreateService();
        var a = service.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        service.Delete(a.Id);
        service.Reminders.Find(r => r.Id == a.Id)!.Dirty = false; // simulate "already synced" before restoring

        service.Restore(a.Id);

        var restored = service.Reminders.Find(r => r.Id == a.Id)!;
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DeletedAtUtc);
        Assert.True(restored.Dirty);
        Assert.Contains(a.Id, service.ActiveReminders.Select(r => r.Id));
    }

    [Fact]
    public void Restore_OnAnActiveReminder_IsANoOp()
    {
        var service = CreateService();
        var a = service.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        service.Restore(a.Id);

        Assert.False(service.Reminders.Find(r => r.Id == a.Id)!.IsDeleted);
    }

    [Fact]
    public void DeletedReminders_OnlyExposesSoftDeletedOnes()
    {
        var service = CreateService();
        var active = service.Create("Activo", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        var deleted = service.Create("Borrado", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        service.Delete(deleted.Id);

        Assert.Equal(deleted.Id, service.DeletedReminders.Single().Id);
        Assert.DoesNotContain(active.Id, service.DeletedReminders.Select(r => r.Id));
    }

    [Fact]
    public void ClearAll_SoftDeletesEveryReminderAndPersists()
    {
        var first = CreateService();
        first.Create("A", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        first.ClearAll();

        Assert.Empty(first.ActiveReminders);
        Assert.Empty(CreateService().ActiveReminders);
    }

    [Fact]
    public void PurgeAllDeleted_RemovesEverySoftDeletedReminderAndPersists()
    {
        var first = CreateService();
        var active = first.Create("Activo", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        var deleted = first.Create("Borrado", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        first.Delete(deleted.Id);

        first.PurgeAllDeleted();

        Assert.Single(first.Reminders);
        Assert.Equal(active.Id, first.Reminders[0].Id);
        Assert.Empty(first.DeletedReminders);
        Assert.Empty(CreateService().DeletedReminders);
    }

    [Fact]
    public void PopDueReminders_ReturnsOnlyPastDueAndOnlyOnce()
    {
        var service = CreateService();
        var past = service.Create("Vencido", "", DateTime.UtcNow.AddMinutes(-5), "BellRingOutline");
        service.Create("Futuro", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        var firstPop = service.PopDueReminders();
        Assert.Single(firstPop);
        Assert.Equal(past.Id, firstPop[0].Id);
        Assert.True(service.Reminders.Find(r => r.Id == past.Id)!.IsTriggered);

        var secondPop = service.PopDueReminders();
        Assert.Empty(secondPop);
    }

    // ===================== Recurrencia local (Fase 10) =====================

    /// <summary>Fase 10 (docs/analisis-proyecto/roadmap.md — "recurrencia nativa a Reminder"): un
    /// recordatorio recurrente vencido no debe quedar marcado <see cref="Reminder.IsTriggered"/> para
    /// siempre como uno normal — se reprograma solo a su siguiente ocurrencia y se deja armado, igual que
    /// <see cref="ReminderService.Reschedule"/> ya rearma un recordatorio arrastrado a otra fecha.</summary>
    [Fact]
    public void PopDueReminders_RecurringReminder_AdvancesToNextOccurrence_InsteadOfStayingTriggered()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var originalNotifyAt = now.AddMinutes(-5);
        var reminder = service.Create("Diario", "", originalNotifyAt, "BellRingOutline",
            recurrence: PolarRecurrence.Daily);

        var firstPop = service.PopDueReminders(now);
        Assert.Single(firstPop);
        Assert.Equal(reminder.Id, firstPop[0].Id);

        // reminder y stored son la misma instancia (Reminders.Insert guarda la referencia devuelta por
        // Create) — por eso originalNotifyAt se capturó ANTES de PopDueReminders en vez de leerlo de
        // reminder.NotifyAtUtc después, que ya reflejaría el valor mutado.
        var stored = service.Reminders.Single(r => r.Id == reminder.Id);
        Assert.False(stored.IsTriggered);
        Assert.True(stored.NotifyAtUtc > now);
        // Roughly a day later — not asserting an exact value, since the recurrence engine preserves local
        // wall-clock time across the (machine-local) timezone, same DST-aware arithmetic already covered
        // exhaustively by PolarTaskServiceTests.CalculateNextDueDate.
        Assert.InRange(stored.NotifyAtUtc - originalNotifyAt, TimeSpan.FromHours(22), TimeSpan.FromHours(26));

        // La nueva fecha aún no ha llegado: no vuelve a dispararse en la misma comprobación.
        Assert.Empty(service.PopDueReminders(now));
    }

    /// <summary>Varios días sin abrir la app: el motor debe alcanzar la primera ocurrencia futura de un
    /// tirón (mismo criterio que <c>PolarTaskService.RollRecurringTasks</c>), no quedarse en la primera
    /// ocurrencia ya vencida también.</summary>
    [Fact]
    public void PopDueReminders_RecurringReminder_CatchesUpMultipleMissedOccurrencesAtOnce()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var reminder = service.Create("Diario", "", now.AddDays(-3), "BellRingOutline",
            recurrence: PolarRecurrence.Daily);

        var pop = service.PopDueReminders(now);
        Assert.Single(pop);

        var stored = service.Reminders.Single(r => r.Id == reminder.Id);
        Assert.False(stored.IsTriggered);
        Assert.True(stored.NotifyAtUtc > now, "la siguiente ocurrencia debe quedar en el futuro, no seguir vencida");
    }

    /// <summary>Un recordatorio sin recurrencia (el caso por defecto de <see cref="ReminderService.Create"/>
    /// sin el parámetro opcional) conserva el comportamiento preexistente: se marca disparado y no se
    /// reprograma solo.</summary>
    [Fact]
    public void PopDueReminders_NonRecurringReminder_StillJustMarksTriggered()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var notifyAt = now.AddMinutes(-5);
        var reminder = service.Create("Único", "", notifyAt, "BellRingOutline");

        Assert.Single(service.PopDueReminders(now));

        var stored = service.Reminders.Single(r => r.Id == reminder.Id);
        Assert.True(stored.IsTriggered);
        Assert.Equal(notifyAt, stored.NotifyAtUtc);
    }

    /// <summary>Fase 2 (docs/analisis-proyecto/roadmap.md): reprogramar un recordatorio ya disparado tiene
    /// que rearmarlo — el escenario del análisis era un recordatorio vencido arrastrado a otra fecha en el
    /// calendario que se mostraba como "Programado" pero no volvía a notificar jamás, porque
    /// <see cref="Reminder.IsTriggered"/> seguía en <c>true</c>.</summary>
    [Fact]
    public void Reschedule_AfterTrigger_ReArmsAndFiresAgainAtTheNewDate()
    {
        var service = CreateService();
        var reminder = service.Create("Vencido", "", DateTime.UtcNow.AddMinutes(-5), "BellRingOutline");

        var firstPop = service.PopDueReminders();
        Assert.Single(firstPop);
        Assert.True(service.Reminders[0].IsTriggered);

        var newDate = DateTime.UtcNow.AddHours(2);
        service.Reschedule(reminder.Id, newDate);
        Assert.False(service.Reminders[0].IsTriggered);

        // Todavía no ha llegado la nueva fecha: no dispara.
        Assert.Empty(service.PopDueReminders());

        // En la nueva fecha vuelve a disparar. Se usa una instancia nueva (proceso recién arrancado, sin
        // checkpoint de la guarda de saltos de reloj) con el instante inyectado vía el overload interno —
        // además confirma que el rearme se persistió en disco.
        var reloaded = CreateService();
        var secondPop = reloaded.PopDueReminders(newDate);
        Assert.Single(secondPop);
        Assert.Equal(reminder.Id, secondPop[0].Id);
    }

    // ===================== Guarda contra saltos de reloj (Fase 2) =====================

    [Fact]
    public void PopDueReminders_ForwardClockJump_DoesNotBulkFireWhatTheJumpMadeDue()
    {
        var service = CreateService();
        var t0 = DateTime.UtcNow;
        var dueInTheGap = service.Create("Durante el hueco", "", t0.AddHours(1), "BellRingOutline");

        Assert.Empty(service.PopDueReminders(t0)); // nada vencido aún; fija el checkpoint

        // El reloj salta 2 h hacia adelante con la app viva: el recordatorio que el salto deja "vencido"
        // no se dispara en bloque ni queda marcado como disparado con una hora de sistema no fiable.
        var jumpPop = service.PopDueReminders(t0.AddHours(2));
        Assert.Empty(jumpPop);
        Assert.False(service.Reminders.Single(r => r.Id == dueInTheGap.Id).IsTriggered);

        // En el siguiente ciclo normal (30 s después) el scheduler vuelve a confiar en el reloj del
        // sistema y dispara lo vencido — la guarda solo evita el disparo masivo en el instante del salto.
        var nextCycle = service.PopDueReminders(t0.AddHours(2).AddSeconds(30));
        Assert.Single(nextCycle);
        Assert.Equal(dueInTheGap.Id, nextCycle[0].Id);
    }

    [Fact]
    public void PopDueReminders_ForwardClockJump_StillFiresWhatWasAlreadyOverdueAtTheLastCheck()
    {
        var service = CreateService();
        var t0 = DateTime.UtcNow;
        Assert.Empty(service.PopDueReminders(t0)); // fija el checkpoint

        // Un recordatorio creado ya vencido (fecha en pasado) justo antes del salto: ese sí estaba
        // genuinamente pendiente con el scheduler vivo y debe dispararse incluso en el ciclo del salto.
        var overdue = service.Create("Ya vencido", "", t0.AddMinutes(-1), "BellRingOutline");
        var dueInTheGap = service.Create("Durante el hueco", "", t0.AddHours(1), "BellRingOutline");

        var jumpPop = service.PopDueReminders(t0.AddHours(2));

        Assert.Single(jumpPop);
        Assert.Equal(overdue.Id, jumpPop[0].Id);
        Assert.False(service.Reminders.Single(r => r.Id == dueInTheGap.Id).IsTriggered);
    }

    [Fact]
    public void PopDueReminders_BackwardClockJump_DoesNotEngageTheGuard()
    {
        var service = CreateService();
        var t0 = DateTime.UtcNow;
        Assert.Empty(service.PopDueReminders(t0)); // fija el checkpoint
        var reminder = service.Create("Vencido", "", t0.AddHours(-3), "BellRingOutline");

        // El reloj se retrasa 2 h: no es un salto hacia adelante, la guarda no aplica y lo vencido
        // respecto a la hora (corregida) actual dispara con normalidad.
        var pop = service.PopDueReminders(t0.AddHours(-2));

        Assert.Single(pop);
        Assert.Equal(reminder.Id, pop[0].Id);
    }

    [Fact]
    public void PopDueReminders_FirstCheckAfterLaunch_StillCatchesUpWithoutGuard()
    {
        var service = CreateService();
        var reminder = service.Create("Vencido con la app cerrada", "", DateTime.UtcNow.AddDays(-2), "BellRingOutline");

        // Primera comprobación del proceso: sin checkpoint previo no hay guarda — pescar al vuelo lo
        // vencido mientras la app estaba cerrada sigue siendo el comportamiento deseado.
        var pop = service.PopDueReminders();

        Assert.Single(pop);
        Assert.Equal(reminder.Id, pop[0].Id);
    }

    // ===================== Aislamiento de cuentas — UserId (Fase 4) =====================

    [Fact]
    public void Create_StampsUserId_WithTheCurrentlyLinkedAccount()
    {
        var service = new ReminderService(_storageDirectory, currentUserIdProvider: () => "user-a");

        var reminder = service.Create("Recordatorio", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        Assert.Equal("user-a", reminder.UserId);
    }

    [Fact]
    public void Create_WithNoAccountLinked_LeavesUserIdNull_SoAPurelyLocalReminderIsUnaffected()
    {
        var service = CreateService();

        var reminder = service.Create("Recordatorio local", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        Assert.Null(reminder.UserId);
    }

    /// <summary>Same rule as <c>PolarTaskService</c>: editing a reminder owned by account A while account B
    /// is linked must not hand it over to B (docs/analisis-proyecto/roadmap.md, Fase 4).</summary>
    [Fact]
    public void ToggleCompleted_WhileADifferentAccountIsLinked_DoesNotReassignOwnership()
    {
        var currentUser = "user-a";
        var service = new ReminderService(_storageDirectory, currentUserIdProvider: () => currentUser);
        var reminder = service.Create("Recordatorio", "", DateTime.UtcNow.AddDays(1), "BellRingOutline");
        Assert.Equal("user-a", reminder.UserId);

        currentUser = "user-b";
        service.ToggleCompleted(reminder.Id);

        Assert.Equal("user-a", reminder.UserId);
        Assert.True(reminder.Dirty);
    }
}
