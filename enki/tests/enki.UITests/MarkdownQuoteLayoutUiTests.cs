using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.UITests;

/// <summary>Regresión de la geometría horizontal de citas dentro del flujo de AvaloniaEdit.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class MarkdownQuoteLayoutUiTests
{
    [Theory]
    [InlineData("> ", 1)]
    [InlineData(">> ", 2)]
    [InlineData("> > ", 2)]
    public void Quote_ReservesEveryLevel_AndWrapsAtItsContentColumn(string prefix, int depth)
    {
        HeadlessApp.EnsureInitialized();
        var quote = prefix + string.Concat(Enumerable.Repeat("texto citado bastante largo para envolver ", 20));
        var text = "Texto normal\n" + quote + "\nTexto normal después";
        var editor = new TextEditor { Text = text, WordWrap = true, Width = 260 };
        var state = new LivePreviewState { CaretOffset = text.Length };
        var style = MarkdownStyle.FromApp(FontFamily.Default, FontFamily.Default, 16);
        editor.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(state, editor.TextArea, style));
        editor.TextArea.TextView.LineTransformers.Add(new MarkdownLivePreviewColorizer(state, style));
        var window = new Window { Content = editor, Width = 300, Height = 700 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var view = editor.TextArea.TextView;
            var normalBefore = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            var visual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(2));
            var normalAfter = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(3));
            var expected = MarkdownQuoteLayout.ContentIndent(depth);
            var contentColumn = visual.GetVisualColumn(prefix.Length);
            var contentX = visual.TextLines[0].GetDistanceFromCharacterHit(new CharacterHit(contentColumn, 0));

            Assert.Equal(0, normalBefore.TextLines[0].Start, precision: 3);
            Assert.Equal(0, normalAfter.TextLines[0].Start, precision: 3);
            Assert.Equal(expected, visual.TextLines[0].Start, precision: 3);
            Assert.Equal(expected, contentX, precision: 3);
            Assert.True(visual.TextLines.Count > 1);
            Assert.All(visual.TextLines.Skip(1), row => Assert.Equal(contentX, row.Start, precision: 3));

            var wrapped = visual.TextLines[1];
            var point = new Point(contentX,
                visual.GetTextLineVisualYPosition(wrapped, AvaloniaEdit.Rendering.VisualYPosition.LineMiddle));
            Assert.NotNull(view.GetPosition(point));
            Assert.Equal(text, editor.Text);
            Assert.False(editor.Document.UndoStack.CanUndo);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void QuoteGeometry_PlacesFirstRailAtNormalOrigin_AndAccumulatesLevels()
    {
        Assert.Equal(0, MarkdownQuoteLayout.RailX(0));
        Assert.Equal(MarkdownQuoteLayout.LevelWidth, MarkdownQuoteLayout.RailX(1));
        Assert.Equal(MarkdownQuoteLayout.RailThickness + MarkdownQuoteLayout.GapToText,
            MarkdownQuoteLayout.ContentIndent(1));
        Assert.Equal(2 * MarkdownQuoteLayout.LevelWidth, MarkdownQuoteLayout.ContentIndent(2));
    }

    [Fact]
    public void Quote_KeepsItsInlineIndentWithoutWordWrap()
    {
        HeadlessApp.EnsureInitialized();
        const string text = "> cita que requiere desplazamiento horizontal";
        var editor = new TextEditor { Text = text, WordWrap = false, Width = 180 };
        var state = new LivePreviewState { CaretOffset = text.Length };
        var style = MarkdownStyle.FromApp(FontFamily.Default, FontFamily.Default, 22);
        editor.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(state, editor.TextArea, style));
        var window = new Window { Content = editor, Width = 220, Height = 160 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var visual = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            Assert.Single(visual.TextLines);
            Assert.Equal(MarkdownQuoteLayout.ContentIndent(1), visual.TextLines[0].Start, precision: 3);
        }
        finally { window.Close(); }
    }
}
