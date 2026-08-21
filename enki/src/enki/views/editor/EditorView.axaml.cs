using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    private readonly LivePreviewState _livePreviewState = new();
    private readonly MarkdownStyle _style;
    private readonly MarkdownLivePreviewColorizer _colorizer;
    private bool _suppressTextChanged;

    /// <summary>The tab this view is currently bound to, tracked separately from DataContext so
    /// OnDataContextChanged can still reach the *outgoing* tab (to save its caret/scroll state into it)
    /// after DataContext has already moved on to the new one.</summary>
    private EditorViewModel? _boundVm;

    public EditorView()
    {
        InitializeComponent();

        var monoFont = Application.Current is { } app && app.TryFindResource("MonoFontFamily", out var monoRes) && monoRes is FontFamily mf
            ? mf
            : FontFamily.Default;
        _style = MarkdownStyle.FromApp(ResolveEditorFontFamily(), monoFont, EditorPreferences.FontSize);

        var textArea = Editor.TextArea;
        var textView = textArea.TextView;

        _colorizer = new MarkdownLivePreviewColorizer(_livePreviewState, _style);
        textView.LineTransformers.Add(_colorizer);
        textView.ElementGenerators.Add(new MarkdownElementGenerator(_livePreviewState, textArea));
        textView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(_livePreviewState, _style));
        textView.CurrentLineBackground = new SolidColorBrush(Colors.White, 0.035);
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

        EditorPreferences.Changed += OnEditorPreferencesChanged;
        DetachedFromVisualTree += (_, _) => EditorPreferences.Changed -= OnEditorPreferencesChanged;
    }

    private static FontFamily ResolveEditorFontFamily()
    {
        var name = EditorPreferences.FontFamily;
        return string.IsNullOrWhiteSpace(name) ? BundledEditorFontFamily : new FontFamily(name);
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
        _style.BaseFontSize = EditorPreferences.FontSize;
        ApplyColumnLayout();
        ApplyScrollbarVisibility();
        ApplyTabSize();
        ApplyLineNumbers();
        ApplyWordWrap();
        ApplySidePanelVisibility();
        ApplyToolbarGroupVisibility();
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

        textArea.TextView.Redraw();
    }

    private void RecomputeFences()
    {
        _livePreviewState.Fences = MarkdownLineParser.ComputeFences(Editor.Text ?? string.Empty);
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
            _boundVm.SlashCommands.CommandSelected -= OnSlashCommandSelected;
            _boundVm.FindReplace.OperationRequested -= OnFindReplaceOperationRequested;
        }
        CaptureEditorState(_boundVm);
        _boundVm = DataContext as EditorViewModel;
        if (_boundVm is not { } vm) return;

        _suppressTextChanged = true;
        Editor.Document = vm.Document;
        Editor.CaretOffset = Math.Clamp(vm.LastCaretOffset, 0, vm.Document.TextLength);
        Editor.FontSize = EditorPreferences.FontSize;
        _colorizer.WikiLinkNotesProvider = () => vm.WikiLinkScopeNotes;
        ApplyColumnLayout();
        ApplySidePanelVisibility();
        ApplyToolbarGroupVisibility();
        RecomputeFences();
        OnCaretOrSelectionChanged();
        _suppressTextChanged = false;

        vm.Outline.NavigationRequested += OnOutlineNavigationRequested;
        vm.SlashCommands.CommandSelected += OnSlashCommandSelected;
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
        _boundVm.FindReplace.IsOpen = true;
        _boundVm.FindReplace.SearchText = Editor.SelectedText;
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

    private void RefreshFindStatus(string searchText, bool matchCase)
    {
        if (_boundVm is null) return;
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

        // Invariante a propósito: "/fecha" inserta una marca de fecha técnica en el contenido de la nota,
        // no una etiqueta de UI — debe quedar estable con independencia del idioma activo.
        var insertText = command.Key == "fecha"
            ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : command.InsertText;
        var cursorOffset = command.Key == "fecha" ? insertText.Length : command.CursorOffset;

        Editor.Document.Replace(context.Value.StartOffset, context.Value.Length, insertText);
        Editor.CaretOffset = context.Value.StartOffset + cursorOffset;
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

    private void PositionSlashCommandsPopup()
    {
        if (SlashCommandsPopup is null) return;
        try
        {
            var rect = Editor.TextArea.Caret.CalculateCaretRectangle();
            var point = Editor.TextArea.TextView.TranslatePoint(rect.BottomLeft, EditorBorder);
            if (point is null) return;
            SlashCommandsPopup.HorizontalOffset = Math.Max(8, point.Value.X);
            SlashCommandsPopup.VerticalOffset = Math.Max(8, point.Value.Y + 4);
        }
        catch
        {
            // Si no se puede calcular la posición, el popup usa su offset por defecto.
        }
    }

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

    /// <summary>Enter commits the title edit immediately (moving focus to the editor triggers the title
    /// box's LostFocus, which is what actually pushes Title — and the file rename it causes — through;
    /// see the UpdateSourceTrigger note in EditorView.axaml) instead of making the user click away.
    /// Escape discards whatever was typed instead: it resets the box back to the VM's still-unchanged
    /// Title before moving focus away, so the LostFocus commit that follows is a same-name no-op
    /// rename (VaultService.RenameNote already treats an unchanged path as one) rather than saving
    /// a half-typed title the user was trying to back out of.</summary>
    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox titleBox) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Editor.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                if (DataContext is EditorViewModel vm) titleBox.Text = vm.Title;
                Editor.Focus();
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
        RecomputeFences();
        if (_suppressTextChanged) return;
        if (DataContext is EditorViewModel vm) vm.OnTextChanged(Editor.Text ?? string.Empty);
        UpdateSlashCommands();
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

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.B: WrapSelection("**"); e.Handled = true; return;
                case Key.I: WrapSelection("_"); e.Handled = true; return;
                case Key.K: InsertAtCaret(LocalizationService.Instance["Editor_LinkInsert_Placeholder"]); e.Handled = true; return;
                case Key.F:
                    e.Handled = true;
                    OpenFindReplace(replaceMode: false);
                    return;
                case Key.H:
                    e.Handled = true;
                    OpenFindReplace(replaceMode: true);
                    return;
            }
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;

        var doc = Editor.Document;
        var caretOffset = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caretOffset);
        var text = doc.GetText(line);
        var fence = _livePreviewState.GetFence(line.LineNumber);
        var model = MarkdownLineParser.Parse(text, line.Offset, fence);

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
            if (span.Kind != MdSpanKind.Link && span.Kind != MdSpanKind.WikiLink) continue;
            if (offset < span.Start || offset > span.End) continue;

            if (span.Kind == MdSpanKind.Link)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
                catch
                {
                    // Ignore: malformed or unsupported URL.
                }
            }
            else if (DataContext is EditorViewModel vm)
            {
                vm.OpenWikiLink(target);
            }

            e.Handled = true;
            return;
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
    private void OnImageClick(object? sender, RoutedEventArgs e) => InsertAtCaret("!" + LocalizationService.Instance["Editor_LinkInsert_Placeholder"]);
    private void OnHorizontalRuleClick(object? sender, RoutedEventArgs e) => InsertAtCaret("\n---\n");
}
