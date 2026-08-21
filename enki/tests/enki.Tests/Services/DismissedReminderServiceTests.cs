using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="DismissedReminderService"/> — en particular el tope de 50 entradas que la
/// Fase 3 de docs/analisis-proyecto/roadmap.md añadió para que <c>dismissed-reminders.json</c> deje de
/// crecer sin límite (mismo criterio que el <c>MaxItems</c> de <see cref="NotificationService"/>). Usa el
/// <c>storageDirectory</c> opcional del constructor, mismo patrón que el resto de tests de servicios.</summary>
public class DismissedReminderServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Dismiss_ThenIsDismissed_WithSameSnapshot_ReturnsTrue()
    {
        var service = new DismissedReminderService(_storageDirectory);
        var dueDate = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        service.Dismiss("card-1", dueDate);

        Assert.True(service.IsDismissed("card-1", dueDate));
        Assert.False(service.IsDismissed("card-1", dueDate.AddHours(1)));
        Assert.False(service.IsDismissed("card-2", dueDate));
    }

    [Fact]
    public void Dismiss_SameCardTwice_KeepsOnlyTheLatestSnapshot()
    {
        var service = new DismissedReminderService(_storageDirectory);
        var oldDue = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var newDue = oldDue.AddDays(1);

        service.Dismiss("card-1", oldDue);
        service.Dismiss("card-1", newDue);

        Assert.False(service.IsDismissed("card-1", oldDue));
        Assert.True(service.IsDismissed("card-1", newDue));
    }

    [Fact]
    public void Dismiss_BeyondMaxItems_DropsTheOldestEntries_AndPersistsTheCap()
    {
        var service = new DismissedReminderService(_storageDirectory);
        var dueDate = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 55; i++) service.Dismiss($"card-{i:D2}", dueDate);

        // Las 5 más antiguas (card-00…card-04) quedan fuera; las 50 más recientes sobreviven.
        for (var i = 0; i < 5; i++) Assert.False(service.IsDismissed($"card-{i:D2}", dueDate));
        for (var i = 5; i < 55; i++) Assert.True(service.IsDismissed($"card-{i:D2}", dueDate));

        var reloaded = new DismissedReminderService(_storageDirectory);
        Assert.False(reloaded.IsDismissed("card-00", dueDate));
        Assert.True(reloaded.IsDismissed("card-54", dueDate));
    }

    // ===================== RemoveForCard (Fase 10) =====================
    // Fase 10 (docs/analisis-proyecto/roadmap.md): dos usos distintos comparten el mismo método —
    // "Deshacer" en la bandeja del shell (ReminderInboxViewModel) y la purga al borrar una tarjeta para
    // siempre (KanbanService.CardPermanentlyDeleted).

    [Fact]
    public void RemoveForCard_ExistingDismissal_ClearsItRegardlessOfDueDate()
    {
        var service = new DismissedReminderService(_storageDirectory);
        var dueDate = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        service.Dismiss("card-1", dueDate);

        service.RemoveForCard("card-1");

        Assert.False(service.IsDismissed("card-1", dueDate));
    }

    [Fact]
    public void RemoveForCard_PersistsAcrossInstances()
    {
        var first = new DismissedReminderService(_storageDirectory);
        var dueDate = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        first.Dismiss("card-1", dueDate);

        first.RemoveForCard("card-1");

        var reloaded = new DismissedReminderService(_storageDirectory);
        Assert.False(reloaded.IsDismissed("card-1", dueDate));
    }

    [Fact]
    public void RemoveForCard_UnknownCard_DoesNothing()
    {
        var service = new DismissedReminderService(_storageDirectory);
        var dueDate = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        service.Dismiss("card-1", dueDate);

        service.RemoveForCard("card-does-not-exist");

        Assert.True(service.IsDismissed("card-1", dueDate));
    }
}
