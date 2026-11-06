using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Verifica filas largas y comandos de filtro en las vistas reales a un ancho compacto.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class ActivityViewsUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-activity-ui-{Guid.NewGuid():N}");

    public ActivityViewsUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Notifications_LongTitleFits_AndUnreadDetailRemainsOpen()
    {
        var service = new NotificationService(new SettingsService(_directory), _directory);
        service.Push(new string('W', 250), new string('M', 2000));
        var vm = new NotificationsViewModel(service);
        var view = new NotificationsView { DataContext = vm };
        var window = new Window { Content = view, Width = 720, Height = 640 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var row = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("activity-row"));
            Assert.InRange(row.Bounds.Width, 100, 648);
            var title = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.Items[0].Title);
            Assert.True(title.Bounds.Width < row.Bounds.Width);
            var unread = view.GetVisualDescendants().OfType<RadioButton>()
                .Single(b => Equals(b.CommandParameter, ActivityFilter.Unread));
            unread.Command!.Execute(unread.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            row = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("activity-row"));
            row.Command!.Execute(row.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Empty(vm.VisibleItems);
            Assert.NotNull(vm.Detail);
            vm.CloseDetailCommand.Execute(null);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Reminders_RowCompletionUpdatesPendingFilter()
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth),
            new PolarTaskService(_directory), reminders, settings);
        reminders.Create(new string('W', 250), "Descripción", DateTime.UtcNow.AddDays(1), "BellOutline");
        var vm = new RemindersViewModel(reminders, auth, sync) { Filter = ActivityFilter.Pending };
        var view = new RemindersView { DataContext = vm };
        var window = new Window { Content = view, Width = 720, Height = 640 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var complete = view.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.ToggleCompletedCommand));
            complete.Command!.Execute(complete.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Empty(vm.VisibleItems);
            Assert.True(vm.HasNoResults);
            Assert.True(Assert.Single(reminders.ActiveReminders).IsCompleted);
        }
        finally { window.Close(); }
    }
}
