using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.UITests;

/// <summary>Comprueba saltos, tipografía y geometría sobre el formateador real de AvaloniaEdit.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class MarkdownJustificationUiTests
{
    private const string Body = "Este párrafo contiene palabras de distintos tamaños para comprobar una lectura cómoda y uniforme. ";

    [Fact]
    public void KeyboardEditing_NavigationSelectionAndUndoKeepDocumentSemantics()
    {
        using var fixture = new Fixture(string.Concat(Enumerable.Repeat(Body, 6)), 450, 16);
        fixture.Rows(true);
        fixture.Editor.TextArea.Focus();
        Press(Key.Home, RawInputModifiers.Control);
        fixture.Window.KeyTextInput("hola ");
        Assert.StartsWith("hola Este", fixture.Editor.Text);
        Press(Key.Enter);
        Assert.Equal(2, fixture.Editor.Document.LineCount);
        Press(Key.Back);
        Assert.Equal(1, fixture.Editor.Document.LineCount);
        Press(Key.Delete);
        Assert.StartsWith("hola ste", fixture.Editor.Text);
        Press(Key.Z, RawInputModifiers.Control);
        Assert.StartsWith("hola Este", fixture.Editor.Text);
        Press(Key.Home, RawInputModifiers.Control);
        Press(Key.Right, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal("hola ", fixture.Editor.SelectedText);
        fixture.Window.KeyTextInput(Body + Body);
        Assert.StartsWith(Body + Body + "Este", fixture.Editor.Text);
        Press(Key.End, RawInputModifiers.Control);
        Assert.Equal(fixture.Editor.Text.Length, fixture.Editor.CaretOffset);
        Press(Key.Home);
        Assert.True(fixture.Editor.CaretOffset < fixture.Editor.Text.Length);
        Press(Key.End);
        Assert.Equal(fixture.Editor.Text.Length, fixture.Editor.CaretOffset);
        fixture.Rows(true);
        Press(Key.Home, RawInputModifiers.Control);
        Press(Key.Right, RawInputModifiers.Control | RawInputModifiers.Shift);
        var copied = fixture.Editor.SelectedText;
        Assert.NotEmpty(copied);
        Press(Key.C, RawInputModifiers.Control);
        Press(Key.End, RawInputModifiers.Control);
        var beforePaste = fixture.Editor.Text;
        Press(Key.V, RawInputModifiers.Control);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(beforePaste + copied, fixture.Editor.Text);

        void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            fixture.Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            fixture.Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }
    }

    [Fact]
    public void MouseClickDoubleClickAndDrag_UseJustifiedCharacterPositions()
    {
        using var fixture = new Fixture(string.Concat(Enumerable.Repeat(Body, 6)), 450, 16);
        var row = fixture.Rows(true)[0];
        var view = fixture.Editor.TextArea.TextView;
        var x = row.GetDistanceFromCharacterHit(new CharacterHit(7, 0));
        var point = view.TranslatePoint(new Point(x + .1, row.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Assert.Equal(7, fixture.Editor.CaretOffset);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Assert.Equal("párrafo", fixture.Editor.SelectedText);
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Assert.NotEmpty(fixture.Editor.SelectedText);
        var end = view.TranslatePoint(new Point(row.Width - 10, row.Height * 2.5), fixture.Window)!.Value;
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseUp(end, MouseButton.Left);
        Assert.NotEmpty(fixture.Editor.SelectedText);
        Assert.Equal(fixture.Text, fixture.Editor.Text);
    }

    [Theory]
    [InlineData(260, 14)]
    [InlineData(400, 16)]
    [InlineData(670, 22)]
    public void Prose_FillsEveryIntermediateRowWithoutChangingBreaksOrLastRow(double width, double size)
    {
        using var fixture = new Fixture(string.Concat(Enumerable.Repeat(Body, 8)), width, size);
        var natural = fixture.Rows(false);
        var expected = natural.Select(row => (row.FirstTextSourceIndex, row.Length, row.Width)).ToArray();
        var rows = fixture.Rows(true);
        Assert.Equal(expected.Select(row => (row.FirstTextSourceIndex, row.Length)),
            rows.Select(row => (row.FirstTextSourceIndex, row.Length)));
        // Todas, no "alguna": un margen derecho que solo cuadra a ratos se lee como un fallo, y era
        // exactamente el síntoma que dejaba la versión anterior al rendirse fila a fila.
        Assert.All(rows.Take(rows.Length - 1),
            row => Assert.Equal(fixture.Editor.TextArea.TextView.Bounds.Width, row.Width, .05));
        Assert.Equal(expected[^1].Width, rows[^1].Width, 5);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.True(rows[i].Width >= expected[i].Width - .01);
            CheckGeometry(rows[i]);
        }
        Assert.Equal(fixture.Text, fixture.Editor.Text);
        fixture.Editor.Select(0, fixture.Text.Length);
        Assert.Equal(fixture.Text, fixture.Editor.SelectedText);
        Assert.False(fixture.Editor.Document.UndoStack.CanUndo);
    }

    /// <summary>Una fila que necesita más blanco del que cabe cómodamente entre sus palabras —la que
    /// precede a una palabra muy larga— también cuadra: es el caso que antes dejaba media página en
    /// bandera. Lo que no puede pasar es que el texto se "descosa": los saltos siguen donde estaban y la
    /// geometría sigue siendo recorrible carácter a carácter.</summary>
    [Fact]
    public void RowsBeforeAVeryLongWord_StillReachTheColumnEdge()
    {
        const string text = "uno dos tres cuatro cinco seis siete ocho nueve diez once doce trece " +
                            "catorce quince dieciseis diecisiete dieciocho diecinueve veinte veintiuno";
        using var fixture = new Fixture(text, 300, 16);
        var natural = fixture.Rows(false);
        var rows = fixture.Rows(true);
        Assert.Equal(natural.Select(row => (row.FirstTextSourceIndex, row.Length)),
            rows.Select(row => (row.FirstTextSourceIndex, row.Length)));
        Assert.All(rows.Take(rows.Length - 1),
            row => Assert.Equal(fixture.Editor.TextArea.TextView.Bounds.Width, row.Width, .05));
        foreach (var row in rows) CheckGeometry(row);
    }

    /// <summary>Listas, tareas y citas se justifican igual que un párrafo —una página con los párrafos a
    /// ras y las listas en bandera se lee como dos composiciones distintas— pero conservan su sangría:
    /// cada fila sigue empezando exactamente donde empezaba.</summary>
    [Theory]
    [InlineData("- ")]
    [InlineData("1. ")]
    [InlineData("- [ ] ")]
    [InlineData("> ")]
    [InlineData("> > ")]
    public void IndentedBlocks_JustifyWithoutLosingTheirIndent(string prefix)
    {
        using var fixture = new Fixture(prefix + string.Concat(Enumerable.Repeat(Body, 5)), 350, 16);
        var natural = fixture.Rows(false);
        var expected = natural.Select(row => (row.Start, row.Length)).ToArray();
        var rows = fixture.Rows(true);
        Assert.Equal(expected.Select(row => row.Length), rows.Select(row => row.Length));
        for (var i = 0; i < rows.Length; i++) Assert.Equal(expected[i].Start, rows[i].Start, .05);
        Assert.All(rows.Take(rows.Length - 1),
            row => Assert.Equal(fixture.Editor.TextArea.TextView.Bounds.Width, row.Width, .05));
        Assert.Equal(natural[^1].Width, rows[^1].Width, 5);
        // Desde la segunda fila: la primera empieza por la viñeta/casilla dibujada, un widget cuya
        // geometría la reporta AvaloniaEdit y no este formateo — se desvía igual con y sin justificar.
        foreach (var row in rows.Skip(1)) CheckGeometry(row);
    }

    [Theory]
    [InlineData("hola mundo")]
    [InlineData("esta linea es corta")]
    [InlineData("primera linea\nsegunda linea")]
    [InlineData("uno dos supercalifragilisticoespialidososupercalifragilisticoespialidoso")]
    [InlineData("https://ejemplo.com/ruta-muy-larga-sin-espacios/otra/ruta/mas/larga/aun")]
    [InlineData("文字の間に不自然な空白を追加しない文章です文字の間に不自然な空白を追加しない文章です")]
    [InlineData("palabra\u00a0inseparable\u00a0otra\u00a0palabra\u00a0inseparable")]
    [InlineData("uno  dos  tres  cuatro  cinco  seis  siete  ocho  nueve  diez  once  doce")]
    public void ShortSparseOrUnspacedText_RemainsNatural(string text)
    {
        using var fixture = new Fixture(text, 230, 16);
        var expected = fixture.Rows(false).Select(row => (row.Length, row.Width)).ToArray();
        Assert.Equal(expected, fixture.Rows(true).Select(row => (row.Length, row.Width)));
    }

    /// <summary>Lo que se queda como está: un encabezado (estirar un título se lee como un error), la
    /// sangría de cuatro espacios y el tabulador (espaciado literal), el código y los bloques que ya
    /// traen su propia alineación HTML.</summary>
    [Theory]
    [InlineData("# ")]
    [InlineData("    ")]
    [InlineData("\t")]
    [InlineData("```csharp\n")]
    [InlineData("<div align=\"left\">\n")]
    [InlineData("<div align=\"center\">\n")]
    public void OtherBlocks_PreserveTheirExistingLayout(string prefix)
    {
        using var fixture = new Fixture(prefix + string.Concat(Enumerable.Repeat(Body, 5)), 350, 16);
        var number = prefix.Contains('\n') ? 2 : 1;
        var expected = fixture.Rows(false, number).Select(row => (row.Start, row.Length, row.Width)).ToArray();
        Assert.Equal(expected, fixture.Rows(true, number).Select(row => (row.Start, row.Length, row.Width)));
    }

    [Fact]
    public void TableRows_KeepTheirColumnGeometry()
    {
        using var fixture = new Fixture("| primera columna con varias palabras | segunda columna |\n| --- | --- |\n| datos para comprobar la tabla | otros datos |", 480, 16);
        for (var number = 1; number <= 3; number++)
        {
            var expected = fixture.Rows(false, number).Select(row => (row.Start, row.Length, row.Width)).ToArray();
            Assert.Equal(expected, fixture.Rows(true, number).Select(row => (row.Start, row.Length, row.Width)));
        }
    }

    [Fact]
    public void InlineFormatting_ProtectsCodeAndRetainsGlyphAndSelectionGeometry()
    {
        var text = string.Concat(Enumerable.Repeat("Texto con **negrita**, *cursiva*, `uno dos tres` y [un enlace](https://example.com) y más palabras. ", 8));
        using var fixture = new Fixture(text, 520, 16);
        var natural = fixture.Rows(false);
        var codeSpaces = SpaceAdvances(natural, fixture, codeOnly: true);
        Assert.NotEmpty(codeSpaces);
        var justified = fixture.Rows(true);
        Assert.Equal(codeSpaces, SpaceAdvances(justified, fixture, codeOnly: true));
        Assert.Contains(justified.Take(justified.Length - 1), row => Math.Abs(row.Width - fixture.Editor.TextArea.TextView.Bounds.Width) < .05);
        foreach (var row in justified) CheckGeometry(row);
        Assert.Equal(text, fixture.Editor.Text);
    }

    [Fact]
    public void ResizeFontMarginsWrapAndDocumentChanges_RecalculateWithoutAccumulatingSpacing()
    {
        using var fixture = new Fixture(string.Concat(Enumerable.Repeat(Body, 7)), 430, 16);
        var initial = fixture.Rows(true).Select(row => row.Width).ToArray();
        var initialFont = fixture.Editor.FontFamily;
        var initialBodyFont = fixture.Style.BodyFontFamily;
        for (var i = 0; i < 12; i++)
        {
            fixture.Window.Width = i % 2 == 0 ? 300 : 650;
            fixture.Editor.ShowLineNumbers = i % 2 == 0;
            fixture.Editor.FontSize = fixture.Style.BaseFontSize = i % 2 == 0 ? 22 : 14;
            fixture.Editor.FontFamily = fixture.Style.BodyFontFamily = i % 2 == 0
                ? new FontFamily("avares://enki/assets/fonts#Space Grotesk") : FontFamily.Default;
            fixture.Window.SetRenderScaling(i % 2 == 0 ? 1.5 : 1);
            foreach (var row in fixture.Rows(true))
            {
                Assert.True(row.Width <= fixture.Editor.TextArea.TextView.Bounds.Width + .1, $"iteration {i}: width {row.Width}, view {fixture.Editor.TextArea.TextView.Bounds.Width}, row {row.FirstTextSourceIndex}");
                CheckGeometry(row);
            }
        }
        fixture.Window.Width = 430;
        fixture.Editor.ShowLineNumbers = false;
        fixture.Editor.FontSize = fixture.Style.BaseFontSize = 16;
        fixture.Editor.FontFamily = initialFont;
        fixture.Style.BodyFontFamily = initialBodyFont;
        fixture.Window.SetRenderScaling(1);
        Assert.Equal(initial, fixture.Rows(true).Select(row => row.Width));
        fixture.Editor.WordWrap = false;
        Assert.Single(fixture.Rows(true));
        fixture.Editor.Document = new TextDocument(fixture.Text);
        fixture.Editor.WordWrap = true;
        Assert.Equal(initial, fixture.Rows(true).Select(row => row.Width));
        fixture.Editor.Document.Insert(20, "texto añadido ");
        fixture.Rows(true);
        fixture.Editor.Document.UndoStack.Undo();
        Assert.Equal(initial, fixture.Rows(true).Select(row => row.Width));
        fixture.Editor.Document.UndoStack.Redo();
        Assert.Contains("texto añadido", fixture.Editor.Text);
    }

    private static double[] SpaceAdvances(TextLine[] rows, Fixture fixture, bool codeOnly)
    {
        var visual = fixture.Editor.TextArea.TextView.GetOrConstructVisualLine(fixture.Editor.Document.GetLineByNumber(1));
        var model = MarkdownLineParser.Parse(fixture.Text, 0, default);
        return rows.SelectMany(row =>
        {
            var column = row.FirstTextSourceIndex;
            var values = new System.Collections.Generic.List<double>();
            foreach (var run in row.TextRuns)
            {
                if (run is ShapedTextRun shaped)
                foreach (var glyph in shaped.ShapedBuffer)
                {
                    var local = glyph.GlyphCluster - shaped.GlyphRun.Metrics.FirstCluster;
                    if (local < 0 || local >= run.Text.Length || run.Text.Span[local] != ' ') continue;
                    var offset = visual.GetRelativeOffset(column + local);
                    if (!codeOnly || model.Spans.Any(span => span.Kind == MdSpanKind.InlineCode && offset >= span.ContentStart && offset < span.ContentEnd))
                        values.Add(glyph.GlyphAdvance);
                }
                column += run.Length;
            }
            return values;
        }).ToArray();
    }

    private static void CheckGeometry(TextLine row)
    {
        var hit = new CharacterHit(row.FirstTextSourceIndex, 0);
        for (var i = 0; i < row.Length; i++)
        {
            var x = row.GetDistanceFromCharacterHit(hit);
            var roundTrip = row.GetCharacterHitFromDistance(x);
            Assert.InRange(Math.Abs(row.GetDistanceFromCharacterHit(roundTrip) - x), 0, .05);
            var next = row.GetNextCaretCharacterHit(hit);
            if (next == hit) break;
            var index = hit.FirstCharacterIndex + hit.TrailingLength;
            var nextIndex = next.FirstCharacterIndex + next.TrailingLength;
            if (nextIndex > index)
            {
                var nextX = row.GetDistanceFromCharacterHit(next);
                var bounds = row.GetTextBounds(index, nextIndex - index);
                if (bounds.Count == 1 && nextX > x)
                {
                    Assert.InRange(Math.Abs(bounds[0].Rectangle.Left - x), 0, .05);
                    Assert.InRange(Math.Abs(bounds[0].Rectangle.Right - nextX), 0, .05);
                }
            }
            hit = next;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Text { get; }
        public TextEditor Editor { get; }
        public Window Window { get; }
        public MarkdownStyle Style { get; }
        private readonly LivePreviewState _state;

        public Fixture(string text, double width, double size)
        {
            HeadlessApp.EnsureInitialized();
            Text = text;
            Editor = new TextEditor { Text = text, WordWrap = true, FontSize = size };
            _state = new LivePreviewState { CaretOffset = text.Length, Fences = MarkdownLineParser.ComputeFences(text) };
            _state.HtmlLines = MarkdownHtmlParser.ComputeLineStates(text, _state.Fences);
            _state.Tables.SetRows(MarkdownTableParser.Scan(text, _state.Fences));
            Style = MarkdownStyle.FromApp(FontFamily.Default, FontFamily.Default, size);
            Editor.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(_state, Editor.TextArea, Style));
            Editor.TextArea.TextView.LineTransformers.Add(new MarkdownLivePreviewColorizer(_state, Style));
            Window = new Window { Content = Editor, Width = width, Height = 850 };
            Window.Show();
        }

        public TextLine[] Rows(bool justify, int number = 1)
        {
            _state.JustifyParagraphs = justify;
            Editor.TextArea.TextView.Redraw();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Window.GetLayoutManager()!.ExecuteLayoutPass();
            return Editor.TextArea.TextView.GetOrConstructVisualLine(Editor.Document.GetLineByNumber(number)).TextLines.ToArray();
        }

        public void Dispose() => Window.Close();
    }
}
