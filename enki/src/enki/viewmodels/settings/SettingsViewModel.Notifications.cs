using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Notificaciones: los toggles fijos del sidebar de Recordatorios/Notificaciones, los
/// toasts de escritorio, el silencio por severidad, la ventana de "próximo" recordatorio y el horario
/// "no molestar" con su volumen de sonido — split out of <c>SettingsViewModel.cs</c> so this whole page's
/// properties and handlers live together instead of interleaved with Apariencia/Editor/Calendario. Still
/// the very same <see cref="SettingsViewModel"/> class — partial types share every field/property
/// regardless of which file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _notificationsEnabled;

    [ObservableProperty]
    private bool _notificationsPanelEnabled;

    [ObservableProperty]
    private bool _remindersPanelEnabled;

    /// <summary>Botón de bandeja de entrada (captura rápida) de la barra de título. Tercer interruptor
    /// del grupo, junto a la campana y a los recordatorios — ver <c>AppSettings.InboxPanelEnabled</c>.</summary>
    [ObservableProperty]
    private bool _inboxPanelEnabled;

    [ObservableProperty]
    private bool _showDesktopToasts;

    [ObservableProperty]
    private bool _muteInfoNotifications;

    [ObservableProperty]
    private bool _muteSuccessNotifications;

    [ObservableProperty]
    private bool _muteWarningNotifications;

    [ObservableProperty]
    private bool _muteErrorNotifications;

    [ObservableProperty]
    private double _reminderSoonWindowDays;

    public string ReminderSoonWindowDaysLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Notifications_Reminders_SoonWindow_Unit"], (int)ReminderSoonWindowDays);

    // ===================== Valores rápidos de "Aplazar" (T5) =====================
    // Lo que el usuario ajusta aquí es exactamente lo que ofrecen los menús "Aplazar" de la bandeja de
    // recordatorios y del centro de notificaciones — ver Services.SnoozeSchedule, que es quien traduce
    // cada opción a un instante y quien acota estos valores si el settings.json trae algo imposible.

    [ObservableProperty]
    private decimal _snoozePrimaryMinutes;

    [ObservableProperty]
    private decimal _snoozeSecondaryMinutes;

    [ObservableProperty]
    private double _snoozeEveningHour;

    [ObservableProperty]
    private double _snoozeTomorrowHour;

    /// <summary>Misma fórmula que la etiqueta del propio menú (<c>ReminderInboxViewModel.FormatDuration</c>)
    /// para que Ajustes y el menú digan literalmente lo mismo: "1 h 30 min", no "90".</summary>
    public string SnoozePrimaryLabel => ReminderInboxViewModel.FormatDuration((int)SnoozePrimaryMinutes);

    public string SnoozeSecondaryLabel => ReminderInboxViewModel.FormatDuration((int)SnoozeSecondaryMinutes);

    public string SnoozeEveningHourLabel => FormatHour(SnoozeEveningHour);

    public string SnoozeTomorrowHourLabel => FormatHour(SnoozeTomorrowHour);

    private static string FormatHour(double hour) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Notifications_Snooze_Hour_Unit"], (int)hour);

    partial void OnSnoozePrimaryMinutesChanged(decimal value)
    {
        _settingsService.Settings.SnoozePrimaryMinutes = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(SnoozePrimaryLabel));
    }

    partial void OnSnoozeSecondaryMinutesChanged(decimal value)
    {
        _settingsService.Settings.SnoozeSecondaryMinutes = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(SnoozeSecondaryLabel));
    }

    partial void OnSnoozeEveningHourChanged(double value)
    {
        _settingsService.Settings.SnoozeEveningHour = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(SnoozeEveningHourLabel));
    }

    partial void OnSnoozeTomorrowHourChanged(double value)
    {
        _settingsService.Settings.SnoozeTomorrowHour = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(SnoozeTomorrowHourLabel));
    }

    // ===================== Horario "no molestar" y sonido (docs/ajustes-docs/04-editor-y-notificaciones.md §2) =====================

    [ObservableProperty]
    private bool _doNotDisturbEnabled;

    [ObservableProperty]
    private string _doNotDisturbStart = "22:00";

    [ObservableProperty]
    private string _doNotDisturbEnd = "08:00";

    [ObservableProperty]
    private bool _notificationSoundEnabled;

    /// <summary>0–100 (Fase 5 del roadmap, gap 11) — ver <c>AppSettings.NotificationSoundVolume</c> y
    /// <c>NotificationSoundPlayer.Play</c> para qué backends de reproducción respetan realmente la escala
    /// vs. solo el "silencio en 0".</summary>
    [ObservableProperty]
    private double _notificationSoundVolume;

    public string NotificationSoundVolumeLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Notifications_Sound_Volume_Unit"], (int)NotificationSoundVolume);

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationsEnabled = value;
        _settingsService.Save();
    }

    partial void OnNotificationsPanelEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationsPanelEnabled = value;
        _settingsService.Save();
        _onNotificationsPanelChanged(value);
    }

    partial void OnRemindersPanelEnabledChanged(bool value)
    {
        _settingsService.Settings.RemindersPanelEnabled = value;
        _settingsService.Save();
        _onRemindersPanelChanged(value);
    }

    partial void OnInboxPanelEnabledChanged(bool value)
    {
        _settingsService.Settings.InboxPanelEnabled = value;
        _settingsService.Save();
        _onInboxPanelChanged?.Invoke(value);
    }

    partial void OnShowDesktopToastsChanged(bool value)
    {
        _settingsService.Settings.ShowDesktopToasts = value;
        _settingsService.Save();
    }

    partial void OnMuteInfoNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteInfoNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteSuccessNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteSuccessNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteWarningNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteWarningNotifications = value;
        _settingsService.Save();
    }

    partial void OnMuteErrorNotificationsChanged(bool value)
    {
        _settingsService.Settings.MuteErrorNotifications = value;
        _settingsService.Save();
    }

    partial void OnReminderSoonWindowDaysChanged(double value)
    {
        _settingsService.Settings.ReminderSoonWindowDays = (int)value;
        DebouncedSave();
        OnPropertyChanged(nameof(ReminderSoonWindowDaysLabel));
    }

    partial void OnDoNotDisturbEnabledChanged(bool value)
    {
        _settingsService.Settings.DoNotDisturbEnabled = value;
        _settingsService.Save();
    }

    partial void OnDoNotDisturbStartChanged(string value)
    {
        _settingsService.Settings.DoNotDisturbStart = value;
        _settingsService.Save();
    }

    partial void OnDoNotDisturbEndChanged(string value)
    {
        _settingsService.Settings.DoNotDisturbEnd = value;
        _settingsService.Save();
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        _settingsService.Settings.NotificationSoundEnabled = value;
        _settingsService.Save();
    }

    partial void OnNotificationSoundVolumeChanged(double value)
    {
        _settingsService.Settings.NotificationSoundVolume = value;
        DebouncedSave();
        OnPropertyChanged(nameof(NotificationSoundVolumeLabel));
    }

    [RelayCommand]
    private void TestNotificationSound()
    {
        if (NotificationsEnabled) NotificationSoundPlayer.Play(NotificationSoundVolume);
    }
}
