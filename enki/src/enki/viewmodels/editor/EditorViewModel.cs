using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class EditorViewModel : ViewModelBase
{
    /// <summary>How long to wait after the last keystroke before writing to disk
    /// (03-problemas-y-deuda-tecnica.md §3: cada tecla disparaba un <c>File.WriteAllText</c> síncrono).</summary>
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(600);

    private readonly VaultService _vaultService;
    private readonly KanbanService? _kanbanService;
    private readonly SettingsService? _settingsService;
    private readonly NotificationService? _notificationService;
    private readonly Action _onBack;
    private readonly Action _onChanged;
    private readonly Action<NoteFile>? _onOpenNote;
    private readonly Action<string>? _onOpenLinkedCard;
    private readonly DispatcherTimer _saveTimer;
    private string _lastSavedContent;
    private string? _pendingText;
    private bool _suppressTitleRename;
    private DateTime _lastSavedUtc;

    public NoteFile Note { get; private set; }

    /// <summary>Tarjetas Kanban cuyo <see cref="KanbanCard.LinkedNotePath"/> apunta a esta nota — lado
    /// inverso, derivado, del enlace nota↔tarea. Se recalcula al abrir la nota y al renombrarla, no en vivo mientras la pestaña
    /// permanece abierta (mismo alcance que el resto del refresco "recargar para ver" del resto de la
    /// app, p. ej. <c>MainWindowViewModel.Navigate</c> con Sesiones/Kanban).</summary>
    public ObservableCollection<LinkedCardItemViewModel> LinkedCards { get; } = [];

    public bool HasLinkedCards => LinkedCards.Count > 0;

    /// <summary>This tab's own AvaloniaEdit document, alive for as long as the tab stays open. The single
    /// shared TextEditor in EditorView swaps which Document it points at when the active tab changes
    /// (rather than resetting its Text to a snapshot), so each note keeps its own undo/redo history
    /// instead of it being wiped — or worse, shared — across tabs.</summary>
    public TextDocument Document { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(ShowSavedIcon));

    [ObservableProperty]
    private string _saveStatus = LocalizationService.Instance["Editor_SaveStatus_Saved"];

    /// <summary>Backs the save indicator's icon ("indicador de guardado prominente + timestamp") — a
    /// checkmark once <see cref="SaveStatus"/>
    /// carries a real timestamp, a plain dot while <see cref="IsDirty"/> is true.</summary>
    public bool ShowSavedIcon => !IsDirty;

    [ObservableProperty]
    private string _tagsInput = string.Empty;

    /// <summary>Whether this is the currently selected tab among possibly several open notes — set by
    /// NotesViewModel whenever its ActiveEditor changes, purely so the tab strip can highlight the right
    /// one (see NotesView.axaml's editor-tab.active style). Doesn't affect editing/saving at all.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Whether a dragged tab is currently hovering over this one — drives the same
    /// editor-tab.drag-over highlight the folder tree uses for its own drop targets (NotesView.axaml.cs,
    /// EditorTab_Drag*), set while reordering tabs by drag-and-drop.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Caret offset captured by EditorView when this tab loses focus, and restored when it
    /// regains it — so switching away and back lands you exactly where you left off instead of
    /// resetting to the top of the note every time.</summary>
    public int LastCaretOffset { get; set; }

    public EditorOutlineViewModel Outline { get; }
    public EditorBacklinksViewModel Backlinks { get; }
    public EditorStatsViewModel Stats { get; }
    public SlashCommandsViewModel SlashCommands { get; } = new SlashCommandsViewModel();
    public FindReplaceViewModel FindReplace { get; } = new FindReplaceViewModel();

    [ObservableProperty]
    private string _activeSidePanelTab = "Outline";

    [RelayCommand]
    private void SelectSidePanelTab(string tab) => ActiveSidePanelTab = tab;

    /// <summary>Mirrors <see cref="EditorPreferences.SidePanelVisible"/> (the app-wide, shared toggle the
    /// Settings page also writes to) so the button next to the header can bind its icon/tooltip per tab
    /// without every open tab needing to observe that static event — EditorView resyncs this from the
    /// static value on every tab switch (see EditorView.OnDataContextChanged).</summary>
    [ObservableProperty]
    private bool _isSidePanelVisible = EditorPreferences.SidePanelVisible;

    /// <summary>Drives the animated width of the side-panel host in EditorView (see
    /// Border.Transitions there) — 0 collapses it and lets the editor column's "*" grid width claim the
    /// freed space, instead of the panel merely toggling IsVisible while the layout keeps reserving its
    /// fixed pixel width regardless.</summary>
    public double SidePanelWidth => IsSidePanelVisible ? 361 : 0;

    /// <summary>Mirrors MainWindowViewModel.SidebarToggleIcon's chevron-swap convention for the app's
    /// left nav rail, flipped for this panel sitting on the right: pointing toward the edge the panel
    /// will collapse to while it's open, and back toward center once it's hidden — instead of the single
    /// static glyph this button used to show regardless of state.</summary>
    public MaterialIconKind SidePanelToggleIcon =>
        IsSidePanelVisible ? MaterialIconKind.ChevronDoubleRight : MaterialIconKind.ChevronDoubleLeft;

    partial void OnIsSidePanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SidePanelWidth));
        OnPropertyChanged(nameof(SidePanelToggleIcon));
    }

    [RelayCommand]
    private void ToggleSidePanel()
    {
        IsSidePanelVisible = !IsSidePanelVisible;
        EditorPreferences.SidePanelVisible = IsSidePanelVisible;
        if (_settingsService is not null)
        {
            _settingsService.Settings.EditorSidePanelVisible = IsSidePanelVisible;
            _settingsService.Save();
        }
    }

    /// <summary>Master show/hide for the whole formatting toolbar (Grid.Row="1" in EditorView.axaml,
    /// "Auto"-height) — independent of the five per-group flags below, which only matter while this is
    /// true. Hiding it collapses that row to 0 and hands the space back to the editor body's "*" row,
    /// same shape as ToggleSidePanel above but without needing an explicit width/animation, since a
    /// hidden Auto row already collapses on its own.</summary>
    [ObservableProperty]
    private bool _isToolbarVisible = EditorPreferences.ToolbarVisible;

    [RelayCommand]
    private void ToggleToolbar()
    {
        IsToolbarVisible = !IsToolbarVisible;
        EditorPreferences.ToolbarVisible = IsToolbarVisible;
        if (_settingsService is not null)
        {
            _settingsService.Settings.EditorToolbarVisible = IsToolbarVisible;
            _settingsService.Save();
        }
    }

    // ===================== Formatting toolbar groups (Fase 4 §"Editor de notas", gap 17) =====================
    // Each group (text style, headings, lists, insert, code) has its own collapse chevron in EditorView's
    // toolbar. State mirrors EditorPreferences' static flags (see IsSidePanelVisible above for the same
    // shape) so every other open tab reflects the choice immediately via EditorView.ApplyToolbarGroupState,
    // and persists straight to AppSettings the same way ToggleSidePanel does.

    [ObservableProperty]
    private bool _isToolbarTextStyleExpanded = EditorPreferences.ToolbarTextStyleExpanded;

    [ObservableProperty]
    private bool _isToolbarHeadingsExpanded = EditorPreferences.ToolbarHeadingsExpanded;

    [ObservableProperty]
    private bool _isToolbarListsExpanded = EditorPreferences.ToolbarListsExpanded;

    [ObservableProperty]
    private bool _isToolbarInsertExpanded = EditorPreferences.ToolbarInsertExpanded;

    [ObservableProperty]
    private bool _isToolbarCodeExpanded = EditorPreferences.ToolbarCodeExpanded;

    [RelayCommand]
    private void ToggleToolbarGroup(string group)
    {
        bool expanded;
        switch (group)
        {
            case "TextStyle":
                expanded = IsToolbarTextStyleExpanded = !IsToolbarTextStyleExpanded;
                EditorPreferences.ToolbarTextStyleExpanded = expanded;
                if (_settingsService is not null) _settingsService.Settings.EditorToolbarTextStyleExpanded = expanded;
                break;
            case "Headings":
                expanded = IsToolbarHeadingsExpanded = !IsToolbarHeadingsExpanded;
                EditorPreferences.ToolbarHeadingsExpanded = expanded;
                if (_settingsService is not null) _settingsService.Settings.EditorToolbarHeadingsExpanded = expanded;
                break;
            case "Lists":
                expanded = IsToolbarListsExpanded = !IsToolbarListsExpanded;
                EditorPreferences.ToolbarListsExpanded = expanded;
                if (_settingsService is not null) _settingsService.Settings.EditorToolbarListsExpanded = expanded;
                break;
            case "Insert":
                expanded = IsToolbarInsertExpanded = !IsToolbarInsertExpanded;
                EditorPreferences.ToolbarInsertExpanded = expanded;
                if (_settingsService is not null) _settingsService.Settings.EditorToolbarInsertExpanded = expanded;
                break;
            case "Code":
                expanded = IsToolbarCodeExpanded = !IsToolbarCodeExpanded;
                EditorPreferences.ToolbarCodeExpanded = expanded;
                if (_settingsService is not null) _settingsService.Settings.EditorToolbarCodeExpanded = expanded;
                break;
            default:
                return;
        }
        _settingsService?.Save();
    }

    /// <summary>Resyncs this tab's toolbar-group flags from <see cref="EditorPreferences"/> — called by
    /// EditorView whenever a *different* tab's toggle (or the shared static state) changed while this one
    /// wasn't the active DataContext, so switching back to it shows the up-to-date collapsed/expanded set
    /// instead of whatever this tab's own instance was constructed with.</summary>
    public void ApplyToolbarGroupState()
    {
        IsToolbarVisible = EditorPreferences.ToolbarVisible;
        IsToolbarTextStyleExpanded = EditorPreferences.ToolbarTextStyleExpanded;
        IsToolbarHeadingsExpanded = EditorPreferences.ToolbarHeadingsExpanded;
        IsToolbarListsExpanded = EditorPreferences.ToolbarListsExpanded;
        IsToolbarInsertExpanded = EditorPreferences.ToolbarInsertExpanded;
        IsToolbarCodeExpanded = EditorPreferences.ToolbarCodeExpanded;
    }

    public EditorViewModel(VaultService vaultService, NoteFile note, Action onBack, Action onChanged,
        Action<NoteFile>? onOpenNote = null, KanbanService? kanbanService = null, Action<string>? onOpenLinkedCard = null,
        SettingsService? settingsService = null, NotificationService? notificationService = null)
    {
        _vaultService = vaultService;
        _onBack = onBack;
        _onChanged = onChanged;
        _onOpenNote = onOpenNote;
        _kanbanService = kanbanService;
        _onOpenLinkedCard = onOpenLinkedCard;
        _settingsService = settingsService;
        _notificationService = notificationService;
        Note = note;
        _title = System.IO.Path.GetFileNameWithoutExtension(note.FullPath);
        _tagsInput = string.Join(", ", note.Tags);

        var content = vaultService.ReadContent(note);
        Document = new TextDocument(content);
        _lastSavedContent = content;
        _lastSavedUtc = note.ModifiedUtc;
        _saveStatus = FormatSaveStatus(_lastSavedUtc);

        Outline = new EditorOutlineViewModel(Document);
        Backlinks = new EditorBacklinksViewModel(note, vaultService, onOpenNote ?? (_ => { }));
        Stats = new EditorStatsViewModel(Document, note.FullPath);
        RefreshWikiLinkScopeNotes();

        _saveTimer = new DispatcherTimer { Interval = SaveDebounceDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (_pendingText is { } text) Save(text);
            Backlinks.Refresh(Note);
        };

        RefreshLinkedCards();
    }

    private void RefreshLinkedCards()
    {
        LinkedCards.Clear();
        if (_kanbanService is not null)
        {
            var cards = _kanbanService.Board.Columns.SelectMany(c => c.Cards)
                .Where(c => string.Equals(c.LinkedNotePath, Note.FullPath, StringComparison.OrdinalIgnoreCase));
            foreach (var card in cards) LinkedCards.Add(new LinkedCardItemViewModel(card.Id, card.Title));
        }
        OnPropertyChanged(nameof(HasLinkedCards));
    }

    [RelayCommand]
    private void OpenLinkedCard(string cardId) => _onOpenLinkedCard?.Invoke(cardId);

    /// <summary>Ctrl+click en un wikilink (<see cref="Views.Editor.Markdown.MdSpanKind.WikiLink"/>) —
    /// resuelve <paramref name="target"/> contra
    /// las notas del mismo workspace que esta nota (la raíz del vault, o la sesión que la contiene) y abre
    /// la que calce, igual que un enlace normal abre una URL. Un target que no resuelve a ninguna nota
    /// (typo, nota borrada) simplemente no hace nada — mismo comportamiento silencioso-pero-inofensivo que
    /// ya tenía un wikilink sin resolver en el grafo de sesiones.</summary>
    public void OpenWikiLink(string target)
    {
        if (_onOpenNote is null) return;

        var match = WikiLinkParser.ResolveTarget(WikiLinkScopeNotes, target);
        if (match is not null) _onOpenNote(match);
    }

    /// <summary>Every note in this note's wikilink scope (<see cref="VaultService.ResolveWikiLinkScopeRoot"/>
    /// — the session it lives under, or the vault root) — what <see cref="OpenWikiLink"/> resolves a
    /// Ctrl+click against, and what the live-preview colorizer
    /// (<c>Views.Editor.Markdown.MarkdownLivePreviewColorizer</c>) uses to tell a resolved wikilink apart
    /// from a broken or ambiguous one. Cached rather than rescanned per keystroke — unlike
    /// <see cref="Backlinks"/> (which needs fresh content from every other note on every change), this only
    /// needs to know which notes *exist*, which only this tab's own rename/delete actions change while it's
    /// open — so it's refreshed there, not in <see cref="OnTextChanged"/>. A note created or renamed from a
    /// different tab isn't picked up until this one reopens, same "reload to see" tolerance the rest of the
    /// app already has for cross-tab state (docs/analisis-proyecto/roadmap.md, Fase 5).</summary>
    public IReadOnlyList<NoteFile> WikiLinkScopeNotes { get; private set; } = [];

    private void RefreshWikiLinkScopeNotes()
    {
        var root = _vaultService.ResolveWikiLinkScopeRoot(Note.FullPath);
        WikiLinkScopeNotes = _vaultService.GetAllNotesRecursive(root);
    }

    public void OnTextChanged(string currentText)
    {
        var dirty = currentText != _lastSavedContent;
        IsDirty = dirty;
        SaveStatus = dirty ? LocalizationService.Instance["Editor_SaveStatus_Unsaved"] : LocalizationService.Instance["Editor_SaveStatus_Saved"];
        _pendingText = currentText;

        // Backlinks.Refresh rescans the vault, so it rides the same 600ms debounce as autosave instead of
        // running on every keystroke (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3) — see the
        // _saveTimer.Tick handler above, which refreshes it right after (or instead of) saving.
        _saveTimer.Stop();
        if (dirty) _saveTimer.Start();
    }

    /// <summary>Writes immediately, skipping the debounce — called when a change must not be left
    /// pending (closing this tab, closing the app) instead of relying on <see cref="_saveTimer"/> to
    /// eventually fire on a document nobody will keep open long enough for that. Also flushes the backlinks
    /// refresh the same debounce was holding back, so switching away from a just-edited tab doesn't leave
    /// its panel showing stale links until the next keystroke.</summary>
    public void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (!IsDirty || _pendingText is not { } text) return;
        Save(text);
        Backlinks.Refresh(Note);
    }

    /// <summary>Writes <paramref name="currentText"/> to disk. On an I/O failure (disco lleno, vault
    /// desconectado, permisos, antivirus bloqueando el fichero) the note is left exactly as it was — dirty,
    /// with <paramref name="currentText"/> still pending — instead of the exception escaping uncaught from
    /// the autosave timer's <c>Tick</c> and, absent any handler for it, potentially taking the whole process
    /// down with every other open tab (docs/analisis-proyecto/roadmap.md, Fase 1, top 10 #2). The save timer
    /// is restarted so the next automatic tick retries on its own, without requiring another keystroke.</summary>
    public void Save(string currentText)
    {
        try
        {
            _vaultService.WriteContent(Note, currentText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo guardar la nota '{Note.FullPath}'", ex);
            _notificationService?.Push(
                LocalizationService.Instance["Notification_NoteSaveFailed_Title"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_NoteSaveFailed_Message"], Title, DescribeFailureReason(ex)),
                NotificationSeverity.Error);

            _pendingText = currentText;
            _saveTimer.Stop();
            _saveTimer.Start();
            return;
        }

        _lastSavedContent = currentText;
        IsDirty = false;
        _lastSavedUtc = DateTime.UtcNow;
        SaveStatus = FormatSaveStatus(_lastSavedUtc);
        _onChanged();
    }

    /// <summary>Maps an I/O failure to a translated, user-facing reason — shared by <see cref="Save"/> and
    /// <see cref="OnTitleChanged"/>, the two write paths in this view model that can fail mid-edit.</summary>
    private static string DescribeFailureReason(Exception ex) => VaultFailureReasons.Describe(ex);

    /// <summary>Refresca las etiquetas localizadas de esta pestaña (y de sus paneles de estadísticas y
    /// buscar/reemplazar) cuando <see cref="MainWindowViewModel"/> coordina un cambio de idioma en
    /// caliente — recalcula <see cref="SaveStatus"/> a partir del estado ya conocido
    /// (<see cref="IsDirty"/>/<see cref="_lastSavedUtc"/>) en vez de perder el timestamp mostrado.</summary>
    public void RefreshLocalizedLabels()
    {
        SaveStatus = IsDirty
            ? LocalizationService.Instance["Editor_SaveStatus_Unsaved"]
            : FormatSaveStatus(_lastSavedUtc);
        Stats.RefreshLocalizedLabels();
        FindReplace.RefreshLocalizedLabels();
    }

    /// <summary>"Saved · HH:mm" (Fase 2 §2) instead of a bare "Saved" — a plain status word gives no
    /// sense of freshness once a note has been open (and possibly stale on disk vs. what's shown) for a
    /// while, unlike a wall-clock timestamp that stays meaningful without needing to keep ticking.</summary>
    private static string FormatSaveStatus(DateTime savedUtc) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Editor_SaveStatus_SavedAt"], savedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));

    /// <summary>Persiste las etiquetas editadas en el header como frontmatter YAML del archivo. Se aplica
    /// al <see cref="Document"/> y se escribe inmediatamente a disco, sin esperar al debounce del cuerpo.
    /// Si el usuario no cambió nada, se omite la escritura.</summary>
    public void ApplyTagsInput()
    {
        _saveTimer.Stop();
        if (IsDirty && _pendingText is { } pending) Save(pending);
        _pendingText = null;

        var parsed = TagsInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => t.ToLowerInvariant())
            .ToList();

        if (Note.Tags.SequenceEqual(parsed, StringComparer.Ordinal)) return;

        var newContent = NoteTagHelper.SetTags(Document.Text, parsed);
        Document.Text = newContent;
        _lastSavedContent = newContent;
        Note.Tags = parsed;
        Save(Document.Text);
    }

    partial void OnTitleChanged(string value)
    {
        if (_suppressTitleRename || string.IsNullOrWhiteSpace(value)) return;
        var oldPath = Note.FullPath;
        var oldTitle = Path.GetFileNameWithoutExtension(oldPath);

        try
        {
            Note = _vaultService.RenameNote(Note, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A rename that fails partway (vault desconectado, permisos) must not leave this tab's title
            // pointing at a file that never actually got renamed on disk — revert it instead of letting the
            // ViewModel and the filesystem silently diverge (docs/analisis-proyecto/roadmap.md, Fase 1).
            LogService.Error($"No se pudo renombrar la nota '{oldPath}'", ex);
            _notificationService?.Push(
                LocalizationService.Instance["Notification_NoteRenameFailed_Title"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_NoteRenameFailed_Message"], oldTitle, DescribeFailureReason(ex)),
                NotificationSeverity.Error);

            _suppressTitleRename = true;
            Title = oldTitle;
            _suppressTitleRename = false;
            return;
        }

        // A rename to an already-taken name gets a numeric suffix instead of silently doing nothing (see
        // VaultService.RenameNote) — reflect the real final name here instead of leaving Title pointing at
        // the name the user typed but the file on disk never actually got (docs/analisis-proyecto/
        // roadmap.md, Fase 5, "no hacer un no-op silencioso").
        var finalTitle = Path.GetFileNameWithoutExtension(Note.FullPath);
        if (!string.Equals(finalTitle, value, StringComparison.Ordinal))
        {
            _suppressTitleRename = true;
            Title = finalTitle;
            _suppressTitleRename = false;

            _notificationService?.Push(
                LocalizationService.Instance["Notification_NoteRenameCollision_Title"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Notification_NoteRenameCollision_Message"], finalTitle),
                NotificationSeverity.Info);
        }

        _kanbanService?.RelinkNotePath(oldPath, Note.FullPath);
        RefreshLinkedCards();
        RefreshWikiLinkScopeNotes();
        Backlinks.Refresh(Note);
        Stats.Refresh();
        _onChanged();
    }

    /// <summary>Reconciles this tab with a rename that already happened on disk elsewhere (the note grid's
    /// context menu, see NotesViewModel.CommitRenameNote) — suppresses <see cref="OnTitleChanged"/> so it
    /// doesn't attempt to rename the file a second time, which would fail since the old path is already gone.</summary>
    public void SyncRenamedNote(NoteFile note)
    {
        Note = note;
        _suppressTitleRename = true;
        Title = System.IO.Path.GetFileNameWithoutExtension(note.FullPath);
        _suppressTitleRename = false;
        RefreshLinkedCards();
        RefreshWikiLinkScopeNotes();
        Backlinks.Refresh(Note);
        Stats.Refresh();
    }

    [RelayCommand]
    private void Back() => _onBack();

    [RelayCommand]
    private void Delete()
    {
        EditorBacklinksService.WarnIfDeletingLeavesBrokenLinks(_vaultService, _notificationService, Note);
        _vaultService.DeleteNote(Note);
        _onChanged();
        _onBack();
    }
}
