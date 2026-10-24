using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="NotificationService.Push"/> — the other piece of logic
/// docs/notificaciones-y-recordatorios.md calls out as most worth a unit test (the 50-item trim). Each
/// test points storage at its own throwaway temp directory (via the storageDirectory constructor
/// parameter added alongside this test project) instead of the developer's real
/// <c>%AppData%/Enki</c>, so running the suite never touches or overwrites a real notification log.</summary>
public class NotificationServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private NotificationService CreateService() =>
        new(new SettingsService(_storageDirectory), _storageDirectory);

    /// <summary>Fase 1 (docs/analisis-proyecto/roadmap.md): a notifications.json that exists but fails to
    /// parse must be distinguishable from a fresh install, same pattern as
    /// <see cref="SettingsService.LoadedFromCorruptData"/>/<see cref="KanbanService.LoadedFromCorruptData"/>.</summary>
    [Fact]
    public void Load_CorruptFile_FallsBackToEmptyList_AndFlagsCorruption()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "notifications.json"), "{ not valid json ");

        var service = CreateService();

        Assert.Empty(service.Notifications);
        Assert.True(service.LoadedFromCorruptData);
    }

    [Fact]
    public void Load_MissingFile_DoesNotFlagAsCorrupt()
    {
        var service = CreateService();

        Assert.False(service.LoadedFromCorruptData);
    }

    [Fact]
    public void Push_InsertsAtTheFront()
    {
        var service = CreateService();

        service.Push("Primero");
        service.Push("Segundo");

        Assert.Equal("Segundo", service.Notifications[0].Title);
        Assert.Equal("Primero", service.Notifications[1].Title);
    }

    [Fact]
    public void Push_TrimsToTheNewest50()
    {
        var service = CreateService();

        for (var i = 0; i < 55; i++) service.Push($"Notificación {i}");

        Assert.Equal(50, service.Notifications.Count);
        Assert.Equal("Notificación 54", service.Notifications[0].Title);
        Assert.Equal("Notificación 5", service.Notifications[^1].Title);
    }

    [Fact]
    public void Push_PersistsAcrossInstances()
    {
        var first = CreateService();
        first.Push("Persistente", severity: NotificationSeverity.Warning);

        var second = CreateService();

        Assert.Single(second.Notifications);
        Assert.Equal("Persistente", second.Notifications[0].Title);
        Assert.Equal(NotificationSeverity.Warning, second.Notifications[0].Severity);
    }

    [Fact]
    public void Push_RaisesNotificationAdded()
    {
        var service = CreateService();
        AppNotification? raised = null;
        service.NotificationAdded += n => raised = n;

        service.Push("Evento");

        Assert.NotNull(raised);
        Assert.Equal("Evento", raised!.Title);
    }

    [Fact]
    public void NotificationsDisabled_SuppressesEverySeverity_AndKeepsExistingHistory()
    {
        var settings = new SettingsService(_storageDirectory);
        var service = new NotificationService(settings, _storageDirectory);
        service.Push("Anterior");
        var added = 0;
        var changed = 0;
        service.NotificationAdded += _ => added++;
        service.NotificationsChanged += () => changed++;

        settings.Settings.NotificationsEnabled = false;
        foreach (var severity in Enum.GetValues<NotificationSeverity>())
            service.Push("Omitida", severity: severity);

        Assert.Equal("Anterior", Assert.Single(service.Notifications).Title);
        Assert.Equal(0, added);
        Assert.Equal(0, changed);

        settings.Settings.NotificationsEnabled = true;
        service.Push("Nueva");
        Assert.Equal(2, service.Notifications.Count);
        Assert.Equal(1, added);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void NotificationsDisabled_PersistsAcrossSettingsReload()
    {
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.NotificationsEnabled = false;
        settings.Save();

        var restored = new SettingsService(_storageDirectory);
        var service = new NotificationService(restored, _storageDirectory);
        service.Push("Omitida");

        Assert.False(restored.Settings.NotificationsEnabled);
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void ClearAll_RaisesNotificationsChanged()
    {
        var service = CreateService();
        service.Push("Evento");
        var raised = false;
        service.NotificationsChanged += () => raised = true;

        service.ClearAll();

        Assert.True(raised);
        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void MarkAsRead_RaisesNotificationsChanged()
    {
        var service = CreateService();
        var notification = service.Push("Evento");
        var raised = false;
        service.NotificationsChanged += () => raised = true;

        service.MarkAsRead(notification.Id);

        Assert.True(raised);
    }

    [Fact]
    public void MarkAllAsRead_RaisesNotificationsChanged()
    {
        var service = CreateService();
        service.Push("Evento");
        var raised = false;
        service.NotificationsChanged += () => raised = true;

        service.MarkAllAsRead();

        Assert.True(raised);
    }
}
