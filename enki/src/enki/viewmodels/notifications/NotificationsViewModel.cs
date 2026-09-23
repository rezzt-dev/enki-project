using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Full "Notificaciones" page — the same event log the title-bar bell shows
/// (<see cref="NotificationCenterViewModel"/>), just rendered as its own page instead of a dropdown, so the
/// two never drift into separate concepts. Stays live via <see cref="NotificationService.NotificationsChanged"/>
/// while the page is open, same as the bell popover.</summary>
public sealed partial class NotificationsViewModel : ViewModelBase
{
    private readonly NotificationService _service;

    public ObservableCollection<NotificationItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private int _unreadCount;

    /// <summary>Non-null while the read-only detail dialog is open for a notification.</summary>
    [ObservableProperty]
    private NotificationItemViewModel? _detail;

    /// <summary>True while the "limpiar notificaciones" confirmation dialog is open.</summary>
    [ObservableProperty]
    private bool _pendingClearAll;

    public NotificationsViewModel(NotificationService service)
    {
        _service = service;
        _service.NotificationsChanged += Reload;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels() => Reload();

    public void Reload()
    {
        Items.Clear();
        foreach (var notification in _service.Notifications)
            Items.Add(new NotificationItemViewModel(notification));

        if (Detail is not null) Detail = Items.FirstOrDefault(i => i.Id == Detail.Id);
        IsEmpty = Items.Count == 0;
        UnreadCount = _service.Notifications.Count(n => !n.IsRead);
    }

    [RelayCommand]
    private void OpenDetail(NotificationItemViewModel item)
    {
        _service.MarkAsRead(item.Id);
        Detail = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    [RelayCommand]
    private void MarkAllRead() => _service.MarkAllAsRead();

    [RelayCommand]
    private void RequestClearAll() => PendingClearAll = true;

    [RelayCommand]
    private void CancelClearAll() => PendingClearAll = false;

    [RelayCommand]
    private void ConfirmClearAll()
    {
        _service.ClearAll();
        PendingClearAll = false;
        Detail = null;
    }
}
