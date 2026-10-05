using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Search;
using enki.Services;
using enki.ViewModels;
using enki.Views.Editor.Markdown;

namespace enki.Views;

/// <summary>Editor de skills — reutiliza los colorizadores Markdown de <c>Views.Editor.Markdown</c>
/// (mismos que <see cref="EditorView"/>) pero enlazados a <see cref="SkillEditorViewModel.Text"/>, un
/// string plano en vez de un <c>TextDocument</c> por pestaña (docs/ia-skills/especificacion-desarrollo.md
/// §13: "no reutilizar EditorViewModel/EditorView... sí reutilizar los colorizadores").</summary>
public partial class SkillEditorView : UserControl
{
    public static readonly StyledProperty<string?> PreviewTextProperty =
        AvaloniaProperty.Register<SkillEditorView, string?>(nameof(PreviewText));
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<SkillEditorView, bool>(nameof(IsReadOnly));

    public string? PreviewText { get => GetValue(PreviewTextProperty); set => SetValue(PreviewTextProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    private MarkdownLivePreviewColorizer? _colorizer;
    private MarkdownElementGenerator? _elementGenerator;
    private MarkdownBackgroundRenderer? _backgroundRenderer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (Editor is null) return;
        if (change.Property == PreviewTextProperty && IsReadOnly)
            SetPreviewDocument();
        if (change.Property == IsReadOnlyProperty)
        {
            Editor.IsReadOnly = IsReadOnly;
            Editor.ShowLineNumbers = !IsReadOnly || SourceToggle.IsChecked == true;
            SaveButton.IsVisible = !IsReadOnly;
            FormattingTools.IsVisible = !IsReadOnly;
            EditorStatus.IsVisible = !IsReadOnly;
            if (IsReadOnly) SetPreviewDocument();
        }
    }

    private void SourceToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_colorizer is null) return;
        var view = Editor.TextArea.TextView;
        view.LineTransformers.Remove(_colorizer);
        view.ElementGenerators.Remove(_elementGenerator!);
        view.BackgroundRenderers.Remove(_backgroundRenderer!);
        _livePreviewState.TableFolding?.Reset();
        if (SourceToggle.IsChecked != true)
        {
            view.LineTransformers.Add(_colorizer);
            view.ElementGenerators.Add(_elementGenerator!);
            view.BackgroundRenderers.Add(_backgroundRenderer!);
        }
        Editor.FontFamily = SourceToggle.IsChecked == true ? _style.MonoFontFamily : _style.BodyFontFamily;
        Editor.ShowLineNumbers = SourceToggle.IsChecked == true || !IsReadOnly;
        if (IsReadOnly) SetPreviewDocument();
        view.Redraw();
    }

    private void Format_Click(object? sender, RoutedEventArgs e)
    {
        if (IsReadOnly || sender is not Button { Tag: string action }) return;
        ApplyFormat(action);
    }

    private void ApplyFormat(string action)
    {
        var selection = Editor.SelectionLength > 0
            ? new MdSelection(Editor.SelectionStart, Editor.SelectionLength) : MdSelection.Caret(Editor.CaretOffset);
        var result = action switch
        {
            "bold" => MarkdownEditing.ToggleInline(Editor.Document, selection, "**"),
            "italic" => MarkdownEditing.ToggleInline(Editor.Document, selection, "_"),
            "heading" => MarkdownEditing.ToggleHeading(Editor.Document, selection, 2),
            "list" => MarkdownEditing.ToggleList(Editor.Document, selection, MdBlockKind.BulletList),
            "code" => MarkdownEditing.InsertCodeBlock(Editor.Document, selection),
            _ => selection
        };
        Editor.Select(result.Start, result.Length);
        Editor.CaretOffset = result.End;
        Editor.Focus();
    }

    private void SetPreviewDocument() => SetDocument(SourceToggle.IsChecked == true
        ? PreviewText ?? string.Empty : SkillService.GetMarkdownBody(PreviewText ?? string.Empty));

    private void SetDocument(string text)
    {
        _suppressTextChanged = true;
        try
        {
            _livePreviewState.TableFolding?.Reset();
            Editor.Text = text;
            Editor.Document.UndoStack.ClearAll();
            RecomputeFences();
            OnCaretOrSelectionChanged();
        }
        finally { _suppressTextChanged = false; }
    }

    private static readonly FontFamily BundledEditorFontFamily = new("avares://enki/assets/fonts#Space Grotesk");
    private static readonly FontFamily BundledCodeFontFamily = new("avares://enki/assets/fonts#JetBrainsMono Nerd Font Mono");

    private readonly LivePreviewState _livePreviewState = new();
    private readonly MarkdownStyle _style;
    private bool _suppressTextChanged;
    private SkillEditorViewModel? _subscribedViewModel;

    public SkillEditorView()
    {
        InitializeComponent();

        _style = MarkdownStyle.FromApp(ResolveEditorFontFamily(), ResolveCodeFontFamily(), EditorPreferences.FontSize);

        var textArea = Editor.TextArea;
        var textView = textArea.TextView;

        // Igual que en EditorView: quien pliega la fila separadora de una tabla dibujada, construido antes
        // del generador de elementos porque este le pregunta qué hay plegado.
        _livePreviewState.TableFolding = new MarkdownTableFolding(textView, _livePreviewState, _style);

        textView.LineTransformers.Add(_colorizer = new MarkdownLivePreviewColorizer(_livePreviewState, _style));
        textView.ElementGenerators.Add(_elementGenerator = new MarkdownElementGenerator(_livePreviewState, textArea, _style));
        textView.BackgroundRenderers.Add(_backgroundRenderer = new MarkdownBackgroundRenderer(_livePreviewState, _style));

        textView.CurrentLineBackground = ResolveCurrentLineBrush();
        textView.CurrentLineBorder = null;

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
        Editor.FontSize = EditorPreferences.FontSize;
        Editor.Options.InheritWordWrapIndentation = true;

        SearchPanel.Install(Editor);
        Editor.KeyDown += (_, e) =>
        {
            if (!IsReadOnly && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta)
                && e.Key is Key.B or Key.I)
            {
                ApplyFormat(e.Key == Key.B ? "bold" : "italic");
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                && !IsReadOnly && DataContext is SkillEditorViewModel vm)
            {
                vm.FlushPendingSave();
                e.Handled = true;
            }
        };

        textArea.Caret.PositionChanged += (_, _) => OnCaretOrSelectionChanged();
        textArea.SelectionChanged += (_, _) => OnCaretOrSelectionChanged();

        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            EditorPreferences.Changed -= OnEditorPreferencesChanged;
            EditorPreferences.Changed += OnEditorPreferencesChanged;
            OnEditorPreferencesChanged();
            SubscribeToEditor();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            EditorPreferences.Changed -= OnEditorPreferencesChanged;
            if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnEditorPropertyChanged;
        };
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

    private void OnEditorPreferencesChanged()
    {
        Editor.FontFamily = _style.BodyFontFamily = ResolveEditorFontFamily();
        Editor.FontSize = _style.BaseFontSize = EditorPreferences.FontSize;
        _style.MonoFontFamily = ResolveCodeFontFamily();
        RefreshTableFolding();
        Editor.TextArea.TextView.InvalidateMeasure();
        Editor.TextArea.TextView.Redraw();
    }

    private void OnCaretOrSelectionChanged()
    {
        var textArea = Editor.TextArea;
        _livePreviewState.CaretOffset = IsReadOnly ? -1 : textArea.Caret.Offset;

        var selections = new List<(int Start, int End)>();
        foreach (var segment in textArea.Selection.Segments)
        {
            if (segment.Length > 0) selections.Add((segment.StartOffset, segment.EndOffset));
        }
        _livePreviewState.Selections = selections;

        RefreshTableFolding();
        textArea.TextView.Redraw();
    }

    private void RefreshTableFolding()
    {
        if (SourceToggle.IsChecked == true) _livePreviewState.TableFolding?.Reset();
        else _livePreviewState.TableFolding?.Refresh();
    }

    private void RecomputeFences()
    {
        var text = Editor.Text ?? string.Empty;
        _livePreviewState.Fences = MarkdownLineParser.ComputeFences(text);
        _livePreviewState.HtmlLines = MarkdownHtmlParser.ComputeLineStates(text, _livePreviewState.Fences);
        _livePreviewState.Tables.SetRows(MarkdownTableParser.Scan(text, _livePreviewState.Fences));
    }

    /// <summary>Cada vez que <c>SkillsView</c> abre una skill distinta, este control se reutiliza con un
    /// nuevo <see cref="SkillEditorViewModel"/> como DataContext — a diferencia de EditorView no hace
    /// falta preservar el historial de deshacer entre skills, así que basta con volcar el texto.</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToEditor();
        if (DataContext is SkillEditorViewModel vm && !IsReadOnly) SetDocument(vm.Text);
    }

    private void SubscribeToEditor()
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged -= OnEditorPropertyChanged;
        _subscribedViewModel = DataContext as SkillEditorViewModel;
        if (_subscribedViewModel is not null) _subscribedViewModel.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkillEditorViewModel.Text) && sender is SkillEditorViewModel vm
            && !IsReadOnly && Editor.Text != vm.Text) SetDocument(vm.Text);
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        RecomputeFences();
        RefreshTableFolding();
        if (_suppressTextChanged) return;
        if (!IsReadOnly && DataContext is SkillEditorViewModel vm) vm.Text = Editor.Text ?? string.Empty;
    }
}
