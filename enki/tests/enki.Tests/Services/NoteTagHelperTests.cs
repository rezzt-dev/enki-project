using System.Collections.Generic;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class NoteTagHelperTests
{
    [Fact]
    public void ExtractTags_NoFrontMatter_ReturnsEmpty()
    {
        var content = "# Título\n\nCuerpo.";
        Assert.Empty(NoteTagHelper.ExtractTags(content));
    }

    [Fact]
    public void ExtractTags_InlineArray_ReturnsTags()
    {
        var content = "---\ntags: [casa, urgente]\n---\n\n# Título";
        Assert.Equal(new List<string> { "casa", "urgente" }, NoteTagHelper.ExtractTags(content));
    }

    [Fact]
    public void ExtractTags_YamlList_ReturnsTags()
    {
        var content = "---\ntags:\n  - trabajo\n  - personal\n---\n\n# Título";
        Assert.Equal(new List<string> { "trabajo", "personal" }, NoteTagHelper.ExtractTags(content));
    }

    [Fact]
    public void SetTags_AddsFrontMatterToPlainContent()
    {
        var content = "# Título\n\nCuerpo.";
        var result = NoteTagHelper.SetTags(content, new List<string> { "casa", "urgente" });
        Assert.StartsWith("---\ntags: [casa, urgente]\n---\n\n", result);
        Assert.Contains("# Título", result);
    }

    [Fact]
    public void SetTags_UpdatesExistingTags()
    {
        var content = "---\ntags: [a, b]\n---\n\n# Título";
        var result = NoteTagHelper.SetTags(content, new List<string> { "c" });
        Assert.Contains("tags: [c]", result);
        Assert.DoesNotContain("tags: [a, b]", result);
        Assert.Contains("# Título", result);
    }

    [Fact]
    public void SetTags_EmptyTags_RemovesFrontMatter()
    {
        var content = "---\ntags: [a, b]\n---\n\n# Título\n\nCuerpo.";
        var result = NoteTagHelper.SetTags(content, new List<string>());
        Assert.Equal("# Título\n\nCuerpo.", result);
    }

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 5 — vaciar las etiquetas no debe borrar claves de
    /// frontmatter ajenas a "tags" (aliases, created, ...).</summary>
    [Fact]
    public void SetTags_EmptyTags_WithOtherFrontMatterKeys_KeepsThem()
    {
        var content = "---\naliases: [Alias]\ntags: [a, b]\ncreated: 2024-01-01\n---\n\n# Título\n\nCuerpo.";
        var result = NoteTagHelper.SetTags(content, new List<string>());

        Assert.Equal("---\naliases: [Alias]\ncreated: 2024-01-01\n---\n\n# Título\n\nCuerpo.", result);
        Assert.Empty(NoteTagHelper.ExtractTags(result));
    }

    [Fact]
    public void SetTags_EmptyTags_WithYamlListForm_RemovesTagLinesButKeepsOtherKeys()
    {
        var content = "---\naliases: [Alias]\ntags:\n  - trabajo\n  - personal\ncreated: 2024-01-01\n---\n\n# Título";
        var result = NoteTagHelper.SetTags(content, new List<string>());

        Assert.Equal("---\naliases: [Alias]\ncreated: 2024-01-01\n---\n\n# Título", result);
    }

    [Fact]
    public void SetTags_UpdatesExistingYamlListTags_DoesNotLeaveOrphanListLines()
    {
        var content = "---\ntags:\n  - trabajo\n  - personal\n---\n\n# Título";
        var result = NoteTagHelper.SetTags(content, new List<string> { "c" });

        Assert.Equal(new List<string> { "c" }, NoteTagHelper.ExtractTags(result));
        Assert.DoesNotContain("- trabajo", result);
        Assert.DoesNotContain("- personal", result);
    }

    [Fact]
    public void StripFrontMatter_RemovesBlockAndBlankLine()
    {
        var content = "---\ntags: [a]\n---\n\n# Título\n\nCuerpo.";
        var result = NoteTagHelper.StripFrontMatter(content);
        Assert.Equal("# Título\n\nCuerpo.", result);
    }
}
