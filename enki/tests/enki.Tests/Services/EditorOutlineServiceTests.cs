using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class EditorOutlineServiceTests
{
    [Fact]
    public void Extract_EmptyInput_ReturnsEmpty()
    {
        var result = EditorOutlineService.Extract(string.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_Headings_AreParsedWithLevelAndOffset()
    {
        var markdown = "# Título 1\n\n## Título 2\n\n### Título 3";
        var result = EditorOutlineService.Extract(markdown);

        Assert.Equal(3, result.Count);
        Assert.Equal("Título 1", result[0].Title);
        Assert.Equal(1, result[0].Level);
        Assert.Equal(1, result[0].LineNumber);
        Assert.Equal(2, result[0].Offset);

        Assert.Equal("Título 2", result[1].Title);
        Assert.Equal(2, result[1].Level);
        Assert.Equal(3, result[1].LineNumber);

        Assert.Equal("Título 3", result[2].Title);
        Assert.Equal(3, result[2].Level);
        Assert.Equal(5, result[2].LineNumber);
    }

    [Fact]
    public void Extract_HeadingWithTrailingHashes_StripsThem()
    {
        var markdown = "# Título ###  ";
        var result = EditorOutlineService.Extract(markdown);

        var item = Assert.Single(result);
        Assert.Equal("Título", item.Title);
    }

    [Fact]
    public void Extract_InvalidHeadings_AreIgnored()
    {
        var markdown = "####### Demasiados\n#SinEspacio\n    # ConIndentaciónExcesiva";
        var result = EditorOutlineService.Extract(markdown);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_ComputedOffsets_IncludeLeadingSpacesAndHashes()
    {
        var markdown = "  ##  Sección";
        var result = EditorOutlineService.Extract(markdown);

        var item = Assert.Single(result);
        Assert.Equal(6, item.Offset);
        Assert.Equal("Sección", item.Title);
    }
}
