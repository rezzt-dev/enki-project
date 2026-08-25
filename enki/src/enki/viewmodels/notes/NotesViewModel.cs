using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class NotesViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly SessionService _sessionService;
    private readonly KanbanService? _kanbanService;
    private readonly Action<string>? _onOpenLinkedCard;

    /// <summary>Null in tests that don't care about tab persistence — every call site below guards on this
    /// being non-null, same optionality as <see cref="_kanbanService"/>. Restaurar pestañas abiertas
    /// (docs/ajustes-docs/02-productividad-como-app-de-escritorio.md §2).</summary>
    private readonly SettingsService? _settingsService;

    /// <summary>Null in tests that don't care about user-facing failure reporting. Passed through to every
    /// <see cref="EditorViewModel"/> this view model opens (autosave failure) and used directly by the
    /// create/rename/move commands below, so a vault that becomes unavailable mid-session (unplugged drive,
    /// permisos) surfaces a clear notification instead of an unhandled exception
    /// (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    private readonly NotificationService? _notificationService;

    // Reloading re-reads and re-parses every note file under the current folder from disk, so a plain
    // per-keystroke call in OnSearchTextChanged would hit the filesystem on every character typed —
    // this collapses a fast burst of keystrokes into a single Reload() once typing pauses.
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<NoteCardViewModel> Notes { get; } = [];

    /// <summary>Elementos mostrados en la rejilla principal de Notas: carpetas navegables y, tras ellas,
    /// las notas del folder actual. Se mantiene <see cref="Notes"/> como fuente de datos de las tarjetas
    /// para no romper tests y consumidores existentes; esta colección es la vista proyectada para la UI.</summary>
    public ObservableCollection<object> BrowserItems { get; } = [];

    /// <summary>"{0} notes" chip shown next to the note grid — recomputed whenever <see cref="Notes"/>'
    /// membership changes (see the <c>CollectionChanged</c> subscription in the constructor).</summary>
    public string NotesCountLabel => Plural.Get("Notes_CountLabel", Notes.Count, Notes.Count);
    public ObservableCollection<NoteFolderViewModel> RootFolders { get; } = [];
    public ObservableCollection<SessionOptionViewModel> SessionOptions { get; } = [];

    /// <summary>Every note currently open in a tab, in the order they were opened — the tab strip in
    /// NotesView.axaml binds directly to this. <see cref="ActiveEditor"/> is which of these (if any) is
    /// the one actually shown; both are kept in sync by <see cref="OpenNote"/>/<see cref="CloseEditor"/>
    /// below rather than derived, since "which tab is selected" genuinely is separate state from "which
    /// tabs exist" (closing a background tab shouldn't change the active one, for instance).</summary>
    public ObservableCollection<EditorViewModel> OpenEditors { get; } = [];

    /// <summary>The vault root, or — when a session is selected — that session's own isolated root.
    /// Every folder/note operation below resolves against this instead of the vault path directly, so
    /// browsing, creating and moving notes all stay scoped to whichever workspace is active.</summary>
    public string EffectiveRootPath => SelectedSessionOption?.Session?.RootPath ?? _vaultService.VaultPath;

    /// <summary>True while browsing a password-protected session that isn't currently unlocked — mirrors
    /// <c>FilesViewModel.IsSelectedSessionLocked</c> (docs/version-alfa-3/01-analisis-detallado.md §4.1):
    /// reading a locked session's note content throws <see cref="SessionLockedException"/>, so
    /// <see cref="Reload"/> checks this before ever calling into <see cref="VaultService"/> instead of
    /// letting that exception escape from the notes grid.</summary>
    public bool IsSelectedSessionLocked =>
        SelectedSessionOption?.Session is { IsEncrypted: true } session && !_sessionService.IsUnlocked(session);

    /// <summary>The regular "no notes here" empty state — distinct from <see cref="IsSelectedSessionLocked"/>'s
    /// own placeholder, which takes over instead when the reason the grid is empty is that the session is
    /// locked rather than genuinely having no notes.</summary>
    public bool ShowEmptyState => IsEmpty && !IsSelectedSessionLocked;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private EditorViewModel? _activeEditor;

    /// <summary>Mirrors the active tab's <see cref="EditorViewModel.IsZenMode"/> — the tab strip in
    /// NotesView.axaml binds its own visibility to this (negated), and <c>MainWindowViewModel</c> mirrors
    /// it again to also hide the shell sidebar, so entering zen/focus mode from any open note collapses
    /// the whole chrome around it, not just that tab's own header/toolbar.</summary>
    [ObservableProperty]
    private bool _isZenMode;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private NoteFolderViewModel? _currentFolder;

    [ObservableProperty]
    private bool _isRootDropTarget;

    [ObservableProperty]
    private SessionOptionViewModel? _selectedSessionOption;

    [ObservableProperty]
    private NoteCardViewModel? _notePendingDelete;

    /// <summary>Filtro de etiquetas compartido para el módulo de Notas. Se recarga con las etiquetas
    /// de todas las notas visibles del folder/workspace actual y conserva la selección del usuario al
    /// navegar entre carpetas o sesiones.</summary>
    public TagFilterViewModel TagFilter { get; } = new();

    public string CurrentFolderLabel => CurrentFolder?.Name ?? LocalizationService.Instance["Notes_AllFolderLabel"];

    private string CurrentFolderPath => CurrentFolder?.FullPath ?? EffectiveRootPath;

    public NotesViewModel(VaultService vaultService, SessionService sessionService,
        KanbanService? kanbanService = null, Action<string>? onOpenLinkedCard = null,
        SettingsService? settingsService = null, NotificationService? notificationService = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _kanbanService = kanbanService;
        _onOpenLinkedCard = onOpenLinkedCard;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            Reload();
        };
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NotesCountLabel));
        TagFilter.Changed += Reload;

        ReloadSessions();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(NotesCountLabel));
        OnPropertyChanged(nameof(CurrentFolderLabel));
        Reload();
        foreach (var editor in OpenEditors) editor.RefreshLocalizedLabels();
    }

    /// <summary>Devuelve la fecha actual como nombre de nota diaria (YYYY-MM-DD). Invariante a propósito
    /// (no <see cref="CultureInfo.CurrentCulture"/>): es un nombre de fichero técnico, no texto de UI —
    /// debe mantenerse estable con independencia del idioma activo.</summary>
    private static string DailyNoteTitle => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnCurrentFolderChanged(NoteFolderViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentFolderLabel));
        Reload();
    }

    partial void OnSelectedSessionOptionChanged(SessionOptionViewModel? value)
    {
        OnPropertyChanged(nameof(IsSelectedSessionLocked));
        OnPropertyChanged(nameof(ShowEmptyState));
        CurrentFolder = null;
        ReloadFolders();
        Reload();
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    // Mirrors "which tab is selected" onto each EditorViewModel itself (see IsActive's doc comment there)
    // purely so the tab strip can highlight it — nothing about editing/saving depends on this.
    partial void OnActiveEditorChanged(EditorViewModel? oldValue, EditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnActiveEditorPropertyChanged;
        }
        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnActiveEditorPropertyChanged;
        }
        IsZenMode = newValue?.IsZenMode ?? false;
        PersistOpenTabs();
    }

    /// <summary>Keeps <see cref="IsZenMode"/> in lockstep with whichever tab is currently active — zen
    /// mode is a shared, transient flag (<see cref="EditorPreferences.IsZenMode"/>) every open tab mirrors,
    /// so this only needs to react to the *active* one's own copy changing, not re-derive anything from
    /// the others.</summary>
    private void OnActiveEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.IsZenMode) && sender is EditorViewModel editor)
            IsZenMode = editor.IsZenMode;
    }

    /// <summary>Escribe qué pestañas están abiertas y cuál es la activa en <see cref="AppSettings.OpenNotePaths"/>/
    /// <see cref="AppSettings.ActiveNotePath"/> — llamado tras cada cambio estructural de
    /// <see cref="OpenEditors"/> (abrir, cerrar, cerrar todas, reordenar) y tras cada cambio de
    /// <see cref="ActiveEditor"/>. No-op en tests que construyen este viewmodel sin
    /// <see cref="SettingsService"/>.</summary>
    private void PersistOpenTabs()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.OpenNotePaths = OpenEditors.Select(e => e.Note.FullPath).ToList();
        _settingsService.Settings.ActiveNotePath = ActiveEditor?.Note.FullPath;
        _settingsService.Save();
    }

    /// <summary>Reabre, en orden, las pestañas guardadas de la sesión anterior (docs/ajustes-docs/
    /// 02-productividad-como-app-de-escritorio.md §2) — llamado una vez desde
    /// <c>MainWindowViewModel.InitializeShell</c> justo después de construir este viewmodel. Una ruta que
    /// ya no exista (nota borrada/movida fuera de lo que se sigue) se ignora en silencio, mismo criterio
    /// tolerante que el resto de la restauración de estado en la app.</summary>
    public void RestoreOpenTabs()
    {
        if (_settingsService is null) return;
        var paths = _settingsService.Settings.OpenNotePaths;
        if (paths.Count == 0) return;

        var activePath = _settingsService.Settings.ActiveNotePath;
        foreach (var path in paths)
        {
            // Every encrypted session starts locked on process launch, so a tab left open on one of its
            // notes at the previous shutdown can't be restored now — skip it the same tolerant way a
            // deleted/moved path already is, instead of letting GetNote throw SessionLockedException
            // straight out of shell initialization (docs/version-alfa-3/01-analisis-detallado.md §4.1).
            if (_vaultService.IsPathInLockedSession(path)) continue;

            var note = _vaultService.GetNote(path);
            if (note is not null) OpenNote(note);
        }

        if (activePath is not null)
        {
            var match = OpenEditors.FirstOrDefault(e =>
                VaultService.PathsEqual(e.Note.FullPath, activePath));
            if (match is not null) ActiveEditor = match;
        }
    }

    /// <summary>Refreshes the session picker (vault + every session, alphabetically), keeping the current
    /// selection if it still exists. Called on startup and whenever a session is created/renamed/deleted,
    /// whether that happened here or on the Sessions page.</summary>
    public void ReloadSessions()
    {
        var previouslySelectedId = SelectedSessionOption?.Session?.Id;

        SessionOptions.Clear();
        SessionOptions.Add(new SessionOptionViewModel(null));
        foreach (var session in _sessionService.List()) SessionOptions.Add(new SessionOptionViewModel(session));

        SelectedSessionOption = previouslySelectedId is null
            ? SessionOptions[0]
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == previouslySelectedId) ?? SessionOptions[0];
    }

    /// <summary>Switches the active workspace to the given session (or the vault, if null) — used when
    /// jumping here from the Sessions page.</summary>
    public void SelectSession(string? sessionId)
    {
        var match = sessionId is null
            ? SessionOptions.FirstOrDefault(o => o.IsVault)
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == sessionId);
        if (match is not null) SelectedSessionOption = match;
    }

    public void Reload()
    {
        if (IsSelectedSessionLocked)
        {
            Notes.Clear();
            BrowserItems.Clear();
            IsEmpty = true;
            TagFilter.Reload([]);
            return;
        }

        var all = _vaultService.GetNotes(CurrentFolderPath);
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? all
            : all.Where(n => n.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                              || n.Preview.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        var selectedTags = TagFilter.SelectedTags.ToList();
        if (selectedTags.Count > 0)
        {
            filtered = filtered.Where(n => n.Tags.Any(tag =>
                selectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        Notes.Clear();
        foreach (var note in filtered) Notes.Add(new NoteCardViewModel(note));

        BrowserItems.Clear();
        var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
        if (!hasSearch)
        {
            if (CurrentFolder is not null)
            {
                var parentPath = Path.GetDirectoryName(CurrentFolder.FullPath);
                if (!string.IsNullOrEmpty(parentPath))
                    BrowserItems.Add(new NoteFolderCardViewModel("..", parentPath, isParent: true));
            }

            foreach (var folder in _vaultService.GetFolderTree(CurrentFolderPath))
                BrowserItems.Add(new NoteFolderCardViewModel(folder.Name, folder.FullPath));
        }

        foreach (var note in Notes) BrowserItems.Add(note);

        IsEmpty = BrowserItems.Count == 0;

        TagFilter.Reload(all.SelectMany(n => n.Tags).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public void ReloadFolders()
    {
        // On the very first call RootFolders is empty, so there is no in-memory expand state to
        // preserve — fall back to whatever the user had last time (persisted in settings). On
        // every subsequent call the in-memory state (kept up-to-date by the OnExpandedChanged
        // callback) is authoritative.
        var expandedPaths = RootFolders.Count > 0
            ? CollectExpandedPaths(RootFolders).ToHashSet()
            : new HashSet<string>(_settingsService?.Settings.NotesExpandedFolderPaths ?? []);
        var selectedPath = CurrentFolder?.FullPath;

        RootFolders.Clear();
        foreach (var folder in _vaultService.GetFolderTree(EffectiveRootPath))
        {
            var vm = new NoteFolderViewModel(folder) { OnExpandedChanged = PersistExpandedFolders };
            ApplyExpandedState(vm, expandedPaths);
            RootFolders.Add(vm);
        }

        CurrentFolder = selectedPath is null ? null : FindFolder(RootFolders, selectedPath);
    }

    private static IEnumerable<string> CollectExpandedPaths(IEnumerable<NoteFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            if (folder.IsExpanded) yield return folder.FullPath;
            foreach (var path in CollectExpandedPaths(folder.Children)) yield return path;
        }
    }

    private static void ApplyExpandedState(NoteFolderViewModel folder, HashSet<string> expandedPaths)
    {
        folder.IsExpanded = expandedPaths.Count > 0 && expandedPaths.Contains(folder.FullPath);
        foreach (var child in folder.Children) ApplyExpandedState(child, expandedPaths);
    }

    /// <summary>Writes the currently expanded folder paths to <see cref="AppSettings"/> so they
    /// survive an app restart — called from <see cref="NoteFolderViewModel.OnExpandedChanged"/>
    /// whenever the user toggles any folder in the sidebar.</summary>
    private void PersistExpandedFolders()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.NotesExpandedFolderPaths = CollectExpandedPaths(RootFolders).ToList();
        _settingsService.Save();
    }

    private static NoteFolderViewModel? FindFolder(IEnumerable<NoteFolderViewModel> folders, string path)
    {
        foreach (var folder in folders)
        {
            if (folder.FullPath == path) return folder;
            var found = FindFolder(folder.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    [RelayCommand]
    private void NewNote()
    {
        NoteFile note;
        try
        {
            note = _vaultService.CreateNote(LocalizationService.Instance["Vault_DefaultNoteTitle"], CurrentFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        Reload();
        OpenNote(note);
    }

    [RelayCommand]
    private void OpenDailyNote()
    {
        var title = DailyNoteTitle;
        var all = _vaultService.GetAllNotesRecursive(_vaultService.VaultPath);
        var existing = all.FirstOrDefault(n =>
            string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));

        NoteFile note;
        try
        {
            note = existing ?? _vaultService.CreateNote(title, CurrentFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        Reload();
        OpenNote(note);
    }

    /// <summary>Shared failure reporting for the create/rename/move commands below — a vault write that
    /// fails (unplugged drive, permisos) logs the exception and surfaces a translated notification instead
    /// of leaving the command silently do nothing (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    private void NotifyVaultWriteFailed(Exception ex)
    {
        LogService.Error("Fallo al escribir en el vault desde Documentos", ex);
        _notificationService?.Push(
            LocalizationService.Instance["Notification_VaultWriteFailed_Title"],
            VaultFailureReasons.Describe(ex),
            NotificationSeverity.Error);
    }

    [RelayCommand]
    private async Task Import()
    {
        var topLevel = App.MainWindowRef is not null
            ? Avalonia.Controls.TopLevel.GetTopLevel(App.MainWindowRef)
            : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Notes_ImportFileDialog_Title"],
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] }]
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) _vaultService.ImportFile(path, CurrentFolderPath);
        }

        if (files.Count > 0) Reload();
    }

    // ===================== Exportar (propuesta 6, docs/propuesta-nuevas-funcionalidades.md) =====================

    /// <summary>"Exportar como HTML…" desde el menú contextual de una tarjeta de nota.</summary>
    [RelayCommand]
    private async Task ExportNoteAsHtml(NoteCardViewModel card)
    {
        if (!TryReadContentForExport(card.Note, out var content)) return;
        await ExportNoteAsHtmlAsync(card.Note, content);
    }

    /// <summary>Exportación HTML directa (diálogo de guardar + escribir archivo + aviso), compartida por
    /// la tarjeta y por el editor (que la invoca con el texto en vivo de su documento).</summary>
    private async Task ExportNoteAsHtmlAsync(NoteFile note, string content)
    {
        var topLevel = App.MainWindowRef is not null
            ? Avalonia.Controls.TopLevel.GetTopLevel(App.MainWindowRef)
            : null;
        await NoteExportService.ExportHtmlAsync(note, content, includeTags: true, topLevel, NotifyCallback);
    }

    /// <summary><see cref="NotificationService.Push"/> como callback suelto (o null en tests sin servicio)
    /// — lo recibe <see cref="NoteExportService"/> para anunciar éxito/fallo sin referenciar el servicio
    /// directamente. La lambda es necesaria: <c>Push</c> devuelve el <see cref="AppNotification"/> creado,
    /// así que no convierte directamente a un <c>Action</c>.</summary>
    private Action<string, string, NotificationSeverity>? NotifyCallback =>
        _notificationService is null
            ? null
            : (title, message, severity) => _notificationService.Push(title, message, severity);

    /// <summary>Contenido a exportar: si la nota está abierta en una pestaña, el texto en vivo de su
    /// documento (incluye cambios aún sin guardar — lo que el usuario ve es lo que se exporta); si no, el
    /// contenido de disco. Un fallo de lectura se notifica y aborta la exportación en vez de exportar una
    /// nota vacía (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
    private bool TryReadContentForExport(NoteFile note, out string content)
    {
        var openEditor = OpenEditors.FirstOrDefault(e => VaultService.PathsEqual(e.Note.FullPath, note.FullPath));
        if (openEditor is not null)
        {
            content = openEditor.Document.Text;
            return true;
        }

        try
        {
            content = _vaultService.ReadContent(note);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error($"No se pudo leer la nota '{note.FullPath}' para exportarla", ex);
            _notificationService?.Push(
                LocalizationService.Instance["Notification_NoteExportFailed_Title"],
                VaultFailureReasons.Describe(ex),
                NotificationSeverity.Error);
            content = string.Empty;
            return false;
        }
    }

    /// <summary>Opens a note in its own tab, or — if it's already open — just switches to that existing
    /// tab instead of opening a second one for the same file. Matched by full path since that's what
    /// actually identifies "the same note on disk" (NoteFile instances handed in from different callers —
    /// a note card, a graph node, the dashboard — aren't reference-equal even for the same file).</summary>
    public void OpenNote(NoteFile note)
    {
        var existing = OpenEditors.FirstOrDefault(e =>
            VaultService.PathsEqual(e.Note.FullPath, note.FullPath));
        if (existing is not null)
        {
            ActiveEditor = existing;
            return;
        }

        // The closure below captures the `editor` variable itself, not its value at this point (still
        // null) — by the time Back/Delete actually invoke it, the assignment on the next line has already
        // run, so it correctly closes this specific tab rather than whichever one was active at the time.
        EditorViewModel? editor = null;
        editor = new EditorViewModel(_vaultService, note, () => CloseEditor(editor!), Reload,
            OpenNote, _kanbanService, _onOpenLinkedCard, _settingsService, _notificationService,
            onExportHtml: (n, content) => _ = ExportNoteAsHtmlAsync(n, content));
        OpenEditors.Add(editor);
        ActiveEditor = editor;
    }

    [RelayCommand]
    private void OpenCard(NoteCardViewModel card) => OpenNote(card.Note);

    [RelayCommand]
    private void SelectEditor(EditorViewModel editor) => ActiveEditor = editor;

    /// <summary>Backs the "+" button on the tab strip — switches to browsing mode (the note/folder grid)
    /// without touching <see cref="OpenEditors"/>, so existing tabs stay open and the user can pick one of
    /// them, an existing note from the grid, or <see cref="NewNoteCommand"/> to actually create a new one.</summary>
    [RelayCommand]
    private void ShowBrowsingMode() => ActiveEditor = null;

    /// <summary>Backs the <c>Ctrl+Shift+Enter</c> shortcut in NotesView.axaml — forwarded to the active
    /// tab's own command instead of duplicating its toggle logic, so it works regardless of exactly which
    /// control inside the note has keyboard focus.</summary>
    [RelayCommand]
    private void ToggleZenMode() => ActiveEditor?.ToggleZenModeCommand.Execute(null);

    [RelayCommand]
    private void CloseEditorTab(EditorViewModel editor) => CloseEditor(editor);

    [RelayCommand]
    private void CloseActiveEditor()
    {
        if (ActiveEditor is not null) CloseEditor(ActiveEditor);
    }

    [RelayCommand]
    private void CloseAllEditors()
    {
        if (OpenEditors.Count == 0) return;
        foreach (var editor in OpenEditors) editor.FlushPendingSave();
        OpenEditors.Clear();
        ActiveEditor = null;
        PersistOpenTabs();
        Reload();
    }

    /// <summary>Writes out every open tab's debounced pending change immediately — called right before the
    /// app window closes (see MainWindow.axaml.cs), since a tab nobody keeps open long enough for
    /// <see cref="EditorViewModel"/>'s save timer to fire would otherwise lose whatever was typed in the
    /// last few hundred milliseconds before the window shut.</summary>
    public void FlushAllEditors()
    {
        foreach (var editor in OpenEditors) editor.FlushPendingSave();
    }

    /// <summary>Called from <see cref="SessionsViewModel.LockSession"/>, while <paramref name="session"/>'s
    /// DEK is still unlocked, right before it actually drops it — closes every open tab on one of that
    /// session's notes instead of leaving its decrypted content sitting on screen and its autosave quietly
    /// failing on every tick once the DEK is gone (docs/version-alfa-3/01-analisis-detallado.md §4.1: the
    /// padlock in Sesiones looked closed while an already-open tab kept showing and letting the user edit
    /// plain-text content underneath it). Flushing first, while still unlocked, means nothing typed in an
    /// affected tab is lost by being closed out from under the user.</summary>
    public void CloseEditorsForLockedSession(Session session)
    {
        var affected = OpenEditors.Where(e => IsUnderSessionRoot(e.Note.FullPath, session.RootPath)).ToList();
        if (affected.Count == 0) return;

        foreach (var editor in affected)
        {
            editor.FlushPendingSave();
            OpenEditors.Remove(editor);
        }

        if (ActiveEditor is not null && affected.Contains(ActiveEditor))
            ActiveEditor = OpenEditors.FirstOrDefault();

        PersistOpenTabs();
        Reload();

        _notificationService?.Push(
            LocalizationService.Instance["Notification_SessionLockedTabsClosed_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_SessionLockedTabsClosed_Message"], session.Name),
            NotificationSeverity.Info);
    }

    private static bool IsUnderSessionRoot(string fullPath, string sessionRootPath)
    {
        var relative = Path.GetRelativePath(sessionRootPath, fullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    [RelayCommand]
    private void SelectNextEditor() => CycleActiveEditor(1);

    [RelayCommand]
    private void SelectPreviousEditor() => CycleActiveEditor(-1);

    private void CycleActiveEditor(int direction)
    {
        if (OpenEditors.Count < 2 || ActiveEditor is null) return;
        var index = OpenEditors.IndexOf(ActiveEditor);
        ActiveEditor = OpenEditors[(index + direction + OpenEditors.Count) % OpenEditors.Count];
    }

    /// <summary>Reorders the tab strip by moving <paramref name="source"/> to sit where
    /// <paramref name="target"/> currently is — driven by dragging a tab in NotesView.axaml.cs. Doesn't
    /// touch <see cref="ActiveEditor"/>: reordering tabs shouldn't change which one is selected.</summary>
    public void ReorderEditor(EditorViewModel source, EditorViewModel target)
    {
        if (source == target) return;
        var oldIndex = OpenEditors.IndexOf(source);
        var newIndex = OpenEditors.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;
        OpenEditors.Move(oldIndex, newIndex);
        PersistOpenTabs();
    }

    private void CloseEditor(EditorViewModel editor)
    {
        var index = OpenEditors.IndexOf(editor);
        if (index < 0) return;

        editor.FlushPendingSave();
        OpenEditors.RemoveAt(index);
        if (ActiveEditor == editor)
        {
            // Falls back to whichever tab now sits at the same index (the next one over), same convention
            // browsers/editors use when closing the active tab — or back to browsing mode if that was the
            // last tab open.
            ActiveEditor = OpenEditors.Count == 0 ? null : OpenEditors[Math.Min(index, OpenEditors.Count - 1)];
        }
        else
        {
            // Closing a background tab doesn't change ActiveEditor, so OnActiveEditorChanged (which also
            // persists) never fires — persist explicitly here so the closed tab still drops out of
            // AppSettings.OpenNotePaths.
            PersistOpenTabs();
        }

        Reload();
    }

    [RelayCommand]
    private void SelectFolder(NoteFolderViewModel? folder) => CurrentFolder = folder;

    [RelayCommand]
    private void OpenFolderCard(NoteFolderCardViewModel card)
    {
        if (card.IsParent)
        {
            CurrentFolder = string.IsNullOrEmpty(card.FullPath)
                ? null
                : FindFolder(RootFolders, card.FullPath);
            return;
        }

        CurrentFolder = FindFolder(RootFolders, card.FullPath);
    }

    [RelayCommand]
    private void CreateSubfolder(NoteFolderViewModel folder)
    {
        var created = VaultService.CreateFolder(folder.FullPath);
        ReloadFolders();

        var vm = FindFolder(RootFolders, created.FullPath);
        if (vm is null) return;
        vm.EditingName = vm.Name;
        vm.IsRenaming = true;
    }

    [RelayCommand]
    private void NewFolder()
    {
        var created = VaultService.CreateFolder(CurrentFolderPath);
        ReloadFolders();

        var vm = FindFolder(RootFolders, created.FullPath);
        if (vm is null) return;
        vm.EditingName = vm.Name;
        vm.IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRenameFolder(NoteFolderViewModel folder)
    {
        if (!string.IsNullOrWhiteSpace(folder.EditingName) && folder.EditingName != folder.Name)
        {
            var updated = VaultService.RenameFolder(folder.Folder, folder.EditingName);
            folder.UpdateFolder(updated);
        }
        folder.IsRenaming = false;
        ReloadFolders();
        Reload();
    }

    [RelayCommand]
    private void DeleteFolder(NoteFolderViewModel folder)
    {
        var affectsCurrent = CurrentFolder is not null &&
                              (CurrentFolder == folder || IsDescendant(folder, CurrentFolder));

        _vaultService.DeleteFolder(folder.Folder);
        if (affectsCurrent) CurrentFolder = null;

        ReloadFolders();
        Reload();
    }

    private static bool IsDescendant(NoteFolderViewModel ancestor, NoteFolderViewModel node)
    {
        foreach (var child in ancestor.Children)
        {
            if (child == node || IsDescendant(child, node)) return true;
        }
        return false;
    }

    [RelayCommand]
    private void CommitRenameNote(NoteCardViewModel card)
    {
        if (!string.IsNullOrWhiteSpace(card.EditingName) && card.EditingName != card.Title)
        {
            var oldPath = card.Note.FullPath;
            NoteFile updated;
            try
            {
                updated = _vaultService.RenameNote(card.Note, card.EditingName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
            {
                NotifyVaultWriteFailed(ex);
                card.IsRenaming = false;
                return;
            }

            card.UpdateNote(updated);
            _kanbanService?.RelinkNotePath(oldPath, updated.FullPath);
            NotifyRenameCollisionIfAny(card.EditingName, updated);

            // Keep an already-open tab for this note in sync — otherwise it'd keep pointing at the
            // file's old path/name after the rename happens from the grid instead of the tab itself.
            var openEditor = OpenEditors.FirstOrDefault(e =>
                VaultService.PathsEqual(e.Note.FullPath, oldPath));
            openEditor?.SyncRenamedNote(updated);
        }
        card.IsRenaming = false;
        Reload();
    }

    /// <summary>A rename to an already-taken name gets a numeric suffix instead of silently doing nothing
    /// (see <see cref="VaultService.RenameNote"/>) — surfaces that here so the user knows the note didn't
    /// end up with the exact name they typed (docs/analisis-proyecto/roadmap.md, Fase 5).</summary>
    private void NotifyRenameCollisionIfAny(string requestedTitle, NoteFile updated)
    {
        var finalName = Path.GetFileNameWithoutExtension(updated.FullPath);
        if (string.Equals(finalName, requestedTitle, StringComparison.Ordinal)) return;

        _notificationService?.Push(
            LocalizationService.Instance["Notification_NoteRenameCollision_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_NoteRenameCollision_Message"], finalName),
            NotificationSeverity.Info);
    }

    // ===================== Delete confirmation dialog =====================
    // análisis alfa-3 §4.5 — "preguntar antes de borrar" se saltaba en silencio para Notas, a
    // diferencia de Hábitos/Polar/Skills/Kanban/Calendario, que sí lo respetan.

    [RelayCommand]
    private void RequestDeleteNote(NoteCardViewModel card)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) NotePendingDelete = card;
        else DeleteNote(card);
    }

    [RelayCommand]
    private void CancelDeleteNote() => NotePendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteNote()
    {
        if (NotePendingDelete is not { } card) return;
        DeleteNote(card);
        NotePendingDelete = null;
    }

    private void DeleteNote(NoteCardViewModel card)
    {
        EditorBacklinksService.WarnIfDeletingLeavesBrokenLinks(_vaultService, _notificationService, card.Note);
        _vaultService.DeleteNote(card.Note);

        var openEditor = OpenEditors.FirstOrDefault(e =>
            VaultService.PathsEqual(e.Note.FullPath, card.Note.FullPath));
        if (openEditor is not null) CloseEditor(openEditor); // also reloads
        else Reload();
    }

    public void MoveNoteToFolder(NoteCardViewModel card, string destinationFolderPath)
    {
        var currentFolderPath = Path.GetDirectoryName(card.Note.FullPath);
        if (currentFolderPath is not null && VaultService.PathsEqual(currentFolderPath, destinationFolderPath)) return;

        var oldPath = card.Note.FullPath;
        try
        {
            _vaultService.MoveNote(card.Note, destinationFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        _kanbanService?.RelinkNotePath(oldPath, card.Note.FullPath);
        Reload();
    }
}
