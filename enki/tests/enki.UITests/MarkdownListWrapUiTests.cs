using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.UITests;

/// <summary>Verifica la geometría real del wrap y sus coordenadas, sin modificar el Markdown.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class MarkdownListWrapUiTests
{
    [Theory]
    [InlineData("- ")]
    [InlineData("* ")]
    [InlineData("+ ")]
    [InlineData("1. ")]
    [InlineData("10. ")]
    [InlineData("123)   ")]
    [InlineData("  - ")]
    [InlineData("    - ")]
    [InlineData("\t- ")]
    [InlineData("  10.\t")]
    [InlineData("- [ ] ")]
    [InlineData("  - [x] ")]
    [InlineData("- ", "**Texto en negrita** y _cursiva_ con `código` y [enlace](https://example.com). ")]
    [InlineData("  10. ", "Palabramuylargaquenocabeenunasolafilavisualsinseparadores ")]
    public void WrappedRows_AlignWithContent_AndPreserveHitTestingAndDocument(string prefix,
        string body = "Texto largo con énfasis y palabras para envolver. ")
    {
        HeadlessApp.EnsureInitialized();
        var text = prefix + string.Concat(Enumerable.Repeat(body, 12)) + "\nPárrafo independiente";
        var editor = new TextEditor { Text = text, WordWrap = true };
        var state = new LivePreviewState { CaretOffset = text.Length };
        var style = MarkdownStyle.FromApp(FontFamily.Default, FontFamily.Default, 16);
        editor.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(state, editor.TextArea, style));
        editor.TextArea.TextView.LineTransformers.Add(new MarkdownLivePreviewColorizer(state, style));
        var window = new Window { Content = editor, Width = 400, Height = 900 };
        window.Show();
        try
        {
            double contentX = 0;
            foreach (var font in new[] { FontFamily.Default, new FontFamily("avares://enki/assets/fonts#Space Grotesk") })
            foreach (var size in new[] { 14.0, 22.0 })
            foreach (var width in new[] { 600.0, 400.0, 260.0, 180.0 })
            {
                editor.FontFamily = font;
                editor.FontSize = size;
                style.BaseFontSize = size;
                state.CaretOffset = width == 400 ? prefix.Length + 3 : text.Length;
                state.Selections = width == 260 ? new() { (0, text.Length) } : new();
                window.Width = width;
                editor.TextArea.TextView.Redraw();
                window.GetLayoutManager()!.ExecuteLayoutPass();
                var view = editor.TextArea.TextView;
                var visual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
                Assert.True(visual.TextLines.Count > 1);
                var contentColumn = visual.GetVisualColumn(prefix.Length);
                if (width == 600) contentX = visual.TextLines[0].GetDistanceFromCharacterHit(new CharacterHit(contentColumn, 0));
                var x = contentX;
                Assert.True(x > 0);
                foreach (var row in visual.TextLines.Skip(1))
                {
                    Assert.True(Math.Abs(row.Start - x) < 0.05, $"{font} {size} {width}: row {row.FirstTextSourceIndex}, start {row.Start}, expected {x}");
                    var first = row.FirstTextSourceIndex;
                    var hit = row.GetCharacterHitFromDistance(x);
                    var hitColumn = hit.FirstCharacterIndex + hit.TrailingLength;
                    // Un marcador oculto puede tener la misma X que el primer carácter visible.
                    Assert.InRange(Math.Abs(row.GetDistanceFromCharacterHit(hit) - x), 0, 0.05);
                    Assert.InRange(Math.Abs(row.GetDistanceFromCharacterHit(new CharacterHit(first, 0)) - x), 0, 0.05);
                    foreach (var bounds in row.GetTextBounds(first, 1))
                        Assert.InRange(Math.Abs(bounds.Rectangle.Left - x), 0, 0.05);
                    var endColumn = Math.Min(first + row.Length - 1, visual.VisualLength);
                    var endX = row.GetDistanceFromCharacterHit(new CharacterHit(endColumn, 0));
                    var endHit = row.GetCharacterHitFromDistance(endX);
                    Assert.Equal(endHit.FirstCharacterIndex + endHit.TrailingLength,
                        visual.GetVisualColumn(row, endX, allowVirtualSpace: true));
                    var point = new Point(x, visual.GetTextLineVisualYPosition(row, VisualYPosition.LineMiddle));
                    var position = view.GetPosition(point);
                    Assert.NotNull(position);
                    Assert.Equal(visual.GetRelativeOffset(hitColumn), editor.Document.GetOffset(position.Value.Location));
                }
                Assert.Equal(text, editor.Text);
                Assert.Equal(2, editor.Document.LineCount);
                Assert.False(editor.Document.UndoStack.CanUndo);
                editor.Select(0, text.Length);
                Assert.Equal(text, editor.SelectedText);
            }
            editor.WordWrap = false;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Single(editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1)).TextLines);
            // Cambiar de documento restablece el formateador interno de AvaloniaEdit.
            editor.Document = new TextDocument(text);
            editor.WordWrap = true;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var replaced = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            Assert.True(replaced.TextLines[1].Start > 0);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("Texto normal ")]
    [InlineData("    Texto indentado ")]
    [InlineData("```\n- Dentro de código ")]
    public void NonLists_KeepNativeLayout(string prefix)
    {
        HeadlessApp.EnsureInitialized();
        var text = prefix + string.Concat(Enumerable.Repeat("palabra ", 60));
        var native = new TextEditor { Text = text, WordWrap = true, Width = 250 };
        var markdown = new TextEditor { Text = text, WordWrap = true, Width = 250 };
        var state = new LivePreviewState { Fences = MarkdownLineParser.ComputeFences(text) };
        var style = MarkdownStyle.FromApp(FontFamily.Default, FontFamily.Default, 14);
        markdown.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(state, markdown.TextArea, style));
        var window = new Window { Content = new StackPanel { Children = { native, markdown } }, Width = 300, Height = 900 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var number = text.Contains('\n') ? 2 : 1;
            var expected = native.TextArea.TextView.GetOrConstructVisualLine(native.Document.GetLineByNumber(number)).TextLines;
            var actual = markdown.TextArea.TextView.GetOrConstructVisualLine(markdown.Document.GetLineByNumber(number)).TextLines;
            Assert.Equal(expected.Select(row => (row.Start, row.Length)), actual.Select(row => (row.Start, row.Length)));
        }
        finally { window.Close(); }
    }
}
