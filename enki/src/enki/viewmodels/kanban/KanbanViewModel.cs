using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls.Primitives;
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

/// <summary>Filtro rápido de la barra de herramientas del tablero. Convierte los dos únicos recuentos
/// que piden una acción ("alta prioridad" y "vencidas") en un filtro de un clic, en vez de dejarlos como
/// cifras pasivas que el usuario tiene que traducir a mano a un rastreo por columnas. <see cref="None"/>
/// es el estado normal, sin filtro rápido.</summary>
public enum KanbanQuickFilter
{
    None,
    HighPriority,
    Overdue
}

/// <summary>Disposición visual del tablero local — Ajustes › Apariencia. <see cref="Vertical"/> apila las
/// tarjetas dentro de columnas lado a lado (la disposición clásica); <see cref="Horizontal"/> gira cada
/// columna 90° en una franja de ancho completo con las tarjetas desplazándose de lado a lado.</summary>
public enum KanbanBoardOrientation
{
    Vertical,
    Horizontal
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

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        ApplyFilters();
    }

    /// <summary>Filtro rápido activo. Vive junto a <see cref="SearchText"/> y <see cref="TagFilter"/> y se
    /// combina con ellos en AND dentro de <see cref="ApplyFilters"/> — no es un modo de vista aparte.</summary>
    [ObservableProperty]
    private KanbanQuickFilter _quickFilter = KanbanQuickFilter.None;

    partial void OnQuickFilterChanged(KanbanQuickFilter value)
    {
        OnPropertyChanged(nameof(IsHighPriorityFilterActive));
        OnPropertyChanged(nameof(IsOverdueFilterActive));
        ApplyFilters();
    }

    public bool IsHighPriorityFilterActive => QuickFilter == KanbanQuickFilter.HighPriority;
    public bool IsOverdueFilterActive => QuickFilter == KanbanQuickFilter.Overdue;

    /// <summary>Hay algo que "limpiar" — respalda la visibilidad del botón de limpiar filtros y del aviso
    /// de "ninguna coincidencia", que sin esto aparecerían también en un tablero simplemente vacío.</summary>
    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText)
        || QuickFilter != KanbanQuickFilter.None
        || TagFilter.SelectedTags.Any();

    /// <summary>El tablero no tiene ni una tarjeta (no confundir con "el filtro no encuentra nada", que es
    /// <see cref="HasNoFilterMatches"/>): es el estado de primer uso, y el único en el que la vista enseña
    /// el texto de ayuda con los atajos en vez de datos.</summary>
    public bool IsBoardEmpty => TotalCards == 0;

    /// <summary>Hay tarjetas, pero ninguna sobrevive al filtro activo. Estado propio para poder ofrecer
    /// "limpiar filtros" en vez de dejar el tablero en blanco sin explicación.</summary>
    [ObservableProperty]
    private bool _hasNoFilterMatches;

    /// <summary>Línea de estado bajo el título del tablero. Sustituye a la rejilla de cuatro tarjetas de
    /// estadísticas: los mismos números, pero en una sola línea y sin robarle 110px de alto al tablero.
    /// Con el tablero vacío cede el sitio al texto de ayuda, que ahí sí es lo útil.</summary>
    public string BoardSummaryLabel
    {
        get
        {
            if (IsBoardEmpty) return LocalizationService.Instance["Kanban_Board_Subtitle"];

            var parts = new List<string>
            {
                Plural.Get("Kanban_Summary_Columns", TotalColumns, TotalColumns),
                Plural.Get("Kanban_Summary_Cards", TotalCards, TotalCards)
            };
            if (OverdueCount > 0) parts.Add(Plural.Get("Kanban_Summary_Overdue", OverdueCount, OverdueCount));

            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    private KanbanCardSortMode _sortMode = KanbanCardSortMode.Manual;

    partial void OnSortModeChanged(KanbanCardSortMode value) => OnPropertyChanged(nameof(SortModeLabel));

    /// <summary>Nombre del orden activo, para pintarlo en el propio botón que abre el menú — el control
    /// segmentado anterior gastaba media barra de herramientas en enseñar cuatro opciones de las que solo
    /// una importa en cada momento.</summary>
    public string SortModeLabel => SortMode switch
    {
        KanbanCardSortMode.Priority => LocalizationService.Instance["Kanban_Sort_Priority"],
        KanbanCardSortMode.DueDate => LocalizationService.Instance["Kanban_Sort_DueDate"],
        KanbanCardSortMode.Created => LocalizationService.Instance["Kanban_Sort_Created"],
        _ => LocalizationService.Instance["Kanban_Sort_Manual"]
    };

    /// <summary>Menú desplegable de orden abierto. Popup con luz de descarte, mismo patrón que el resto de
    /// desplegables de la app.</summary>
    [ObservableProperty]
    private bool _isSortMenuOpen;

    /// <summary>Ajustes › Apariencia › Tablero Kanban. Se inicializa desde <see cref="SettingsService"/> en
    /// el constructor y <see cref="MainWindowViewModel"/> la mantiene sincronizada en vivo si se cambia
    /// desde Ajustes mientras el tablero está abierto (mismo cableado que <c>DashboardShowScrollbar</c>).</summary>
    [ObservableProperty]
    private KanbanBoardOrientation _boardOrientation = KanbanBoardOrientation.Vertical;

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

        if (settingsService is not null &&
            Enum.TryParse<KanbanBoardOrientation>(settingsService.Settings.KanbanBoardOrientation, out var storedOrientation))
            _boardOrientation = storedOrientation;

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
        OnPropertyChanged(nameof(SortModeLabel));
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
        IsSortMenuOpen = false;
        if (mode == KanbanCardSortMode.Manual) return;

        foreach (var column in Columns) column.Sort(mode);
        Persist();
    }

    [RelayCommand]
    private void ToggleSortMenu() => IsSortMenuOpen = !IsSortMenuOpen;

    // ===================== Filtros rápidos y limpieza de filtros =====================
    // "Alta prioridad" y "vencidas" eran dos de las cuatro tarjetas de estadísticas: números que el
    // usuario tenía que traducir a mano a un repaso columna por columna. Ahora son un filtro de un clic
    // que se apaga volviendo a pulsarlo, y comparten el mismo ApplyFilters que el buscador y las
    // etiquetas en vez de introducir una segunda vía de filtrado.

    /// <summary>Aspa del propio campo de búsqueda. Antes había que seleccionar el texto y borrarlo a
    /// mano para recuperar el tablero entero.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleQuickFilter(KanbanQuickFilter filter) =>
        QuickFilter = QuickFilter == filter ? KanbanQuickFilter.None : filter;

    /// <summary>Devuelve el tablero a "sin filtros" de una sola acción — buscador, etiquetas y filtro
    /// rápido a la vez. Sin esto, salir de un filtrado combinado obliga a deshacer tres cosas por
    /// separado y a adivinar cuál de ellas seguía activa.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        QuickFilter = KanbanQuickFilter.None;
        TagFilter.ClearSelection();
        // Asignar SearchText dispara OnSearchTextChanged → ApplyFilters, así que va el último y hace de
        // única pasada de filtrado cuando el texto cambia de verdad; si ya estaba vacío, el ApplyFilters
        // de QuickFilter/TagFilter ya ha dejado el tablero al día.
        SearchText = string.Empty;
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
        var selectedTags = TagFilter.SelectedTags.ToList();
        foreach (var column in Columns) column.ApplyFilters(selectedTags, SearchText, QuickFilter);

        HasNoFilterMatches = TotalCards > 0 && Columns.All(c => c.VisibleCardCount == 0);
        OnPropertyChanged(nameof(HasActiveFilters));
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

        // Derivadas de los cuatro contadores de arriba: se anuncian a mano porque son propiedades
        // calculadas sin campo propio (mismo motivo que KanbanColumnViewModel.IsEmpty).
        OnPropertyChanged(nameof(IsBoardEmpty));
        OnPropertyChanged(nameof(BoardSummaryLabel));
        OnPropertyChanged(nameof(HasHighPriorityCards));
        OnPropertyChanged(nameof(HasOverdueCards));
        OnPropertyChanged(nameof(ColumnsShareAvailableWidth));
        OnPropertyChanged(nameof(BoardColumnWidth));
        OnPropertyChanged(nameof(BoardHorizontalScrollBarVisibility));
    }

    /// <summary>Los chips de filtro rápido solo existen cuando hay algo que filtrar: un chip "VENCIDAS 0"
    /// permanente sería exactamente el ruido que la rejilla de estadísticas ya cometía.</summary>
    public bool HasHighPriorityCards => HighPriorityCount > 0;
    public bool HasOverdueCards => OverdueCount > 0;

    // ===================== Ancho de las columnas del tablero vertical =====================
    // Por defecto las columnas se reparten a partes iguales todo el ancho del contenido, para que el
    // tablero se lea como un bloque y no como unas cajas de 300px flotando en el centro. Eso deja de
    // funcionar cuando hay tantas columnas que el reparto las estrecha por debajo de lo que la propia
    // tarjeta necesita: con menos de ~190px útiles la fila de metadatos (subtareas + fecha) ya no cabe y
    // se sale, y el nombre de la columna se queda sin sitio en su cabecera. A partir de ahí es mejor
    // volver a un ancho fijo cómodo y desplazarse en horizontal que apretarlo todo hasta romperlo.
    //
    // El umbral sale de la aritmética del contenedor: el contenido de la página mide como mucho 1180px
    // (MaxWidth en la vista), y 1180/5 ≈ 236px por columna — 220 descontando el hueco entre ellas, que
    // deja unos 176px útiles dentro de la tarjeta y todavía entra todo. La sexta columna ya bajaría de
    // 190.

    private const int MaxColumnsSharingWidth = 5;
    private const double ScrollingColumnWidth = 280;

    /// <summary>True mientras las columnas quepan repartidas; false en cuanto son tantas que conviene
    /// darles ancho fijo y desplazar el tablero.</summary>
    public bool ColumnsShareAvailableWidth => Columns.Count <= MaxColumnsSharingWidth;

    /// <summary><see cref="double.NaN"/> (ancho automático: lo decide el reparto del UniformGrid) mientras
    /// las columnas se reparten el ancho, y un ancho fijo en cuanto hay demasiadas.</summary>
    public double BoardColumnWidth => ColumnsShareAvailableWidth ? double.NaN : ScrollingColumnWidth;

    /// <summary>Las dos mitades de la misma decisión: un ScrollViewer que puede desplazarse en horizontal
    /// mide su contenido con ancho infinito, y con ancho infinito no hay "ancho disponible" que repartir.
    /// Así que mientras las columnas se reparten el ancho el scroll horizontal está desactivado, y solo se
    /// habilita a la vez que el ancho fijo de <see cref="BoardColumnWidth"/>.</summary>
    public ScrollBarVisibility BoardHorizontalScrollBarVisibility =>
        ColumnsShareAvailableWidth ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

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
    private void OpenCardDetail(KanbanCardViewModel card)
    {
        var columnTitle = Columns.FirstOrDefault(c => c.Cards.Contains(card))?.Title ?? string.Empty;
        CardDetail = new KanbanCardDetailViewModel(card, Persist, _onOpenLinkedNote, columnTitle);
    }

    /// <summary>"Editar" y "Eliminar" desde el propio diálogo de detalle. Las acciones de la tarjeta en el
    /// tablero solo aparecen al pasar el ratón o al enfocarla, así que el detalle —al que se llega con un
    /// clic, con Enter desde el teclado o desde el buzón de recordatorios— tiene que ofrecer las mismas
    /// dos acciones para que nunca dependan del hover. Cierran el detalle antes de abrir el siguiente
    /// diálogo: los tres son modales excluyentes sobre el mismo tablero.</summary>
    [RelayCommand]
    private void EditCardFromDetail()
    {
        if (CardDetail is not { } detail) return;
        var card = detail.SourceCard;
        CardDetail = null;
        OpenEditCard(card);
    }

    [RelayCommand]
    private void DeleteCardFromDetail()
    {
        if (CardDetail is not { } detail) return;
        var card = detail.SourceCard;
        CardDetail = null;
        RequestDeleteCard(card);
    }

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

        if (!sameColumn)
        {
            ApplySubtaskAutomation(card, targetColumn);

            // Stamps/clears Models.KanbanCard.CompletedAtUtc — the single chokepoint every card move
            // (drag&drop and this same method's own callers alike) already goes through, so
            // StatisticsAggregationService can trust it instead of inferring completion from column
            // membership alone. Only stamped on the transition into Done, not re-stamped on every
            // same-column reorder inside it.
            card.Card.CompletedAtUtc = targetColumn.Column.Role == CoreColumnRole.Done
                ? card.Card.CompletedAtUtc ?? DateTime.UtcNow
                : null;
        }

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
