using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
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
        Action<string> onOpenCard,
        Action<string> onOpenReminder)
    {
        _kanbanService = kanbanService;
        _reminderService = reminderService;
        _settingsService = settingsService;
        _dismissedReminderService = dismissedReminderService;
        _onOpenCard = onOpenCard;
        _onOpenReminder = onOpenReminder;

        // Live refresh: a due date edited from the Kanban page, or a reminder created/fired/completed from
        // the Recordatorios page (or the calendar), while this panel is open now shows up without waiting
        // for the next Toggle (see docs/notificaciones-y-recordatorios.md).
        _kanbanService.BoardChanged += Reload;
        _reminderService.RemindersChanged += Reload;

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
    public void RefreshLocalizedLabels() => Reload();

    public void Reload()
    {
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

        var merged = kanbanItems.Concat(standaloneItems).OrderBy(r => r.DueDateUtc);
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
}
