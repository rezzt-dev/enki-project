using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Views.Editor.Markdown;

public class MarkdownLineParserTests
{
    private static readonly FenceInfo NoFence = new(isDelimiter: false, isInside: false, language: null);

    [Fact]
    public void Parse_RecognizesPlainWikiLink()
    {
        var text = "Ver [[Nota A]] para más contexto.";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.WikiLink, span.Kind);
        Assert.Equal("Nota A", span.LinkTarget);
        Assert.Equal("Nota A", text[span.ContentStart..span.ContentEnd]);
        Assert.Equal("[[Nota A]]", text[span.Start..span.End]);
    }

    [Fact]
    public void Parse_WikiLinkWithAlias_TargetIsBeforePipe_ContentIsAlias()
    {
        var text = "[[Nota A|texto visible]]";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.WikiLink, span.Kind);
        Assert.Equal("Nota A", span.LinkTarget);
        Assert.Equal("texto visible", text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Parse_WikiLinkWithHeadingAnchor_TargetExcludesAnchor()
    {
        var text = "[[Nota A#Sección]]";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal("Nota A", span.LinkTarget);
    }

    [Fact]
    public void Parse_DoesNotConfuseWikiLinkWithPlainMarkdownLink()
    {
        var text = "[[Nota A]] y [texto](https://example.com)";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(2, model.Spans.Count);
        Assert.Equal(MdSpanKind.WikiLink, model.Spans[0].Kind);
        Assert.Equal(MdSpanKind.Link, model.Spans[1].Kind);
        Assert.Equal("https://example.com", model.Spans[1].LinkTarget);
    }

    [Fact]
    public void Parse_UnclosedWikiLink_ProducesNoSpan()
    {
        var text = "esto es [[incompleto";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.DoesNotContain(model.Spans, s => s.Kind == MdSpanKind.WikiLink);
    }

    [Theory]
    [InlineData("| --- | --- |", true)]
    [InlineData("| :-: | ---: | :--- |", true)]
    [InlineData("| -- | -- |", false)]
    [InlineData("| a | b |", false)]
    [InlineData("| - | - |", false)]
    [InlineData("| --- | texto |", false)]
    public void Parse_TableSeparatorDetection(string text, bool expected)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.Table, model.Kind);
        Assert.Equal(expected, model.IsTableSeparator);
    }

    [Fact]
    public void Parse_TableDataRow_IsDetectedAsTable()
    {
        var text = "| Celda 1 | Celda 2 |";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.Table, model.Kind);
        Assert.False(model.IsTableSeparator);
    }

    [Fact]
    public void Parse_TableRow_InlineSpansStillParsed()
    {
        var text = "| **bold** | `code` |";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.Table, model.Kind);
        Assert.Equal(2, model.Spans.Count);
        Assert.Contains(model.Spans, s => s.Kind == MdSpanKind.Bold);
        Assert.Contains(model.Spans, s => s.Kind == MdSpanKind.InlineCode);
    }

    [Fact]
    public void Parse_Strikethrough_IsParsed()
    {
        var text = "~~tachado~~";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Strikethrough, span.Kind);
        Assert.Equal("tachado", text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Parse_Highlight_IsParsed()
    {
        var text = "==resaltado==";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Highlight, span.Kind);
        Assert.Equal("resaltado", text[span.ContentStart..span.ContentEnd]);
    }

    [Theory]
    [InlineData("# HOLA", 0, 2, 2)]
    [InlineData("#  HOLA", 0, 2, 3)]
    [InlineData("##   HOLA", 0, 3, 5)]
    public void Parse_Heading_PreservesExtraSpaces(string text, int markerStart, int markerEnd, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.Heading, model.Kind);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(markerEnd, model.MarkerEnd);
        Assert.Equal(contentStart, model.ContentStart);
    }

    [Theory]
    [InlineData("- Item", 0, 2, 2)]
    [InlineData("-  Item", 0, 2, 3)]
    [InlineData("-   Item", 0, 2, 4)]
    public void Parse_BulletList_PreservesExtraSpaces(string text, int markerStart, int markerEnd, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.BulletList, model.Kind);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(markerEnd, model.MarkerEnd);
        Assert.Equal(contentStart, model.ContentStart);
    }

    [Theory]
    [InlineData("1. Item", 0, 3, 3)]
    [InlineData("1.  Item", 0, 3, 4)]
    [InlineData("12.   Item", 0, 4, 6)]
    public void Parse_OrderedList_PreservesExtraSpaces(string text, int markerStart, int markerEnd, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.OrderedList, model.Kind);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(markerEnd, model.MarkerEnd);
        Assert.Equal(contentStart, model.ContentStart);
    }

    [Theory]
    [InlineData("- [ ] Item", 0, 6, 6)]
    [InlineData("- [ ]  Item", 0, 6, 7)]
    [InlineData("- [x]   Item", 0, 6, 8)]
    public void Parse_TaskList_PreservesExtraSpaces(string text, int markerStart, int markerEnd, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.TaskList, model.Kind);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(markerEnd, model.MarkerEnd);
        Assert.Equal(contentStart, model.ContentStart);
    }
}
