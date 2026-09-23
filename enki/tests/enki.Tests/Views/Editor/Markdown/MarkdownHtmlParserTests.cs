using System.Collections.Generic;
using System.Diagnostics;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Views.Editor.Markdown;

/// <summary>Cubre <see cref="MarkdownHtmlParser"/> — el HTML simple que el editor reconoce para poder
/// ocultar las etiquetas y alinear ("&lt;div align=…&gt;") el texto que envuelven.</summary>
public class MarkdownHtmlParserTests
{
    private static Dictionary<int, FenceInfo> NoFences => new();

    private static Dictionary<int, HtmlLineState> States(string document) =>
        MarkdownHtmlParser.ComputeLineStates(document, MarkdownLineParser.ComputeFences(document));

    private static Dictionary<int, MdAlign> Alignments(string document)
    {
        var result = new Dictionary<int, MdAlign>();
        foreach (var (line, state) in States(document))
        {
            if (state.Align != MdAlign.None) result[line] = state.Align;
        }
        return result;
    }

    private static List<(int Start, int End, MdSpanKind Kind)> StyleRanges(string lineText) =>
        MarkdownHtmlParser.BuildStyleRanges(MarkdownHtmlParser.ParseTags(lineText, 0), 0, lineText.Length, new List<string>());

    [Fact]
    public void ParseTags_RecognizesOpenCloseAndAlignment()
    {
        var tags = MarkdownHtmlParser.ParseTags("<div align=\"center\">Hola</div>", 0);

        Assert.Equal(2, tags.Count);
        Assert.Equal("div", tags[0].Name);
        Assert.Equal(HtmlTagKind.Open, tags[0].Kind);
        Assert.True(tags[0].IsBlock);
        Assert.Equal(MdAlign.Center, tags[0].Align);
        Assert.Equal(0, tags[0].Start);
        Assert.Equal("<div align=\"center\">".Length, tags[0].End);
        Assert.Equal(HtmlTagKind.Close, tags[1].Kind);
    }

    [Theory]
    [InlineData("<div align=\"center\">", MdAlign.Center)]
    [InlineData("<div align=center>", MdAlign.Center)]
    [InlineData("<DIV ALIGN='RIGHT'>", MdAlign.Right)]
    [InlineData("<p align=\"justify\">", MdAlign.Justify)]
    [InlineData("<div style=\"text-align: justify\">", MdAlign.Justify)]
    [InlineData("<center>", MdAlign.Center)]
    [InlineData("<div>", MdAlign.None)]
    public void ParseTags_ReadsEveryAlignmentSpelling(string tagText, MdAlign expected)
    {
        var tag = Assert.Single(MarkdownHtmlParser.ParseTags(tagText, 0));
        Assert.Equal(expected, tag.Align);
    }

    [Fact]
    public void ParseTags_IgnoresUnknownTags()
    {
        // Una nota que menciona <cualquiercosa> tiene que seguir viéndose literal, no desaparecer.
        Assert.Empty(MarkdownHtmlParser.ParseTags("un <cualquiercosa> suelto", 0));
    }

    [Fact]
    public void ParseTags_EscapedTag_IsNotMarkup()
    {
        Assert.Empty(MarkdownHtmlParser.ParseTags(@"escribe \<div> para agrupar", 0));
    }

    [Fact]
    public void ComputeAlignments_AppliesToEveryLineInsideTheBlock()
    {
        var aligns = Alignments("normal\n<div align=\"center\">\nuno\ndos\n</div>\notra vez normal");

        Assert.False(aligns.ContainsKey(1));
        Assert.Equal(MdAlign.Center, aligns[3]);
        Assert.Equal(MdAlign.Center, aligns[4]);
        Assert.False(aligns.ContainsKey(6));
    }

    [Fact]
    public void ComputeAlignments_SingleLineBlockAlignsItsOwnLine()
    {
        var aligns = Alignments("<div align=\"right\">a la derecha</div>\nnormal");

        Assert.Equal(MdAlign.Right, aligns[1]);
        Assert.False(aligns.ContainsKey(2));
    }

    [Fact]
    public void ComputeAlignments_NestedBlocksRestoreTheOuterAlignment()
    {
        var aligns = Alignments("<div align=\"right\">\na\n<div align=\"left\">\nb\n</div>\nc\n</div>");

        Assert.Equal(MdAlign.Right, aligns[2]);
        Assert.Equal(MdAlign.Left, aligns[4]);
        Assert.Equal(MdAlign.Right, aligns[6]);
    }

    [Fact]
    public void ComputeAlignments_IgnoresBlocksInsideFencedCode()
    {
        // Un <div> escrito dentro de ``` es código de ejemplo: ni se oculta ni arrastra al resto de la nota.
        var aligns = Alignments("```html\n<div align=\"center\">\n```\ndespués");

        Assert.False(aligns.ContainsKey(2));
        Assert.False(aligns.ContainsKey(4));
    }

    [Fact]
    public void ComputeAlignments_DocumentWithoutHtml_IsEmpty()
    {
        Assert.Empty(Alignments("# Título\n\nUn párrafo normal."));
    }

    [Fact]
    public void BuildStyleRanges_PairsInlineTags()
    {
        var text = "hola <b>mundo</b>";
        var ranges = StyleRanges(text);

        var range = Assert.Single(ranges);
        Assert.Equal(MdSpanKind.Bold, range.Kind);
        Assert.Equal("mundo", text[range.Start..range.End]);
    }

    [Fact]
    public void BuildStyleRanges_NestedBoldAndItalic_CombineIntoBoldItalic()
    {
        var text = "<b>uno<i>dos</i></b>";
        var ranges = StyleRanges(text);

        Assert.Equal(2, ranges.Count);
        // Ordenadas de fuera adentro: la interior se aplica la última y gana.
        Assert.Equal(MdSpanKind.Bold, ranges[0].Kind);
        Assert.Equal(MdSpanKind.BoldItalic, ranges[1].Kind);
        Assert.Equal("dos", text[ranges[1].Start..ranges[1].End]);
    }

    [Fact]
    public void BuildStyleRanges_UnclosedTag_StylesTheRestOfTheLine()
    {
        var text = "antes <u>después";
        var ranges = StyleRanges(text);

        var range = Assert.Single(ranges);
        Assert.Equal(MdSpanKind.Underline, range.Kind);
        Assert.Equal("después", text[range.Start..range.End]);
    }

    [Fact]
    public void ComputeLineStates_InlineTagOpenOnAnEarlierLine_StaysOpen()
    {
        // <i> abre en la línea 1 y cierra en la 3: la 2 y la 3 tienen que heredarlo.
        var states = States("<i>uno\ndos\ntres</i>\ncuatro");

        // La línea 1 no guarda estado: no hereda nada (el diccionario solo lista lo que lleva contexto).
        Assert.False(states.ContainsKey(1));
        Assert.Equal(new[] { "i" }, states[2].OpenInlineTags);
        Assert.Equal(new[] { "i" }, states[3].OpenInlineTags);
        Assert.False(states.ContainsKey(4));
    }

    [Fact]
    public void ComputeLineStates_ClosingTagAlsoClosesWhatIsOpenInsideIt()
    {
        var states = States("<b><i>uno\ndos</b>\ntres");

        Assert.Equal(new[] { "b", "i" }, states[2].OpenInlineTags);
        Assert.False(states.ContainsKey(3));
    }

    [Fact]
    public void BuildStyleRanges_InheritedTag_StylesTheLineUpToItsClosingTag()
    {
        var text = "hasta aquí</i> y ya no";
        var ranges = MarkdownHtmlParser.BuildStyleRanges(
            MarkdownHtmlParser.ParseTags(text, 0), 0, text.Length, new List<string> { "i" });

        var range = Assert.Single(ranges);
        Assert.Equal(MdSpanKind.Italic, range.Kind);
        Assert.Equal("hasta aquí", text[range.Start..range.End]);
    }

    [Fact]
    public void BuildStyleRanges_InheritedTagWithNoClosingTag_StylesTheWholeLine()
    {
        var text = "línea entera en cursiva";
        var ranges = MarkdownHtmlParser.BuildStyleRanges(
            MarkdownHtmlParser.ParseTags(text, 0), 0, text.Length, new List<string> { "i" });

        var range = Assert.Single(ranges);
        Assert.Equal(0, range.Start);
        Assert.Equal(text.Length, range.End);
    }

    [Fact]
    public void BuildStyleRanges_StrayClosingTag_IsIgnored()
    {
        Assert.Empty(StyleRanges("texto suelto</i>"));
    }

    [Fact]
    public void ComputeLineStates_TagQuotedInInlineCode_DoesNotOpenAnything()
    {
        var states = States("usa `<div align=\"center\">` para centrar\nesta línea es normal");

        Assert.False(states.ContainsKey(2));
    }

    [Fact]
    public void SanitizedTag_KeepsOnlyTheAlignment()
    {
        var tags = MarkdownHtmlParser.ParseTags("<div align=\"center\" onclick=\"alert(1)\" class=\"x\">", 0);

        Assert.Equal("<div style=\"text-align:center\">", MarkdownHtmlParser.SanitizedTag(tags[0]));
    }

    [Fact]
    public void SanitizedTag_ClosingAndVoidTags()
    {
        Assert.Equal("</div>", MarkdownHtmlParser.SanitizedTag(MarkdownHtmlParser.ParseTags("</div>", 0)[0]));
        Assert.Equal("<br/>", MarkdownHtmlParser.SanitizedTag(MarkdownHtmlParser.ParseTags("<br>", 0)[0]));
    }

    [Fact]
    public void ParseTags_PathologicalUnclosedAttributes_ReturnsPromptlyAsPlainText()
    {
        var line = "<div " + string.Concat(System.Linq.Enumerable.Repeat("a='aaaaaaaa' ", 20_000));
        var stopwatch = Stopwatch.StartNew();

        var tags = MarkdownHtmlParser.ParseTags(line, 0);

        stopwatch.Stop();
        Assert.Empty(tags);
        Assert.True(stopwatch.Elapsed < System.TimeSpan.FromSeconds(2));
    }
}
