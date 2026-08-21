using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Which content the Kanban page shows — see docs/supabase-sync/. The local board keeps working
/// unchanged in <see cref="Board"/> mode; <see cref="TaskManagement"/> is an additional, Polar-compatible
/// data domain only reachable once the user links Enki to Polar (<see cref="KanbanViewModel.IsTaskManagementUnlocked"/>).</summary>
public enum KanbanDisplayMode
{
    Board,
    TaskManagement
}

/// <summary>Ordenamiento automático por columna — <see cref="Manual"/> conserva el orden de drag &amp;
/// drop; el resto reordena cada columna al
/// aplicarse y vuelve a dejar la reordenación manual disponible en cuanto se cambia de nuevo a Manual.</summary>
public enum KanbanCardSortMode
{
    Manual,
    Priority,
    DueDate,
    Created
}

public sealed partial class KanbanViewModel : ViewModelBase
{
    private readonly KanbanService _kanbanService;
    private readonly PolarAuthService _polarAuthService;
    private readonly VaultService? _vaultService;
    private readonly SessionService? _sessionService;
    private readonly SettingsService? _settingsService;
    private readonly NotificationService? _notificationService;
    private readonly Action<string>? _onOpenLinkedNote;

    public ObservableCollection<KanbanColumnViewModel> Columns { get; } = [];

    /// <summary>Non-null as soon as the shell initializes — the "Gestión de tareas" content lives here
    /// regardless of whether it's currently unlocked, so linking mid-session shows it immediately without
    /// rebuilding the page.</summary>
    public PolarTaskManagementViewModel TaskManagement { get; }

    [ObservableProperty]
    private KanbanDisplayMode _displayMode = KanbanDisplayMode.Board;

    [ObservableProperty]
    private bool _isTaskManagementUnlocked;

    public string TaskManagementLockedHint => IsTaskManagementUnlocked
        ? LocalizationService.Instance["Kanban_TaskMgmt_UnlockedHint"]
        : LocalizationService.Instance["Kanban_TaskMgmt_LockedHint"];

    partial void OnIsTaskManagementUnlockedChanged(bool value) => OnPropertyChanged(nameof(TaskManagementLockedHint));

    [ObservableProperty]
    private string _boardName = string.Empty;

    [ObservableProperty]
    private int _totalCards;

    [ObservableProperty]
    private int _totalColumns;

    [ObservableProperty]
    private int _highPriorityCount;

    [ObservableProperty]
    private int _overdueCount;

    [ObservableProperty]
    private KanbanCardEditorViewModel? _cardEditor;

    [ObservableProperty]
    private KanbanCardViewModel? _cardPendingDelete;

    /// <summary>Non-null mientras el diálogo de confirmación de borrado de columna está abierto — mismo
    /// patrón request/cancel/confirm que <see cref="_cardPendingDelete"/>, para columnas (Fase 3 de
    /// docs/analisis-proyecto/roadmap.md: borrar una columna era antes irreversible y sin confirmación).</summary>
    [ObservableProperty]
    private KanbanColumnViewModel? _columnPendingDelete;

    /// <summary>Cuerpo del diálogo de confirmación de borrado de columna, con el recuento de tarjetas
    /// ya pluralizado por idioma — se calcula aquí y no en XAML porque el orden de las palabras alrededor
    /// del placeholder no es fijo entre idiomas (docs/agent-docs/internacionalizacion-i18n.md §1.3).</summary>
    public string PendingDeleteColumnMessage
    {
        get
        {
            if (ColumnPendingDelete is not { } column) return string.Empty;
            var cardsFragment = Plural.Get("Kanban_DeleteColumn_Cards", column.Cards.Count, column.Cards.Count);
            return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Kanban_DeleteColumn_Message"], cardsFragment);
        }
    }

    partial void OnColumnPendingDeleteChanged(KanbanColumnViewModel? value) =>
        OnPropertyChanged(nameof(PendingDeleteColumnMessage));

    [ObservableProperty]
    private KanbanCardDetailViewModel? _cardDetail;

    /// <summary>Filtro de etiquetas compartido para el tablero Kanban local. Se recarga con todas las
    /// etiquetas de las tarjetas del tablero y conserva la selección entre recargas.</summary>
    public TagFilterViewModel TagFilter { get; } = new();

    /// <summary>Buscador interno del tablero por título/descripción — combinado con <see cref="TagFilter"/> en
    /// <see cref="ApplyFilters"/>, no como un filtro independiente.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    [ObservableProperty]
    private KanbanCardSortMode _sortMode = KanbanCardSortMode.Manual;

    public KanbanViewModel(KanbanService kanbanService, PolarAuthService polarAuthService, PolarTaskManagementViewModel taskManagement,
        VaultService? vaultService = null, SessionService? sessionService = null, Action<string>? onOpenLinkedNote = null,
        SettingsService? settingsService = null, NotificationService? notificationService = null)
    {
        _kanbanService = kanbanService;
        _polarAuthService = polarAuthService;
        _vaultService = vaultService;
        _sessionService = sessionService;
        _onOpenLinkedNote = onOpenLinkedNote;
        _settingsService = settingsService;
        _notificationService = notificationService;
        TaskManagement = taskManagement;

        _isTaskManagementUnlocked = polarAuthService.IsLinked;
        polarAuthService.LinkStateChanged += () =>
        {
            IsTaskManagementUnlocked = polarAuthService.IsLinked;
            // Losing the link (e.g. an expired refresh token) while "Gestión de tareas" is open would
            // otherwise leave the user stranded on a now-locked tab with no way back via the switch.
            if (!IsTaskManagementUnlocked && DisplayMode == KanbanDisplayMode.TaskManagement)
                DisplayMode = KanbanDisplayMode.Board;
        };
        TagFilter.Changed += ApplyFilters;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(TaskManagementLockedHint));
        OnPropertyChanged(nameof(PendingDeleteColumnMessage));
        Reload();
    }

    [RelayCommand]
    private void SetDisplayMode(string mode)
    {
        if (!Enum.TryParse<KanbanDisplayMode>(mode, out var parsed)) return;
        if (parsed == KanbanDisplayMode.TaskManagement && !IsTaskManagementUnlocked) return;

        DisplayMode = parsed;
    }

    /// <summary>Ordenamiento automático por columna (Fase 2 §5) — aplica <see cref="SortMode"/> a cada
    /// columna y persiste el nuevo orden, mismo criterio que cualquier otro cambio de tablero.</summary>
    [RelayCommand]
    private void SetSortMode(KanbanCardSortMode mode)
    {
        SortMode = mode;
        if (mode == KanbanCardSortMode.Manual) return;

        foreach (var column in Columns) column.Sort(mode);
        Persist();
    }

    public void Reload()
    {
        Columns.Clear();
        foreach (var column in _kanbanService.Board.Columns) AddColumnViewModel(column);
        BoardName = _kanbanService.Board.Name;
        RefreshStats();

        var allTags = _kanbanService.Board.Columns
            .SelectMany(c => c.Cards)
            .SelectMany(card => card.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        TagFilter.Reload(allTags);
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        foreach (var column in Columns) column.ApplyFilters(TagFilter.SelectedTags, SearchText);
    }

    private KanbanColumnViewModel AddColumnViewModel(KanbanColumn column)
    {
        var vm = new KanbanColumnViewModel(column);
        vm.Cards.CollectionChanged += (_, _) => RefreshStats();
        Columns.Add(vm);
        return vm;
    }

    private void RefreshStats()
    {
        var allCards = Columns.SelectMany(c => c.Cards).ToList();
        TotalCards = allCards.Count;
        TotalColumns = Columns.Count;
        HighPriorityCount = allCards.Count(c => c.Priority == KanbanPriority.High);
        OverdueCount = allCards.Count(c => c.IsOverdue);
    }

    private void Persist() => _kanbanService.Save();

    [RelayCommand]
    private void AddColumn()
    {
        var column = new KanbanColumn { Id = Guid.NewGuid().ToString("N"), Title = LocalizationService.Instance["Kanban_Column_DefaultTitle"] };
        _kanbanService.Board.Columns.Add(column);
        var vm = AddColumnViewModel(column);
        RefreshStats();
        Persist();

        vm.BeginRenameCommand.Execute(null);
    }

    [RelayCommand]
    private void CommitRenameColumn(KanbanColumnViewModel column)
    {
        var title = column.EditingTitle.Trim();
        if (!string.IsNullOrWhiteSpace(title)) column.ApplyTitle(title);
        column.IsRenaming = false;
        Persist();
    }

    // ===================== Delete column confirmation dialog =====================
    // Fase 3 de docs/analisis-proyecto/roadmap.md (top 10 #4 del análisis): borrar una columna solía
    // destruir sus tarjetas al momento, sin papelera ni confirmación. Ahora sigue exactamente el mismo
    // flujo que RequestDeleteCard — respeta el ajuste "preguntar antes de borrar" y las tarjetas pasan
    // por Board.TrashedCards, recuperables desde la Papelera.

    [RelayCommand]
    private void RequestDeleteColumn(KanbanColumnViewModel column)
    {
        if (column.IsCore) return;

        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) ColumnPendingDelete = column;
        else DeleteColumn(column);
    }

    [RelayCommand]
    private void CancelDeleteColumn() => ColumnPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteColumn()
    {
        if (ColumnPendingDelete is not { } column) return;
        DeleteColumn(column);
        ColumnPendingDelete = null;
    }

    private void DeleteColumn(KanbanColumnViewModel column)
    {
        if (column.IsCore) return;

        var cardCount = column.Column.Cards.Count;
        var columnTitle = column.Column.Title;

        // Las tarjetas de la columna no desaparecen con ella: pasan a la papelera una a una (mismo
        // camino que DeleteCard), conservando su índice original para una posible restauración.
        for (var i = 0; i < column.Column.Cards.Count; i++)
        {
            _kanbanService.Board.TrashedCards.Add(new KanbanTrashedCard
            {
                Card = column.Column.Cards[i],
                ColumnId = column.Column.Id,
                ColumnTitle = column.Column.Title,
                CardIndex = i
            });
        }

        _kanbanService.Board.Columns.Remove(column.Column);
        Columns.Remove(column);
        RefreshStats();
        RefreshTagFilter();
        Persist();

        _notificationService?.Push(
            LocalizationService.Instance["Kanban_Notify_ColumnDeleted_Title"],
            string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance["Kanban_Notify_ColumnDeleted_Message"],
                columnTitle,
                Plural.Get("Kanban_DeleteColumn_Cards", cardCount, cardCount)),
            NotificationSeverity.Warning);
    }

    [RelayCommand]
    private void CyclePriority(KanbanCardViewModel card)
    {
        card.CyclePriority();
        Persist();
    }

    // ===================== Card editor popup (create + edit share one form) =====================

    [RelayCommand]
    private void OpenCreateCard(KanbanColumnViewModel column) =>
        CardEditor = KanbanCardEditorViewModel.ForCreate(column, Columns, _vaultService, _sessionService, _onOpenLinkedNote);

    [RelayCommand]
    private void OpenEditCard(KanbanCardViewModel card)
    {
        var column = Columns.FirstOrDefault(c => c.Cards.Contains(card));
        if (column is null) return;

        CardEditor = KanbanCardEditorViewModel.ForEdit(column, card, Columns, _vaultService, _sessionService, _onOpenLinkedNote);
    }

    [RelayCommand]
    private void CancelCardEditor() => CardEditor = null;

    [RelayCommand]
    private void SaveCardEditor()
    {
        if (CardEditor is not { } editor) return;
        if (!editor.CanSave) return;

        if (editor.IsCreateMode)
        {
            var card = editor.BuildCard(Guid.NewGuid().ToString("N"));
            editor.SelectedColumn.Column.Cards.Add(card);
            editor.SelectedColumn.Cards.Add(new KanbanCardViewModel(card));
        }
        else if (editor.SourceCard is { } sourceCard)
        {
            editor.ApplyTo(sourceCard.Card);
            sourceCard.RefreshFromModel();

            // Moving between columns via the selector (Fase 7 accessibility — a non-drag&drop way to
            // relocate a card) reuses the same MoveCard the drag&drop handlers call, so subtask automation
            // (ApplySubtaskAutomation) and persistence stay identical either way.
            if (!ReferenceEquals(editor.SelectedColumn, editor.TargetColumn))
                MoveCard(sourceCard, editor.SelectedColumn, beforeCard: null);
        }

        CardEditor = null;
        RefreshStats();
        RefreshTagFilter();
        Persist();
    }

    private void RefreshTagFilter()
    {
        var allTags = _kanbanService.Board.Columns
            .SelectMany(c => c.Cards)
            .SelectMany(card => card.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        TagFilter.Reload(allTags);
        ApplyFilters();
    }

    // ===================== Read-only card detail dialog =====================

    [RelayCommand]
    private void OpenCardDetail(KanbanCardViewModel card) => CardDetail = new KanbanCardDetailViewModel(card, Persist, _onOpenLinkedNote);

    /// <summary>Entry point for the reminder inbox — jumps straight to a card's read-only detail by id,
    /// without the caller needing to know which column it lives in.</summary>
    public void OpenCardById(string cardId)
    {
        var card = Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == cardId);
        if (card is not null) OpenCardDetail(card);
    }

    /// <summary>Entry point for the calendar's "Editar" action (docs/calendar-feature/
    /// 06-creacion-y-edicion-de-items.md §3) — same id lookup as <see cref="OpenCardById"/> but opens the
    /// full editor directly instead of the read-only detail, since the calendar never reimplements card
    /// editing itself.</summary>
    public void OpenCardEditorById(string cardId)
    {
        var card = Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == cardId);
        if (card is null)
        {
            // Columns is only rebuilt from _kanbanService.Board on Reload(), never live-synced — a card
            // just created elsewhere (e.g. the calendar's "más opciones" quick-create) won't be in it yet.
            Reload();
            card = Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == cardId);
        }
        if (card is not null) OpenEditCard(card);
    }

    [RelayCommand]
    private void CloseCardDetail() => CardDetail = null;

    // ===================== Delete confirmation dialog =====================
    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de borrar" salta
    // directo al borrado cuando está desactivado, en vez de abrir el diálogo de confirmación.

    [RelayCommand]
    private void RequestDeleteCard(KanbanCardViewModel card)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) CardPendingDelete = card;
        else DeleteCard(card);
    }

    [RelayCommand]
    private void CancelDeleteCard() => CardPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteCard()
    {
        if (CardPendingDelete is not { } card) return;
        DeleteCard(card);
        CardPendingDelete = null;
    }

    private void DeleteCard(KanbanCardViewModel card)
    {
        var column = Columns.FirstOrDefault(c => c.Cards.Contains(card));
        if (column is not null)
        {
            var index = column.Column.Cards.IndexOf(card.Card);
            column.Cards.Remove(card);
            column.Column.Cards.Remove(card.Card);
            _kanbanService.Board.TrashedCards.Add(new KanbanTrashedCard
            {
                Card = card.Card,
                ColumnId = column.Column.Id,
                ColumnTitle = column.Column.Title,
                CardIndex = index
            });
        }

        RefreshStats();
        RefreshTagFilter();
        Persist();
    }

    /// <summary>Moves a card into <paramref name="targetColumn"/>, positioned right before
    /// <paramref name="beforeCard"/> (or appended at the end when null) — backs both cross-column
    /// moves and same-column reordering from the view's drag&amp;drop handlers.</summary>
    public void MoveCard(KanbanCardViewModel card, KanbanColumnViewModel targetColumn, KanbanCardViewModel? beforeCard)
    {
        var sourceColumn = Columns.FirstOrDefault(c => c.Cards.Contains(card));
        if (sourceColumn is null || beforeCard == card) return;

        var sameColumn = ReferenceEquals(sourceColumn, targetColumn);
        var insertIndex = beforeCard is null ? targetColumn.Cards.Count : targetColumn.Cards.IndexOf(beforeCard);
        if (insertIndex < 0) insertIndex = targetColumn.Cards.Count;

        // Removing the card first would shift a later insertIndex down by one when reordering
        // within the same column, so that case adjusts for the removal before inserting.
        if (sameColumn && sourceColumn.Cards.IndexOf(card) < insertIndex) insertIndex--;

        sourceColumn.Cards.Remove(card);
        sourceColumn.Column.Cards.Remove(card.Card);

        targetColumn.Cards.Insert(insertIndex, card);
        targetColumn.Column.Cards.Insert(insertIndex, card.Card);

        if (!sameColumn) ApplySubtaskAutomation(card, targetColumn);

        Persist();
    }

    /// <summary>Moving a card to the "Done" core column checks off every subtask; moving it back to the
    /// "Backlog" core column clears them all. Columns are matched by <see cref="CoreColumnRole"/> so
    /// renaming or (future) reordering doesn't break the automation.</summary>
    private static void ApplySubtaskAutomation(KanbanCardViewModel card, KanbanColumnViewModel targetColumn)
    {
        if (card.Card.Subtasks.Count == 0) return;

        bool? markDone = targetColumn.Column.Role switch
        {
            CoreColumnRole.Done => true,
            CoreColumnRole.Backlog => false,
            _ => null
        };
        if (markDone is null) return;

        foreach (var subtask in card.Card.Subtasks) subtask.IsDone = markDone.Value;
        card.RefreshFromModel();
    }
}
