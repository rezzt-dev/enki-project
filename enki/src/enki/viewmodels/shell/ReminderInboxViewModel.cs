using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Backs the inbox button's dropdown — every kanban card with a due date *and* every standalone
/// <see cref="Models.Reminder"/> from the Recordatorios page, merged and bucketed into vencidas/hoy/próximas
/// (docs/agent-docs/notificaciones-y-recordatorios.md: the title-bar icon is literally labeled
/// "Recordatorios", so it needs to actually reflect the user's own reminders, not just Kanban due dates).
/// No independent persistence of its own besides which items were dismissed, see
/// <see cref="DismissedReminderService"/>. Kept live via <see cref="KanbanService.BoardChanged"/> and
/// <see cref="ReminderService.RemindersChanged"/> — see <see cref="Reload"/>.</summary>
public sealed partial class ReminderInboxViewModel : ViewModelBase
{
    private readonly KanbanService _kanbanService;
    private readonly ReminderService _reminderService;
    private readonly SettingsService _settingsService;
    private readonly DismissedReminderService _dismissedReminderService;
    private readonly SnoozedReminderService _snoozedReminderService;
    private readonly Action<string> _onOpenCard;
    private readonly Action<string> _onOpenReminder;

    public ObservableCollection<ReminderItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Vencidas only — the same population as <c>KanbanViewModel.OverdueCount</c>, kept as the
    /// badge count so it reads as "needs attention now" rather than every card due in the next window.</summary>
    [ObservableProperty]
    private int _overdueCount;

    public bool HasItems => Items.Count > 0;

    /// <summary>Cuánto queda visible el aviso "Deshacer" tras un <see cref="Dismiss"/> (Fase 10,
    /// docs/analisis-proyecto/roadmap.md) antes de que el descarte se vuelva permanente sin más aviso.</summary>
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _undoTimer;

    /// <summary>No nulo mientras el aviso "Deshacer" del último <see cref="Dismiss"/> sigue visible — se
    /// limpia solo (sin deshacer nada) cuando expira <see cref="UndoWindow"/>, o de inmediato al pulsar
    /// Deshacer o al descartar otro elemento distinto.</summary>
    [ObservableProperty]
    private ReminderItemViewModel? _lastDismissed;

    public bool HasLastDismissed => LastDismissed is not null;

    partial void OnLastDismissedChanged(ReminderItemViewModel? value) => OnPropertyChanged(nameof(HasLastDismissed));

    public ReminderInboxViewModel(
        KanbanService kanbanService,
        ReminderService reminderService,
        SettingsService settingsService,
        DismissedReminderService dismissedReminderService,
        SnoozedReminderService snoozedReminderService,
        Action<string> onOpenCard,
        Action<string> onOpenReminder)
    {
        _kanbanService = kanbanService;
        _reminderService = reminderService;
        _settingsService = settingsService;
        _dismissedReminderService = dismissedReminderService;
        _snoozedReminderService = snoozedReminderService;
        _onOpenCard = onOpenCard;
        _onOpenReminder = onOpenReminder;

        // Live refresh: a due date edited from the Kanban page, or a reminder created/fired/completed from
        // the Recordatorios page (or the calendar), while this panel is open now shows up without waiting
        // for the next Toggle (see docs/notificaciones-y-recordatorios.md).
        _kanbanService.BoardChanged += Reload;
        _reminderService.RemindersChanged += Reload;
        // Un aplazamiento creado desde el centro de notificaciones —o uno que acaba de vencer en el
        // poller del shell— tiene que repintar esta lista igual que lo hace una edición del tablero.
        _snoozedReminderService.SnoozesChanged += Reload;

        _undoTimer = new DispatcherTimer { Interval = UndoWindow };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            LastDismissed = null;
        };

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

    public void Reload()
    {
        // Reconstruir la lista deja sin sentido cualquier menú abierto: el item que lo tenía desplegado
        // deja de existir en este mismo instante.
        SnoozeMenuItem = null;
        Items.Clear();

        var soonWindowDays = Math.Clamp(_settingsService.Settings.ReminderSoonWindowDays, 1, 90);
        var soonLimit = DateTime.UtcNow + TimeSpan.FromDays(soonWindowDays);

        var kanbanItems = _kanbanService.Board.Columns
            .SelectMany(column => column.Cards.Select(card => new { column.Title, Card = card }))
            .Where(x => x.Card.CompletedAtUtc is null)
            .Where(x => x.Card.DueDateUtc is { } due && due <= soonLimit)
            .Where(x => !_dismissedReminderService.IsDismissed(x.Card.Id, x.Card.DueDateUtc!.Value))
            .Select(x => new ReminderItemViewModel(x.Card, x.Title));

        var standaloneItems = _reminderService.ActiveReminders
            .Where(r => !r.IsCompleted)
            .Where(r => r.NotifyAtUtc <= soonLimit)
            .Where(r => !_dismissedReminderService.IsDismissed(r.Id, r.NotifyAtUtc))
            .Select(r => new ReminderItemViewModel(r));

        // Orden: primero lo que reclama atención por su fecha, y al final lo aplazado (que por definición
        // el usuario ya ha decidido no mirar todavía), cada bloque por fecha ascendente.
        var merged = kanbanItems.Concat(standaloneItems)
            .Select(item => item.ApplySnooze(_snoozedReminderService.Find(item.SourceId, item.DueDateUtc)))
            .OrderBy(r => r.IsSnoozed)
            .ThenBy(r => r.IsSnoozed ? r.SnoozedUntilUtc!.Value : r.DueDateUtc);
        foreach (var reminder in merged) Items.Add(reminder);

        OverdueCount = Items.Count(r => r.Bucket == ReminderBucket.Overdue);
        OnPropertyChanged(nameof(HasItems));
    }

    [RelayCommand]
    private void Toggle()
    {
        // Kept even though BoardChanged already keeps this live: bucketing (Overdue/Today/Soon) shifts
        // purely with the passage of time, not just with board edits, so "about to be shown" is still
        // worth a fresh pass to catch a card that crossed into "vencida" since the panel last opened.
        if (!IsOpen) Reload();
        IsOpen = !IsOpen;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void OpenCard(ReminderItemViewModel item)
    {
        IsOpen = false;
        if (item.SourceKind == ReminderItemSourceKind.Reminder) _onOpenReminder(item.SourceId);
        else _onOpenCard(item.SourceId);
    }

    /// <summary>"Ya lo vi, no me lo muestres hasta mañana" (see docs/notificaciones-y-recordatorios.md).
    /// Doesn't touch the underlying card; only records the dismissal (keyed to today's due date) so a
    /// later reschedule makes it reappear instead of hiding it silently forever. Leaves a short "Deshacer"
    /// window open (Fase 10, docs/analisis-proyecto/roadmap.md, "undo al Dismiss") — see
    /// <see cref="UndoDismiss"/>.</summary>
    [RelayCommand]
    private void Dismiss(ReminderItemViewModel item)
    {
        _dismissedReminderService.Dismiss(item.SourceId, item.DueDateUtc);
        // Descartar gana a aplazar: dejar vivo el aplazamiento de algo que el usuario acaba de apartar
        // lo haría reaparecer solo al vencer, que es justo lo contrario de lo que pidió.
        _snoozedReminderService.Cancel(item.SourceId);
        Items.Remove(item);
        OverdueCount = Items.Count(r => r.Bucket == ReminderBucket.Overdue);
        OnPropertyChanged(nameof(HasItems));

        LastDismissed = item;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>Revierte el último <see cref="Dismiss"/> mientras el aviso siga visible — quita la entrada
    /// de <see cref="Services.DismissedReminderService"/> (por id de tarjeta, no por la fecha exacta: dentro
    /// de la ventana de unos segundos que dura el aviso no ha podido cambiar) y recarga para que la tarjeta
    /// reaparezca en el cubo que le corresponda ahora mismo.</summary>
    [RelayCommand]
    private void UndoDismiss()
    {
        if (LastDismissed is not { } item) return;

        _undoTimer.Stop();
        LastDismissed = null;
        _dismissedReminderService.RemoveForCard(item.SourceId);
        Reload();
    }

    // ===================== Aplazar (T5, .docs/funcionalidades-pendientes-priorizadas.md §4.1) =====================
    // Aplazar no toca la tarjeta ni el recordatorio de origen: escribe en SnoozedReminderService, que es
    // quien vuelve a avisar cuando llegue la hora (ver MainWindowViewModel.CheckDueSnoozes). Ver
    // Models.SnoozedReminder para por qué aplazar y reprogramar son cosas distintas.

    /// <summary>Item cuyo menú "Aplazar" está desplegado, o <see langword="null"/> con todos cerrados. Uno
    /// solo a la vez: son menús dentro de un popover de 320 px, y dos abiertos a la vez no caben ni se
    /// entienden.</summary>
    [ObservableProperty]
    private ReminderItemViewModel? _snoozeMenuItem;

    /// <summary>Minutos del aplazamiento "Personalizado…", sembrados con el último valor usado.</summary>
    [ObservableProperty]
    private decimal _customSnoozeMinutes;

    public bool IsCustomSnoozeOpen => SnoozeMenuItem is not null;

    /// <summary>Etiquetas de los dos valores rápidos, que el usuario puede cambiar en Ajustes —
    /// el menú tiene que decir "15 min" si eso es lo que hará, no un "10 min" fijo de plantilla.</summary>
    public string SnoozePrimaryLabel => FormatDuration(_settingsService.Settings.SnoozePrimaryMinutes);

    public string SnoozeSecondaryLabel => FormatDuration(_settingsService.Settings.SnoozeSecondaryMinutes);

    partial void OnSnoozeMenuItemChanged(ReminderItemViewModel? value)
    {
        if (value is not null) CustomSnoozeMinutes = _settingsService.Settings.SnoozeCustomMinutes;
        foreach (var item in Items) item.IsSnoozeMenuOpen = ReferenceEquals(item, value);
        OnPropertyChanged(nameof(IsCustomSnoozeOpen));
    }

    /// <summary>"45 min" / "1 h" / "1 h 30 min" — el mismo texto sirve para el botón del menú y para el
    /// resumen del ajuste, así que se formatea una sola vez aquí.</summary>
    public static string FormatDuration(int minutes)
    {
        var loc = LocalizationService.Instance;
        if (minutes < 60) return Plural.Get("Reminder_Snooze_MinutesFormat", minutes, minutes);

        var hours = minutes / 60;
        var rest = minutes % 60;
        var hoursText = Plural.Get("Reminder_Snooze_HoursFormat", hours, hours);
        return rest == 0
            ? hoursText
            : hoursText + " " + Plural.Get("Reminder_Snooze_MinutesFormat", rest, rest);
    }

    [RelayCommand]
    private void ToggleSnoozeMenu(ReminderItemViewModel item) =>
        SnoozeMenuItem = ReferenceEquals(SnoozeMenuItem, item) ? null : item;

    [RelayCommand]
    private void SnoozePrimary(ReminderItemViewModel item) => Snooze(item, SnoozeOption.QuickPrimary);

    [RelayCommand]
    private void SnoozeSecondary(ReminderItemViewModel item) => Snooze(item, SnoozeOption.QuickSecondary);

    [RelayCommand]
    private void SnoozeEvening(ReminderItemViewModel item) => Snooze(item, SnoozeOption.Evening);

    [RelayCommand]
    private void SnoozeTomorrow(ReminderItemViewModel item) => Snooze(item, SnoozeOption.Tomorrow);

    /// <summary>Confirma el aplazamiento personalizado con los minutos del control. Recuerda el valor en
    /// los ajustes para la próxima vez, igual que el editor de notas recuerda su última plantilla.</summary>
    [RelayCommand]
    private void SnoozeCustom(ReminderItemViewModel item)
    {
        var minutes = (int)Math.Clamp(CustomSnoozeMinutes, SnoozeSchedule.MinMinutes, SnoozeSchedule.MaxMinutes);
        _settingsService.Settings.SnoozeCustomMinutes = minutes;
        _settingsService.Save();
        Snooze(item, SnoozeOption.Custom, minutes);
    }

    /// <summary>Devuelve el aviso a su sitio sin esperar a que venza el aplazamiento.</summary>
    [RelayCommand]
    private void CancelSnooze(ReminderItemViewModel item)
    {
        SnoozeMenuItem = null;
        // Cancel dispara SnoozesChanged, que ya provoca el Reload de la lista.
        _snoozedReminderService.Cancel(item.SourceId);
    }

    private void Snooze(ReminderItemViewModel item, SnoozeOption option, int customMinutes = 0)
    {
        SnoozeMenuItem = null;
        var untilUtc = SnoozeSchedule.ResolveRespectingQuietHours(option, DateTime.UtcNow, _settingsService.Settings, customMinutes);
        _snoozedReminderService.Snooze(item.SnoozeSourceKind, item.SourceId, item.DueDateUtc, untilUtc);
    }
}
