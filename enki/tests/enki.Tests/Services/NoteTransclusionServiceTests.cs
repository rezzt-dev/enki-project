using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class NoteTransclusionServiceTests
{
    [Fact]
    public void ExtractSection_IncludesNestedHeadingsAndStopsAtPeer()
    {
        var markdown = "# Inicio\nuno\n## Detalle\ndos\n# Siguiente\ntres";

        var found = NoteTransclusionService.TryExtractSection(markdown, "Inicio", out var section);

        Assert.True(found);
        Assert.Equal("# Inicio\nuno\n## Detalle\ndos", section);
    }

    [Fact]
    public void ExtractSection_NormalizesHeadingLikeObsidianAnchor()
    {
        var found = NoteTransclusionService.TryExtractSection(
            "## Fórmulas y diagramas\nContenido", "fórmulas-y-diagramas", out var section);

        Assert.True(found);
        Assert.Equal("## Fórmulas y diagramas\nContenido", section);
    }

    [Theory]
    [InlineData("Una idea importante ^idea-1", "Una idea importante")]
    [InlineData("Un párrafo\n^idea-1", "Un párrafo")]
    public void ExtractBlock_RemovesIdentifier(string markdown, string expected)
    {
        var found = NoteTransclusionService.TryExtractBlock(markdown, "idea-1", out var block);

        Assert.True(found);
        Assert.Equal(expected, block);
    }

    [Fact]
    public void Extractors_ReturnFalse_WhenFragmentDoesNotExist()
    {
        Assert.False(NoteTransclusionService.TryExtractSection("# Existe", "No existe", out _));
        Assert.False(NoteTransclusionService.TryExtractBlock("Texto ^otro", "ausente", out _));
    }
}
