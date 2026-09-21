using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="SnoozedReminderService"/> — el almacén de aplazamientos de T5. Usa el
/// <c>storageDirectory</c> opcional del constructor, mismo patrón que
/// <see cref="DismissedReminderServiceTests"/>.</summary>
public class SnoozedReminderServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTime Due = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Snooze_ThenFind_WithTheSameDueSnapshot_ReturnsTheEntry()
    {
        var service = new SnoozedReminderService(_storageDirectory);

        service.Snooze(SnoozeSourceKind.KanbanCard, "card-1", Due, Due.AddMinutes(10));

        var entry = service.Find("card-1", Due);
        Assert.NotNull(entry);
        Assert.Equal(SnoozeSourceKind.KanbanCard, entry!.SourceKind);
        Assert.Equal(Due.AddMinutes(10), entry.SnoozedUntilUtc);
        Assert.Equal(1, entry.Count);
    }

    /// <summary>Si la fecha real de la tarjeta cambia después de aplazarla, el aplazamiento ya no
    /// corresponde a ese aviso — mismo criterio que <see cref="DismissedReminderService.IsDismissed"/>.</summary>
    [Fact]
    public void Find_WithADifferentDueSnapshot_ReturnsNull()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        service.Snooze(SnoozeSourceKind.KanbanCard, "card-1", Due, Due.AddMinutes(10));

        Assert.Null(service.Find("card-1", Due.AddDays(1)));
    }

    [Fact]
    public void Snooze_Twice_ReplacesTheEntryAndCountsTheRepeat()
    {
        var service = new SnoozedReminderService(_storageDirectory);

        service.Snooze(SnoozeSourceKind.Reminder, "rem-1", Due, Due.AddMinutes(10));
        var second = service.Snooze(SnoozeSourceKind.Reminder, "rem-1", Due, Due.AddHours(1));

        Assert.Equal(2, second.Count);
        Assert.Equal(Due.AddHours(1), service.Find("rem-1", Due)!.SnoozedUntilUtc);
    }

    [Fact]
    public void PopDue_ReturnsAndRemovesOnlyTheElapsedOnes()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        service.Snooze(SnoozeSourceKind.Reminder, "elapsed", Due, Due.AddMinutes(10));
        service.Snooze(SnoozeSourceKind.Reminder, "pending", Due, Due.AddHours(5));

        var due = service.PopDue(Due.AddMinutes(30));

        Assert.Equal(["elapsed"], due.Select(s => s.SourceId));
        Assert.Null(service.Find("elapsed", Due));
        Assert.NotNull(service.Find("pending", Due));
    }

    [Fact]
    public void PopDue_WithNothingElapsed_ReturnsEmptyAndKeepsEverything()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        service.Snooze(SnoozeSourceKind.Reminder, "pending", Due, Due.AddHours(5));

        Assert.Empty(service.PopDue(Due));
        Assert.NotNull(service.Find("pending", Due));
    }

    [Fact]
    public void Cancel_DropsTheEntryRegardlessOfTheDueSnapshot()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        service.Snooze(SnoozeSourceKind.KanbanCard, "card-1", Due, Due.AddMinutes(10));

        service.Cancel("card-1");

        Assert.Null(service.Find("card-1", Due));
    }

    [Fact]
    public void PruneWhere_DropsOnlyTheEntriesTheCallerMarksStale()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        service.Snooze(SnoozeSourceKind.KanbanCard, "gone", Due, Due.AddMinutes(10));
        service.Snooze(SnoozeSourceKind.KanbanCard, "alive", Due, Due.AddMinutes(10));

        service.PruneWhere(s => s.SourceId == "gone");

        Assert.Null(service.Find("gone", Due));
        Assert.NotNull(service.Find("alive", Due));
    }

    /// <summary>Criterio EARS: "el sistema deberá conservar los aplazamientos tras cerrar y reabrir la
    /// aplicación".</summary>
    [Fact]
    public void Snoozes_SurviveAReopenOfTheService()
    {
        new SnoozedReminderService(_storageDirectory).Snooze(SnoozeSourceKind.Reminder, "rem-1", Due, Due.AddHours(2));

        var reopened = new SnoozedReminderService(_storageDirectory);

        Assert.Equal(Due.AddHours(2), reopened.Find("rem-1", Due)!.SnoozedUntilUtc);
    }

    [Fact]
    public void Snooze_RaisesSnoozesChanged()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        var raised = 0;
        service.SnoozesChanged += () => raised++;

        service.Snooze(SnoozeSourceKind.Reminder, "rem-1", Due, Due.AddHours(2));

        Assert.Equal(1, raised);
    }

    /// <summary>Tope de 100 entradas, mismo criterio que el <c>MaxItems</c> de
    /// <see cref="DismissedReminderService"/>: el archivo no puede crecer sin límite, y lo que se suelta
    /// son siempre los aplazamientos más antiguos.</summary>
    [Fact]
    public void Snooze_BeyondTheCap_DropsTheOldestEntries()
    {
        var service = new SnoozedReminderService(_storageDirectory);
        for (var i = 0; i < 120; i++)
            service.Snooze(SnoozeSourceKind.Reminder, $"rem-{i}", Due, Due.AddHours(2));

        Assert.Null(service.Find("rem-0", Due));
        Assert.NotNull(service.Find("rem-119", Due));
    }
}
