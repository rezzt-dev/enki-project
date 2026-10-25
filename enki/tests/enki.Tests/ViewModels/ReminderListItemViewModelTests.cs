using System;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="ReminderListItemViewModel.IsOverdue"/> — Fase 3 (análisis §4.4, Media):
/// a completed reminder past its notify time must not keep reading as "VENCIDO", replicating the
/// <c>!IsCompleted</c> criterion <see cref="ViewModels.CalendarItemViewModel.IsPastDue"/> already applies.</summary>
public class ReminderListItemViewModelTests
{
    private static Reminder MakePastDueReminder(bool isCompleted) => new()
    {
        Id = "r1",
        Title = "Recordatorio",
        NotifyAtUtc = DateTime.UtcNow.AddDays(-1),
        IsCompleted = isCompleted
    };

    [Fact]
    public void IsOverdue_IsTrue_ForPastDueReminder_WhenNotCompleted()
    {
        var vm = new ReminderListItemViewModel(MakePastDueReminder(isCompleted: false));

        Assert.True(vm.IsOverdue);
    }

    [Fact]
    public void IsOverdue_IsFalse_ForPastDueReminder_WhenCompleted()
    {
        var vm = new ReminderListItemViewModel(MakePastDueReminder(isCompleted: true));

        Assert.False(vm.IsOverdue);
    }

    [Fact]
    public void IsOverdue_IsFalse_ForFutureReminder()
    {
        var reminder = new Reminder { Id = "r2", Title = "Futuro", NotifyAtUtc = DateTime.UtcNow.AddDays(1) };

        var vm = new ReminderListItemViewModel(reminder);

        Assert.False(vm.IsOverdue);
    }
}
