using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Full "Recordatorios" page — user-created reminders (title, description, date, time, icon). The
/// title-bar inbox button (<see cref="ReminderInboxViewModel"/>) surfaces the same reminders (merged with
/// Kanban due dates) as a "what's due soon" preview; this page is the actual CRUD surface and detail view
/// it jumps to. A reminder that comes due is turned into a real notification by <c>MainWindowViewModel</c>'s
/// scheduler, which also calls <see cref="Reload"/> here so an open page picks up the change live. Also reloads
/// whenever <see cref="PolarSyncService"/> finishes a push/pull — otherwise a reminder pulled down from
/// Polar while this page is already open wouldn't show up until the user navigated away and back.</summary>
public sealed partial class RemindersViewModel : ViewModelBase
{
    /// <summary>Monday-first, matching <see cref="ReminderEditorViewModel.RebuildCalendarDays"/>'s grid
    /// order — backs <see cref="WeekdayLabels"/> below.</summary>
    private static readonly DayOfWeek[] WeekOrderMondayFirst =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    private readonly ReminderService _reminderService;
    private readonly PolarAuthService _polarAuthService;
    private readonly PolarSyncService _polarSyncService;

    public ObservableCollection<ReminderListItemViewModel> Items { get; } = [];

    /// <summary>2-letter (or culture-equivalent) weekday header for the "nuevo recordatorio" popover's
    /// inline calendar grid (<see cref="ReminderEditorViewModel"/>'s <c>DataContext</c>, cross-referenced
    /// from RemindersView.axaml the same way its Cancelar/Crear buttons already reach this view model) —
    /// was 7 static TextBlocks hardcoding "LU"/"MA"/"MI"... in the view, another instance of the
    /// array-duplication bug documented in docs/agent-docs/internacionalizacion-i18n.md §1.7; now built
    /// once from <see cref="CultureInfo"/> instead, same approach as
    /// <see cref="CalendarQuickCreateViewModel.WeekdayHeaders"/>.</summary>
    public IReadOnlyList<string> WeekdayLabels { get; private set; } = BuildWeekdayLabels();

    private static List<string> BuildWeekdayLabels()
    {
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        return WeekOrderMondayFirst.Select(d => dtf.GetShortestDayName(d).ToUpper(CultureInfo.CurrentCulture)).ToList();
    }

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private int _unreadCount;

    /// <summary>Non-null while the "nuevo recordatorio" popup is open.</summary>
    [ObservableProperty]
    private ReminderEditorViewModel? _editor;

    /// <summary>Non-null while the read-only detail dialog is open for a reminder.</summary>
    [ObservableProperty]
    private ReminderListItemViewModel? _detail;

    /// <summary>True while the "limpiar recordatorios" confirmation dialog is open.</summary>
    [ObservableProperty]
    private bool _pendingClearAll;

    // ===================== Estado de sincronización con Polar =====================

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string? _syncError;

    [ObservableProperty]
    private string _lastSyncedLabel = LocalizationService.Instance["Common_Never"];

    /// <summary>Non-null only while <see cref="PolarSyncService.Status"/> is
    /// <see cref="PolarSyncStatus.Offline"/> and a retry is actually scheduled — "Retrying in 2 minutes",
    /// so the user knows the app isn't just stuck (10.2 §8: "mostrar tiempo hasta el próximo reintento").</summary>
    [ObservableProperty]
    private string? _nextRetryLabel;

    /// <summary>True right after this view's own <see cref="SyncNow"/> click was discarded because another
    /// sync (the background timer, the realtime debounce, or another view's own "Sync now") already held
    /// the gate — distinct from a completed cycle that simply changed nothing (Fase 9,
    /// docs/analisis-proyecto/roadmap.md). Cleared the next time any sync cycle actually completes (see
    /// <see cref="HandleSyncCompleted"/>).</summary>
    [ObservableProperty]
    private bool _lastSyncNowWasSkipped;

    /// <summary>Composed "Sincronizado con Polar · {0}" label — was a fixed-word-order XAML
    /// <c>StringFormat</c> (docs/agent-docs/internacionalizacion-i18n.md §1.3), which can't be reordered
    /// per language. The resource key itself carries the <c>{0}</c> placeholder instead.</summary>
    public string PolarSyncStatusLabel => string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Reminders_PolarSyncStatusLabel"], LastSyncedLabel);

    /// <summary>Composed "No se pudo sincronizar con Polar: {0}" label — same fixed-word-order
    /// <c>StringFormat</c> issue as <see cref="PolarSyncStatusLabel"/>. Null (and therefore hidden in the
    /// view) whenever <see cref="SyncError"/> itself is null.</summary>
    public string? SyncErrorLabel => SyncError is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Reminders_SyncErrorLabel"], SyncError);

    partial void OnLastSyncedLabelChanged(string value) => OnPropertyChanged(nameof(PolarSyncStatusLabel));

    partial void OnSyncErrorChanged(string? value) => OnPropertyChanged(nameof(SyncErrorLabel));

    public RemindersViewModel(ReminderService reminderService, PolarAuthService polarAuthService, PolarSyncService polarSyncService)
    {
        _reminderService = reminderService;
        _polarAuthService = polarAuthService;
        _polarSyncService = polarSyncService;

        _isPolarLinked = polarAuthService.IsLinked;
        polarAuthService.LinkStateChanged += () => IsPolarLinked = polarAuthService.IsLinked;
        polarSyncService.SyncCompleted += HandleSyncCompleted;

        RefreshSyncStatus();

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        RefreshSyncStatus();
        OnPropertyChanged(nameof(PolarSyncStatusLabel));
        OnPropertyChanged(nameof(SyncErrorLabel));
        OnPropertyChanged(nameof(LastSyncedLabel));
        WeekdayLabels = BuildWeekdayLabels();
        OnPropertyChanged(nameof(WeekdayLabels));
        Reload();
        // El editor abierto no se reconstruye en Reload() para no perder lo escrito.
        Editor?.RefreshLocalizedLabels();
    }

    private void HandleSyncCompleted(bool tasksChanged, bool remindersChanged)
    {
        RefreshSyncStatus();
        LastSyncNowWasSkipped = false;
        if (remindersChanged) Reload();
    }

    private void RefreshSyncStatus()
    {
        SyncError = _polarSyncService.LastError;
        LastSyncedLabel = _polarSyncService.LastSyncedUtc is { } utc
            ? RelativeTimeFormatter.FormatAbsoluteShort(utc)
            : LocalizationService.Instance["Common_Never"];
        NextRetryLabel = _polarSyncService.Status == PolarSyncStatus.Offline
            && _polarSyncService.NextSyncAttemptUtc is { } next
                ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Sync_NextRetryLabel"], RelativeTimeFormatter.FormatUntil(next, DateTime.UtcNow))
                : null;
    }

    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        var outcome = await _polarSyncService.SyncNowAsync();
        IsSyncing = false;
        // A completed cycle clears this via HandleSyncCompleted (wired to PolarSyncService.SyncCompleted),
        // which also refreshes status and reloads if the sync actually changed something.
        LastSyncNowWasSkipped = outcome == PolarSyncOutcome.SkippedAlreadyRunning;
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var reminder in _reminderService.ActiveReminders)
            Items.Add(new ReminderListItemViewModel(reminder));

        IsEmpty = Items.Count == 0;
        UnreadCount = Items.Count(i => !i.IsRead);
    }

    // ===================== Crear recordatorio =====================

    /// <summary>Abre el formulario de "nuevo recordatorio". La guarda evita que el atajo Ctrl+N
    /// (RemindersView.axaml KeyBindings) reinicie el borrador si el editor ya está abierto — el botón de
    /// la barra no lo necesita porque el overlay modal lo deja inalcanzable, pero el teclado sigue
    /// llegando al comando.</summary>
    [RelayCommand]
    private void OpenCreateEditor()
    {
        if (Editor is not null) return;
        Editor = new ReminderEditorViewModel();
    }

    [RelayCommand]
    private void CancelEditor() => Editor = null;

    [RelayCommand]
    private void SaveEditor()
    {
        if (Editor is not { CanSave: true } editor) return;

        _reminderService.Create(
            editor.Title.Trim(),
            editor.Description.Trim(),
            editor.BuildNotifyAtUtc(),
            editor.SelectedIcon.ToString(),
            recurrence: editor.Recurrence);

        Editor = null;
        Reload();
    }

    // ===================== Detalle =====================

    /// <summary>Entry point for the calendar's "Editar" action (docs/calendar-feature/
    /// 06-creacion-y-edicion-de-items.md §3) — <see cref="ReminderEditorViewModel"/> has no edit mode of
    /// its own (only "crear"), so the closest existing editing surface for an already-created reminder is
    /// this read-only-plus-actions detail dialog (complete/eliminar), same one opened by clicking a row
    /// here directly.</summary>
    public void OpenReminderById(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            // Items is only rebuilt on Reload(), never live-synced to ReminderService — a reminder just
            // created elsewhere (e.g. the calendar's "más opciones" quick-create) won't be in it yet.
            Reload();
            item = Items.FirstOrDefault(i => i.Id == id);
        }
        if (item is not null) OpenDetail(item);
    }

    [RelayCommand]
    private void OpenDetail(ReminderListItemViewModel item)
    {
        _reminderService.MarkAsRead(item.Id);
        Detail = item;
        Reload();
        Detail = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    [RelayCommand]
    private void DeleteFromDetail(ReminderListItemViewModel item)
    {
        _reminderService.Delete(item.Id);
        Detail = null;
        Reload();
    }

    /// <summary>Parity with Polar's <c>is_completed</c> (docs/supabase-sync/05) — Enki previously only had
    /// "leído" (seen it) and "disparado" (already notified), neither of which round-trips as "done" to
    /// Polar's own UI, so completing a reminder needed an explicit toggle of its own.</summary>
    [RelayCommand]
    private void ToggleCompleted(ReminderListItemViewModel item)
    {
        _reminderService.ToggleCompleted(item.Id);
        Reload();
        if (Detail is not null) Detail = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    // ===================== Acciones masivas =====================

    [RelayCommand]
    private void MarkAllRead()
    {
        _reminderService.MarkAllAsRead();
        Reload();
    }

    [RelayCommand]
    private void RequestClearAll() => PendingClearAll = true;

    [RelayCommand]
    private void CancelClearAll() => PendingClearAll = false;

    [RelayCommand]
    private void ConfirmClearAll()
    {
        _reminderService.ClearAll();
        PendingClearAll = false;
        Reload();
    }
}
