using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="NotificationCenterViewModel.OnNotificationAdded"/> — la auditoría pedida por
/// la Fase 5 del roadmap de mejora continua: "No molestar" debe silenciar el toast y el sonido pero nunca
/// el registro en la campana. Antes de esta fase solo <see cref="DoNotDisturbScheduleTests"/> probaba la
/// función pura <c>IsWithin</c>; estos tests fijan además el comportamiento del punto donde de verdad se
/// decide silenciar toast/sonido, usando los parámetros <c>showToast</c>/<c>playSound</c> inyectables para
/// no depender de un <c>TopLevel</c>/reproductor de audio real.</summary>
public class NotificationCenterViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void OutsideQuietHours_ShowsToastAndPlaysSound()
    {
        var settings = new SettingsService(_storageDirectory);
        var notifications = new NotificationService(settings, _storageDirectory);
        var toastCalls = 0;
        var soundCalls = 0;
        var viewModel = new NotificationCenterViewModel(notifications, settings,
            showToast: _ => toastCalls++, playSound: _ => soundCalls++);

        settings.Settings.DoNotDisturbEnabled = false;
        settings.Settings.ShowDesktopToasts = true;
        settings.Settings.NotificationSoundEnabled = true;

        notifications.Push("Evento");

        Assert.Equal(1, toastCalls);
        Assert.Equal(1, soundCalls);
        Assert.True(viewModel.HasItems);
    }

    [Fact]
    public void DuringQuietHours_SilencesToastAndSoundButKeepsTheLogEntry()
    {
        var settings = new SettingsService(_storageDirectory);
        var notifications = new NotificationService(settings, _storageDirectory);
        var toastCalls = 0;
        var soundCalls = 0;
        var viewModel = new NotificationCenterViewModel(notifications, settings,
            showToast: _ => toastCalls++, playSound: _ => soundCalls++);

        // Ventana "no molestar" 00:00–23:59 cubre cualquier hora a la que corra el test.
        settings.Settings.DoNotDisturbEnabled = true;
        settings.Settings.DoNotDisturbStart = "00:00";
        settings.Settings.DoNotDisturbEnd = "23:59";
        settings.Settings.ShowDesktopToasts = true;
        settings.Settings.NotificationSoundEnabled = true;

        notifications.Push("Evento silencioso");

        Assert.Equal(0, toastCalls);
        Assert.Equal(0, soundCalls);
        Assert.True(viewModel.HasItems);
        Assert.Single(notifications.Notifications);
        Assert.Equal("Evento silencioso", notifications.Notifications[0].Title);
        Assert.Equal(1, viewModel.UnreadCount);
    }

    [Fact]
    public void DuringQuietHours_SoundDisabledIndependentlyOfToasts_NeitherFires()
    {
        var settings = new SettingsService(_storageDirectory);
        var notifications = new NotificationService(settings, _storageDirectory);
        var toastCalls = 0;
        var soundCalls = 0;
        var viewModel = new NotificationCenterViewModel(notifications, settings,
            showToast: _ => toastCalls++, playSound: _ => soundCalls++);

        settings.Settings.DoNotDisturbEnabled = false;
        settings.Settings.ShowDesktopToasts = false;
        settings.Settings.NotificationSoundEnabled = false;

        notifications.Push("Silencioso por preferencia, no por horario");

        Assert.Equal(0, toastCalls);
        Assert.Equal(0, soundCalls);
        Assert.True(viewModel.HasItems);
    }
}
