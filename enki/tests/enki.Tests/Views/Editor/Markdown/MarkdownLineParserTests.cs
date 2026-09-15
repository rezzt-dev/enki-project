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
    [InlineData("> cita", 1, 0, 2)]
    [InlineData(">> cita", 2, 0, 3)]
    [InlineData("> > cita", 2, 0, 4)]
    [InlineData("  >> cita", 2, 2, 5)]
    public void Parse_Blockquote_ReportsEveryNestedLevel(string text, int depth, int markerStart, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.Blockquote, model.Kind);
        Assert.Equal(depth, model.QuoteDepth);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(contentStart, model.MarkerEnd);
        Assert.Equal(contentStart, model.ContentStart);
    }

    /// <summary>A diferencia de encabezados y citas, la viñeta se traga los espacios extra: la "• " que
    /// se pinta en su lugar ya reserva su propia separación, así que conservarlos visibles duplicaría el
    /// hueco entre el marcador y el texto.</summary>
    [Theory]
    [InlineData("- Item", 0, 2)]
    [InlineData("-  Item", 0, 3)]
    [InlineData("-   Item", 0, 4)]
    public void Parse_BulletList_AbsorbsExtraSpacesIntoTheMarker(string text, int markerStart, int contentStart)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(MdBlockKind.BulletList, model.Kind);
        Assert.Equal(markerStart, model.MarkerStart);
        Assert.Equal(contentStart, model.MarkerEnd);
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

    [Fact]
    public void Parse_HtmlTags_BecomeConcealableSpans()
    {
        var text = "<div align=\"center\">Hola</div>";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(2, model.HtmlTags.Count);
        Assert.Equal(2, model.Spans.Count);
        Assert.All(model.Spans, span => Assert.Equal(MdSpanKind.HtmlTag, span.Kind));
        // Un span de etiqueta no tiene contenido: se oculta entero, como cualquier marcador Markdown.
        Assert.All(model.Spans, span => Assert.Equal(span.End, span.ContentStart));
        Assert.Equal("<div align=\"center\">", text[model.Spans[0].Start..model.Spans[0].End]);
        Assert.Equal("</div>", text[model.Spans[1].Start..model.Spans[1].End]);
    }

    [Fact]
    public void Parse_MarkdownInsideAnHtmlBlock_StillParses()
    {
        var text = "<div align=\"center\">**negrita**</div>";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var bold = Assert.Single(model.Spans, span => span.Kind == MdSpanKind.Bold);
        Assert.Equal("negrita", text[bold.ContentStart..bold.ContentEnd]);
    }

    [Fact]
    public void Parse_HtmlInsideInlineCode_StaysLiteral()
    {
        // `<div>` entre comillas invertidas es texto de ejemplo: ocultarlo dejaría la línea vacía.
        var text = "usa `<div>` para agrupar";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Empty(model.HtmlTags);
        Assert.DoesNotContain(model.Spans, span => span.Kind == MdSpanKind.HtmlTag);
    }

    [Fact]
    public void Parse_HtmlInsideCodeFence_IsNotMarkup()
    {
        var model = MarkdownLineParser.Parse("<div align=\"center\">", 0, new FenceInfo(isDelimiter: false, isInside: true, language: "html"));

        Assert.Empty(model.HtmlTags);
        Assert.Equal(MdBlockKind.CodeFence, model.Kind);
    }

    [Fact]
    public void Parse_UnknownTag_IsPlainText()
    {
        var model = MarkdownLineParser.Parse("un <cualquiercosa> suelto", 0, NoFence);

        Assert.Empty(model.HtmlTags);
        Assert.Empty(model.Spans);
    }

    [Fact]
    public void Parse_AttributePunctuation_DoesNotOpenEmphasis()
    {
        // El '*' vive dentro de la etiqueta: es markup, no una cursiva a medio abrir.
        var model = MarkdownLineParser.Parse("<div style=\"a*b\">texto *cursiva* fin</div>", 0, NoFence);

        var italic = Assert.Single(model.Spans, span => span.Kind == MdSpanKind.Italic);
        Assert.Equal(2, model.HtmlTags.Count);
        Assert.True(italic.Start > model.HtmlTags[0].End);
    }

    // ===================== Imágenes =====================

    [Fact]
    public void Parse_Image_SpanCoversTheExclamationMark()
    {
        var text = "Antes ![captura](adjuntos/foto.png) después";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Image, span.Kind);
        Assert.Equal("adjuntos/foto.png", span.LinkTarget);
        Assert.Equal("captura", text[span.ContentStart..span.ContentEnd]);
        // El '!' entra en el span: si quedara fuera, ocultar la imagen dejaría un '!' suelto en la línea.
        Assert.Equal("![captura](adjuntos/foto.png)", text[span.Start..span.End]);
    }

    [Fact]
    public void Parse_LinkAfterImage_StillParsedAsLink()
    {
        var text = "![foto](a.png) y [enlace](https://ejemplo.test)";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        Assert.Equal(2, model.Spans.Count);
        Assert.Equal(MdSpanKind.Image, model.Spans[0].Kind);
        Assert.Equal(MdSpanKind.Link, model.Spans[1].Kind);
    }

    [Theory]
    [InlineData("![alt|300](a.png)", 300, 0, "alt")]
    [InlineData("![alt|300x200](a.png)", 300, 200, "alt")]
    [InlineData("![|420](a.png)", 420, 0, "")]
    public void Parse_ImageWithExplicitSize_SplitsSizeOutOfAltText(string text, int width, int height, string alt)
    {
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Image, span.Kind);
        Assert.Equal(width, span.ImageWidth);
        Assert.Equal(height, span.ImageHeight);
        Assert.Equal(alt, text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Parse_ImageWithNonNumericPipeSuffix_KeepsItAsAltText()
    {
        var text = "![diagrama|versión 2](a.png)";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(0, span.ImageWidth);
        Assert.Equal("diagrama|versión 2", text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Parse_ImageWithTitle_TargetStopsAtFirstSpace()
    {
        var text = "![alt](foto.png \"Un título\")";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal("foto.png", span.LinkTarget);
    }

    [Fact]
    public void Parse_EmbeddedImageWikiLink_IsAnImageSpan()
    {
        var text = "![[captura.png]]";
        var model = MarkdownLineParser.Parse(text, 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Image, span.Kind);
        Assert.Equal("captura.png", span.LinkTarget);
        Assert.Equal(text, text[span.Start..span.End]);
    }

    [Fact]
    public void Parse_EmbeddedImageWikiLinkWithSize_ReadsWidth()
    {
        var model = MarkdownLineParser.Parse("![[captura.png|300]]", 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Image, span.Kind);
        Assert.Equal("captura.png", span.LinkTarget);
        Assert.Equal(300, span.ImageWidth);
    }

    [Fact]
    public void Parse_EmbeddedNoteWikiLink_StaysAWikiLink()
    {
        // Una transclusión de nota (`![[Otra nota]]`) no es una imagen; degradarla a wikilink normal la
        // deja al menos navegable en vez de dibujarla como imagen rota.
        var model = MarkdownLineParser.Parse("![[Otra nota]]", 0, NoFence);

        var span = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.WikiLink, span.Kind);
        Assert.Equal("Otra nota", span.LinkTarget);
    }

    [Fact]
    public void Parse_ImageInsideCodeFence_IsNotParsed()
    {
        var fence = new FenceInfo(isDelimiter: false, isInside: true, language: "md");
        var model = MarkdownLineParser.Parse("![alt](foto.png)", 0, fence);

        Assert.Empty(model.Spans);
    }

    [Theory]
    [InlineData("foto.png", true)]
    [InlineData("FOTO.PNG", true)]
    [InlineData("https://ejemplo.test/foto.jpg?v=2", true)]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("Otra nota", false)]
    [InlineData("documento.pdf", false)]
    public void LooksLikeImage_JudgesByExtension(string target, bool expected) =>
        Assert.Equal(expected, MarkdownLineParser.LooksLikeImage(target));
}
