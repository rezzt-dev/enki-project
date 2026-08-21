using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Backs the bell button's dropdown in the title bar — the app's own event log (vault
/// switched, sync errors, etc.). See <see cref="ReminderInboxViewModel"/> for the separate, pull-driven
/// "tasks due soon" panel behind the inbox button; the two are intentionally not merged.</summary>
public sealed partial class NotificationCenterViewModel : ViewModelBase
{
    private readonly NotificationService _service;
    private readonly SettingsService _settingsService;
    private readonly Action<AppNotification> _showToast;
    private readonly Action<double> _playSound;

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private int _unreadCount;

    public bool HasItems => Items.Count > 0;

    /// <summary><paramref name="showToast"/>/<paramref name="playSound"/> son inyectables solo para tests
    /// (mismo patrón que el <c>HttpMessageHandler</c> opcional de <c>SupabaseRestClient</c>): en producción
    /// se omiten y quedan los efectos reales, <see cref="ToastService.Show"/> y
    /// <see cref="NotificationSoundPlayer.Play"/>.</summary>
    public NotificationCenterViewModel(NotificationService service, SettingsService settingsService,
        Action<AppNotification>? showToast = null, Action<double>? playSound = null)
    {
        _service = service;
        _settingsService = settingsService;
        _showToast = showToast ?? ToastService.Show;
        _playSound = playSound ?? NotificationSoundPlayer.Play;
        _service.NotificationAdded += OnNotificationAdded;
        _service.NotificationsChanged += Reload;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels() => Reload();

    private void OnNotificationAdded(AppNotification notification)
    {
        // El horario "no molestar" (docs/ajustes-docs/04-editor-y-notificaciones.md §2) solo afecta al
        // toast/sonido, nunca al registro en la campana — la notificación ya se guardó en NotificationService
        // antes de llegar aquí (o ni siquiera dispararía este evento si estuviera silenciada por categoría).
        var settings = _settingsService.Settings;
        var isQuietHours = settings.DoNotDisturbEnabled &&
            DoNotDisturbSchedule.IsWithin(settings.DoNotDisturbStart, settings.DoNotDisturbEnd, TimeOnly.FromDateTime(DateTime.Now));
        if (isQuietHours) return;

        if (settings.ShowDesktopToasts) _showToast(notification);
        if (settings.NotificationSoundEnabled) _playSound(settings.NotificationSoundVolume);
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var notification in _service.Notifications)
            Items.Add(new NotificationItemViewModel(notification));

        UnreadCount = _service.Notifications.Count(n => !n.IsRead);
        OnPropertyChanged(nameof(HasItems));
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void MarkAllRead() => _service.MarkAllAsRead();

    [RelayCommand]
    private void SelectItem(NotificationItemViewModel item) => _service.MarkAsRead(item.Id);
}
