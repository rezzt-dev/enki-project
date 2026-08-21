using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SessionsViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly SessionService _sessionService;
    private readonly NotificationService _notificationService;
    private readonly Action<Session, Models.NoteFile?> _onOpenInNotes;
    private readonly Action _onSessionsChanged;
    private readonly KanbanService? _kanbanService;
    private readonly Action<string>? _onOpenLinkedCard;

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    /// <summary>"{0} sessions" chip shown next to the session list — recomputed whenever <see cref="Sessions"/>'
    /// membership changes (see the <c>CollectionChanged</c> subscription in the constructor).</summary>
    public string SessionsCountLabel => Plural.Get("Sessions_CountLabel", Sessions.Count, Sessions.Count);

    /// <summary>"{0} notes · {1} links" chip overlaid on the graph — recomputed via the
    /// <c>OnNodeCountChanged</c>/<c>OnEdgeCountChanged</c> partial hooks below whenever either count changes.
    /// Independently pluralized (each count needs its own singular/plural form).</summary>
    public string GraphSummaryLabel =>
        Plural.Get("Sessions_Graph_Summary_Notes", NodeCount, NodeCount) + " · " +
        Plural.Get("Sessions_Graph_Summary_Links", EdgeCount, EdgeCount);

    /// <summary>Tarjetas Kanban vinculadas a alguna nota del subárbol de <see cref="SelectedSession"/> —
    /// consecuencia derivada del enlace nota↔tarea, sin almacén propio: se recalcula contra
    /// <see cref="KanbanService.Board"/> cada vez que cambia la
    /// sesión seleccionada.</summary>
    public ObservableCollection<LinkedCardItemViewModel> LinkedCards { get; } = [];

    public bool HasLinkedCards => LinkedCards.Count > 0;

    [ObservableProperty]
    private SessionListItemViewModel? _selectedSession;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isCreateDialogOpen;

    /// <summary>Non-null mientras el diálogo de confirmación de borrado de sesión está abierto — Fase 3 de
    /// docs/analisis-proyecto/roadmap.md (top 10 #10 del análisis): borrar una sesión mueve al lugar su
    /// carpeta entera de notas, así que no puede seguir siendo una acción de un solo clic. Mismo patrón
    /// request/cancel/confirm que el borrado de tarjetas/columnas de <c>KanbanViewModel</c>.</summary>
    [ObservableProperty]
    private SessionListItemViewModel? _sessionPendingDelete;

    /// <summary>Cuerpo del diálogo de confirmación de borrado de sesión, con el recuento de notas ya
    /// pluralizado por idioma — calculado aquí y no en XAML porque el orden de las palabras alrededor de
    /// los placeholders no es fijo entre idiomas (docs/agent-docs/internacionalizacion-i18n.md §1.3).</summary>
    public string PendingDeleteSessionMessage
    {
        get
        {
            if (SessionPendingDelete is not { } item) return string.Empty;
            var notesFragment = Plural.Get("Sessions_NoteCount", item.NoteCount, item.NoteCount);
            return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_DeleteModal_Message"], item.Name, notesFragment);
        }
    }

    partial void OnSessionPendingDeleteChanged(SessionListItemViewModel? value) =>
        OnPropertyChanged(nameof(PendingDeleteSessionMessage));

    [ObservableProperty]
    private string _newSessionName = string.Empty;

    [ObservableProperty]
    private bool _isLinkMode;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private int _edgeCount;

    public bool CanConfirmCreate => !string.IsNullOrWhiteSpace(NewSessionName);

    /// <summary>Raised when the whole graph document should be (re)rendered from scratch — the page was
    /// (re)opened, a session was created/renamed/deleted, or notes were created/renamed/deleted from the
    /// Notes page while this page was idle. The graph always spans every session at once.</summary>
    public event Action<SessionGraph>? GraphReady;

    /// <summary>Raised when the graph changed in a way that should be pushed into the already-loaded
    /// document in place, so the camera and running simulation aren't disturbed.</summary>
    public event Action<SessionGraph>? GraphUpdated;

    /// <summary>Raised when a manual-link click pair was refused (currently: the two notes belong to
    /// different sessions — see <see cref="HandleLinkToggled"/>), so the WebView can flash the two nodes
    /// instead of the click silently doing nothing.</summary>
    public event Action<string, string>? LinkRejected;

    /// <summary>Raised whenever the "first note of the pair" selection changes (a note picked, a link
    /// completed, link mode turned off) — purely cosmetic, so the WebView can pulse the pending node. Unlike
    /// <see cref="IsLinkMode"/> itself, whether that pulse actually reaches the WebView depends on a script
    /// injection succeeding (see <see cref="_pendingLinkGlobalId"/>'s doc comment); the link mechanics below
    /// never depend on it.</summary>
    public event Action<string?>? PendingLinkChanged;

    /// <summary>The first note clicked while <see cref="IsLinkMode"/> is on, awaiting a second click to
    /// complete the pair — kept here, not mirrored into the WebView's own JS state, because the WebView has
    /// no reliable way to report back "I successfully received your last state update": <c>InvokeScript</c>
    /// calls are fired without awaiting their result (see every call site in <c>SessionsView</c>), so a JS
    /// mirror of this flag can silently drift out of sync with it. This field is what every click is judged
    /// against, so the two-click "pick a note, pick another" flow works even if the WebView never learns
    /// which note is pending.</summary>
    private string? _pendingLinkGlobalId;

    /// <summary>The most recently computed combined graph, if any — lets <see cref="RepublishGraph"/>
    /// re-raise <see cref="GraphReady"/> for a newly-(re)attached view without redoing the vault-wide
    /// scan+parse <see cref="LoadGraph"/> just did a moment earlier.</summary>
    private SessionGraph? LastGraph { get; set; }

    /// <summary>Bumped by every <see cref="Reload"/>/<see cref="LoadGraph"/>/link-toggle call so an older,
    /// still-in-flight background load can tell it's been superseded and skip publishing its (now stale)
    /// result instead of clobbering a newer one that finished first.</summary>
    private int _loadGeneration;

    public SessionsViewModel(
        VaultService vaultService,
        SessionService sessionService,
        NotificationService notificationService,
        Action<Session, Models.NoteFile?> onOpenInNotes,
        Action onSessionsChanged,
        KanbanService? kanbanService = null,
        Action<string>? onOpenLinkedCard = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _notificationService = notificationService;
        _onOpenInNotes = onOpenInNotes;
        _onSessionsChanged = onSessionsChanged;
        _kanbanService = kanbanService;
        _onOpenLinkedCard = onOpenLinkedCard;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SessionsCountLabel));

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(SessionsCountLabel));
        OnPropertyChanged(nameof(GraphSummaryLabel));
        OnPropertyChanged(nameof(PendingDeleteSessionMessage));
        Reload();
    }

    partial void OnNewSessionNameChanged(string value) => OnPropertyChanged(nameof(CanConfirmCreate));

    partial void OnNodeCountChanged(int value) => OnPropertyChanged(nameof(GraphSummaryLabel));

    partial void OnEdgeCountChanged(int value) => OnPropertyChanged(nameof(GraphSummaryLabel));

    // Selecting a session in the left panel only scopes "Abrir en notas" and the rename/delete row
    // actions — the graph itself is vault-wide (see LoadGraph) and never depends on a selection existing.
    partial void OnSelectedSessionChanged(SessionListItemViewModel? oldValue, SessionListItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        RefreshLinkedCards();
    }

    /// <summary>Recomputes <see cref="LinkedCards"/> for whichever session is now selected — every note
    /// under its subtree, then every board card (including trashed ones would be noise, so only the live
    /// board is considered) whose <see cref="Models.KanbanCard.LinkedNotePath"/> lands inside that set.</summary>
    private void RefreshLinkedCards()
    {
        LinkedCards.Clear();
        if (_kanbanService is not null && SelectedSession is { } selected)
        {
            var notePaths = _vaultService.GetAllNotesRecursive(selected.Session.RootPath)
                .Select(n => n.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (notePaths.Count > 0)
            {
                var cards = _kanbanService.Board.Columns.SelectMany(c => c.Cards)
                    .Where(c => c.LinkedNotePath is not null && notePaths.Contains(c.LinkedNotePath));
                foreach (var card in cards) LinkedCards.Add(new LinkedCardItemViewModel(card.Id, card.Title));
            }
        }
        OnPropertyChanged(nameof(HasLinkedCards));
    }

    [RelayCommand]
    private void OpenLinkedCard(string cardId) => _onOpenLinkedCard?.Invoke(cardId);

    // Reload/LoadGraph used to run every bit of this synchronously on the UI thread: List() and
    // LoadCombinedGraph() both walk the vault's directory tree and read+parse every note's content, which
    // froze the whole app for the duration on every tab switch/session change, worse the more notes a
    // vault has. Both now hand the disk I/O to a background thread (see SessionService.ListAsync/
    // LoadCombinedGraphAsync) and only touch bound properties once the result is back — "async void" is
    // fine here since these are effectively fire-and-forget UI actions (constructor call, command,
    // navigation), same as every [RelayCommand] below already was from the caller's point of view.
    public async void Reload()
    {
        var generation = ++_loadGeneration;
        var previouslySelectedId = SelectedSession?.Session.Id;

        var sessions = await _sessionService.ListAsync();
        if (generation != _loadGeneration) return; // superseded by a newer Reload/LoadGraph call

        Sessions.Clear();
        foreach (var session in sessions) Sessions.Add(new SessionListItemViewModel(session));
        IsEmpty = Sessions.Count == 0;

        SelectedSession = previouslySelectedId is null
            ? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(s => s.Session.Id == previouslySelectedId) ?? Sessions.FirstOrDefault();

        await LoadGraphCoreAsync(generation);
    }

    /// <summary>(Re)computes the combined graph — every session's notes at once — and pushes a full
    /// reload to the WebView. Doesn't require (or use) a selected session at all.</summary>
    public async void LoadGraph() => await LoadGraphCoreAsync(++_loadGeneration);

    private async Task LoadGraphCoreAsync(int generation)
    {
        var graph = await _sessionService.LoadCombinedGraphAsync();
        if (generation != _loadGeneration) return;

        NodeCount = graph.Nodes.Count;
        EdgeCount = graph.Edges.Count;
        LastGraph = graph;
        GraphReady?.Invoke(graph);
    }

    /// <summary>Re-raises <see cref="GraphReady"/> with the last graph already computed, instead of
    /// redoing the vault-wide scan — for a view that just (re)attached and missed the event from whatever
    /// load triggered it (see <see cref="LastGraph"/>'s doc comment). Falls back to a real load only if
    /// nothing has been computed yet (the very first attach, before any load has completed).</summary>
    public void RepublishGraph()
    {
        if (LastGraph is { } graph) GraphReady?.Invoke(graph);
        else LoadGraph();
    }

    [RelayCommand]
    private void RefreshGraph() => LoadGraph();

    [RelayCommand]
    private void SelectSession(SessionListItemViewModel item) => SelectedSession = item;

    [RelayCommand]
    private void OpenInNotes()
    {
        if (SelectedSession is null) return;
        _onOpenInNotes(SelectedSession.Session, null);
    }

    [RelayCommand]
    private void BeginCreateSession()
    {
        NewSessionName = string.Empty;
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    private void CancelCreateSession() => IsCreateDialogOpen = false;

    [RelayCommand]
    private void ConfirmCreateSession()
    {
        if (!CanConfirmCreate) return;

        var created = _sessionService.Create(NewSessionName.Trim());
        IsCreateDialogOpen = false;
        Reload();
        SelectedSession = Sessions.FirstOrDefault(s => s.Session.Id == created.Id);
        _onSessionsChanged();
        _notificationService.Push(
            LocalizationService.Instance["Sessions_CreatedNotification_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Sessions_CreatedNotification_Message"], created.Name),
            NotificationSeverity.Success);
    }

    [RelayCommand]
    private static void BeginRenameSession(SessionListItemViewModel item)
    {
        item.EditingName = item.Name;
        item.IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRenameSession(SessionListItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(item.EditingName) && item.EditingName != item.Name)
        {
            var updated = _sessionService.Rename(item.Session, item.EditingName);
            item.UpdateSession(updated);
            LoadGraph(); // node tooltips in the combined graph carry the session name
        }
        item.IsRenaming = false;
        _onSessionsChanged();
    }

    [RelayCommand]
    private void RequestDeleteSession(SessionListItemViewModel item)
    {
        // The UI already hides the delete action for the default session — this guard just keeps the
        // invariant ("every vault always has at least one session") true even if that ever changes.
        if (item.IsDefault) return;

        SessionPendingDelete = item;
    }

    [RelayCommand]
    private void CancelDeleteSession() => SessionPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteSession()
    {
        if (SessionPendingDelete is not { } item) return;
        SessionPendingDelete = null;

        var wasSelected = SelectedSession == item;
        if (!_sessionService.Delete(item.Session)) return;

        Reload();
        if (wasSelected) SelectedSession = Sessions.FirstOrDefault();
        _onSessionsChanged();
    }

    [RelayCommand]
    private void ToggleLinkMode()
    {
        IsLinkMode = !IsLinkMode;
        SetPendingLink(null);
    }

    // ===================== Messages posted from the graph's WebView =====================
    // The combined graph's node ids are "{sessionId}::{relativePath}" (SessionGraphNode.GlobalId) since
    // plain relative paths collide across sessions — every message coming back from it has to be
    // unpacked against that shape before it means anything to the session-scoped services below.

    /// <summary>Single entry point for every plain node click the WebView reports. Only meaningful in link
    /// mode now (picking the two notes to pair up) — opening a note used to happen here too on a single
    /// click outside link mode, but that moved to <see cref="HandleNodeDoubleClicked"/> so a single click on
    /// the graph no longer immediately navigates away from it (the WebView itself still doesn't decide the
    /// difference: it just reports every click as-is, same as before — see the doc comment on
    /// <see cref="IsLinkMode"/>'s check below for why that decision stays host-side).</summary>
    public void HandleNodeClicked(string globalId)
    {
        if (!IsLinkMode) return;

        if (_pendingLinkGlobalId is null)
        {
            SetPendingLink(globalId);
            return;
        }

        if (_pendingLinkGlobalId == globalId)
        {
            SetPendingLink(null);
            return;
        }

        var fromGlobalId = _pendingLinkGlobalId;
        SetPendingLink(null);
        HandleLinkToggled(fromGlobalId, globalId);
    }

    /// <summary>A node was double-clicked — always opens that note in the Notes section, regardless of link
    /// mode, matching the double-click-to-open convention used elsewhere in the app (note cards, dashboard).
    /// Reported by the WebView alongside (not instead of) the plain click that starts the pair (see
    /// handleNodeDoubleClick in SessionGraphRenderer), so this doesn't interfere with an in-progress link
    /// pick — it just also opens the note.</summary>
    public void HandleNodeDoubleClicked(string globalId) => HandleNodeOpened(globalId);

    private void SetPendingLink(string? globalId)
    {
        _pendingLinkGlobalId = globalId;
        PendingLinkChanged?.Invoke(globalId);
    }

    private void HandleNodeOpened(string globalId)
    {
        if (!TryParseGlobalId(globalId, out var sessionId, out var relativePath)) return;
        if (FindSession(sessionId) is not { } session) return;

        var fullPath = Path.Combine(session.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var note = _vaultService.GetNote(fullPath);
        if (note is null)
        {
            // The node referenced a file that's gone (deleted from another view) — resync instead of
            // silently doing nothing, so the graph doesn't keep dangling nodes around.
            LoadGraph();
            return;
        }

        _onOpenInNotes(session, note);
    }

    public void HandleLinkToggled(string fromGlobalId, string toGlobalId)
    {
        if (!TryParseGlobalId(fromGlobalId, out var fromSessionId, out var fromRelativePath)) return;
        if (!TryParseGlobalId(toGlobalId, out var toSessionId, out var toRelativePath)) return;

        // Notes only ever link within their own session's tree — a manual link can't span two sessions.
        // This used to fail silently, which read as the whole link feature being broken (the click that
        // triggered it produced no visible reaction at all); now the WebView flashes both nodes instead.
        if (fromSessionId != toSessionId)
        {
            LinkRejected?.Invoke(fromGlobalId, toGlobalId);
            return;
        }
        if (FindSession(fromSessionId) is not { } session) return;

        _ = HandleLinkToggledAsync(session, fromRelativePath, toRelativePath);
    }

    private async Task HandleLinkToggledAsync(Session session, string fromRelativePath, string toRelativePath)
    {
        var generation = ++_loadGeneration;
        // The manifest write and the full combined-graph rescan that follows it are both synchronous disk
        // I/O (see SessionService.ToggleManualLink/LoadCombinedGraph) — running them on a background thread
        // keeps a single link click from freezing the UI for however long the vault-wide rescan takes.
        var graph = await Task.Run(() =>
        {
            _sessionService.ToggleManualLink(session, fromRelativePath, toRelativePath);
            return _sessionService.LoadCombinedGraph();
        });
        if (generation != _loadGeneration) return;

        NodeCount = graph.Nodes.Count;
        EdgeCount = graph.Edges.Count;
        LastGraph = graph;
        GraphUpdated?.Invoke(graph);
    }

    public void HandlePositionsReported(IReadOnlyList<(string RelativePath, double X, double Y, double Z)> positions)
    {
        if (positions.Count == 0) return;

        var bySession = new Dictionary<string, List<(string RelativePath, double X, double Y, double Z)>>();
        foreach (var (globalId, x, y, z) in positions)
        {
            if (!TryParseGlobalId(globalId, out var sessionId, out var relativePath)) continue;
            if (!bySession.TryGetValue(sessionId, out var list)) bySession[sessionId] = list = [];
            list.Add((relativePath, x, y, z));
        }

        var toSave = bySession
            .Select(kv => (Session: FindSession(kv.Key), Positions: kv.Value))
            .Where(t => t.Session is not null)
            .ToList();
        if (toSave.Count == 0) return;

        // Fires on every drag-settle — each save is a synchronous read+parse+serialize+write of a session's
        // whole .session.json (see SessionService.SavePositions/WriteManifest). Running it on a background
        // thread keeps that from hitching the UI right as a drag interaction ends, which is when it's most
        // noticeable. Positions are last-write-wins per note, so an older save racing a newer one is harmless.
        _ = Task.Run(() =>
        {
            foreach (var (session, list) in toSave) _sessionService.SavePositions(session!, list);
        });
    }

    private Session? FindSession(string sessionId) =>
        Sessions.FirstOrDefault(s => s.Session.Id == sessionId)?.Session;

    private static bool TryParseGlobalId(string globalId, out string sessionId, out string relativePath)
    {
        var separatorIndex = globalId.IndexOf("::", StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            sessionId = string.Empty;
            relativePath = string.Empty;
            return false;
        }

        sessionId = globalId[..separatorIndex];
        relativePath = globalId[(separatorIndex + 2)..];
        return true;
    }
}
