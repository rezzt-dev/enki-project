using System;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>La insignia de «Recordatorios» (<see cref="RemindersViewModel.AttentionCount"/>) solo cuenta
/// lo que ya está en su ventana de aviso previo — el fallo original era que un recordatorio para la
/// semana siguiente ya sumaba en cuanto se creaba.</summary>
public class RemindersViewModelAttentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-reminders-attention-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private (RemindersViewModel Vm, ReminderService Reminders) Create()
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
            new PolarTaskService(_directory), reminders, settings);
        return (new RemindersViewModel(reminders, auth, sync), reminders);
    }

    [Fact]
    public void FutureRemindersOutsideTheirWindow_DoNotCountInTheBadge()
    {
        var (vm, reminders) = Create();

        reminders.Create("Semana que viene", "", DateTime.UtcNow.AddDays(7), "BellOutline");
        reminders.Create("Año que viene", "", DateTime.UtcNow.AddYears(1), "BellOutline");

        Assert.Equal(0, vm.AttentionCount);
        Assert.All(vm.Items, item => Assert.False(item.NeedsAttention));
        Assert.All(vm.Items, item => Assert.Equal(LocalizationService.Instance["Reminders_Status_Scheduled"], item.StatusLabel));
    }

    [Fact]
    public void ReminderInsideItsWindow_CountsAsUpcoming_UntilOpened()
    {
        var (vm, reminders) = Create();
        reminders.Create("Mañana", "", DateTime.UtcNow.AddHours(20), "BellOutline");
        reminders.Create("Semana que viene", "", DateTime.UtcNow.AddDays(7), "BellOutline");

        Assert.Equal(1, vm.AttentionCount);
        var upcoming = vm.Items.Single(i => i.NeedsAttention);
        Assert.True(upcoming.IsUpcoming);
        Assert.Equal(LocalizationService.Instance["Reminders_Status_Upcoming"], upcoming.StatusLabel);

        vm.OpenDetailCommand.Execute(upcoming);

        Assert.Equal(0, vm.AttentionCount);
    }

    [Fact]
    public void OpeningAFutureReminderEarly_DoesNotSilenceItsDayBeforeNotice()
    {
        var (vm, reminders) = Create();
        var reminder = reminders.Create("Dentro de un mes", "", DateTime.UtcNow.AddDays(30), "BellOutline");

        vm.OpenDetailCommand.Execute(vm.Items.Single());

        Assert.False(reminder.IsRead);
    }

    [Fact]
    public void MarkAllRead_ClearsTheBadge_WithoutTouchingFutureReminders()
    {
        var (vm, reminders) = Create();
        reminders.Create("Vencido", "", DateTime.UtcNow.AddHours(-2), "BellOutline");
        reminders.Create("Hoy", "", DateTime.UtcNow.AddHours(2), "BellOutline");
        var future = reminders.Create("Futuro", "", DateTime.UtcNow.AddDays(5), "BellOutline");
        Assert.Equal(2, vm.AttentionCount);

        vm.MarkAllReadCommand.Execute(null);

        Assert.Equal(0, vm.AttentionCount);
        Assert.False(future.IsRead);
    }

    [Fact]
    public void CompletingAReminder_RemovesItFromTheBadge()
    {
        var (vm, reminders) = Create();
        var reminder = reminders.Create("Hoy", "", DateTime.UtcNow.AddHours(2), "BellOutline");
        Assert.Equal(1, vm.AttentionCount);

        reminders.ToggleCompleted(reminder.Id);

        Assert.Equal(0, vm.AttentionCount);
    }
}
