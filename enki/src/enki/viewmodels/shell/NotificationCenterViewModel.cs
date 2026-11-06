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
    private readonly SnoozedReminderService? _snoozedReminderService;
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
    /// <param name="snoozedReminderService">Almacén de aplazamientos (T5). Opcional —y null en los tests
    /// que solo ejercitan el toast/sonido— porque sin él las filas simplemente no ofrecen "Aplazar";
    /// mismo criterio de degradar en silencio que <paramref name="showToast"/>.</param>
    public NotificationCenterViewModel(NotificationService service, SettingsService settingsService,
        Action<AppNotification>? showToast = null, Action<double>? playSound = null,
        SnoozedReminderService? snoozedReminderService = null)
    {
        _service = service;
        _settingsService = settingsService;
        _snoozedReminderService = snoozedReminderService;
        _showToast = showToast ?? ToastService.Show;
        _playSound = playSound ?? NotificationSoundPlayer.Play;
        _service.NotificationAdded += OnNotificationAdded;
        _service.NotificationsChanged += Reload;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(SnoozePrimaryLabel));
        OnPropertyChanged(nameof(SnoozeSecondaryLabel));
        Reload();
    }

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
        SnoozeMenuItem = null;
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

    /// <summary>Abre el enlace del aviso (hoy, la página de la release nueva). Pasa por
    /// <see cref="UrlOpener"/>, que vuelve a filtrar el destino: lo que hay guardado en
    /// <c>notifications.json</c> no se abre a ciegas por estar guardado.</summary>
    [RelayCommand]
    private void OpenLink(NotificationItemViewModel item)
    {
        if (item.Notification.LinkUrl is not { } url) return;

        _service.MarkAsRead(item.Id);
        IsOpen = false;
        UrlOpener.Open(url);
    }


    // ===================== Aplazar desde la campana (T5) =====================
    // Mismas opciones y mismo almacén que la bandeja de recordatorios (ReminderInboxViewModel): un aviso
    // de vencimiento se puede posponer desde donde el usuario lo vea primero, sin que ninguna de las dos
    // pantallas tenga su propia idea de qué significa "10 min".

    /// <summary>Fila con el menú "Aplazar" desplegado, o <see langword="null"/> con todos cerrados.</summary>
    [ObservableProperty]
    private NotificationItemViewModel? _snoozeMenuItem;

    public string SnoozePrimaryLabel => ReminderInboxViewModel.FormatDuration(_settingsService.Settings.SnoozePrimaryMinutes);

    public string SnoozeSecondaryLabel => ReminderInboxViewModel.FormatDuration(_settingsService.Settings.SnoozeSecondaryMinutes);

    partial void OnSnoozeMenuItemChanged(NotificationItemViewModel? value)
    {
        foreach (var item in Items) item.IsSnoozeMenuOpen = ReferenceEquals(item, value);
    }

    [RelayCommand]
    private void ToggleSnoozeMenu(NotificationItemViewModel item) =>
        SnoozeMenuItem = ReferenceEquals(SnoozeMenuItem, item) ? null : item;

    [RelayCommand]
    private void SnoozePrimary(NotificationItemViewModel item) => Snooze(item, SnoozeOption.QuickPrimary);

    [RelayCommand]
    private void SnoozeSecondary(NotificationItemViewModel item) => Snooze(item, SnoozeOption.QuickSecondary);

    [RelayCommand]
    private void SnoozeEvening(NotificationItemViewModel item) => Snooze(item, SnoozeOption.Evening);

    [RelayCommand]
    private void SnoozeTomorrow(NotificationItemViewModel item) => Snooze(item, SnoozeOption.Tomorrow);

    private void Snooze(NotificationItemViewModel item, SnoozeOption option)
    {
        SnoozeMenuItem = null;
        if (_snoozedReminderService is null || item.Notification.SnoozeTarget is not { } target) return;

        var untilUtc = SnoozeSchedule.ResolveRespectingQuietHours(option, DateTime.UtcNow, _settingsService.Settings);
        _snoozedReminderService.Snooze(target.Kind, target.SourceId, target.DueUtc, untilUtc);

        // El aviso ya cumplió su función: dejarlo sin leer haría que la campana siguiera reclamando
        // atención sobre algo que el usuario acaba de posponer explícitamente.
        _service.MarkAsRead(item.Id);
    }
}
