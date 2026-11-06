using System;
using System.Linq;
using Avalonia.Media;
using AvaloniaEdit.Document;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.UITests;

/// <summary>El resaltado de sintaxis de los bloques de código: que las gramáticas se cargan, que colorea
/// lo que tiene que colorear y que no toca nada de lo que hay fuera de un bloque.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class MarkdownCodeHighlightUiTests
{
    private const string Note = """
        Un párrafo normal antes del bloque.

        ```python
        def saluda(nombre):
            # un comentario
            return f"hola {nombre}"
        ```

        ```
        sin lenguaje declarado
        ```
        """;

    private static (MarkdownCodeHighlighter Highlighter, TextDocument Document) Build(string text)
    {
        HeadlessApp.EnsureInitialized();
        var state = new LivePreviewState { Fences = MarkdownLineParser.ComputeFences(text) };
        var highlighter = new MarkdownCodeHighlighter(state);
        return (highlighter, new TextDocument(text));
    }

    /// <summary>Las gramáticas se cargan en segundo plano: el primer repintado devuelve null (el código se
    /// ve plano) y cuando terminan se pide otro. El test espera a ese aviso igual que lo hace la vista.</summary>
    private static void WaitUntilReady(MarkdownCodeHighlighter highlighter, TextDocument document, int lineNumber)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (highlighter.Tokens(document, lineNumber) is { Count: > 0 }) return;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(25);
        }
        throw new TimeoutException("El resaltado de código no llegó a estar listo.");
    }

    [Fact]
    public void CodeInsideAFencedBlock_GetsSyntaxColours()
    {
        var (highlighter, document) = Build(Note);
        // Línea 4: "def saluda(nombre):"
        WaitUntilReady(highlighter, document, 4);

        var tokens = highlighter.Tokens(document, 4)!;
        Assert.True(tokens.Count > 1, "la línea debería partirse en varios tramos");
        Assert.Contains(tokens, token => token.Foreground is not null);
        // Los tramos se entregan en orden y dentro de la línea, que es lo que el colorizador da por hecho.
        var lineLength = document.GetLineByNumber(4).Length;
        Assert.All(tokens, token =>
        {
            Assert.True(token.Start < token.End);
            Assert.True(token.End <= lineLength);
        });
        Assert.Equal(tokens.OrderBy(token => token.Start).Select(token => token.Start), tokens.Select(token => token.Start));
    }

    /// <summary>Los temas Plus colorean los comentarios sin imponer estilo tipográfico. El valor
    /// NotSet de TextMate no es una máscara de estilos: tratarlo como tal activaba todos a la vez.</summary>
    [Fact]
    public void Comments_WithoutExplicitFontStyle_DoNotAcquireEveryDecoration()
    {
        var (highlighter, document) = Build(Note);
        WaitUntilReady(highlighter, document, 5);

        var tokens = highlighter.Tokens(document, 5)!;
        Assert.Contains(tokens, token => token.Foreground is not null);
        Assert.All(tokens, token =>
        {
            Assert.Equal(FontStyle.Normal, token.Style);
            Assert.Equal(FontWeight.Normal, token.Weight);
            Assert.False(token.Underline);
        });
    }

    [Fact]
    public void ProseAndUndeclaredBlocks_AreLeftAlone()
    {
        var (highlighter, document) = Build(Note);
        WaitUntilReady(highlighter, document, 4);

        Assert.Null(highlighter.Tokens(document, 1));   // párrafo
        Assert.Null(highlighter.Tokens(document, 3));   // el propio ```python
        Assert.Null(highlighter.Tokens(document, 10));  // bloque sin lenguaje
    }

    [Fact]
    public void Invalidate_DropsTheCachedBlockSoEditsAreReflected()
    {
        var (highlighter, document) = Build(Note);
        WaitUntilReady(highlighter, document, 4);
        var before = highlighter.Tokens(document, 4)!.Count;

        document.Replace(document.GetLineByNumber(4).Offset, document.GetLineByNumber(4).Length, "x = 1");
        highlighter.Invalidate();

        var after = highlighter.Tokens(document, 4)!;
        Assert.NotEqual(before, after.Count);
        Assert.All(after, token => Assert.True(token.End <= 5));
    }
}
