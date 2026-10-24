using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Material.Icons.Avalonia;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.UITests;

/// <summary>El texto de cada línea del aviso queda centrado dentro de su fondo.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class MarkdownCalloutLayoutUiTests
{
    [Theory]
    [InlineData(14.5, "> [!note] Título")]
    [InlineData(20, "> [!note] Título")]
    [InlineData(14.5, "> [!note]")]
    public void HeaderAndBody_HaveSymmetricVerticalSpace_WithoutChangingPlainQuotes(double fontSize,
        string headerText)
    {
        HeadlessApp.EnsureInitialized();
        var text = headerText + "\n> cuerpo\n\n> cita normal";
        var font = new FontFamily("avares://enki/assets/fonts#Space Grotesk");
        var editor = new TextEditor { Text = text, Width = 300, FontFamily = font, FontSize = fontSize };
        var state = new LivePreviewState
        {
            CaretOffset = text.Length,
            Callouts = MarkdownCallouts.Scan(text, MarkdownLineParser.ComputeFences(text))
        };
        var style = MarkdownStyle.FromApp(font, FontFamily.Default, fontSize);
        editor.TextArea.TextView.ElementGenerators.Add(new MarkdownElementGenerator(state, editor.TextArea, style));
        editor.TextArea.TextView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(state, style));
        editor.TextArea.TextView.LineTransformers.Add(new MarkdownLivePreviewColorizer(state, style));
        var window = new Window { Content = editor, Width = 340, Height = 180 };
        window.Show();
        try
        {
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var view = editor.TextArea.TextView;
            var headerVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            var header = headerVisual.TextLines[0];
            var body = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(2)).TextLines[0];
            var plain = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(4)).TextLines[0];
            var icon = view.GetVisualDescendants().OfType<MaterialIcon>().First();
            var iconTop = icon.TranslatePoint(new Point(0, 0), view)!.Value.Y;
            var iconCenter = iconTop + icon.Bounds.Height / 2;
            var headerCenter = headerVisual.VisualTop - view.VerticalOffset + headerVisual.Height / 2;

            Assert.InRange(header.Height - plain.Height, 7, 9);
            Assert.InRange(body.Height - plain.Height, 7, 9);
            Assert.InRange(header.Baseline - plain.Baseline, 3, 5);
            Assert.InRange(body.Baseline - plain.Baseline, 3, 5);
            Assert.InRange(iconCenter - headerCenter, -1, 1);
        }
        finally { window.Close(); }
    }
}
