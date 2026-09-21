using System.Collections.Generic;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Views.Editor.Markdown;

/// <summary>Cubre <see cref="MarkdownSpecialBlocks"/> — el escaneo de los bloques enriquecidos que abarcan
/// varias líneas (fórmulas <c>$$…$$</c> y cercas <c>```mermaid</c>) y que por eso no caben en
/// <see cref="MarkdownLineParser"/>, que solo ve una línea suelta (T11).</summary>
public class MarkdownSpecialBlocksTests
{
    private static IReadOnlyList<MarkdownSpecialBlock> Scan(string document) =>
        MarkdownSpecialBlocks.Scan(document, MarkdownLineParser.ComputeFences(document));

    [Fact]
    public void Scan_DisplayMath_SpansFromOpenerToCloser()
    {
        var block = Assert.Single(Scan("Antes\n\n$$\n\\int_0^1 x^2 dx\n$$\n\nDespués"));

        Assert.Equal(MarkdownSpecialBlockKind.Math, block.Kind);
        Assert.Equal(3, block.StartLine);
        Assert.Equal(5, block.EndLine);
        Assert.Equal("\\int_0^1 x^2 dx", block.Source);
    }

    [Fact]
    public void Scan_MultiLineDisplayMath_KeepsEveryLineOfTheSource()
    {
        var block = Assert.Single(Scan("$$\na = b\n+ c\n$$"));

        Assert.Equal("a = b\n+ c", block.Source);
    }

    [Fact]
    public void Scan_MermaidFence_IsItsOwnBlockWithoutTheDelimiters()
    {
        var block = Assert.Single(Scan("```mermaid\ngraph TD;\nA-->B;\n```"));

        Assert.Equal(MarkdownSpecialBlockKind.Mermaid, block.Kind);
        Assert.Equal(1, block.StartLine);
        Assert.Equal(4, block.EndLine);
        Assert.Equal("graph TD;\nA-->B;", block.Source);
    }

    [Fact]
    public void Scan_OtherLanguageFence_IsNotADiagram()
    {
        Assert.Empty(Scan("```csharp\nvar x = 1;\n```"));
    }

    [Fact]
    public void Scan_MathDelimitersInsideACodeFence_AreLiteralText()
    {
        // Dentro de un bloque de código `$$` es texto, no una fórmula: el documento enseña el LaTeX en
        // crudo a propósito (es justo como se documenta la sintaxis).
        Assert.Empty(Scan("```\n$$\nx^2\n$$\n```"));
    }

    [Fact]
    public void Scan_UnclosedBlock_IsIgnored()
    {
        // Mientras se está escribiendo el bloque no hay cierre todavía: sin él no se pliega ni se dibuja
        // nada, y el editor sigue mostrando texto normal.
        Assert.Empty(Scan("$$\nx^2"));
        Assert.Empty(Scan("```mermaid\ngraph TD;"));
    }

    [Fact]
    public void Scan_TwoBlocks_AreBothFound()
    {
        var blocks = Scan("$$\na\n$$\n\ntexto\n\n```mermaid\ngraph TD;\n```");

        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownSpecialBlockKind.Math, blocks[0].Kind);
        Assert.Equal(MarkdownSpecialBlockKind.Mermaid, blocks[1].Kind);
    }

    [Fact]
    public void Scan_Offsets_MatchThePositionsInTheDocument()
    {
        const string document = "ab\n$$\nx\n$$";
        var block = Assert.Single(Scan(document));

        Assert.Equal(3, block.StartOffset);
        Assert.Equal(document.Length, block.EndOffset);
    }
}
