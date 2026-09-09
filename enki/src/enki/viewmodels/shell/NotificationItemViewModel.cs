using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

public sealed class NotificationItemViewModel(AppNotification notification)
{
    public AppNotification Notification { get; } = notification;

    public string Id => Notification.Id;
    public string Title => LocalizedTextFormatter.Resolve(Notification.LocalizedTitle, Notification.Title);
    public string Message => LocalizedTextFormatter.Resolve(Notification.LocalizedMessage, Notification.Message);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsRead => Notification.IsRead;

    /// <summary>Fase 2 §4.7: replaces this class's own hand-rolled "hace 2 min" logic with the single
    /// shared <see cref="RelativeTimeFormatter"/> used across the app.</summary>
    public string TimeLabel => RelativeTimeFormatter.Format(Notification.CreatedUtc);

    /// <summary>Absolute date/time for the full-page detail dialog — <see cref="TimeLabel"/> stays relative
    /// for the compact list row.</summary>
    public string CreatedAtLabel => RelativeTimeFormatter.FormatAbsolute(Notification.CreatedUtc);

    /// <summary>Exposed so the view can pick styling per severity via
    /// <c>Classes.info/success/warning/error="{Binding Severity, Converter={StaticResource EnumEqualsConverter}, ConverterParameter=...}"</c>,
    /// matching how <c>KanbanCardViewModel.Priority</c> drives its priority-dot classes.</summary>
    public NotificationSeverity Severity => Notification.Severity;

    public MaterialIconKind SeverityIcon => Notification.Severity switch
    {
        NotificationSeverity.Success => MaterialIconKind.CheckCircleOutline,
        NotificationSeverity.Warning => MaterialIconKind.AlertCircleOutline,
        NotificationSeverity.Error => MaterialIconKind.CloseCircleOutline,
        _ => MaterialIconKind.InformationCircleOutline
    };

    public string SeverityLabel => Notification.Severity switch
    {
        NotificationSeverity.Success => LocalizationService.Instance["Notification_Severity_Success"],
        NotificationSeverity.Warning => LocalizationService.Instance["Notification_Severity_Warning"],
        NotificationSeverity.Error => LocalizationService.Instance["Notification_Severity_Error"],
        _ => LocalizationService.Instance["Notification_Severity_Info"]
    };
}
