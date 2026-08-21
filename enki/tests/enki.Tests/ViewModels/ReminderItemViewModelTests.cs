using System;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="ReminderItemViewModel"/>'s bucketing — the one piece of non-trivial logic
/// docs/notificaciones-y-recordatorios.md calls out as most worth a unit test, now that it also has to
/// tell a plain calendar date (all-day, see <see cref="KanbanCard.IsPastDue"/>) apart from a due date that
/// carries a specific time of day.</summary>
public class ReminderItemViewModelTests
{
    private static KanbanCard MakeCard(DateTime? dueUtc) => new()
    {
        Id = "card-1",
        Title = "Tarjeta de prueba",
        DueDateUtc = dueUtc
    };

    [Fact]
    public void Throws_WhenCardHasNoDueDate()
    {
        var card = MakeCard(null);

        Assert.Throws<ArgumentException>(() => new ReminderItemViewModel(card, "Columna"));
    }

    [Fact]
    public void Buckets_AsOverdue_WhenDueDateIsInThePast()
    {
        var card = MakeCard(DateTime.UtcNow.AddDays(-2));

        var item = new ReminderItemViewModel(card, "Columna");

        Assert.Equal(ReminderBucket.Overdue, item.Bucket);
    }

    [Fact]
    public void Buckets_AsToday_WhenDueDateIsTodayWithoutASpecificTime()
    {
        // The card editor's calendar picker alone always leaves the due date at local midnight — this
        // exercises the all-day rule (whole-day comparison, not instant-vs-instant).
        var card = MakeCard(DateTime.Today.ToUniversalTime());

        var item = new ReminderItemViewModel(card, "Columna");

        Assert.Equal(ReminderBucket.Today, item.Bucket);
    }

    [Fact]
    public void Buckets_AsOverdue_WhenDueTimeTodayAlreadyPassed()
    {
        // A due date with a real time of day (the editor's optional "Agregar hora") is compared against
        // the current instant, not the whole day — a moment strictly in the past is always overdue.
        var dueLocal = DateTime.Now.AddMinutes(-1);
        var card = MakeCard(dueLocal.ToUniversalTime());

        var item = new ReminderItemViewModel(card, "Columna");

        Assert.Equal(ReminderBucket.Overdue, item.Bucket);
    }

    [Fact]
    public void Buckets_AsSoon_WhenDueDateIsInTheFuture()
    {
        var card = MakeCard(DateTime.UtcNow.AddDays(3));

        var item = new ReminderItemViewModel(card, "Columna");

        Assert.Equal(ReminderBucket.Soon, item.Bucket);
    }
}
