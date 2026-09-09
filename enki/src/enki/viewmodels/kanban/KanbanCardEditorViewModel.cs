using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public enum CardEditorMode
{
    Create,
    Edit
}

/// <summary>Draft state backing the popup used to both create and edit a <see cref="KanbanCard"/> —
/// nothing here touches the underlying model/board until <see cref="BuildCard"/>/<see cref="ApplyTo"/>
/// is called from <see cref="KanbanViewModel.SaveCardEditor"/>, so cancelling just discards this object.</summary>
public sealed partial class KanbanCardEditorViewModel : ObservableObject
{
    public const int DescriptionMaxLength = 240;

    private readonly VaultService? _vaultService;
    private readonly SessionService? _sessionService;
    private readonly Action<string>? _onOpenNote;

    public CardEditorMode Mode { get; }
    public bool IsCreateMode => Mode == CardEditorMode.Create;

    public KanbanColumnViewModel TargetColumn { get; }
    public KanbanCardViewModel? SourceCard { get; }

    /// <summary>Every column on the board, for the "move to column" selector — a keyboard/screen-reader
    /// reachable alternative to dragging a card between columns (docs/analisis-proyecto/roadmap.md, Fase 7).
    /// Starts on <see cref="TargetColumn"/> and only actually moves the card on save
    /// (<see cref="KanbanViewModel.SaveCardEditor"/>) if changed, same "nothing until Guardar" contract as
    /// every other field here.</summary>
    public IReadOnlyList<KanbanColumnViewModel> AvailableColumns { get; }

    [ObservableProperty]
    private KanbanColumnViewModel _selectedColumn;

    public ObservableCollection<KanbanSubtaskItemViewModel> Subtasks { get; } = [];

    /// <summary>Notas que calzan con <see cref="NotePickerSearchText"/> en el picker de "vincular nota" —
    /// ver <see cref="ToggleNotePicker"/>.</summary>
    public ObservableCollection<NotePickerItemViewModel> NotePickerResults { get; } = [];

    /// <summary>42 fixed cells (6 weeks) for the month currently shown in the due-date calendar
    /// popover — see <see cref="RebuildCalendarDays"/>.</summary>
    public ObservableCollection<KanbanCalendarDayViewModel> CalendarDays { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private KanbanPriority _selectedPriority = KanbanPriority.Medium;

    [ObservableProperty]
    private DateTime? _dueDate;

    /// <summary>Opt-in — the calendar alone only ever picks a day, leaving the time at local midnight
    /// (see <see cref="KanbanCard.IsPastDue"/>'s "no time of day" case). Turning this on adds
    /// <see cref="DueHour"/>/<see cref="DueMinute"/> on top of that day instead of replacing it, so
    /// clearing it back to off drops the time and returns to the old all-day semantics.</summary>
    [ObservableProperty]
    private bool _hasDueTime;

    [ObservableProperty]
    private int _dueHour = 9;

    [ObservableProperty]
    private int _dueMinute;

    /// <summary>Opt-in franja horaria (docs/calendar-feature/04-vistas-y-navegacion.md §5) — only ever
    /// meaningful alongside <see cref="HasDueTime"/>, since a duration without a start time of day has
    /// nothing to be relative to. Off by default even when <see cref="HasDueTime"/> is on, so the common
    /// "just a due time, no real duration" case never grows an extra field the user didn't ask for.</summary>
    [ObservableProperty]
    private bool _hasDuration;

    /// <summary>Minutes long, same 5-minute grain as <see cref="IncrementDueMinute"/> and
    /// <c>PolarTask.TimeEstimate</c>'s stepper — the calendar's resize handle
    /// (CalendarView.axaml.cs, doc 05 §2b) snaps to the same grain, so a duration set here always matches
    /// something the resize gesture could also produce.</summary>
    [ObservableProperty]
    private int _durationMinutes = 30;

    [ObservableProperty]
    private string _tagsInput = string.Empty;

    /// <summary>Ruta completa de la nota vinculada (enlace nota↔tarea, "Capa 1"), o <c>null</c> si no hay
    /// ninguna. Se persiste tal cual en
    /// <see cref="KanbanCard.LinkedNotePath"/> vía <see cref="BuildCard"/>/<see cref="ApplyTo"/>.</summary>
    [ObservableProperty]
    private string? _linkedNotePath;

    /// <summary>Etiqueta legible de <see cref="LinkedNotePath"/> para el botón del formulario — el título
    /// de la nota si todavía existe en el vault, o su nombre de archivo si el enlace quedó apuntando a una
    /// ruta que ya no existe (mismo criterio tolerante que <see cref="KanbanCard.LinkedNotePath"/>).</summary>
    [ObservableProperty]
    private string _linkedNoteLabel = string.Empty;

    [ObservableProperty]
    private bool _isNotePickerOpen;

    [ObservableProperty]
    private string _notePickerSearchText = string.Empty;

    public bool HasLinkedNote => !string.IsNullOrEmpty(LinkedNotePath);

    /// <summary>What the picker trigger button actually shows — <see cref="LinkedNoteLabel"/> once a note
    /// is linked, or a placeholder prompt before that, same "combined result, not raw field" pattern as
    /// <see cref="DueDateDisplayLabel"/>.</summary>
    public string LinkedNoteDisplayLabel => HasLinkedNote ? LinkedNoteLabel : LocalizationService.Instance["Kanban_CardEditor_LinkNotePlaceholder"];

    [ObservableProperty]
    private bool _isCalendarOpen;

    [ObservableProperty]
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    private string _newSubtaskTitle = string.Empty;

    public string DescriptionCounterLabel => $"{Description.Length}/{DescriptionMaxLength}";
    public bool CanSave => !string.IsNullOrWhiteSpace(Title);

    public bool HasDueDate => DueDate is not null;

    public string DueDateDisplayLabel => DueDate is { } due
        ? (HasDueTime
            ? $"{due.ToString("d", CultureInfo.CurrentCulture)} {DueHour:00}:{DueMinute:00}"
            : due.ToString("d", CultureInfo.CurrentCulture))
        : LocalizationService.Instance["Kanban_CardEditor_SelectDatePlaceholder"];

    public string CalendarMonthLabel =>
        $"{CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(CalendarMonth.Month).ToUpper(CultureInfo.CurrentCulture)} {CalendarMonth.Year}";

    /// <summary>The actual franja horaria the duration produces (e.g. "21:30 – 22:15"), shown read-only
    /// next to the minutes stepper so the user never has to do the arithmetic themselves — same principle
    /// as <see cref="DueDateDisplayLabel"/> already showing the combined result instead of raw fields.</summary>
    public string DurationRangeLabel => HasDueTime && HasDuration
        ? $"{DueHour:00}:{DueMinute:00} – {new DateTime(1, 1, 1, DueHour, DueMinute, 0).AddMinutes(DurationMinutes):HH:mm}"
        : string.Empty;

    /// <summary>Longest duration that keeps the end time within the same day as the start — resizing from
    /// the calendar grid enforces the same cap (<c>CalendarViewModel.MaxDurationMinutesSameDay</c>), so a
    /// value set here can never disagree with what dragging the chip's bottom edge would allow.</summary>
    private int MaxDurationMinutes => Math.Max(5, 24 * 60 - (DueHour * 60 + DueMinute));

    /// <summary>What actually gets saved: the picked day, plus the picked time of day when
    /// <see cref="HasDueTime"/> is on — otherwise the day alone (local midnight), same as before this
    /// field existed. Shared by <see cref="BuildCard"/>/<see cref="ApplyTo"/>.</summary>
    private DateTime? CombinedDueDate => DueDate is { } date
        ? (HasDueTime ? date.Date.Add(new TimeSpan(DueHour, DueMinute, 0)) : date.Date)
        : null;

    public string HeadingEyebrow => IsCreateMode
        ? LocalizationService.Instance["Kanban_CardEditor_NewEyebrow"]
        : LocalizationService.Instance["Kanban_CardEditor_EditEyebrow"];
    public string HeadingTitle => IsCreateMode
        ? LocalizationService.Instance["Kanban_CardEditor_CreateTitle"]
        : LocalizationService.Instance["Kanban_CardEditor_EditTitle"];
    public string ConfirmLabel => IsCreateMode
        ? LocalizationService.Instance["Common_Create"]
        : LocalizationService.Instance["Common_Save"];

    /// <summary>El editor es un overlay dentro de KanbanView, no una ventana modal: la barra lateral sigue
    /// activa, así que se puede dejar una tarjeta abierta, ir a Ajustes, cambiar de idioma y volver. Sin
    /// esto, el diálogo reaparecía con su cabecera, su botón de confirmar y sus fechas en el idioma
    /// anterior. Lo invoca <see cref="KanbanViewModel.RefreshLocalizedLabels"/>, que no reconstruye el
    /// editor abierto porque haría perder lo que el usuario llevara escrito.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(HeadingEyebrow));
        OnPropertyChanged(nameof(HeadingTitle));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(LinkedNoteDisplayLabel));
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        OnPropertyChanged(nameof(CalendarMonthLabel));
    }

    private KanbanCardEditorViewModel(CardEditorMode mode, KanbanColumnViewModel targetColumn,
        IReadOnlyList<KanbanColumnViewModel> availableColumns, KanbanCardViewModel? sourceCard,
        VaultService? vaultService, SessionService? sessionService, Action<string>? onOpenNote)
    {
        Mode = mode;
        TargetColumn = targetColumn;
        AvailableColumns = availableColumns;
        _selectedColumn = targetColumn;
        SourceCard = sourceCard;
        _vaultService = vaultService;
        _sessionService = sessionService;
        _onOpenNote = onOpenNote;

        RebuildCalendarDays();
    }

    public static KanbanCardEditorViewModel ForCreate(KanbanColumnViewModel column,
        IReadOnlyList<KanbanColumnViewModel> availableColumns,
        VaultService? vaultService = null, SessionService? sessionService = null, Action<string>? onOpenNote = null) =>
        new(CardEditorMode.Create, column, availableColumns, null, vaultService, sessionService, onOpenNote);

    public static KanbanCardEditorViewModel ForEdit(KanbanColumnViewModel column, KanbanCardViewModel card,
        IReadOnlyList<KanbanColumnViewModel> availableColumns,
        VaultService? vaultService = null, SessionService? sessionService = null, Action<string>? onOpenNote = null)
    {
        var dueLocal = card.Card.DueDateUtc?.ToLocalTime();
        var dueDate = dueLocal?.Date;
        var hasDueTime = dueLocal is { } dl && dl.TimeOfDay != TimeSpan.Zero;
        var editor = new KanbanCardEditorViewModel(CardEditorMode.Edit, column, availableColumns, card, vaultService, sessionService, onOpenNote)
        {
            Title = card.Title,
            Description = card.Description,
            SelectedPriority = card.Priority,
            TagsInput = string.Join(", ", card.Card.Tags),
            DueDate = dueDate,
            HasDueTime = hasDueTime,
            DueHour = hasDueTime ? dueLocal!.Value.Hour : 9,
            DueMinute = hasDueTime ? dueLocal!.Value.Minute : 0,
            HasDuration = hasDueTime && card.Card.DurationMinutes is > 0,
            DurationMinutes = card.Card.DurationMinutes is > 0 ? card.Card.DurationMinutes.Value : 30,
            LinkedNotePath = card.Card.LinkedNotePath
        };

        if (dueDate is { } due) editor.CalendarMonth = new DateTime(due.Year, due.Month, 1);
        editor.RefreshLinkedNoteLabel();

        foreach (var subtask in card.Card.Subtasks)
            editor.Subtasks.Add(new KanbanSubtaskItemViewModel(subtask.Id, subtask.Title, subtask.IsDone));

        return editor;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(DescriptionCounterLabel));

    partial void OnNewSubtaskTitleChanged(string value) => AddSubtaskCommand.NotifyCanExecuteChanged();

    partial void OnDueDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(HasDueDate));
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        RebuildCalendarDays();
    }

    partial void OnHasDueTimeChanged(bool value)
    {
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        // A duration is only meaningful alongside a start time — turning the time off drops any duration
        // that was set, same "no orphaned state" rule ClearDueDate already follows for HasDueTime itself.
        if (!value) HasDuration = false;
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnDueHourChanged(int value)
    {
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        DurationMinutes = Math.Min(DurationMinutes, MaxDurationMinutes);
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnDueMinuteChanged(int value)
    {
        OnPropertyChanged(nameof(DueDateDisplayLabel));
        DurationMinutes = Math.Min(DurationMinutes, MaxDurationMinutes);
        OnPropertyChanged(nameof(DurationRangeLabel));
    }

    partial void OnHasDurationChanged(bool value) => OnPropertyChanged(nameof(DurationRangeLabel));

    partial void OnDurationMinutesChanged(int value) => OnPropertyChanged(nameof(DurationRangeLabel));

    /// <summary>Same 5-minute grain as <see cref="IncrementDueMinute"/> — see
    /// <see cref="DurationMinutes"/>'s doc comment for why that grain matters here specifically.</summary>
    [RelayCommand]
    private void IncrementDuration() => DurationMinutes = Math.Min(DurationMinutes + 5, MaxDurationMinutes);

    [RelayCommand]
    private void DecrementDuration() => DurationMinutes = Math.Max(DurationMinutes - 5, 5);

    /// <summary>Backs the custom hour/minute stepper in the due-date popover (replaces the stock
    /// NumericUpDown, which didn't match the rest of the form's flat/hairline visual language) —
    /// same [0,23]/[0,59] clamping the old control's Minimum/Maximum enforced.</summary>
    [RelayCommand]
    private void IncrementDueHour() => DueHour = Math.Min(DueHour + 1, 23);

    [RelayCommand]
    private void DecrementDueHour() => DueHour = Math.Max(DueHour - 1, 0);

    [RelayCommand]
    private void IncrementDueMinute() => DueMinute = Math.Min(DueMinute + 5, 59);

    [RelayCommand]
    private void DecrementDueMinute() => DueMinute = Math.Max(DueMinute - 5, 0);

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthLabel));
        RebuildCalendarDays();
    }

    [RelayCommand]
    private void SelectPriority(string priorityName)
    {
        if (Enum.TryParse<KanbanPriority>(priorityName, out var priority)) SelectedPriority = priority;
    }

    /// <summary>Opens the calendar jumped to the month of the current due date (or today, if none is
    /// set yet) so it never opens on whatever month was last browsed to.</summary>
    [RelayCommand]
    private void ToggleCalendar()
    {
        if (!IsCalendarOpen)
        {
            var anchor = DueDate ?? DateTime.Today;
            CalendarMonth = new DateTime(anchor.Year, anchor.Month, 1);
            RebuildCalendarDays();
        }

        IsCalendarOpen = !IsCalendarOpen;
    }

    [RelayCommand]
    private void PreviousMonth() => CalendarMonth = CalendarMonth.AddMonths(-1);

    [RelayCommand]
    private void NextMonth() => CalendarMonth = CalendarMonth.AddMonths(1);

    [RelayCommand]
    private void SelectCalendarDay(KanbanCalendarDayViewModel day)
    {
        DueDate = day.Date;
        IsCalendarOpen = false;
    }

    [RelayCommand]
    private void SetDueDateToday()
    {
        CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        DueDate = DateTime.Today;
    }

    [RelayCommand]
    private void ClearDueDate()
    {
        DueDate = null;
        HasDueTime = false;
        HasDuration = false;
    }

    private void RebuildCalendarDays()
    {
        var today = DateTime.Today;

        CalendarDays.Clear();
        foreach (var date in MonthGridCalculator.BuildDates(CalendarMonth))
        {
            CalendarDays.Add(new KanbanCalendarDayViewModel(
                date,
                isCurrentMonth: date.Month == CalendarMonth.Month,
                isToday: date == today,
                isSelected: DueDate is { } due && due.Date == date));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddSubtask))]
    private void AddSubtask()
    {
        var title = NewSubtaskTitle.Trim();
        if (title.Length == 0) return;

        Subtasks.Add(new KanbanSubtaskItemViewModel(Guid.NewGuid().ToString("N"), title, false));
        NewSubtaskTitle = string.Empty;
    }

    private bool CanAddSubtask() => !string.IsNullOrWhiteSpace(NewSubtaskTitle);

    [RelayCommand]
    private void RemoveSubtask(KanbanSubtaskItemViewModel item) => Subtasks.Remove(item);

    // ===================== Nota vinculada (docs/analisis-proyecto/04 §2, "Capa 1") =====================

    /// <summary>Every note in the vault, cached the moment the picker opens (see
    /// <see cref="ToggleNotePicker"/>) so typing in <see cref="NotePickerSearchText"/> filters in memory
    /// instead of re-walking the vault's directory tree on every keystroke.</summary>
    private List<(NoteFile Note, string Workspace)> _allNotesCache = [];

    /// <summary>Abre el picker cargando todas las notas del vault (raíz + cada sesión, mismo alcance que
    /// <see cref="ViewModels.GlobalSearchViewModel"/> más las sesiones, que esa búsqueda global no cubre —
    /// ver <see cref="Services.SessionService.List"/>) o lo cierra si ya estaba abierto.</summary>
    [RelayCommand]
    private void ToggleNotePicker()
    {
        if (!IsNotePickerOpen)
        {
            _allNotesCache = CollectAllNotes().ToList();
            NotePickerSearchText = string.Empty;
            RefreshNotePickerResults();
        }
        IsNotePickerOpen = !IsNotePickerOpen;
    }

    partial void OnNotePickerSearchTextChanged(string value) => RefreshNotePickerResults();

    private void RefreshNotePickerResults()
    {
        NotePickerResults.Clear();

        var query = NotePickerSearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _allNotesCache
            : _allNotesCache.Where(t => t.Note.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var (note, workspace) in matches.Take(40))
            NotePickerResults.Add(new NotePickerItemViewModel(note, workspace));
    }

    /// <summary>Toda nota del vault, con la etiqueta de a qué workspace pertenece — delega en
    /// <see cref="Services.SessionService.ListAllNotesByWorkspace"/> (raíz del vault más cada sesión
    /// desbloqueada; una sesión bloqueada se salta en vez de intentar leerla, evitando el
    /// <see cref="Services.SessionLockedException"/> que antes reventaba este picker si había alguna
    /// bloqueada en el vault). Sin <see cref="_sessionService"/> (headless/tests), cae a solo las notas de
    /// la raíz.</summary>
    private IEnumerable<(NoteFile Note, string Workspace)> CollectAllNotes()
    {
        if (_vaultService is not { } vaultService) yield break;

        if (_sessionService is not { } sessionService)
        {
            foreach (var note in vaultService.GetAllNotesRecursive(vaultService.VaultPath))
                yield return (note, LocalizationService.Instance["Kanban_NotePicker_VaultWorkspaceLabel"]);
            yield break;
        }

        foreach (var (note, workspace) in sessionService.ListAllNotesByWorkspace())
            yield return (note, workspace);
    }

    [RelayCommand]
    private void SelectLinkedNote(NotePickerItemViewModel item)
    {
        LinkedNotePath = item.Note.FullPath;
        LinkedNoteLabel = item.Note.Title;
        IsNotePickerOpen = false;
    }

    [RelayCommand]
    private void ClearLinkedNote()
    {
        LinkedNotePath = null;
        LinkedNoteLabel = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(HasLinkedNote))]
    private void OpenLinkedNote()
    {
        if (LinkedNotePath is { } path) _onOpenNote?.Invoke(path);
    }

    partial void OnLinkedNotePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLinkedNote));
        OnPropertyChanged(nameof(LinkedNoteDisplayLabel));
        OpenLinkedNoteCommand.NotifyCanExecuteChanged();
    }

    partial void OnLinkedNoteLabelChanged(string value) => OnPropertyChanged(nameof(LinkedNoteDisplayLabel));

    /// <summary>Resuelve <see cref="LinkedNotePath"/> a un título legible en modo edición — llamado una
    /// sola vez al construir el editor (<see cref="ForEdit"/>), no en cada cambio de propiedad, porque
    /// solo el valor inicial (venido de la tarjeta) necesita resolverse contra disco; una vez que el
    /// usuario elige una nota nueva en el picker, <see cref="SelectLinkedNote"/> ya conoce su título sin
    /// tener que releerlo.</summary>
    private void RefreshLinkedNoteLabel()
    {
        if (LinkedNotePath is not { } path) return;
        var note = _vaultService?.GetNote(path);
        LinkedNoteLabel = note?.Title ?? System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>What actually gets persisted for the franja horaria — only when both a real start time and
    /// <see cref="HasDuration"/> are set, matching <see cref="DurationRangeLabel"/>'s own gate.</summary>
    private int? EffectiveDurationMinutes => HasDueTime && HasDuration ? DurationMinutes : null;

    /// <summary>Same comma-separated parsing used by <see cref="PolarTaskEditorViewModel.BuildTags"/>.
    /// Trimming and case-insensitive deduplication keep the input forgiving.</summary>
    public string[] BuildTags() => TagsInput
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public KanbanCard BuildCard(string id) => new()
    {
        Id = id,
        Title = Title.Trim(),
        Description = Description.Trim(),
        Priority = SelectedPriority,
        DueDateUtc = ToUtc(CombinedDueDate),
        DurationMinutes = EffectiveDurationMinutes,
        Subtasks = Subtasks.Select(ToModel).ToList(),
        Tags = BuildTags().ToList(),
        LinkedNotePath = LinkedNotePath
    };

    public void ApplyTo(KanbanCard card)
    {
        card.Title = Title.Trim();
        card.Description = Description.Trim();
        card.Priority = SelectedPriority;
        card.DueDateUtc = ToUtc(CombinedDueDate);
        card.DurationMinutes = EffectiveDurationMinutes;
        card.Subtasks = Subtasks.Select(ToModel).ToList();
        card.Tags = BuildTags().ToList();
        card.LinkedNotePath = LinkedNotePath;
    }

    private static KanbanSubtask ToModel(KanbanSubtaskItemViewModel item) =>
        new() { Id = item.Id, Title = item.Title, IsDone = item.IsDone };

    private static DateTime? ToUtc(DateTime? localDate) =>
        localDate is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime() : null;
}
