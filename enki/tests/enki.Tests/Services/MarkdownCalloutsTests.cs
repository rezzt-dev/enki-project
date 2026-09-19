using System.Collections.Generic;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Los avisos estilo Obsidian: qué líneas forman parte de uno y qué reconoce el analizador de
/// línea dentro de la cabecera.</summary>
public class MarkdownCalloutsTests
{
    private static Dictionary<int, MdCalloutLine> Scan(string text) =>
        MarkdownCallouts.Scan(text, MarkdownLineParser.ComputeFences(text));

    [Fact]
    public void Scan_MarksTheHeaderAndEveryBodyLineOfTheCallout()
    {
        var callouts = Scan("> [!warning] Cuidado\n> primera\n> segunda\n\nfuera");

        Assert.Equal(3, callouts.Count);
        Assert.True(callouts[1].IsHeader);
        Assert.False(callouts[2].IsHeader);
        Assert.Equal("warning", callouts[3].Kind);
        Assert.Equal(MdCalloutTone.Warning, callouts[3].Tone);
        Assert.Equal((1, 3), (callouts[3].StartLine, callouts[3].EndLine));
        Assert.False(callouts.ContainsKey(5));
    }

    [Fact]
    public void Scan_LeavesAPlainQuoteAlone()
    {
        Assert.Empty(Scan("> una cita normal\n> con dos líneas"));
    }

    [Fact]
    public void Scan_IgnoresAnUnknownKind()
    {
        Assert.Empty(Scan("> [!inventado] no existe"));
    }

    /// <summary>Dos avisos pegados son dos avisos: la segunda cabecera cierra el primero.</summary>
    [Fact]
    public void Scan_StartsANewCalloutOnEachHeader()
    {
        var callouts = Scan("> [!tip] uno\n> cuerpo\n> [!bug] dos\n> cuerpo");

        Assert.Equal((1, 2), (callouts[1].StartLine, callouts[1].EndLine));
        Assert.Equal("tip", callouts[2].Kind);
        Assert.True(callouts[3].IsHeader);
        Assert.Equal("bug", callouts[4].Kind);
        Assert.Equal(MdCalloutTone.Danger, callouts[4].Tone);
    }

    /// <summary>Dentro de un bloque de código no hay avisos: ahí un <c>&gt; [!note]</c> es texto de
    /// ejemplo, probablemente el de alguien documentando esta misma sintaxis.</summary>
    [Fact]
    public void Scan_IgnoresWhatIsInsideAFencedCodeBlock()
    {
        Assert.Empty(Scan("```markdown\n> [!note] ejemplo\n> cuerpo\n```"));
    }

    [Theory]
    [InlineData("[!note]", "note")]
    [InlineData("[!NOTE] Título", "NOTE")]
    [InlineData("[!tip]+ plegado abierto", "tip")]
    [InlineData("[!info]- plegado cerrado", "info")]
    public void ParseHeader_AcceptsTheFormsObsidianWrites(string content, string expected) =>
        Assert.Equal(expected, MarkdownCallouts.ParseHeader(content));

    [Theory]
    [InlineData("texto normal")]
    [InlineData("[!]")]
    [InlineData("[note]")]
    [InlineData("[!con espacio] x")]
    public void ParseHeader_RejectsWhatIsNotAHeader(string content) =>
        Assert.Null(MarkdownCallouts.ParseHeader(content));

    [Fact]
    public void LineParser_ExposesTheMarkerAndWhereTheTitleStarts()
    {
        const string text = "> [!danger] No hagas esto";
        var model = MarkdownLineParser.Parse(text, 0, default);

        Assert.Equal(MdBlockKind.Blockquote, model.Kind);
        Assert.Equal("danger", model.CalloutKind);
        Assert.Equal("[!danger] ", text[model.CalloutMarkerStart..model.CalloutMarkerEnd]);
        Assert.Equal("No hagas esto", text[model.CalloutTitleStart..]);
    }

    /// <summary>Sin título propio, el título empieza donde acaba la línea — que es la señal de que hay que
    /// enseñar el nombre del tipo en su lugar.</summary>
    [Fact]
    public void LineParser_ReportsAnEmptyTitleWhenTheCalloutHasNone()
    {
        const string text = "> [!note]";
        var model = MarkdownLineParser.Parse(text, 0, default);

        Assert.Equal("note", model.CalloutKind);
        Assert.Equal(text.Length, model.CalloutTitleStart);
        Assert.Equal("Note", MarkdownCallouts.DefaultTitle(model.CalloutKind!));
    }

    [Fact]
    public void LineParser_LeavesAPlainQuoteWithoutACalloutKind()
    {
        Assert.Null(MarkdownLineParser.Parse("> una cita", 0, default).CalloutKind);
    }

    /// <summary>El título lleva formato en línea como cualquier otro texto, y el marcador no: un
    /// <c>[!note]</c> no puede acabar dentro de un span.</summary>
    [Fact]
    public void LineParser_ParsesInlineFormattingInTheTitleOnly()
    {
        const string text = "> [!tip] con **negrita**";
        var model = MarkdownLineParser.Parse(text, 0, default);

        var bold = Assert.Single(model.Spans);
        Assert.Equal(MdSpanKind.Bold, bold.Kind);
        Assert.True(bold.Start >= model.CalloutMarkerEnd);
    }
}
