using Avalonia.Controls.Notifications;
using enki.Models;

namespace enki.Services;

/// <summary>Thin wrapper around Avalonia's own <see cref="WindowNotificationManager"/> for OS-style toast
/// popups (see docs/notificaciones-y-recordatorios.md), so an urgent event doesn't depend on
/// the user noticing the bell badge. Kept separate from <see cref="NotificationService"/> (which only
/// persists the event log) since a toast is a transient UI concern, not something to save. Reaches the
/// main window the same way <c>SettingsViewModel.ChangeVaultFolder</c> already does from a viewmodel —
/// via the static <c>App.MainWindowRef</c> — since Avalonia's notification manager needs a live
/// <c>TopLevel</c> to attach its overlay to.</summary>
public static class ToastService
{
    private static WindowNotificationManager? _manager;

    public static void Show(AppNotification notification)
    {
        if (App.MainWindowRef is not { } window) return;

        _manager ??= new WindowNotificationManager(window)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };

        _manager.Show(new Notification(notification.Title, notification.Message, ToType(notification.Severity)));
    }

    private static NotificationType ToType(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => NotificationType.Success,
        NotificationSeverity.Warning => NotificationType.Warning,
        NotificationSeverity.Error => NotificationType.Error,
        _ => NotificationType.Information
    };
}
