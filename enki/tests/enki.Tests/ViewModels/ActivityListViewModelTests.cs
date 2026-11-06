using System;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Comprueba filtros y acciones sobre las entidades reales, incluidos detalles fuera del filtro.</summary>
public class ActivityListViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-activity-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void OpeningUnreadNotification_RemovesRowButKeepsDetail()
    {
        var service = new NotificationService(new SettingsService(_directory), _directory);
        service.Push("Sincronización", "Archivo actualizado");
        var vm = new NotificationsViewModel(service) { Filter = ActivityFilter.Unread, SearchText = " ARCHIVO " };
        var item = Assert.Single(vm.VisibleItems);

        vm.OpenDetailCommand.Execute(item);

        Assert.Empty(vm.VisibleItems);
        Assert.True(vm.HasNoResults);
        Assert.False(vm.IsEmpty);
        Assert.Equal(item.Id, vm.Detail?.Id);
        Assert.True(vm.Detail?.IsRead);
        vm.ResetFiltersCommand.Execute(null);
        Assert.Single(vm.VisibleItems);
    }

    [Fact]
    public void NotificationFilters_SurviveServiceChanges_AndDistinguishEmptyHistory()
    {
        var service = new NotificationService(new SettingsService(_directory), _directory);
        var vm = new NotificationsViewModel(service) { SearchText = "backup" };
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasNoResults);
        service.Push("Sync");
        Assert.True(vm.HasNoResults);
        service.Push("Backup ready");
        Assert.Single(vm.VisibleItems);
        service.ClearAll();
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasNoResults);
    }

    [Fact]
    public void ExternalReminderChanges_RefreshFilteredListAndOpenDetail()
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
            new PolarTaskService(_directory), reminders, settings);
        var vm = new RemindersViewModel(reminders, auth, sync) { Filter = ActivityFilter.Pending };
        var reminder = reminders.Create("Desde calendario", "", DateTime.UtcNow.AddDays(1), "BellOutline");
        vm.OpenDetailCommand.Execute(Assert.Single(vm.VisibleItems));
        reminders.ToggleCompleted(reminder.Id);
        Assert.Empty(vm.VisibleItems);
        Assert.True(vm.Detail?.IsCompleted);
        reminders.Delete(reminder.Id);
        Assert.Null(vm.Detail);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void ReminderFilters_SortByDueDate_AndCompletionKeepsDetail()
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
            new PolarTaskService(_directory), reminders, settings);
        reminders.Create("Mañana", "Trabajo", DateTime.UtcNow.AddDays(1), "BellOutline");
        var overdue = reminders.Create("Ayer", "Trabajo", DateTime.UtcNow.AddDays(-1), "BellOutline");
        var vm = new RemindersViewModel(reminders, auth, sync) { Filter = ActivityFilter.Pending };
        Assert.Equal(overdue.Id, vm.VisibleItems.First().Id);
        vm.Filter = ActivityFilter.Overdue;
        var item = Assert.Single(vm.VisibleItems);
        vm.OpenDetailCommand.Execute(item);
        vm.ToggleCompletedCommand.Execute(vm.Detail);
        Assert.Empty(vm.VisibleItems);
        Assert.True(vm.Detail?.IsCompleted);
        Assert.Equal(LocalizationService.Instance["Calendar_Item_Completed"], vm.Detail?.StatusLabel);
        vm.Filter = ActivityFilter.Completed;
        Assert.Single(vm.VisibleItems);
        vm.SearchText = "missing";
        Assert.True(vm.HasNoResults);
        vm.ResetFiltersCommand.Execute(null);
        Assert.Equal(2, vm.VisibleItems.Count);
    }
}
