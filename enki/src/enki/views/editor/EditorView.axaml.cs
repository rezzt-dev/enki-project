using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views.Editor.Markdown;

namespace enki.Views;

public partial class EditorView : UserControl
{
    /// <summary>The app's bundled body font — the editor's default when no custom editor font is
    /// chosen, kept independent of "DisplayFontFamily" since that resource can be swapped at
    /// runtime by the interface font picker and the editor font must not follow it.</summary>
    private static readonly FontFamily BundledEditorFontFamily = new("avares://enki/assets/fonts#Space Grotesk");
    private static readonly FontFamily BundledCodeFontFamily = new("avares://enki/assets/fonts#JetBrainsMono Nerd Font Mono");

    private readonly LivePreviewState _livePreviewState = new();
    private readonly MarkdownStyle _style;
    private readonly MarkdownLivePreviewColorizer _colorizer;

    /// <summary>Los botones de copiar de los bloques de código, en su lienzo superpuesto al editor. Se
    /// guarda aunque nadie le pida nada después: es quien escucha al TextView para recolocarlos.</summary>
    private readonly CodeBlockCopyOverlay _codeBlockCopyOverlay;
    private bool _suppressTextChanged;

    /// <summary>Debounces <see cref="RecomputeFences"/> against every keystroke — on a long note with
    /// several code blocks, rescanning the whole document for fence boundaries on each character typed
    /// showed up as input lag (análisis §4.2, Fase 8). Same delay and restart-on-Tick pattern as
    /// <see cref="EditorOutlineViewModel"/>'s own debounce timer.</summary>
    private readonly DispatcherTimer _fencesTimer;

    /// <summary>The tab this view is currently bound to, tracked separately from DataContext so
    /// OnDataContextChanged can still reach the *outgoing* tab (to save its caret/scroll state into it)
    /// after DataContext has already moved on to the new one.</summary>
    private EditorViewModel? _boundVm;

    public EditorView()
    {
        InitializeComponent();

        _style = MarkdownStyle.FromApp(ResolveEditorFontFamily(), ResolveCodeFontFamily(), EditorPreferences.FontSize);

        var textArea = Editor.TextArea;
        var textView = textArea.TextView;

        _colorizer = new MarkdownLivePreviewColorizer(_livePreviewState, _style);
        _fencesTimer = new DispatcherTimer { Interval = EditorDebounce.Delay };
        _fencesTimer.Tick += (_, _) =>
        {
            _fencesTimer.Stop();
            RecomputeFences();
            _livePreviewState.TableFolding?.Refresh();
            textView.Redraw();
        };
        // La fila separadora de una tabla dibujada se pliega para que no deje una banda vacía (ver
        // MarkdownTableFolding). Se construye antes que el generador de elementos porque este le pregunta
        // qué hay plegado en cada repintado.
        _livePreviewState.TableFolding = new MarkdownTableFolding(textView, _livePreviewState, _style);

        textView.LineTransformers.Add(_colorizer);
        textView.ElementGenerators.Add(new MarkdownElementGenerator(_livePreviewState, textArea, _style));
        textView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(_livePreviewState, _style));
        // El botón de copiar de cada bloque de código va en un lienzo superpuesto al editor, no dentro de
        // él — ver CodeBlockCopyOverlay.
        _codeBlockCopyOverlay = new CodeBlockCopyOverlay(
            CodeBlockOverlay, CodeBlockInputHost, textView, _livePreviewState);

        // Let the fenced code block's box bleed past the text column - see UpdateCodeBlockBleed().
        textView.SizeChanged += (_, _) => UpdateCodeBlockBleed(textArea, textView);
        // Una tabla puede dejar de caber (o volver a caber) al cambiar el ancho, y con ello cambia si su
        // fila separadora debe seguir plegada. Va aplazado a propósito: plegar toca el árbol de alturas y
        // esto se dispara en mitad de un pase de layout.
        textView.SizeChanged += (_, _) => Dispatcher.UIThread.Post(
            () => _livePreviewState.TableFolding?.Refresh(), DispatcherPriority.Background);
        textView.AttachedToVisualTree += (_, _) => UpdateCodeBlockBleed(textArea, textView);
        UpdateCodeBlockBleed(textArea, textView);

        textView.CurrentLineBackground = ResolveCurrentLineBrush();
        textView.CurrentLineBorder = null;

        // AvaloniaEdit's Fluent theme selection defaults to a rounded, opaque box that doesn't
        // reflow the covered text's foreground color, so selected text (light gray on the app's dark
        // background) stayed nearly unreadable against it. Match the inverted selection every other
        // text control in the app uses (white fill, black text, square corners, no border).
        if (Application.Current is { } currentApp)
        {
            if (currentApp.TryFindResource("SelectionBackgroundBrush", out var selectionBg) && selectionBg is IBrush selectionBgBrush)
                textArea.SelectionBrush = selectionBgBrush;
            if (currentApp.TryFindResource("SelectionForegroundBrush", out var selectionFg) && selectionFg is IBrush selectionFgBrush)
                textArea.SelectionForeground = selectionFgBrush;
        }
        textArea.SelectionCornerRadius = 0;
        textArea.SelectionBorder = null;

        Editor.FontFamily = _style.BodyFontFamily;
        ApplyScrollbarVisibility();
        ApplyTabSize();
        ApplyLineNumbers();
        ApplyWordWrap();

        // Ctrl+F / Ctrl+H abren el buscador/reemplazo propio del editor en lugar del integrado de
        // AvaloniaEdit, para unificar estilo y añadir contador de coincidencias.

        // AvaloniaEdit's TextView doesn't always re-run word-wrap purely off an ancestor resize (e.g.
        // dragging the window narrower) — it can keep wrapping at the old width until something else
        // (typing, switching notes) forces a fresh layout pass. Nudging measure/arrange on every size
        // change keeps the wrap live.
        Editor.SizeChanged += (_, _) =>
        {
            textView.InvalidateMeasure();
            textView.InvalidateArrange();
        };

        // The column's fixed Width (see ApplyColumnLayout) is computed from the card's available space,
        // so it has to be recomputed whenever that space changes — otherwise the column would stay
        // pinned at whatever width the window happened to be when the note was opened.
        EditorBorder.SizeChanged += (_, _) => ApplyColumnLayout();

        textArea.Caret.PositionChanged += (_, _) => OnCaretOrSelectionChanged();
        textArea.SelectionChanged += (_, _) => OnCaretOrSelectionChanged();
        textArea.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        textArea.AddHandler(InputElement.PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);

        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
        InitializeImageDropTarget();

        AttachedToVisualTree += (_, _) =>
        {
            EditorPreferences.Changed -= OnEditorPreferencesChanged;
            EditorPreferences.Changed += OnEditorPreferencesChanged;
            OnEditorPreferencesChanged();
        };
        DetachedFromVisualTree += (_, _) => EditorPreferences.Changed -= OnEditorPreferencesChanged;
    }

    /// <summary>Lets <see cref="MarkdownBackgroundRenderer"/> paint the fenced code block's box
    /// <see cref="MarkdownStyle.CodeBlockHorizontalPadding"/> past the text column on each side, which is
    /// what puts every glyph of a code block - including the continuation rows of a wrapped line, which
    /// always start flush left and always run to the full wrap width - safely inside the box.
    ///
    /// Two things are in the way. First, <c>TextView</c> overrides <c>ClipToBounds</c>'s default to true,
    /// so anything drawn outside its bounds is cut; swapping that for an explicit <see cref="Visual.Clip"/>
    /// widened by exactly that padding keeps the vertical crop (without it, a half-scrolled line would
    /// spill over the editor's chrome) while opening up the horizontal bleed. Second, the presenter
    /// TextArea hosts the TextView in stops at the text column's edge, so it is unclipped too - the walk
    /// is bounded at the TextArea itself so nothing above the editor is ever touched.
    ///
    /// The room the box bleeds into is real, reserved space on both sides, not other content:
    /// the editor's 20px inset on both sides.</summary>
    private static void UpdateCodeBlockBleed(AvaloniaEdit.Editing.TextArea textArea, AvaloniaEdit.Rendering.TextView textView)
    {
        var pad = MarkdownStyle.CodeBlockHorizontalPadding;
        var bounds = textView.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        textView.ClipToBounds = false;
        textView.Clip = new RectangleGeometry(new Rect(-pad, 0, bounds.Width + pad * 2, bounds.Height));

        // Bounded walk: PART_CP (TextArea's content presenter) is the only host between the two, but a
        // depth cap keeps a future template change from unclipping the whole editor by accident.
        var parent = textView.GetVisualParent();
        for (var depth = 0; parent is not null && depth < 4; depth++)
        {
            if (parent is Control control) control.ClipToBounds = false;
            if (ReferenceEquals(parent, textArea)) break;
            parent = parent.GetVisualParent();
        }
    }

    private static IBrush ResolveCurrentLineBrush()
    {
        if (Application.Current is { } app
            && app.TryFindResource("InkBrush", out var ink) && ink is IBrush inkBrush)
        {
            return inkBrush is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color, 0.035)
                : inkBrush;
        }
        return new SolidColorBrush(Colors.White, 0.035);
    }

    private static FontFamily ResolveEditorFontFamily()
    {
        var name = EditorPreferences.FontFamily;
        return string.IsNullOrWhiteSpace(name) ? BundledEditorFontFamily : new FontFamily(name);
    }

    private static FontFamily ResolveCodeFontFamily()
    {
        var name = EditorPreferences.CodeFontFamily;
        return string.IsNullOrWhiteSpace(name) ? BundledCodeFontFamily : new FontFamily(name);
    }

    /// <summary>Reapplies font, column and scrollbar preferences to whichever note is currently open —
    /// fired whenever the Settings page changes one of them, so an already-open note reflects it
    /// immediately instead of only on next open.</summary>
    private void OnEditorPreferencesChanged()
    {
        var fontFamily = ResolveEditorFontFamily();
        Editor.FontFamily = fontFamily;
        Editor.FontSize = EditorPreferences.FontSize;
        _style.BodyFontFamily = fontFamily;
        _style.MonoFontFamily = ResolveCodeFontFamily();
        _style.BaseFontSize = EditorPreferences.FontSize;
        ApplyColumnLayout();
        ApplyScrollbarVisibility();
        ApplyTabSize();
        ApplyLineNumbers();
        ApplyWordWrap();
        ApplySidePanelVisibility();
        ApplyToolbarGroupVisibility();
        // Una imagen que falló (una remota con la descarga desactivada, típicamente) merece otro intento
        // en el redibujado de abajo: el interruptor que la bloqueaba puede ser justo el que se acaba de
        // cambiar, y obligar a cerrar y reabrir la nota para verlo sería absurdo.
        MarkdownImageCache.ForgetFailed();
        // Otro tamaño de letra o de columna puede dejar a una tabla sin sitio (o devolvérselo), y con ello
        // cambia si su fila separadora sigue plegada.
        _livePreviewState.TableFolding?.Refresh();
        Editor.TextArea.TextView.InvalidateMeasure();
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>Pushes the shared preference into the bound tab's own IsSidePanelVisible — which drives
    /// SidePanelHost's animated Width in EditorView.axaml — instead of toggling a control's IsVisible
    /// directly, so a change made from the Settings page (or another tab) still slides this tab's panel
    /// in/out rather than snapping it.</summary>
    private void ApplySidePanelVisibility()
    {
        if (_boundVm is not { } vm) return;
        vm.IsSidePanelVisible = EditorPreferences.SidePanelVisible;
    }

    /// <summary>Same resync as <see cref="ApplySidePanelVisibility"/>, for the formatting toolbar's
    /// per-group collapse state (Fase 4, gap 17) — called from the same two spots (a shared preference
    /// changed, or a different tab just became bound) so the toolbar shown always reflects whichever tab
    /// last toggled a group, not whatever this tab's own EditorViewModel was constructed with.</summary>
    private void ApplyToolbarGroupVisibility() => _boundVm?.ApplyToolbarGroupState();

    private void ApplyScrollbarVisibility()
    {
        Editor.VerticalScrollBarVisibility = EditorPreferences.ShowScrollbar
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
    }

    private void ApplyTabSize()
    {
        Editor.Options.IndentationSize = EditorPreferences.TabSize;
        Editor.Options.ConvertTabsToSpaces = true;
    }

    private void ApplyLineNumbers()
    {
        Editor.ShowLineNumbers = EditorPreferences.LineNumbers;
    }

    private void ApplyWordWrap()
    {
        Editor.WordWrap = EditorPreferences.WordWrap;
        _livePreviewState.JustifyParagraphs = EditorPreferences.JustifyParagraphs;
    }

    private void OnCaretOrSelectionChanged()
    {
        var textArea = Editor.TextArea;
        _livePreviewState.CaretOffset = textArea.Caret.Offset;

        var selections = new List<(int Start, int End)>();
        foreach (var segment in textArea.Selection.Segments)
        {
            if (segment.Length > 0) selections.Add((segment.StartOffset, segment.EndOffset));
        }
        _livePreviewState.Selections = selections;

        if (_boundVm is not null) _boundVm.Outline.UpdateActiveItem(textArea.Caret.Offset);

        // Entrar en una tabla la devuelve a su forma de texto, con su fila separadora incluida; salir de
        // ella vuelve a plegarla.
        _livePreviewState.TableFolding?.Refresh();
        textArea.TextView.Redraw();
    }

    /// <summary>Recomputes the pieces of live-preview state that can only be derived from the whole
    /// document — where the fenced code blocks are, the HTML context of each line (which block container
    /// aligns it, which inline tags an earlier line left open) y qué líneas forman una tabla. Van
    /// debounced juntas contra el mismo temporizador: las tres recorren el mismo texto y ninguna se puede
    /// contestar desde la única línea que se está dibujando.</summary>
    private void RecomputeFences()
    {
        var text = Editor.Text ?? string.Empty;
        _livePreviewState.Fences = MarkdownLineParser.ComputeFences(text);
        _livePreviewState.HtmlLines = MarkdownHtmlParser.ComputeLineStates(text, _livePreviewState.Fences);
        _livePreviewState.Tables.SetRows(MarkdownTableParser.Scan(text, _livePreviewState.Fences));
    }

    /// <summary>Fires whenever the tab strip switches which note is active (NotesViewModel.ActiveEditor).
    /// The single TextEditor instance is shared by every open tab, so switching tabs means: stash the
    /// outgoing note's caret/mode into its own EditorViewModel, then point the editor at the incoming
    /// note's <see cref="EditorViewModel.Document"/> instead of overwriting Text with a snapshot — that
    /// keeps each note's undo/redo history intact and lets us restore exactly where the user left off.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.Outline.NavigationRequested -= OnOutlineNavigationRequested;
            _boundVm.CaretNavigationRequested -= OnOutlineNavigationRequested;
            _boundVm.SlashCommands.CommandSelected -= OnSlashCommandSelected;
            _boundVm.NoteLinkPicker.NoteSelected -= OnNoteLinkSelected;
            _boundVm.FindReplace.OperationRequested -= OnFindReplaceOperationRequested;
        }
        CaptureEditorState(_boundVm);
        _boundVm = DataContext as EditorViewModel;
        if (_boundVm is not { } vm) return;

        _suppressTextChanged = true;
        // Los pliegues viven en el árbol de alturas del TextView, que se reconstruye entero al cambiar de
        // documento: hay que deshacerlos sobre el documento saliente, no dejarlos apuntando a él.
        _livePreviewState.TableFolding?.Reset();
        Editor.Document = vm.Document;
        Editor.CaretOffset = Math.Clamp(vm.LastCaretOffset, 0, vm.Document.TextLength);
        Editor.FontSize = EditorPreferences.FontSize;
        _colorizer.WikiLinkNotesProvider = () => vm.WikiLinkScopeNotes;
        ApplyImageContext(vm);
        ApplyColumnLayout();
        ApplySidePanelVisibility();
        ApplyToolbarGroupVisibility();
        RecomputeFences();
        OnCaretOrSelectionChanged();
        _suppressTextChanged = false;

        vm.Outline.NavigationRequested += OnOutlineNavigationRequested;
        vm.CaretNavigationRequested += OnOutlineNavigationRequested;
        vm.SlashCommands.CommandSelected += OnSlashCommandSelected;
        vm.NoteLinkPicker.NoteSelected += OnNoteLinkSelected;
        vm.FindReplace.OperationRequested += OnFindReplaceOperationRequested;

        // Deferred: the caret's on-screen position depends on visual lines that don't exist yet for a
        // document that was just swapped in, so scrolling it into view has to wait for the layout pass
        // that ApplyColumnLayout/the document swap above just triggered.
        Dispatcher.UIThread.Post(() => Editor.TextArea.Caret.BringCaretToView(), DispatcherPriority.Background);
    }

    private void OnOutlineNavigationRequested(int offset)
    {
        Editor.CaretOffset = Math.Clamp(offset, 0, Editor.Document.TextLength);
        Editor.Focus();
        Dispatcher.UIThread.Post(() => Editor.TextArea.Caret.BringCaretToView(), DispatcherPriority.Background);
    }

    private void OpenFindReplace(bool replaceMode)
    {
        if (_boundVm is null) return;
        _boundVm.FindReplace.IsReplaceMode = replaceMode;
        // SearchText before IsOpen: OnIsOpenChanged does an immediate (non-debounced) status refresh, so
        // opening with pre-selected text shows its match count/highlight right away instead of waiting on
        // the SearchText debounce (Fase 8) to catch up.
        _boundVm.FindReplace.SearchText = Editor.SelectedText;
        _boundVm.FindReplace.IsOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            FindReplaceSearchTextBox?.Focus();
            FindReplaceSearchTextBox?.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void OnFindReplaceOperationRequested(FindReplaceOperation operation, string searchText, string replaceText, bool matchCase)
    {
        if (_boundVm is null) return;

        switch (operation)
        {
            case FindReplaceOperation.FindNext:
                FindNext(searchText, matchCase);
                break;
            case FindReplaceOperation.FindPrevious:
                FindPrevious(searchText, matchCase);
                break;
            case FindReplaceOperation.Replace:
                Replace(searchText, replaceText, matchCase);
                break;
            case FindReplaceOperation.ReplaceAll:
                ReplaceAll(searchText, replaceText, matchCase);
                break;
            case FindReplaceOperation.RefreshStatus:
                RefreshFindStatus(searchText, matchCase);
                break;
        }
    }

    private void FindNext(string searchText, bool matchCase)
    {
        if (string.IsNullOrEmpty(searchText)) return;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = Editor.Text ?? string.Empty;
        var start = Editor.SelectionLength > 0 ? Editor.SelectionStart + 1 : Editor.CaretOffset;
        if (start >= text.Length) start = 0;

        var index = text.IndexOf(searchText, start, comparison);
        if (index == -1)
            index = text.IndexOf(searchText, 0, comparison);

        if (index != -1) SelectMatch(index, searchText.Length);
        RefreshFindStatus(searchText, matchCase);
    }

    private void FindPrevious(string searchText, bool matchCase)
    {
        if (string.IsNullOrEmpty(searchText)) return;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = Editor.Text ?? string.Empty;
        var start = Editor.SelectionLength > 0
            ? Editor.SelectionStart - 1
            : Editor.CaretOffset - 1;
        if (start < 0) start = text.Length - 1;

        var index = text.LastIndexOf(searchText, start, comparison);
        if (index == -1 && start < text.Length - 1)
            index = text.LastIndexOf(searchText, text.Length - 1, comparison);

        if (index != -1) SelectMatch(index, searchText.Length);
        RefreshFindStatus(searchText, matchCase);
    }

    private void Replace(string searchText, string replaceText, bool matchCase)
    {
        if (string.IsNullOrEmpty(searchText) || _boundVm is null) return;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var selected = Editor.Document.GetText(Editor.SelectionStart, Editor.SelectionLength);
        if (string.Equals(selected, searchText, comparison))
        {
            Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, replaceText);
            _boundVm.OnTextChanged(Editor.Text ?? string.Empty);
        }
        FindNext(searchText, matchCase);
    }

    private void ReplaceAll(string searchText, string replaceText, bool matchCase)
    {
        if (string.IsNullOrEmpty(searchText) || _boundVm is null) return;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = Editor.Text ?? string.Empty;
        var count = 0;
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var index = text.IndexOf(searchText, i, comparison);
            if (index == -1)
            {
                sb.Append(text[i..]);
                break;
            }
            sb.Append(text[i..index]);
            sb.Append(replaceText);
            i = index + searchText.Length;
            count++;
        }

        if (count > 0)
        {
            Editor.Document.Text = sb.ToString();
            _boundVm.OnTextChanged(Editor.Text ?? string.Empty);
        }

        _boundVm.FindReplace.ReportStatus(0, count);
    }

    private void SelectMatch(int index, int length)
    {
        Editor.SelectionStart = index;
        Editor.SelectionLength = length;
        Editor.Focus();
        Dispatcher.UIThread.Post(() => Editor.TextArea.Caret.BringCaretToView(), DispatcherPriority.Background);
    }

    /// <summary>Keeps <see cref="LivePreviewState.SearchHighlightText"/> in sync with whether the
    /// find/replace panel is actually open — otherwise closing it would leave the last search term's
    /// matches shaded even though the panel (and its "current match" selection) is gone.</summary>
    private void UpdateSearchHighlight(string searchText, bool matchCase)
    {
        var isOpen = _boundVm?.FindReplace.IsOpen == true;
        _livePreviewState.SearchHighlightText = isOpen && !string.IsNullOrEmpty(searchText) ? searchText : null;
        _livePreviewState.SearchHighlightMatchCase = matchCase;
        Editor.TextArea.TextView.Redraw();
    }

    private void RefreshFindStatus(string searchText, bool matchCase)
    {
        if (_boundVm is null) return;
        UpdateSearchHighlight(searchText, matchCase);
        if (string.IsNullOrEmpty(searchText))
        {
            _boundVm.FindReplace.ReportStatus(0, 0);
            return;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = Editor.Text ?? string.Empty;
        var total = 0;
        var current = -1;
        var i = 0;
        while (i < text.Length)
        {
            var index = text.IndexOf(searchText, i, comparison);
            if (index == -1) break;
            if (current == -1 && index >= Editor.SelectionStart) current = total;
            total++;
            i = index + searchText.Length;
        }

        if (current == -1 && total > 0) current = 0;
        _boundVm.FindReplace.ReportStatus(current, total);
    }

    private void OnSlashCommandSelected(SlashCommand command)
    {
        if (_boundVm is null) return;
        var context = GetSlashCommandContext();
        if (context is null) return;

        // "/imagen" no inserta texto: abre el diálogo de archivos y deja que el flujo de adjuntos escriba
        // el ![](…) ya resuelto, igual que el botón de la barra de formato.
        if (command.Key == "imagen")
        {
            Editor.Document.Remove(context.Value.StartOffset, context.Value.Length);
            Editor.CaretOffset = context.Value.StartOffset;
            _ = PickAndInsertImagesAsync(context.Value.StartOffset);
            return;
        }

        // Invariante a propósito: "/fecha" inserta una marca de fecha técnica en el contenido de la nota,
        // no una etiqueta de UI — debe quedar estable con independencia del idioma activo.
        var insertText = command.Key == "fecha"
            ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : command.InsertText;
        var cursorOffset = command.Key == "fecha" ? insertText.Length : command.CursorOffset;

        Editor.Document.Replace(context.Value.StartOffset, context.Value.Length, insertText);
        Editor.CaretOffset = context.Value.StartOffset + cursorOffset;
        // "/nota" inserta un "[[" abierto (ver su InsertTextLiteral en SlashCommandsViewModel) — esta
        // llamada es lo que realmente abre el picker de notas justo tras insertarlo, sin depender de si
        // TextChanged ya se disparó (y con qué CaretOffset) antes de que la línea de arriba lo reposicionara.
        // Para cualquier otro comando es un no-op inofensivo: su texto insertado no deja un "[[" abierto.
        UpdateNoteLinkPicker();
        Editor.Focus();
    }

    private void UpdateSlashCommands()
    {
        if (_boundVm is null) return;
        var context = GetSlashCommandContext();
        if (context is null)
        {
            _boundVm.SlashCommands.IsOpen = false;
            return;
        }

        _boundVm.SlashCommands.Filter = context.Value.Filter;
        if (!_boundVm.SlashCommands.IsOpen) _boundVm.SlashCommands.IsOpen = true;
        PositionSlashCommandsPopup();
    }

    private void PositionSlashCommandsPopup() => PositionPopup(SlashCommandsPopup);

    private (int StartOffset, int Length, string Filter)? GetSlashCommandContext()
    {
        var caretOffset = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(caretOffset);
        if (caretOffset > line.EndOffset) return null;

        var lineText = Editor.Document.GetText(line);
        var prefixLength = caretOffset - line.Offset;
        if (prefixLength < 1 || lineText[0] != '/') return null;

        for (var i = 1; i < prefixLength; i++)
        {
            var c = lineText[i];
            if (!char.IsLetter(c) && c != '-') return null;
        }

        var filter = lineText.Substring(1, prefixLength - 1);
        return (line.Offset, prefixLength, filter);
    }

    /// <summary>Opens/filters/closes <see cref="EditorViewModel.NoteLinkPicker"/> based on whether the caret
    /// currently sits right after an unclosed <c>[[</c> on the current line (see
    /// <see cref="GetNoteLinkPickerContext"/>) — the single mechanism behind all three ways to start a
    /// wikilink: typing <c>[[</c> directly (called from <see cref="OnEditorTextChanged"/>), the toolbar's
    /// "enlazar nota" button, and the <c>/nota</c> slash command (both call this explicitly right after
    /// inserting the <c>[[</c>, rather than relying on the text-changed event that insertion also fires,
    /// since whether that fires before or after the caret is repositioned isn't something to depend on).</summary>
    private void UpdateNoteLinkPicker()
    {
        if (_boundVm is null) return;
        var context = GetNoteLinkPickerContext();
        if (context is null)
        {
            _boundVm.NoteLinkPicker.IsOpen = false;
            return;
        }

        if (!_boundVm.NoteLinkPicker.IsOpen) _boundVm.NoteLinkPicker.Open(_boundVm.CollectLinkableNotes());
        _boundVm.NoteLinkPicker.Filter = context.Value.Filter;
        PositionNoteLinkPickerPopup();
    }

    /// <summary>Unlike <see cref="PositionSlashCommandsPopup"/> (anchored right under the caret), the
    /// note-link picker centers on the app window — closer to a quick-switcher dialog than an inline
    /// autocomplete, deliberate given it can hold many results and the caret is often too close to an edge
    /// for a caret-anchored popup to fit comfortably. Retargets <see cref="NoteLinkPickerPopup"/> at the
    /// window's <see cref="TopLevel"/> (not <c>EditorBorder</c>, which only covers the editor pane — the
    /// sidebar/title bar would throw the centering off) every time, since a plain <c>Popup</c> has no
    /// "center on target" placement mode to do this for us.</summary>
    private void PositionNoteLinkPickerPopup()
    {
        if (NoteLinkPickerPopup is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        NoteLinkPickerPopup.PlacementTarget = topLevel;
        NoteLinkPickerPopup.HorizontalOffset = Math.Max(0, (topLevel.Bounds.Width - NoteLinkPickerPopup.Width) / 2);
        // MaxHeight, not the popup's actual (content-dependent) height: the real height isn't known until
        // after it's shown/measured, and centering against the worst case reads as "centered enough" even
        // with few results, without needing a second reposition pass once the content settles.
        NoteLinkPickerPopup.VerticalOffset = Math.Max(0, (topLevel.Bounds.Height - NoteLinkPickerPopup.MaxHeight) / 2);
    }

    private void PositionPopup(Popup? popup)
    {
        if (popup is null) return;
        try
        {
            var rect = Editor.TextArea.Caret.CalculateCaretRectangle();
            var point = Editor.TextArea.TextView.TranslatePoint(rect.BottomLeft, EditorBorder);
            if (point is null) return;
            popup.HorizontalOffset = Math.Max(8, point.Value.X);
            popup.VerticalOffset = Math.Max(8, point.Value.Y + 4);
        }
        catch
        {
            // Si no se puede calcular la posición, el popup usa su offset por defecto.
        }
    }

    /// <summary>Finds the last <c>[[</c> before the caret on the current line and reports the span from
    /// right after it to the caret — what the user has typed so far as the wikilink's target — as long as
    /// nothing has closed or restarted it since (a <c>]</c> already closing the link, or another <c>[</c>
    /// opening a new one). No match at all (no <c>[[</c> on the line, or it's already closed) means the
    /// picker should be closed.</summary>
    private (int StartOffset, int Length, string Filter)? GetNoteLinkPickerContext()
    {
        var caretOffset = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(caretOffset);
        if (caretOffset > line.EndOffset) return null;

        var lineText = Editor.Document.GetText(line);
        var prefixLength = caretOffset - line.Offset;
        var textBeforeCaret = lineText[..prefixLength];

        var openIndex = textBeforeCaret.LastIndexOf("[[", StringComparison.Ordinal);
        if (openIndex == -1) return null;

        var between = textBeforeCaret[(openIndex + 2)..];
        if (between.Contains(']') || between.Contains('[')) return null;

        return (line.Offset + openIndex + 2, between.Length, between);
    }

    private void CaptureEditorState(EditorViewModel? vm)
    {
        if (vm is null) return;
        vm.LastCaretOffset = Editor.CaretOffset;
    }

    /// <summary>Constrains the readable column (<see cref="EditorColumnHost"/>, wrapping the text editor)
    /// to <see cref="EditorPreferences.ColumnWidth"/>, centered or left-aligned within the card per
    /// <see cref="EditorPreferences.ColumnPosition"/>. Sets an explicit <c>Width</c> rather than relying
    /// on <c>MaxWidth</c> alone: a Border with only MaxWidth sizes itself to its child's *desired* width,
    /// so a note with little text would shrink the column down to fit the text instead of holding the
    /// configured page width — the column width must stay fixed regardless of how much content is in it,
    /// only yielding when the window itself is too narrow to fit it.</summary>
    private void ApplyColumnLayout()
    {
        var chrome = EditorBorder.Padding.Left + EditorBorder.Padding.Right
                     + EditorColumnHost.Margin.Left + EditorColumnHost.Margin.Right;
        var available = EditorBorder.Bounds.Width - chrome;
        var target = available > 0 ? Math.Min(EditorPreferences.ColumnWidth, available) : EditorPreferences.ColumnWidth;

        EditorColumnHost.Width = target;
        EditorColumnHost.MaxWidth = EditorPreferences.ColumnWidth;
        EditorColumnHost.HorizontalAlignment = EditorPreferences.ColumnPosition == "Left"
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Center;
    }

    /// <summary>Enter commits the rename there and then, without moving the caret anywhere: it pushes the
    /// typed text straight into <see cref="EditorViewModel.Title"/> (whose setter renames the file on disk
    /// and reloads the note tree/grid) and syncs the box back to the final name — which may carry a
    /// numeric suffix if the target was taken. Focus stays on the title box, so the rename feels like the
    /// inline rename in the tree (press Enter, done) instead of yanking the user into the document body.
    /// The <c>UpdateSourceTrigger=LostFocus</c> binding still fires later when focus does leave, but by
    /// then the name matches and <see cref="VaultService.RenameNote"/> treats it as a no-op.
    /// Escape backs out: it restores the box to the VM's still-unchanged Title.</summary>
    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox titleBox || DataContext is not EditorViewModel vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                vm.Title = titleBox.Text ?? string.Empty;
                titleBox.Text = vm.Title;
                titleBox.SelectAll();
                break;
            case Key.Escape:
                e.Handled = true;
                titleBox.Text = vm.Title;
                titleBox.SelectAll();
                break;
        }
    }

    /// <summary>Renaming a note is a click-to-overwrite action, not a click-to-position-cursor one — the
    /// whole title is almost always what you mean to replace, so selecting it all on focus (matching the
    /// rename affordance in a file explorer) saves the extra "select all" step every time.</summary>
    private void OnTitleGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox titleBox) titleBox.SelectAll();
    }

    private void OnTagsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is EditorViewModel vm) vm.ApplyTagsInput();
    }

    private void OnTagsLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EditorViewModel vm) vm.ApplyTagsInput();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _fencesTimer.Stop();
        _fencesTimer.Start();
        if (_suppressTextChanged) return;
        if (DataContext is EditorViewModel vm) vm.OnTextChanged(Editor.Text ?? string.Empty);
        UpdateSlashCommands();
        UpdateNoteLinkPicker();
    }

    /// <summary>Continues lists/quotes on Enter and exits an empty list item instead of adding another marker,
    /// matching Obsidian's list-editing behavior; also backs the Ctrl+B/Ctrl+I/Ctrl+K formatting shortcuts —
    /// same <see cref="WrapSelection"/>/
    /// <see cref="InsertAtCaret"/> helpers the toolbar buttons already use.</summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_boundVm?.SlashCommands.IsOpen == true)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    _boundVm.SlashCommands.MoveDownCommand.Execute(null);
                    return;
                case Key.Up:
                    e.Handled = true;
                    _boundVm.SlashCommands.MoveUpCommand.Execute(null);
                    return;
                case Key.Enter:
                    e.Handled = true;
                    _boundVm.SlashCommands.ConfirmSelectedCommand.Execute(null);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    _boundVm.SlashCommands.IsOpen = false;
                    return;
            }
        }

        if (_boundVm?.NoteLinkPicker.IsOpen == true)
        {
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    _boundVm.NoteLinkPicker.MoveDownCommand.Execute(null);
                    return;
                case Key.Up:
                    e.Handled = true;
                    _boundVm.NoteLinkPicker.MoveUpCommand.Execute(null);
                    return;
                case Key.Enter:
                    e.Handled = true;
                    _boundVm.NoteLinkPicker.ConfirmSelectedCommand.Execute(null);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    _boundVm.NoteLinkPicker.IsOpen = false;
                    return;
            }
        }

        // Escape exits zen/focus mode (propuesta 7, docs/propuesta-nuevas-funcionalidades.md) — only
        // while it's actually active, so a stray Escape elsewhere in the editor keeps its usual no-op
        // behavior instead of this swallowing every Escape press unconditionally.
        if (_boundVm?.IsZenMode == true && e.Key == Key.Escape)
        {
            e.Handled = true;
            _boundVm.ToggleZenModeCommand.Execute(null);
            return;
        }

        // Pegar se intercepta aquí porque el portapapeles puede traer una imagen (una captura, un archivo
        // copiado en el explorador) y eso hay que averiguarlo de forma asíncrona antes de decidir. Lo que
        // no sea imagen vuelve al pegado normal de AvaloniaEdit dentro de PasteAsync.
        if (IsPasteGesture(e))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        // Atajos de formato remapeables (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) — ya no
        // se comparan literales Key.B/Key.I/... contra KeyModifiers.Control, sino el gesto efectivo que
        // Ajustes › Atajos haya asignado a cada uno vía ShortcutRegistry.
        if (ShortcutRegistry.Matches(ShortcutIds.EditorBold, e)) { WrapSelection("**"); e.Handled = true; return; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorItalic, e)) { WrapSelection("_"); e.Handled = true; return; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorInsertLink, e))
        {
            InsertAtCaret(LocalizationService.Instance["Editor_LinkInsert_Placeholder"]);
            e.Handled = true;
            return;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorFind, e))
        {
            e.Handled = true;
            OpenFindReplace(replaceMode: false);
            return;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorFindReplace, e))
        {
            e.Handled = true;
            OpenFindReplace(replaceMode: true);
            return;
        }

        // Tabulador dentro de una tabla: salta de celda en celda, como en cualquier hoja de cálculo. Fuera
        // de una tabla el tabulador sigue indentando, que es lo que hace AvaloniaEdit por su cuenta.
        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift &&
            MoveToAdjacentTableCell(forward: e.KeyModifiers != KeyModifiers.Shift))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;

        var doc = Editor.Document;
        var caretOffset = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caretOffset);
        var text = doc.GetText(line);
        var fence = _livePreviewState.GetFence(line.LineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);

        // Intro dentro de una tabla añade otra fila, igual que dentro de una lista añade otra viñeta.
        if (_livePreviewState.Tables.GetRow(line.LineNumber) is { } tableRow && HandleTableEnter(line, text, tableRow))
        {
            e.Handled = true;
            return;
        }

        var continuation = model.Kind switch
        {
            MdBlockKind.BulletList => ListContinuation(text, line, model),
            MdBlockKind.TaskList => TaskContinuation(text, line, model),
            MdBlockKind.OrderedList => OrderedContinuation(text, line, model),
            MdBlockKind.Blockquote => BlockquoteContinuation(text, line, model),
            _ => null
        };

        if (continuation is null) return;

        var lineIsEmpty = model.ContentStart >= line.EndOffset;
        if (lineIsEmpty && caretOffset >= model.MarkerStart)
        {
            doc.Remove(model.MarkerStart, line.EndOffset - model.MarkerStart);
            e.Handled = true;
            return;
        }

        doc.Insert(caretOffset, "\n" + continuation);
        Editor.CaretOffset = caretOffset + 1 + continuation.Length;
        e.Handled = true;
    }

    /// <summary>Intro sobre una fila de tabla: añade otra fila vacía debajo con las mismas columnas y deja
    /// el cursor en su primera celda. Sobre el encabezado o la fila separadora la nueva fila va debajo de
    /// la separadora — nunca entre las dos, que rompería la tabla. Y una fila de datos que ya está vacía
    /// no genera otra igual: se borra, que es la forma de salir de la tabla, exactamente como una viñeta
    /// vacía cierra una lista.</summary>
    private bool HandleTableEnter(DocumentLine line, string text, MdTableRow row)
    {
        var doc = Editor.Document;

        if (row.Kind == MdTableRowKind.Body && MarkdownTableParser.SplitCells(text, line.Offset).TrueForAll(c => c.Length == 0))
        {
            doc.Remove(line.Offset, line.Length);
            return true;
        }

        var afterLine = row.Kind == MdTableRowKind.Body
            ? line
            : doc.GetLineByNumber(Math.Min(row.Table.DelimiterLine, doc.LineCount));

        var template = EmptyTableRow(row.Table.ColumnCount);
        doc.Insert(afterLine.EndOffset, "\n" + template);
        // +2: justo detrás del "| " de la primera celda, que es donde se empieza a escribir.
        Editor.CaretOffset = afterLine.EndOffset + 1 + 2;
        return true;
    }

    /// <summary>Fila vacía de <paramref name="columns"/> columnas: <c>|  |  |</c>. Los dos espacios por
    /// celda no son decorativos — dejan sitio para que el cursor caiga entre ellos y lo que se escriba
    /// quede separado de las barras, que es como se escribe una tabla a mano.</summary>
    private static string EmptyTableRow(int columns)
    {
        var builder = new System.Text.StringBuilder("|");
        for (var i = 0; i < columns; i++) builder.Append("  |");
        return builder.ToString();
    }

    /// <summary>Mueve el cursor a la celda siguiente (o anterior) de la tabla en la que esté, saltándose la
    /// fila separadora, y devuelve si ha hecho algo — fuera de una tabla, o al final de la última fila,
    /// devuelve false para que el tabulador siga haciendo lo suyo. Al final de la última fila sí crea una
    /// fila nueva: es la forma natural de seguir rellenando una tabla sin soltar el teclado.</summary>
    private bool MoveToAdjacentTableCell(bool forward)
    {
        var doc = Editor.Document;
        var caretOffset = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caretOffset);
        if (_livePreviewState.Tables.GetRow(line.LineNumber) is not { } row) return false;

        var cells = MarkdownTableParser.SplitCells(doc.GetText(line), line.Offset);
        if (row.Kind != MdTableRowKind.Delimiter)
        {
            var current = IndexOfCellAt(cells, caretOffset);
            var next = forward
                ? (current < 0 ? cells.Count : current + 1)
                : (current < 0 ? cells.Count - 1 : current - 1);
            if (next >= 0 && next < cells.Count)
            {
                PlaceCaretInCell(line, cells[next]);
                return true;
            }
        }

        // No queda celda en esta fila: se salta a la siguiente (o a la anterior), sin pisar la separadora.
        var target = row.LineNumber + (forward ? 1 : -1);
        if (target == row.Table.DelimiterLine) target += forward ? 1 : -1;

        if (target < row.Table.StartLine)
        {
            return false;
        }

        if (target > row.Table.EndLine)
        {
            if (!forward) return false;
            var last = doc.GetLineByNumber(row.Table.EndLine);
            var template = EmptyTableRow(row.Table.ColumnCount);
            doc.Insert(last.EndOffset, "\n" + template);
            Editor.CaretOffset = last.EndOffset + 1 + 2;
            return true;
        }

        var targetLine = doc.GetLineByNumber(target);
        var targetCells = MarkdownTableParser.SplitCells(doc.GetText(targetLine), targetLine.Offset);
        if (targetCells.Count == 0) return false;

        PlaceCaretInCell(targetLine, forward ? targetCells[0] : targetCells[^1]);
        return true;
    }

    /// <summary>Índice de la celda a la que pertenece el cursor: la primera cuyo contenido no haya quedado
    /// ya atrás. Cuenta como "dentro de la celda" todo lo que va desde la barra que la abre hasta el final
    /// de su texto, así que el cursor pegado a una barra pertenece a la celda que esa barra abre y no a la
    /// que cierra. -1 si está más allá de la última celda (tras la barra de cierre de la fila).</summary>
    private static int IndexOfCellAt(System.Collections.Generic.List<MdTableCell> cells, int caretOffset)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (caretOffset <= cells[i].End && (i == 0 || caretOffset > cells[i - 1].End)) return i;
        }
        return -1;
    }

    /// <summary>Deja el cursor dentro de la celda. En una celda vacía se coloca antes del espacio que
    /// precede a la barra de cierre, no pegado a ella: escribir ahí deja <c>| texto |</c> y no
    /// <c>|  texto|</c>.</summary>
    private void PlaceCaretInCell(DocumentLine line, MdTableCell cell)
    {
        var offset = cell.Start;
        if (cell.Length == 0 && offset - 1 > line.Offset && Editor.Document.GetCharAt(offset - 1) == ' ') offset--;
        Editor.CaretOffset = offset;
    }

    private static string MarkerPrefix(string text, DocumentLine line, MdLine model)
    {
        var start = model.MarkerStart - line.Offset;
        var end = model.ContentStart - line.Offset;
        if (end > text.Length) end = text.Length;
        return text[start..end];
    }

    private static string ListContinuation(string text, DocumentLine line, MdLine model)
    {
        var indent = text[..(model.MarkerStart - line.Offset)];
        return indent + MarkerPrefix(text, line, model);
    }

    private static string TaskContinuation(string text, DocumentLine line, MdLine model)
    {
        var indent = text[..(model.MarkerStart - line.Offset)];
        var prefix = MarkerPrefix(text, line, model);
        if (prefix.Length > 3)
            return indent + prefix[..3] + " " + prefix[4..];
        return indent + "- [ ] ";
    }

    private static string BlockquoteContinuation(string text, DocumentLine line, MdLine model)
    {
        var indent = text[..(model.MarkerStart - line.Offset)];
        return indent + MarkerPrefix(text, line, model);
    }

    private static string OrderedContinuation(string text, DocumentLine line, MdLine model)
    {
        var indent = text[..(model.MarkerStart - line.Offset)];
        var prefix = MarkerPrefix(text, line, model);
        var digits = 0;
        while (digits < prefix.Length && char.IsDigit(prefix[digits])) digits++;
        if (digits == 0 || digits >= prefix.Length || !int.TryParse(prefix[..digits], out var number))
            return indent + prefix;
        var punctuation = prefix[digits];
        var trailing = (digits + 1 < prefix.Length) ? prefix[(digits + 1)..] : " ";
        return $"{indent}{number + 1}{punctuation}{trailing}";
    }

    /// <summary>Ctrl+click opens the link under the cursor, like Obsidian.</summary>
    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var textView = Editor.TextArea.TextView;
        var point = e.GetPosition(textView);
        var position = textView.GetPosition(point);
        if (position is null) return;

        var offset = Editor.Document.GetOffset(position.Value.Location);
        var line = Editor.Document.GetLineByOffset(offset);
        var text = Editor.Document.GetText(line);
        var fence = _livePreviewState.GetFence(line.LineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);

        foreach (var span in model.Spans)
        {
            if (span.LinkTarget is not { } target) continue;
            if (span.Kind is not (MdSpanKind.Link or MdSpanKind.WikiLink or MdSpanKind.Image)) continue;
            if (offset < span.Start || offset > span.End) continue;

            if (span.Kind == MdSpanKind.Image)
            {
                // Una imagen que no se puede abrir (ruta rota, esquema desconocido) deja pasar el clic:
                // marcarlo como atendido dejaría el cursor sin colocarse y el gesto sin efecto visible.
                if (!TryOpenImage(span)) return;
            }
            else if (span.Kind == MdSpanKind.Link)
            {
                OpenExternalUrl(target);
            }
            else if (DataContext is EditorViewModel vm)
            {
                vm.OpenWikiLink(target);
            }

            e.Handled = true;
            return;
        }
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Warning($"No se pudo abrir '{url}' con la aplicación del sistema", ex);
        }
    }

    private void WrapSelection(string prefix, string? suffix = null)
    {
        suffix ??= prefix;
        var start = Editor.SelectionStart;
        var length = Editor.SelectionLength;
        var doc = Editor.Document;

        doc.Insert(start + length, suffix);
        doc.Insert(start, prefix);

        Editor.SelectionStart = start + prefix.Length;
        Editor.SelectionLength = length;
        Editor.Focus();
    }

    private void InsertLinePrefix(string token)
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        Editor.Document.Insert(line.Offset, token);
        Editor.Focus();
    }

    private void InsertAtCaret(string token)
    {
        Editor.Document.Insert(Editor.CaretOffset, token);
        Editor.Focus();
    }

    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelection("**");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelection("_");
    private void OnStrikethroughClick(object? sender, RoutedEventArgs e) => WrapSelection("~~");
    private void OnH1Click(object? sender, RoutedEventArgs e) => InsertLinePrefix("# ");
    private void OnH2Click(object? sender, RoutedEventArgs e) => InsertLinePrefix("## ");
    private void OnH3Click(object? sender, RoutedEventArgs e) => InsertLinePrefix("### ");
    private void OnQuoteClick(object? sender, RoutedEventArgs e) => InsertLinePrefix("> ");
    private void OnBulletClick(object? sender, RoutedEventArgs e) => InsertLinePrefix("- ");
    private void OnOrderedClick(object? sender, RoutedEventArgs e) => InsertLinePrefix("1. ");
    private void OnTaskListClick(object? sender, RoutedEventArgs e) => InsertLinePrefix("- [ ] ");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => WrapSelection("`");
    private void OnCodeBlockClick(object? sender, RoutedEventArgs e)
    {
        var start = Editor.CaretOffset;
        Editor.Document.Insert(start, "```\n\n```");
        Editor.CaretOffset = start + 4;
        Editor.Focus();
    }
    private void OnLinkClick(object? sender, RoutedEventArgs e) => InsertAtCaret(LocalizationService.Instance["Editor_LinkInsert_Placeholder"]);
    /// <summary>Botón "insertar imagen" de la barra de formato: abre el diálogo de archivos y adjunta lo
    /// que se elija. Antes insertaba la plantilla de texto <c>![texto](url)</c> para que el usuario la
    /// rellenara a mano, lo que obligaba a saberse de memoria la ruta del archivo.</summary>
    private void OnImageClick(object? sender, RoutedEventArgs e) => _ = PickAndInsertImagesAsync(Editor.CaretOffset);
    private void OnHorizontalRuleClick(object? sender, RoutedEventArgs e) => InsertAtCaret("\n---\n");

    /// <summary>Botón "insertar tabla" de la barra de formato: escribe la misma plantilla que <c>/tabla</c>
    /// (encabezados traducidos incluidos) en su propia línea y deja el cursor en la primera celda del
    /// encabezado, listo para renombrarla.</summary>
    private void OnTableClick(object? sender, RoutedEventArgs e)
    {
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset);
        // Una tabla es un bloque: si la línea donde está el cursor ya tiene texto, se empieza en la
        // siguiente en vez de partirla por la mitad.
        var start = line.Length == 0 ? line.Offset : line.EndOffset;
        var prefix = line.Length == 0 ? string.Empty : "\n";
        var template = LocalizationService.Instance["Editor_SlashCommand_Table_InsertText"];

        Editor.Document.Insert(start, prefix + template + "\n");
        Editor.CaretOffset = start + prefix.Length + 2;
        Editor.Focus();
    }

    /// <summary>Toolbar entry point for the note-link picker — inserts an open <c>[[</c> at the caret and
    /// opens the picker right there, same end state <c>/nota</c> and typing <c>[[</c> by hand converge on.</summary>
    private void OnNoteLinkClick(object? sender, RoutedEventArgs e)
    {
        var offset = Editor.CaretOffset;
        Editor.Document.Insert(offset, "[[");
        Editor.CaretOffset = offset + 2;
        UpdateNoteLinkPicker();
        Editor.Focus();
    }

    /// <summary>A note was confirmed in the picker (click, or Enter on the highlighted row) — closes the
    /// open <c>[[filter</c> into a complete <c>[[FileName]]</c> wikilink and lands the caret right after it,
    /// ready to keep typing. Uses the file name rather than the title as the link target since that's what
    /// <see cref="WikiLinkParser.ResolveTargetWithStatus"/> tries first.</summary>
    private void OnNoteLinkSelected(NotePickerItemViewModel item)
    {
        var context = GetNoteLinkPickerContext();
        if (context is null) return;

        var linkText = System.IO.Path.GetFileNameWithoutExtension(item.Note.FullPath) + "]]";
        Editor.Document.Replace(context.Value.StartOffset, context.Value.Length, linkText);
        Editor.CaretOffset = context.Value.StartOffset + linkText.Length;
        Editor.Focus();
    }
}
