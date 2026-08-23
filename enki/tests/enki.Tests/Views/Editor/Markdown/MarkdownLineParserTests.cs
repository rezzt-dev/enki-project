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

    // ===================== Énfasis: negrita, cursiva, negrita+cursiva =====================

    [Theory]
    [InlineData("*cursiva*", MdSpanKind.Italic, "cursiva")]
    [InlineData("_cursiva_", MdSpanKind.Italic, "cursiva")]
    [InlineData("**negrita**", MdSpanKind.Bold, "negrita")]
    [InlineData("__negrita__", MdSpanKind.Bold, "negrita")]
    [InlineData("***negrita cursiva***", MdSpanKind.BoldItalic, "negrita cursiva")]
    [InlineData("___negrita cursiva___", MdSpanKind.BoldItalic, "negrita cursiva")]
    public void Parse_Emphasis_IsParsedWithCorrectKindAndContent(string text, MdSpanKind expectedKind, string expectedContent)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(expectedKind, span.Kind);
        Assert.Equal(expectedContent, text[span.ContentStart..span.ContentEnd]);
        Assert.Equal(text, text[span.Start..span.End]);
    }

    [Fact]
    public void Parse_BoldAndItalic_TogetherOnOneLine_ProducesTwoSeparateSpans()
    {
        var text = "Esto es **negrita** y esto *cursiva*.";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(2, model.Spans.Count);
        Assert.Equal(MdSpanKind.Bold, model.Spans[0].Kind);
        Assert.Equal("negrita", text[model.Spans[0].ContentStart..model.Spans[0].ContentEnd]);
        Assert.Equal(MdSpanKind.Italic, model.Spans[1].Kind);
        Assert.Equal("cursiva", text[model.Spans[1].ContentStart..model.Spans[1].ContentEnd]);
    }

    [Fact]
    public void Parse_UnclosedEmphasisMarker_ProducesNoSpan()
    {
        var text = "esto tiene un *asterisco suelto sin cerrar";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.DoesNotContain(model.Spans, s => s.Kind is MdSpanKind.Italic or MdSpanKind.Bold or MdSpanKind.BoldItalic);
    }

    [Fact]
    public void Parse_EmphasisMarkerImmediatelyFollowedByWhitespace_DoesNotOpen()
    {
        // "* " with a trailing space right after the marker is not emphasis (it would also collide with
        // "* " as a bullet-list marker at the start of a line, but this asserts the inline case).
        var text = "esto no es * cursiva *";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.DoesNotContain(model.Spans, s => s.Kind == MdSpanKind.Italic);
    }

    // ===================== snake_case: el guion bajo intra-palabra no debe abrir énfasis =====================
    // análisis §4.2 — antes de esta corrección, "mi_variable_de_ejemplo" se parseaba como texto en
    // cursiva empezando en el primer "_" (siguiendo la misma regla que abrir/cerrar con un solo
    // asterisco), dejando "variable" en itálica y el resto literal. La regla de CommonMark para "_"
    // (no puede abrir ni cerrar entre dos caracteres alfanuméricos) evita justamente ese caso sin tocar
    // el comportamiento de "*", que sí soporta énfasis intra-palabra.

    [Theory]
    [InlineData("mi_variable_de_ejemplo")]
    [InlineData("snake_case_identifier")]
    [InlineData("un_solo_guion_no_deberia_marcar_nada")]
    public void Parse_SnakeCaseIdentifier_IsNotParsedAsEmphasis(string text)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.DoesNotContain(model.Spans, s => s.Kind is MdSpanKind.Italic or MdSpanKind.Bold or MdSpanKind.BoldItalic);
    }

    [Fact]
    public void Parse_UnderscoreEmphasis_StillWorksWhenSurroundedByWhitespaceOrPunctuation()
    {
        var text = "esto es _cursiva_ de verdad.";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Italic, span.Kind);
        Assert.Equal("cursiva", text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Parse_AsteriskEmphasis_StillWorksIntraword()
    {
        // Only "_" gets the intraword restriction — "*" keeps CommonMark's more permissive behavior,
        // matching this parser's pre-existing support for e.g. "un*fitt*ing".
        var text = "un*fitt*ing";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Italic, span.Kind);
        Assert.Equal("fitt", text[span.ContentStart..span.ContentEnd]);
    }
}
