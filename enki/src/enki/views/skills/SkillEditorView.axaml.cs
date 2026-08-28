using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
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
    private static readonly FontFamily BundledEditorFontFamily = new("avares://enki/assets/fonts#Space Grotesk");

    private readonly LivePreviewState _livePreviewState = new();
    private readonly MarkdownStyle _style;
    private bool _suppressTextChanged;

    public SkillEditorView()
    {
        InitializeComponent();

        var monoFont = Application.Current is { } app && app.TryFindResource("MonoFontFamily", out var monoRes) && monoRes is FontFamily mf
            ? mf
            : FontFamily.Default;
        _style = MarkdownStyle.FromApp(ResolveEditorFontFamily(), monoFont, EditorPreferences.FontSize);

        var textArea = Editor.TextArea;
        var textView = textArea.TextView;

        // Igual que en EditorView: quien pliega la fila separadora de una tabla dibujada, construido antes
        // del generador de elementos porque este le pregunta qué hay plegado.
        _livePreviewState.TableFolding = new MarkdownTableFolding(textView, _livePreviewState, _style);

        textView.LineTransformers.Add(new MarkdownLivePreviewColorizer(_livePreviewState, _style));
        textView.ElementGenerators.Add(new MarkdownElementGenerator(_livePreviewState, textArea, _style));
        textView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(_livePreviewState, _style));

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

        textArea.Caret.PositionChanged += (_, _) => OnCaretOrSelectionChanged();
        textArea.SelectionChanged += (_, _) => OnCaretOrSelectionChanged();

        Editor.TextChanged += OnEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
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

        _livePreviewState.TableFolding?.Refresh();
        textArea.TextView.Redraw();
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
        if (DataContext is not SkillEditorViewModel vm) return;

        _suppressTextChanged = true;
        // Una skill distinta es otro texto entero: los pliegues de la anterior ya no significan nada.
        _livePreviewState.TableFolding?.Reset();
        Editor.Text = vm.Text;
        RecomputeFences();
        OnCaretOrSelectionChanged();
        _suppressTextChanged = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        RecomputeFences();
        _livePreviewState.TableFolding?.Refresh();
        if (_suppressTextChanged) return;
        if (DataContext is SkillEditorViewModel vm) vm.Text = Editor.Text ?? string.Empty;
    }
}
