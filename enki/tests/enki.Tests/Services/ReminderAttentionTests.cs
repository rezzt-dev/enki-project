using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre cuándo un recordatorio empieza a reclamar atención (<see cref="ReminderAttention"/>):
/// la insignia de «Recordatorios» ya no cuenta uno del año que viene, el aviso previo sale una sola vez
/// el día antes, los completados no avisan y lo que llega de Polar sigue las mismas reglas. Cada test usa
/// su propia carpeta temporal, como <see cref="ReminderServiceTests"/>.</summary>
public class ReminderAttentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ReminderService CreateService(int leadHours = ReminderAttention.DefaultLeadHours) =>
        new(_storageDirectory, advanceNoticeHoursProvider: () => leadHours);

    /// <summary>Recordatorio creado hace tiempo (antes de que se abra su ventana), que es el caso normal
    /// del aviso previo: lo creaste la semana pasada para mañana.</summary>
    private static Reminder Scheduled(ReminderService service, DateTime notifyAtUtc, string title = "Dentista")
    {
        var reminder = service.Create(title, "", notifyAtUtc, "BellRingOutline");
        reminder.CreatedUtc = Now.AddDays(-30);
        return reminder;
    }

    // ===================== Insignia: ventana de aviso previo =====================

    [Fact]
    public void ReminderForNextWeek_DoesNotNeedAttention()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddDays(7));

        Assert.False(service.NeedsAttention(reminder, Now));
        Assert.False(service.IsInAttentionWindow(reminder, Now));
    }

    [Fact]
    public void ReminderForNextYear_DoesNotNeedAttention()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddYears(1));

        Assert.False(service.NeedsAttention(reminder, Now));
    }

    [Fact]
    public void ReminderEntersItsWindowTheDayBefore_AtTheSameTime()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddDays(1));

        Assert.False(service.NeedsAttention(reminder, Now.AddMinutes(-1)));
        Assert.True(service.NeedsAttention(reminder, Now));
    }

    [Fact]
    public void OverdueReminder_StillNeedsAttentionUntilSeen()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddHours(-3));

        Assert.True(service.NeedsAttention(reminder, Now));
    }

    [Fact]
    public void CompletedDeletedOrImportedReminders_NeverNeedAttention()
    {
        var service = CreateService();
        var completed = Scheduled(service, Now.AddHours(1));
        completed.IsCompleted = true;
        var deleted = Scheduled(service, Now.AddHours(1));
        deleted.IsDeleted = true;
        var imported = service.CreateImportedEvent("Evento ICS", "", Now.AddHours(1), 60);

        Assert.False(service.NeedsAttention(completed, Now));
        Assert.False(service.NeedsAttention(deleted, Now));
        Assert.False(service.NeedsAttention(imported, Now));
    }

    [Fact]
    public void ZeroLead_OnlyNeedsAttentionOnceDue()
    {
        var service = CreateService(leadHours: 0);
        var reminder = Scheduled(service, Now.AddMinutes(30));

        Assert.False(service.NeedsAttention(reminder, Now));
        Assert.True(service.NeedsAttention(reminder, Now.AddMinutes(30)));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(24, 24)]
    [InlineData(500, ReminderAttention.MaxLeadHours)]
    public void ClampLeadHours_KeepsTheSettingsRange(int value, int expected) =>
        Assert.Equal(expected, ReminderAttention.ClampLeadHours(value));

    [Fact]
    public void WindowStart_DoesNotOverflowOnCorruptDates() =>
        Assert.Equal(DateTime.MinValue, ReminderAttention.WindowStartUtc(DateTime.MinValue.AddHours(1), 24));

    // ===================== Marcar como visto =====================

    [Fact]
    public void MarkAsRead_OutsideTheWindow_IsANoOp_SoTheDayBeforeNoticeStillCounts()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddDays(10));

        service.MarkAsRead(reminder.Id, Now);
        Assert.False(reminder.IsRead);

        var dayBefore = reminder.NotifyAtUtc.AddDays(-1);
        Assert.True(service.NeedsAttention(reminder, dayBefore));
        service.MarkAsRead(reminder.Id, dayBefore);
        Assert.True(reminder.IsRead);
        Assert.False(service.NeedsAttention(reminder, dayBefore));
    }

    [Fact]
    public void Reschedule_MakesTheNewOccurrenceCountAgain()
    {
        var service = CreateService();
        var reminder = Scheduled(service, DateTime.UtcNow.AddHours(1));
        service.MarkAsRead(reminder.Id);
        Assert.True(reminder.IsRead);

        service.Reschedule(reminder.Id, DateTime.UtcNow.AddDays(5));

        Assert.False(reminder.IsRead);
    }

    // ===================== Aviso previo =====================

    [Fact]
    public void PopUpcoming_FiresOnceWhenTheWindowOpens()
    {
        var service = CreateService();
        var reminder = Scheduled(service, Now.AddDays(1));

        Assert.Empty(service.PopUpcomingReminders(Now.AddMinutes(-1)));
        var first = service.PopUpcomingReminders(Now);
        Assert.Equal(reminder.Id, Assert.Single(first).Id);
        Assert.Empty(service.PopUpcomingReminders(Now.AddMinutes(1)));

        // Persistido: otra instancia (la app recién arrancada) no vuelve a avisar.
        Assert.Empty(CreateService().PopUpcomingReminders(Now.AddMinutes(2)));
    }

    [Fact]
    public void PopUpcoming_SkipsRemindersCreatedInsideTheirWindow()
    {
        var service = CreateService();
        service.Create("Dentro de dos horas", "", DateTime.UtcNow.AddHours(2), "BellRingOutline");

        Assert.Empty(service.PopUpcomingReminders(DateTime.UtcNow));
    }

    [Fact]
    public void PopUpcoming_SkipsOverdueCompletedAndZeroLead()
    {
        var service = CreateService();
        Scheduled(service, Now.AddMinutes(-1), "Vencido");
        var completed = Scheduled(service, Now.AddHours(3), "Completado");
        completed.IsCompleted = true;

        Assert.Empty(service.PopUpcomingReminders(Now));
        Scheduled(service, Now.AddHours(3), "Sin aviso previo");
        Assert.Empty(CreateService(leadHours: 0).PopUpcomingReminders(Now));
    }

    [Fact]
    public void PopUpcoming_UsesTheConfiguredLead()
    {
        var lead = 24;
        var service = new ReminderService(_storageDirectory, advanceNoticeHoursProvider: () => lead);
        var reminder = Scheduled(service, Now.AddHours(48));

        Assert.Empty(service.PopUpcomingReminders(Now));
        lead = 48;
        Assert.Equal(reminder.Id, Assert.Single(service.PopUpcomingReminders(Now)).Id);
    }

    [Fact]
    public void PopUpcoming_RearmsWhenTheReminderMovesOutOfItsWindow()
    {
        var service = CreateService();
        var reminder = Scheduled(service, DateTime.UtcNow.AddHours(5));
        Assert.Single(service.PopUpcomingReminders(DateTime.UtcNow));

        var later = DateTime.UtcNow.AddDays(4);
        service.Reschedule(reminder.Id, later);

        Assert.Empty(service.PopUpcomingReminders(DateTime.UtcNow));
        Assert.Single(service.PopUpcomingReminders(later.AddDays(-1)));
    }

    [Fact]
    public void RescheduleIntoTheWindow_DoesNotAnnounceWhatTheUserJustSet()
    {
        var service = CreateService();
        var reminder = Scheduled(service, DateTime.UtcNow.AddDays(9));

        service.Reschedule(reminder.Id, DateTime.UtcNow.AddHours(3));

        Assert.Empty(service.PopUpcomingReminders(DateTime.UtcNow));
    }

    /// <summary>Una serie diaria con un día de antelación: al avanzar a mañana, la ocurrencia nueva ya
    /// está en su ventana — no debe soltar el aviso de «mañana» justo después del de «ahora».</summary>
    [Fact]
    public void DailyRecurringReminder_DoesNotChainAnAdvanceNoticeRightAfterFiring()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var reminder = service.Create("Pastilla", "", now.AddMinutes(-1), "PillMultiple",
            recurrence: PolarRecurrence.Daily);
        reminder.CreatedUtc = now.AddDays(-30);

        Assert.Single(service.PopDueReminders(now));
        Assert.True(reminder.NotifyAtUtc > now);

        Assert.Empty(service.PopUpcomingReminders(now.AddSeconds(30)));
    }

    // ===================== Disparo a la hora =====================

    [Fact]
    public void PopDue_SkipsRemindersCompletedBeforeTheirTime()
    {
        var service = CreateService();
        var reminder = service.Create("Hecho antes de hora", "", DateTime.UtcNow.AddMinutes(-1), "BellRingOutline");
        service.ToggleCompleted(reminder.Id);

        Assert.Empty(service.PopDueReminders());
    }

    // ===================== Polar =====================

    [Fact]
    public void PulledReminderAlreadyPast_ArrivesAsHistory_WithoutNotifyingOrCounting()
    {
        var dto = new ReminderDto
        {
            Id = "polar-1",
            Title = "Del mes pasado",
            DateTime = PolarWire.ToEpochMs(DateTime.UtcNow.AddDays(-30)),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow.AddDays(-31)),
            UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow.AddDays(-30))
        };

        var reminder = PolarSyncService.NewReminderFromDto(dto);

        Assert.True(reminder.IsTriggered);
        Assert.True(reminder.IsRead);
        Assert.False(ReminderAttention.NeedsAttention(reminder, DateTime.UtcNow, ReminderAttention.DefaultLeadHours));
    }

    [Fact]
    public void PulledFutureReminder_FollowsTheSameAdvanceNoticeRules()
    {
        var notifyAt = DateTime.UtcNow.AddDays(3);
        var dto = new ReminderDto
        {
            Id = "polar-2",
            Title = "Creado en el móvil",
            DateTime = PolarWire.ToEpochMs(notifyAt),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow.AddDays(-1)),
            UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow.AddDays(-1))
        };

        var reminder = PolarSyncService.NewReminderFromDto(dto);
        var lead = ReminderAttention.DefaultLeadHours;

        Assert.False(reminder.IsTriggered);
        Assert.False(ReminderAttention.NeedsAttention(reminder, DateTime.UtcNow, lead));
        var dayBefore = reminder.NotifyAtUtc.AddDays(-1);
        Assert.True(ReminderAttention.NeedsAttention(reminder, dayBefore, lead));
        Assert.True(ReminderAttention.ShouldSendAdvanceNotice(reminder, dayBefore, lead));
    }

    [Fact]
    public void PolarMovingTheDate_MakesTheReminderUnseenAgain()
    {
        var reminder = new Reminder
        {
            Id = "r1", Title = "Original", IsRead = true, IsTriggered = true,
            NotifyAtUtc = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)
        };
        var dto = new ReminderDto
        {
            Id = "r1", Title = "Original",
            DateTime = PolarWire.ToEpochMs(new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc)),
            CreatedAt = PolarWire.ToEpochMs(DateTime.UtcNow),
            UpdatedAt = PolarWire.ToEpochMs(DateTime.UtcNow)
        };

        PolarSyncService.ApplyReminderDto(reminder, dto);

        Assert.False(reminder.IsRead);
        Assert.False(reminder.IsTriggered);
    }
}
