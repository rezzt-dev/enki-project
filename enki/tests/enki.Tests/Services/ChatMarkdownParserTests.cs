using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class ChatMarkdownParserTests
{
    [Fact]
    public void ParseBlocks_RecognizesTheCommonBlocks()
    {
        var blocks = ChatMarkdownParser.ParseBlocks(
            "# Título\n\nUn párrafo\nque sigue.\n\n- uno\n  - dos\n1. tres\n- [x] hecha\n- [ ] pendiente\n\n> cita\n> larga\n\n---\n\n```csharp\nvar x = 1;\n```");

        Assert.Collection(blocks,
            b => { Assert.Equal(ChatBlockKind.Heading, b.Kind); Assert.Equal(1, b.Level); Assert.Equal("Título", b.Text); },
            b => { Assert.Equal(ChatBlockKind.Paragraph, b.Kind); Assert.Equal("Un párrafo\nque sigue.", b.Text); },
            b => { Assert.Equal(ChatBlockKind.ListItem, b.Kind); Assert.Equal("•", b.Marker); Assert.Equal(0, b.Level); },
            b => { Assert.Equal(ChatBlockKind.ListItem, b.Kind); Assert.Equal(1, b.Level); Assert.Equal("dos", b.Text); },
            b => { Assert.Equal(ChatBlockKind.ListItem, b.Kind); Assert.Equal("1.", b.Marker); },
            b => { Assert.Equal(true, b.IsChecked); Assert.Equal("hecha", b.Text); },
            b => { Assert.Equal(false, b.IsChecked); Assert.Equal("pendiente", b.Text); },
            b => { Assert.Equal(ChatBlockKind.Quote, b.Kind); Assert.Equal("cita\nlarga", b.Text); },
            b => Assert.Equal(ChatBlockKind.Rule, b.Kind),
            b => { Assert.Equal(ChatBlockKind.Code, b.Kind); Assert.Equal("csharp", b.Language); Assert.Equal("var x = 1;", b.Text); });
    }

    [Fact]
    public void ParseBlocks_UnclosedFenceWhileStreaming_IsStillCode()
    {
        var block = Assert.Single(ChatMarkdownParser.ParseBlocks("```\nlinea 1\nlinea 2"));

        Assert.Equal(ChatBlockKind.Code, block.Kind);
        Assert.Null(block.Language);
        Assert.Equal("linea 1\nlinea 2", block.Text);
    }

    [Fact]
    public void ParseBlocks_CodeContentIsNotInterpretedAsMarkdown()
    {
        var block = Assert.Single(ChatMarkdownParser.ParseBlocks("```\n# no es título\n- ni lista\n```"));

        Assert.Equal("# no es título\n- ni lista", block.Text);
    }

    [Fact]
    public void ParseBlocks_Table_PadsShortRows()
    {
        var table = Assert.Single(ChatMarkdownParser.ParseBlocks("| a | b |\n|---|:---:|\n| 1 | 2 |\n| 3 |"));

        Assert.Equal(ChatBlockKind.Table, table.Kind);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["a", "b"], table.Rows[0]);
        Assert.Equal(["3", ""], table.Rows[2]);
    }

    [Fact]
    public void ParseInlines_SplitsStyles()
    {
        var inlines = ChatMarkdownParser.ParseInlines("Usa **negrita**, *cursiva*, `código` y [enlace](https://x.dev).");

        Assert.Equal(
            [ChatInlineStyle.Plain, ChatInlineStyle.Bold, ChatInlineStyle.Plain, ChatInlineStyle.Italic, ChatInlineStyle.Plain,
             ChatInlineStyle.Code, ChatInlineStyle.Plain, ChatInlineStyle.Link, ChatInlineStyle.Plain],
            inlines.Select(i => i.Style));
        Assert.Equal("negrita", inlines[1].Text);
        Assert.Equal("https://x.dev", inlines[7].Url);
    }

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("snake_case_name")]
    [InlineData("**sin cerrar")]
    public void ParseInlines_LeavesNonMarkupAsPlainText(string text)
    {
        var inline = Assert.Single(ChatMarkdownParser.ParseInlines(text));

        Assert.Equal(ChatInlineStyle.Plain, inline.Style);
        Assert.Equal(text, inline.Text);
    }
}
