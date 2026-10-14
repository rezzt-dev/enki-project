using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    /// <summary>Null <c>Session</c> means "open this note in the vault-root workspace" — the only way a
    /// graph node can reach here without a real session (see <see cref="HandleNodeOpened"/>), since notes
    /// living directly at the vault root have no session to pass.</summary>
    private readonly Action<Session?, Models.NoteFile?> _onOpenInNotes;
    private readonly Action _onSessionsChanged;
    private readonly KanbanService? _kanbanService;
    private readonly Action<string>? _onOpenLinkedCard;
    /// <summary>Notifies Notas so it can close/flush any tab open on one of the just-locked session's notes
    /// (análisis §4.1) — invoked from <see cref="LockSession"/> while the DEK is still unlocked. Optional so
    /// existing tests/call sites that don't care about this coupling keep compiling unchanged.</summary>
    private readonly Func<Session, bool>? _onSessionLocked;

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

    /// <summary>One row per group currently in the graph — every session plus the vault-root pseudo-group,
    /// each with an on/off toggle — so the floating filter popover can let the user isolate the global
    /// graph down to a subset (e.g. only "sin sesión", or exactly two sessions to see just what connects
    /// them). Rebuilt whenever a fresh graph is loaded (see <see cref="RebuildGraphGroupFilters"/>),
    /// preserving whatever selection was already in place.</summary>
    public ObservableCollection<GraphGroupFilterItemViewModel> GraphGroupFilters { get; } = [];

    /// <summary>Whether any group filter is currently narrowing the graph — drives the filter chip's
    /// "active" visual state, same convention as <see cref="IsLinkMode"/>'s toggle button.</summary>
    public bool IsGraphFiltered => !string.IsNullOrWhiteSpace(GraphSearchText) || GraphGroupFilters.Any(f => !f.IsActive)
        || GraphTagFilters.Any(f => f.IsActive) || GraphExplorationIndex != 0
        || !string.IsNullOrEmpty(SelectedGraphCluster?.Id);

    /// <summary>Tarjetas Kanban vinculadas a alguna nota del subárbol de <see cref="SelectedSession"/> —
    /// consecuencia derivada del enlace nota↔tarea, sin almacén propio: se recalcula contra
    /// <see cref="KanbanService.AllColumns"/> cada vez que cambia la
    /// sesión seleccionada.</summary>
    public ObservableCollection<LinkedCardItemViewModel> LinkedCards { get; } = [];

    public bool HasLinkedCards => LinkedCards.Count > 0;

    [ObservableProperty]
    private SessionListItemViewModel? _selectedSession;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>True while a PBKDF2-210k derivation (create/unlock/reset a protected session) is running on
    /// a background thread — binds to a spinner and disables the confirm button in each of the three
    /// dialogs, so the UI thread never blocks for however long those ~200ms-per-derivation operations take
    /// and the user gets visible feedback instead of a dialog that looks unresponsive
    /// (docs/version-alfa-3/01-analisis-detallado.md §4.1, Media).</summary>
    [ObservableProperty]
    private bool _isProcessingSessionAction;

    [ObservableProperty]
    private bool _isCreateDialogOpen;

    /// <summary>Non-null mientras el diálogo de confirmación de borrado de sesión está abierto — Fase 3 de
    /// docs/analisis-proyecto/roadmap.md (top 10 #10 del análisis): borrar una sesión mueve al lugar su
    /// carpeta entera de notas, así que no puede seguir siendo una acción de un solo clic. Mismo patrón
    /// request/cancel/confirm que el borrado de tarjetas/columnas de <c>KanbanViewModel</c>.</summary>
    [ObservableProperty]
    private SessionListItemViewModel? _sessionPendingDelete;

    /// <summary>Sesión cuyo color se está editando. El borrador permanece separado de la entidad real hasta
    /// confirmar, de modo que cerrar el diálogo no escriba el manifiesto ni altere el grafo.</summary>
    [ObservableProperty]
    private SessionListItemViewModel? _sessionPendingColor;

    [ObservableProperty]
    private Color _sessionColorDraft = Colors.White;

    [ObservableProperty]
    private string _sessionColorHex = "#FFFFFF";

    public bool CanConfirmSessionColor =>
        SessionPendingColor is not null && HexColorUtility.NormalizeHex(SessionColorHex) is not null;

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

    partial void OnSessionPendingColorChanged(SessionListItemViewModel? value) =>
        OnPropertyChanged(nameof(CanConfirmSessionColor));

    partial void OnSessionColorDraftChanged(Color value)
    {
        var hex = HexColorUtility.ColorToHex(value);
        if (SessionColorHex != hex) SessionColorHex = hex;
    }

    partial void OnSessionColorHexChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirmSessionColor));
        if (HexColorUtility.NormalizeHex(value) is not { } normalized) return;

        var parsed = Color.Parse(normalized);
        if (SessionColorDraft != parsed) SessionColorDraft = parsed;
    }

    [ObservableProperty]
    private string _newSessionName = string.Empty;

    [ObservableProperty]
    private bool _newSessionIsProtected;

    [ObservableProperty]
    private string _newSessionPassword = string.Empty;

    [ObservableProperty]
    private string _newSessionPasswordConfirm = string.Empty;

    /// <summary>Longitud mínima de la contraseña de una sesión protegida. Sigue siendo el <em>único</em>
    /// requisito: no hay reglas de composición (mayúsculas, dígitos, símbolos) ni las va a haber — la
    /// contraseña llega tal cual a <c>SessionEncryptionService</c>, que la pasa por PBKDF2-SHA256 sobre sus
    /// bytes UTF-8, así que cualquier combinación de caracteres vale por igual y una frase larga protege
    /// mucho más que una palabra corta con símbolos.
    ///
    /// <para>El mínimo era 4 con el argumento de que Enki es una app local y no un servicio expuesto a
    /// fuerza bruta remota. Ese argumento no se sostiene aquí: el atacante de una sesión protegida no ataca
    /// la app, ataca el archivo. <c>.session.json</c> lleva la DEK envuelta y viaja en cualquier copia del
    /// vault — un backup en un USB, una carpeta sincronizada, el .zip de "Crear copia de seguridad", un
    /// portátil robado — así que el ataque es <em>offline</em> y sin límite de intentos. Con PBKDF2-SHA256 a
    /// 210.000 iteraciones, 4 caracteres alfanuméricos son ~1,7 millones de candidatos: minutos en GPU. 12
    /// caracteres mueven ese coste varios órdenes de magnitud y siguen siendo una contraseña que se teclea
    /// una vez por sesión de trabajo, no cada minuto (ver .docs/auditoria-seguridad-y-fallos-criticos.md,
    /// SEC-02).</para>
    ///
    /// <para>Las sesiones creadas con una contraseña más corta siguen abriéndose con normalidad — este
    /// mínimo solo se aplica al crear una sesión o al cambiar la contraseña con un código de recuperación,
    /// que son los dos únicos puntos donde el usuario elige una nueva. El texto visible que anuncia el
    /// mínimo vive en <c>Sessions_CreateDialog_PasswordTooShort</c>/<c>Sessions_Unlock_PasswordMismatch</c>
    /// y menciona este número: cambiarlo aquí obliga a actualizar esas cadenas en los 7 idiomas.</para></summary>
    private const int MinPasswordLength = 12;

    /// <summary>Non-null while the "here are your 5 recovery codes" dialog is open — opened once, right
    /// after <see cref="ConfirmCreateSession"/> creates a protected session, and never reopened later (the
    /// codes only ever exist in memory here, for this one screen).</summary>
    [ObservableProperty]
    private bool _isRecoveryCodesDialogOpen;

    public ObservableCollection<string> PendingRecoveryCodes { get; } = [];

    /// <summary>Name of the just-created protected session, used to build the suggested download filename
    /// for <see cref="DownloadRecoveryCodes"/> — captured at creation time since
    /// <see cref="PendingRecoveryCodes"/> outlives any particular <see cref="SelectedSession"/>.</summary>
    private string _pendingRecoveryCodesSessionName = string.Empty;

    /// <summary>Non-null while the unlock dialog is open for this session — covers both "enter your
    /// password" and (when <see cref="IsForgotPasswordMode"/>) "reset it with a recovery code".</summary>
    [ObservableProperty]
    private SessionListItemViewModel? _sessionPendingUnlock;

    [ObservableProperty]
    private string _unlockPasswordInput = string.Empty;

    [ObservableProperty]
    private string? _unlockError;

    [ObservableProperty]
    private bool _isForgotPasswordMode;

    [ObservableProperty]
    private string _recoveryCodeInput = string.Empty;

    [ObservableProperty]
    private string _newPasswordInput = string.Empty;

    [ObservableProperty]
    private string _newPasswordConfirmInput = string.Empty;

    [ObservableProperty]
    private bool _isLinkMode;

    [ObservableProperty]
    private bool _isGraphFilterOpen;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private int _edgeCount;

    public bool CanConfirmCreate =>
        !IsProcessingSessionAction &&
        !string.IsNullOrWhiteSpace(NewSessionName) &&
        (!NewSessionIsProtected || (NewSessionPassword.Length >= MinPasswordLength && NewSessionPassword == NewSessionPasswordConfirm));

    /// <summary>Cierto cuando el usuario ya empezó a escribir una contraseña que todavía no llega a
    /// <see cref="MinPasswordLength"/>. Existe para poder decirlo en el propio diálogo: con el mínimo en 12,
    /// dejar el botón "Crear" deshabilitado sin explicar por qué convierte un requisito razonable en un
    /// formulario que no responde. Falso con el campo vacío — ahí todavía no hay nada que corregir.</summary>
    public bool IsNewSessionPasswordTooShort =>
        NewSessionIsProtected && NewSessionPassword.Length > 0 && NewSessionPassword.Length < MinPasswordLength;

    /// <summary>Raised when the whole graph document should be (re)rendered from scratch — the page was
    /// (re)opened, a session was created/renamed/deleted, or notes were created/renamed/deleted from the
    /// Notes page while this page was idle. The graph always spans the whole vault at once: every session
    /// plus every note living directly at the vault root, minus whatever <see cref="GraphGroupFilters"/>
    /// currently hides.</summary>
    public event Action<SessionGraph>? GraphReady;

    /// <summary>Raised when the graph changed in a way that should be pushed into the already-loaded
    /// document in place, so the camera and running simulation aren't disturbed.</summary>
    public event Action<SessionGraph>? GraphUpdated;

    /// <summary>Actualización puramente visual del color de una sesión. Se separa de
    /// <see cref="GraphUpdated"/> porque no cambia nodos ni aristas y, por tanto, no debe recalentar la
    /// simulación ni reencuadrar la cámara.</summary>
    public event Action<string, string>? SessionColorChanged;

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

    /// <summary>The most recently computed global graph, unfiltered, if any — lets
    /// <see cref="RepublishGraph"/> re-raise <see cref="GraphReady"/> for a newly-(re)attached view, and
    /// lets <see cref="GraphGroupFilters"/> toggling re-derive the visible subset, without redoing the
    /// vault-wide scan+parse <see cref="LoadGraph()"/> just did a moment earlier.</summary>
    private SessionGraph? LastGraph { get; set; }

    /// <summary>Bumped by every <see cref="Reload"/>/<see cref="LoadGraph"/>/link-toggle call so an older,
    /// still-in-flight background load can tell it's been superseded and skip publishing its (now stale)
    /// result instead of clobbering a newer one that finished first.</summary>
    private int _loadGeneration;

    public SessionsViewModel(
        VaultService vaultService,
        SessionService sessionService,
        NotificationService notificationService,
        Action<Session?, Models.NoteFile?> onOpenInNotes,
        Action onSessionsChanged,
        KanbanService? kanbanService = null,
        Action<string>? onOpenLinkedCard = null,
        Func<Session, bool>? onSessionLocked = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _notificationService = notificationService;
        _onOpenInNotes = onOpenInNotes;
        _onSessionsChanged = onSessionsChanged;
        _kanbanService = kanbanService;
        _onOpenLinkedCard = onOpenLinkedCard;
        _onSessionLocked = onSessionLocked;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SessionsCountLabel));

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(SessionsCountLabel));
        OnPropertyChanged(nameof(GraphSummaryLabel));
        var explorationIndex = GraphExplorationIndex;
        _updatingGraphFilters = true;
        try
        {
            OnPropertyChanged(nameof(GraphExplorationOptions));
            GraphExplorationIndex = explorationIndex;
        }
        finally
        {
            _updatingGraphFilters = false;
        }
        OnPropertyChanged(nameof(PendingDeleteSessionMessage));
        Reload();
    }

    partial void OnNewSessionNameChanged(string value) => OnPropertyChanged(nameof(CanConfirmCreate));

    partial void OnNewSessionIsProtectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirmCreate));
        OnPropertyChanged(nameof(IsNewSessionPasswordTooShort));
    }

    partial void OnNewSessionPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirmCreate));
        OnPropertyChanged(nameof(IsNewSessionPasswordTooShort));
    }

    partial void OnNewSessionPasswordConfirmChanged(string value) => OnPropertyChanged(nameof(CanConfirmCreate));

    partial void OnIsProcessingSessionActionChanged(bool value) => OnPropertyChanged(nameof(CanConfirmCreate));

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
                var cards = _kanbanService.AllColumns.SelectMany(c => c.Cards)
                    .Where(c => c.LinkedNotePath is not null && notePaths.Contains(c.LinkedNotePath));
                foreach (var card in cards) LinkedCards.Add(new LinkedCardItemViewModel(card.Id, card.Title));
            }
        }
        OnPropertyChanged(nameof(HasLinkedCards));
    }

    [RelayCommand]
    private void OpenLinkedCard(string cardId) => _onOpenLinkedCard?.Invoke(cardId);

    // Reload/LoadGraph used to run every bit of this synchronously on the UI thread: List() and
    // LoadGlobalGraph() both walk the vault's directory tree and read+parse every note's content, which
    // froze the whole app for the duration on every tab switch/session change, worse the more notes a
    // vault has. Both now hand the disk I/O to a background thread (see SessionService.ListAsync/
    // LoadGlobalGraphAsync) and only touch bound properties once the result is back.
    //
    // Siguen siendo "dispara y olvida" para quien llama (el constructor, un comando, una navegación), pero
    // ya no son "async void": lo que sale de aquí se guarda en PendingLoad, y eso es lo que separa un fallo
    // de disco a mitad del recorrido de que se caiga el proceso entero — ver Track.
    public void Reload() => PendingLoad = Track(ReloadAsync());

    private async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        var previouslySelectedId = SelectedSession?.Session.Id;

        var sessions = await _sessionService.ListAsync();
        if (generation != _loadGeneration) return; // superseded by a newer Reload/LoadGraph call

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(new SessionListItemViewModel(session) { IsUnlocked = _sessionService.IsUnlocked(session) });
        }

        SelectedSession = previouslySelectedId is null
            ? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault(s => s.Session.Id == previouslySelectedId) ?? Sessions.FirstOrDefault();

        await LoadGraphCoreAsync(generation);
    }

    /// <summary>(Re)computes the combined graph — every session's notes at once — and pushes a full
    /// reload to the WebView. Doesn't require (or use) a selected session at all.</summary>
    public void LoadGraph() => PendingLoad = Track(LoadGraphCoreAsync(++_loadGeneration));

    /// <summary>La carga en segundo plano que lanzó el último <see cref="Reload"/>/<see cref="LoadGraph"/>,
    /// ya envuelta por <see cref="Track"/> y por tanto siempre completable sin fallo. La app no la espera
    /// nunca —por eso esos dos son métodos void y no <c>async Task</c> que nadie fuera de aquí querría
    /// await—: esto existe para que los tests tengan un punto de encuentro determinista con el recorrido de
    /// disco que arranca el constructor, en vez de competir con él por el árbol de ficheros o quedarse con
    /// un grafo anterior a lo que el propio test acaba de crear.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Aísla una carga en segundo plano de dos cosas que la app no puede permitirse.
    ///
    /// <para>La primera, que un fallo de disco se lleve el proceso por delante. Hasta aquí llegan recorridos
    /// del vault entero (<see cref="SessionService.ListAsync"/>, <see cref="SessionService.LoadGlobalGraphAsync"/>)
    /// que corren mientras el usuario mueve, borra e importa carpetas delante —borrar una sesión mueve su
    /// carpeta entera a la papelera justo mientras el recorrido la visita—, así que encontrarse con algo que
    /// ya no está es lo normal, no lo excepcional. Mientras esto fue <c>async void</c> esa excepción no tenía
    /// dónde ir y terminaba el proceso. No se avisa al usuario porque no hay nada que decidir ni que
    /// reintentar a mano: el siguiente Reload/LoadGraph (cambiar de pestaña, crear o borrar una sesión) ya
    /// vuelve a leer el árbol como haya quedado.</para>
    ///
    /// <para>La segunda, que la carga quede como tarea fallida sin observar: <see cref="PendingLoad"/> la
    /// guarda y los tests la esperan, y una tarea fallida ahí volvería a lanzar en quien la espere.</para></summary>
    private static async Task Track(Task load)
    {
        try
        {
            await load;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error("No se pudo completar la carga de sesiones/grafo en segundo plano", ex);
        }
    }

    private async Task LoadGraphCoreAsync(int generation)
    {
        var graph = await _sessionService.LoadGlobalGraphAsync();
        if (generation != _loadGeneration) return;

        LastGraph = graph;
        RebuildGraphGroupFilters(graph);
        PublishGraph(fullReload: true);
    }

    /// <summary>Re-raises <see cref="GraphReady"/> with the last graph already computed (through the
    /// current filter), instead of redoing the vault-wide scan — for a view that just (re)attached and
    /// missed the event from whatever load triggered it (see <see cref="LastGraph"/>'s doc comment). Falls
    /// back to a real load only if nothing has been computed yet (the very first attach, before any load
    /// has completed).</summary>
    public void RepublishGraph()
    {
        if (LastGraph is not null) PublishGraph(fullReload: true);
        else LoadGraph();
    }

    /// <summary>Rebuilds <see cref="GraphGroupFilters"/> from whichever groups are actually present in
    /// <paramref name="graph"/> — one row per distinct <see cref="SessionGraphNode.SessionId"/>, vault-root
    /// first. Preserves each surviving group's on/off state across the rebuild (a session renamed, or a new
    /// note appearing in a previously-empty group, shouldn't silently reset an in-progress filter).</summary>
    private void RebuildGraphGroupFilters(SessionGraph graph)
    {
        var previousSelection = GraphGroupFilters.ToDictionary(f => f.GroupId, f => f.IsActive);
        GraphGroupFilters.Clear();

        var groups = graph.Nodes
            .Select(n => (n.SessionId, n.SessionName))
            .Distinct()
            .OrderBy(g => g.SessionId == Models.SessionGraph.RootGroupId ? 0 : 1)
            .ThenBy(g => g.SessionName, StringComparer.OrdinalIgnoreCase);

        foreach (var (groupId, name) in groups)
        {
            var isActive = !previousSelection.TryGetValue(groupId, out var previouslyActive) || previouslyActive;
            GraphGroupFilters.Add(new GraphGroupFilterItemViewModel(groupId, name, isActive, OnGraphFilterToggled,
                graph.Nodes.Count(n => n.SessionId == groupId)));
        }

        RebuildGraphDiscoveryFilters(graph);
        OnPropertyChanged(nameof(IsGraphFiltered));
    }

    private void OnGraphFilterToggled()
    {
        if (_updatingGraphFilters) return;
        SetPendingLink(null);
        OnPropertyChanged(nameof(IsGraphFiltered));
        if (LastGraph is not null) PublishGraph(fullReload: false);
    }

    /// <summary>Applies <see cref="GraphGroupFilters"/> to <see cref="LastGraph"/> and pushes the result to
    /// the WebView — a full document reload (<see cref="GraphReady"/>) when the graph document itself needs
    /// (re)building (first load, or a freshly re-attached view), or an in-place update
    /// (<see cref="GraphUpdated"/>) that leaves the camera/running simulation alone, for everything else
    /// (a filter toggle, a manual link change). Also keeps <see cref="NodeCount"/>/<see cref="EdgeCount"/>
    /// reflecting what's actually visible, not the unfiltered total.</summary>
    private void PublishGraph(bool fullReload)
    {
        if (LastGraph is not { } graph) return;

        var visible = FilterGraph(graph);
        IsEmpty = Sessions.Count == 0 && graph.Nodes.Count == 0;

        NodeCount = visible.Nodes.Count;
        EdgeCount = visible.Edges.Count;
        OnPropertyChanged(nameof(IsGraphEmpty));
        OnPropertyChanged(nameof(GraphEmptyMessage));
        if (fullReload) GraphReady?.Invoke(visible);
        else GraphUpdated?.Invoke(visible);
    }

    [RelayCommand]
    private void RefreshGraph() => LoadGraph();

    [RelayCommand]
    private void SelectSession(SessionListItemViewModel item) => SelectedSession = item;

    [RelayCommand]
    private void OpenInNotes()
    {
        if (SelectedSession is not { } item) return;

        if (item.IsEncrypted && !item.IsUnlocked)
        {
            RequestUnlockSession(item);
            return;
        }

        _onOpenInNotes(item.Session, null);
    }

    // ===================== Bloqueo/desbloqueo de sesiones protegidas =====================

    [RelayCommand]
    private void RequestUnlockSession(SessionListItemViewModel item)
    {
        SessionPendingUnlock = item;
        UnlockPasswordInput = string.Empty;
        UnlockError = null;
        IsForgotPasswordMode = false;
        RecoveryCodeInput = string.Empty;
        NewPasswordInput = string.Empty;
        NewPasswordConfirmInput = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmUnlock()
    {
        if (SessionPendingUnlock is not { } item || IsProcessingSessionAction) return;

        var password = UnlockPasswordInput;
        bool unlocked;
        IsProcessingSessionAction = true;
        try
        {
            // The PBKDF2-210k derivation this hides behind is the whole point of the cost factor — moving
            // it off the UI thread keeps the dialog responsive without weakening it (docs/version-alfa-3/
            // 01-analisis-detallado.md §4.1, Media).
            unlocked = await Task.Run(() => _sessionService.Unlock(item.Session, password));
        }
        catch (SessionManifestCorruptException ex)
        {
            ReportCorruptManifest(item, ex);
            return;
        }
        finally
        {
            IsProcessingSessionAction = false;
        }

        if (unlocked)
        {
            item.IsUnlocked = true;
            SessionPendingUnlock = null;
            _onOpenInNotes(item.Session, null);
            LoadGraph();
        }
        else
        {
            UnlockError = LocalizationService.Instance["Sessions_Unlock_WrongPassword"];
        }
    }

    [RelayCommand]
    private void BeginForgotPassword()
    {
        IsForgotPasswordMode = true;
        UnlockError = null;
    }

    [RelayCommand]
    private async Task ConfirmPasswordReset()
    {
        if (SessionPendingUnlock is not { } item || IsProcessingSessionAction) return;

        if (NewPasswordInput.Length < MinPasswordLength || NewPasswordInput != NewPasswordConfirmInput)
        {
            UnlockError = LocalizationService.Instance["Sessions_Unlock_PasswordMismatch"];
            return;
        }

        var recoveryCode = RecoveryCodeInput.Trim();
        var newPassword = NewPasswordInput;
        bool succeeded;
        IsProcessingSessionAction = true;
        try
        {
            // Same reasoning as ConfirmUnlock: this recovers the DEK under the recovery-code slot and
            // rewraps it under the new password, each its own PBKDF2-210k derivation.
            succeeded = await Task.Run(() => _sessionService.ResetPasswordWithRecoveryCode(item.Session, recoveryCode, newPassword));
        }
        catch (SessionManifestCorruptException ex)
        {
            ReportCorruptManifest(item, ex);
            return;
        }
        finally
        {
            IsProcessingSessionAction = false;
        }

        if (succeeded)
        {
            item.IsUnlocked = true;
            SessionPendingUnlock = null;
            _onOpenInNotes(item.Session, null);
            LoadGraph();
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Sessions_Unlock_PasswordResetTitle"),
                LocalizedTextFormatter.Format("Sessions_Unlock_PasswordResetMessage"),
                NotificationSeverity.Success);
        }
        else
        {
            UnlockError = LocalizationService.Instance["Sessions_Unlock_InvalidRecoveryCode"];
        }
    }

    /// <summary>Un manifiesto ilegible no es una contraseña equivocada, y decirle al usuario que lo es le
    /// hace reintentar indefinidamente algo que nunca va a funcionar — de ahí un mensaje propio en vez de
    /// reutilizar <c>Sessions_Unlock_WrongPassword</c> (.docs/auditoria-seguridad-y-fallos-criticos.md,
    /// SEC-06). El motivo técnico concreto va al log, que es donde sirve de algo; el diálogo solo dice que
    /// la sesión está dañada y que hay que recurrir a una copia de seguridad.</summary>
    private void ReportCorruptManifest(SessionListItemViewModel item, SessionManifestCorruptException ex)
    {
        LogService.Warning($"El cifrado de la sesión '{item.Session.Name}' no se puede leer", ex);
        UnlockError = LocalizationService.Instance["Sessions_Unlock_CorruptManifest"];
    }

    [RelayCommand]
    private void CancelUnlock()
    {
        SessionPendingUnlock = null;
        IsForgotPasswordMode = false;
    }

    [RelayCommand]
    private void LockSession(SessionListItemViewModel item)
    {
        // Must run before the DEK is actually dropped below — CloseEditorsForLockedSession flushes any
        // dirty open tab on this session's notes, which needs to still be able to encrypt+write
        // (análisis §4.1: locking used to leave those tabs open, editable, and silently failing to save).
        if (_onSessionLocked?.Invoke(item.Session) == false) return;
        _sessionService.Lock(item.Session);
        item.IsUnlocked = false;
        if (SelectedSession == item) LoadGraph();
    }

    [RelayCommand]
    private void BeginCreateSession()
    {
        NewSessionName = string.Empty;
        NewSessionIsProtected = false;
        NewSessionPassword = string.Empty;
        NewSessionPasswordConfirm = string.Empty;
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    private void CancelCreateSession() => IsCreateDialogOpen = false;

    [RelayCommand]
    private async Task ConfirmCreateSession()
    {
        if (!CanConfirmCreate || IsProcessingSessionAction) return;

        var name = NewSessionName.Trim();
        var isProtected = NewSessionIsProtected;
        var password = NewSessionPassword;

        Session created;
        IReadOnlyList<string>? recoveryCodes = null;
        IsProcessingSessionAction = true;
        try
        {
            // A protected session pays 6 PBKDF2-210k derivations up front (password + 5 recovery codes,
            // see SessionEncryptionService.CreateEncryptionInfo) — noticeably heavier than the single
            // derivation ConfirmUnlock/ConfirmPasswordReset pay, and the reason this command needed the
            // same off-UI-thread treatment most (docs/version-alfa-3/01-analisis-detallado.md §4.1, Media).
            if (isProtected)
            {
                var (session, codes) = await Task.Run(() => _sessionService.CreateEncrypted(name, password));
                created = session;
                recoveryCodes = codes;
            }
            else
            {
                created = await Task.Run(() => _sessionService.Create(name));
            }
        }
        finally
        {
            IsProcessingSessionAction = false;
        }

        if (recoveryCodes is not null)
        {
            _pendingRecoveryCodesSessionName = created.Name;
            PendingRecoveryCodes.Clear();
            foreach (var code in recoveryCodes) PendingRecoveryCodes.Add(code);
            IsRecoveryCodesDialogOpen = true;
        }

        IsCreateDialogOpen = false;
        Reload();
        SelectedSession = Sessions.FirstOrDefault(s => s.Session.Id == created.Id);
        _onSessionsChanged();
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Sessions_CreatedNotification_Title"),
            LocalizedTextFormatter.Format("Sessions_CreatedNotification_Message", created.Name),
            NotificationSeverity.Success);
    }

    /// <summary>Saves <see cref="PendingRecoveryCodes"/> as a plain-text file the user picks the location
    /// for — deliberately a "Save as" picker rather than silently writing to the OS Downloads folder, so
    /// the user chooses where these end up, same as the manual vault backup export.</summary>
    [RelayCommand]
    private async Task DownloadRecoveryCodes()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var safeName = VaultService.SanitizeFileName(_pendingRecoveryCodesSessionName);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Sessions_RecoveryCodes_DownloadDialogTitle"],
            SuggestedFileName = $"enki-{safeName}-recovery-codes.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Sessions_RecoveryCodes_TextFileType"]) { Patterns = ["*.txt"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            await File.WriteAllTextAsync(destination, string.Join(Environment.NewLine, PendingRecoveryCodes));
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Sessions_RecoveryCodes_DownloadedNotification_Title"),
                LocalizedTextFormatter.Format("Sessions_RecoveryCodes_DownloadedNotification_Message", destination),
                NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Sessions_RecoveryCodes_DownloadFailedTitle"),
                VaultFailureReasons.DescribeLocalized(ex),
                NotificationSeverity.Error);
        }
    }

    /// <summary>How long a copied recovery code stays on the clipboard before it's auto-cleared — long
    /// enough to paste it once into a password manager or a downloaded file, short enough that it doesn't
    /// sit there indefinitely for any other app with clipboard access to read
    /// (docs/version-alfa-3/01-analisis-detallado.md §4.1, Media).</summary>
    private static readonly TimeSpan RecoveryCodesClipboardClearDelay = TimeSpan.FromSeconds(30);

    [RelayCommand]
    private async Task CopyRecoveryCodesToClipboard()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null) return;

        var text = string.Join(Environment.NewLine, PendingRecoveryCodes);
        await clipboard.SetTextAsync(text);
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Sessions_RecoveryCodes_CopiedNotification_Title"),
            LocalizedTextFormatter.Format("Sessions_RecoveryCodes_CopiedNotification_Message"),
            NotificationSeverity.Success);

        // Fire-and-forget: the command itself is done the moment the copy succeeds, the clear just
        // happens later in the background.
        _ = ClearClipboardAfterDelay(clipboard, text);
    }

    /// <summary>Clears the clipboard <see cref="RecoveryCodesClipboardClearDelay"/> after a recovery-code
    /// copy — but only if it still holds exactly <paramref name="expectedText"/>, so a later, unrelated
    /// copy the user made in the meantime isn't wiped out from under them. Best-effort: some platforms
    /// restrict clipboard reads to a focused window, and headless tests have no clipboard at all — either
    /// failing here just means the auto-clear silently doesn't happen, not an app-visible error.</summary>
    private static async Task ClearClipboardAfterDelay(IClipboard clipboard, string expectedText)
    {
        await Task.Delay(RecoveryCodesClipboardClearDelay);
        try
        {
            var current = await clipboard.TryGetTextAsync();
            if (current == expectedText) await clipboard.ClearAsync();
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo limpiar el portapapeles tras copiar códigos de recuperación", ex);
        }
    }

    [RelayCommand]
    private void CloseRecoveryCodesDialog()
    {
        IsRecoveryCodesDialogOpen = false;
        PendingRecoveryCodes.Clear();
        _pendingRecoveryCodesSessionName = string.Empty;
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
    private void BeginChangeSessionColor(SessionListItemViewModel item)
    {
        SessionColorDraft = item.SessionColorBrush.Color;
        SessionColorHex = HexColorUtility.ColorToHex(SessionColorDraft);
        SessionPendingColor = item;
    }

    [RelayCommand]
    private void ConfirmSessionColor()
    {
        if (SessionPendingColor is not { } item ||
            HexColorUtility.NormalizeHex(SessionColorHex) is not { } normalized) return;

        ApplySessionColor(item, normalized);
        SessionPendingColor = null;
    }

    [RelayCommand]
    private void UseAutomaticSessionColor()
    {
        if (SessionPendingColor is not { } item) return;

        ApplySessionColor(item, null);
        SessionPendingColor = null;
    }

    [RelayCommand]
    private void CancelSessionColor() => SessionPendingColor = null;

    /// <summary>Persiste el color sobre la sesión real y actualiza tanto el grafo cacheado como su vista
    /// viva. El evento especializado evita recolocar nodos o alterar la cámara por un cambio cosmético.</summary>
    private void ApplySessionColor(SessionListItemViewModel item, string? color)
    {
        var updated = _sessionService.SetColor(item.Session, color);
        item.UpdateSession(updated);

        if (LastGraph is not null)
        {
            foreach (var node in LastGraph.Nodes.Where(n => n.SessionId == updated.Id))
                node.SessionColor = updated.Color;
        }

        SessionColorChanged?.Invoke(updated.Id, HexColorUtility.ColorToHex(item.SessionColorBrush.Color));

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

    [RelayCommand]
    private void ToggleGraphFilterPopover() => IsGraphFilterOpen = !IsGraphFilterOpen;

    [RelayCommand]
    private void CloseGraphFilter() => IsGraphFilterOpen = false;

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

    /// <summary>Abre en Notas el nodo activado con doble clic, seleccionando primero su sesión y después su
    /// archivo en el editor. La activación cancela cualquier extremo pendiente del modo enlace: ese estado
    /// pertenece al grafo que se está abandonando y no debe reaparecer al volver.</summary>
    public void HandleNodeDoubleClicked(string globalId)
    {
        SetPendingLink(null);
        HandleNodeOpened(globalId);
    }

    private void SetPendingLink(string? globalId)
    {
        _pendingLinkGlobalId = globalId;
        PendingLinkChanged?.Invoke(globalId);
    }

    private void HandleNodeOpened(string globalId)
    {
        if (!TryParseGlobalId(globalId, out var sessionId, out var relativePath)) return;

        Session? session = null;
        string rootPath;
        if (sessionId == Models.SessionGraph.RootGroupId)
        {
            rootPath = _vaultService.VaultPath;
        }
        else
        {
            if (FindSession(sessionId) is not { } found) return;
            session = found;
            rootPath = session.RootPath;
        }

        var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    /// <summary>A manual-link click pair from the graph — notes can now link within the same session, across
    /// two different sessions, or with a vault-root note, so the only cases left that count as a genuine
    /// failure (worth flashing both nodes via <see cref="LinkRejected"/>, instead of failing silently) are a
    /// GlobalId the WebView sent that doesn't parse, or a same-session pair naming a session that's since
    /// been deleted/renamed out from under this click.</summary>
    public void HandleLinkToggled(string fromGlobalId, string toGlobalId)
    {
        if (!TryParseGlobalId(fromGlobalId, out var fromSessionId, out var fromRelativePath) ||
            !TryParseGlobalId(toGlobalId, out var toSessionId, out var toRelativePath))
        {
            LinkRejected?.Invoke(fromGlobalId, toGlobalId);
            return;
        }

        Action toggle;
        if (fromSessionId == toSessionId && fromSessionId != Models.SessionGraph.RootGroupId)
        {
            if (FindSession(fromSessionId) is not { } session)
            {
                LinkRejected?.Invoke(fromGlobalId, toGlobalId);
                return;
            }
            toggle = () => _sessionService.ToggleManualLink(session, fromRelativePath, toRelativePath);
        }
        else
        {
            // Two different sessions, a session paired with a vault-root note, or two vault-root notes —
            // none of these share a single session manifest to store a plain ManualLinks entry in, so they
            // go through the vault-level cross-scope store instead (SessionService.ToggleGlobalManualLink).
            toggle = () => _sessionService.ToggleGlobalManualLink(fromSessionId, fromRelativePath, toSessionId, toRelativePath);
        }

        _ = ApplyLinkToggleAsync(toggle);
    }

    private async Task ApplyLinkToggleAsync(Action toggle)
    {
        var generation = ++_loadGeneration;
        // The manifest write and the full global-graph rescan that follows it are both synchronous disk I/O
        // (see SessionService.ToggleManualLink/ToggleGlobalManualLink/LoadGlobalGraph) — running them on a
        // background thread keeps a single link click from freezing the UI for however long the vault-wide
        // rescan takes.
        var graph = await Task.Run(() =>
        {
            toggle();
            return _sessionService.LoadGlobalGraph();
        });
        if (generation != _loadGeneration) return;

        LastGraph = graph;
        RebuildGraphGroupFilters(graph);
        PublishGraph(fullReload: false);
    }

    public void HandlePositionsReported(IReadOnlyList<(string RelativePath, double X, double Y, double Z)> positions)
    {
        if (positions.Count == 0) return;

        var bySession = new Dictionary<string, List<(string RelativePath, double X, double Y, double Z)>>();
        var rootPositions = new List<(string RelativePath, double X, double Y, double Z)>();
        foreach (var (globalId, x, y, z) in positions)
        {
            if (!TryParseGlobalId(globalId, out var sessionId, out var relativePath)) continue;
            if (sessionId == Models.SessionGraph.RootGroupId)
            {
                rootPositions.Add((relativePath, x, y, z));
                continue;
            }
            if (!bySession.TryGetValue(sessionId, out var list)) bySession[sessionId] = list = [];
            list.Add((relativePath, x, y, z));
        }

        var toSave = bySession
            .Select(kv => (Session: FindSession(kv.Key), Positions: kv.Value))
            .Where(t => t.Session is not null)
            .ToList();
        if (toSave.Count == 0 && rootPositions.Count == 0) return;

        // Fires on every drag-settle — each save is a synchronous read+parse+serialize+write of a session's
        // whole .session.json, or the vault-level .graph.json for root notes (see
        // SessionService.SavePositions/SaveRootPositions). Running it on a background thread keeps that
        // from hitching the UI right as a drag interaction ends, which is when it's most noticeable.
        // Positions are last-write-wins per note, so an older save racing a newer one is harmless.
        _ = Task.Run(() =>
        {
            foreach (var (session, list) in toSave) _sessionService.SavePositions(session!, list);
            if (rootPositions.Count > 0) _sessionService.SaveRootPositions(rootPositions);
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
