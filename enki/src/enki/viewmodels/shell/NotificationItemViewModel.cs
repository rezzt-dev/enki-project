using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

public sealed partial class NotificationItemViewModel(AppNotification notification) : ViewModelBase
{
    /// <summary>Si esta fila tiene desplegado su menú "Aplazar" — ver
    /// <see cref="ReminderItemViewModel.IsSnoozeMenuOpen"/>, mismo patrón y mismo motivo.</summary>
    [ObservableProperty]
    private bool _isSnoozeMenuOpen;

    public AppNotification Notification { get; } = notification;

    public string Id => Notification.Id;
    public string Title => LocalizedTextFormatter.Resolve(Notification.LocalizedTitle, Notification.Title);
    public string Message => LocalizedTextFormatter.Resolve(Notification.LocalizedMessage, Notification.Message);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsRead => Notification.IsRead;

    /// <summary>Solo los avisos de vencimiento llevan origen aplazable — ver
    /// <see cref="AppNotification.SnoozeTarget"/>. La fila esconde el botón "Aplazar" en el resto en vez
    /// de mostrarlo deshabilitado: un fallo de sincronización no es algo que el usuario pueda posponer, y
    /// un botón muerto en cada fila del historial solo añadiría ruido.</summary>
    public bool CanSnooze => Notification.SnoozeTarget is not null;

    /// <summary>Si la fila ofrece un botón que abre un enlace — ver <see cref="AppNotification.LinkUrl"/>.</summary>
    public bool HasLink => !string.IsNullOrWhiteSpace(Notification.LinkUrl);

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
