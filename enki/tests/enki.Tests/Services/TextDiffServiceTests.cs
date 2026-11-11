using System;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="TextDiffService"/> — the line-based LCS diff behind the version-history
/// compare view (historial local de versiones).</summary>
public class TextDiffServiceTests
{
    [Fact]
    public void Compare_IdenticalText_ProducesOnlyUnchangedLines()
    {
        var result = TextDiffService.Compare("a\nb\nc", "a\nb\nc");

        Assert.All(result, line => Assert.Equal(DiffLineKind.Unchanged, line.Kind));
        Assert.Equal(["a", "b", "c"], result.Select(l => l.Text));
    }

    [Fact]
    public void Compare_OneLineChangedInTheMiddle_MarksItRemovedAndAdded()
    {
        var result = TextDiffService.Compare("a\nb\nc", "a\nx\nc");

        Assert.Equal(DiffLineKind.Unchanged, result[0].Kind);
        Assert.Contains(result, l => l is { Kind: DiffLineKind.Removed, Text: "b" });
        Assert.Contains(result, l => l is { Kind: DiffLineKind.Added, Text: "x" });
        Assert.Equal(DiffLineKind.Unchanged, result[^1].Kind);
    }

    [Fact]
    public void Compare_AppendedLine_IsMarkedAdded()
    {
        var result = TextDiffService.Compare("a\nb", "a\nb\nc");

        Assert.DoesNotContain(result, l => l.Kind == DiffLineKind.Removed);
        Assert.Single(result, l => l is { Kind: DiffLineKind.Added, Text: "c" });
    }

    [Fact]
    public void Compare_RemovedLine_IsMarkedRemoved()
    {
        var result = TextDiffService.Compare("a\nb\nc", "a\nc");

        Assert.DoesNotContain(result, l => l.Kind == DiffLineKind.Added);
        Assert.Single(result, l => l is { Kind: DiffLineKind.Removed, Text: "b" });
    }

    [Fact]
    public void Compare_EmptyOldText_MarksEveryLineOfNewTextAsAdded()
    {
        var result = TextDiffService.Compare("", "a\nb");

        Assert.All(result, line => Assert.Equal(DiffLineKind.Added, line.Kind));
        Assert.Equal(["a", "b"], result.Select(l => l.Text));
    }

    [Fact]
    public void Excerpt_KeepsChangesWithContext_AndMarksSkippedStretches()
    {
        var old = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"línea {i}"));
        var changed = old.Replace("línea 10", "línea diez", StringComparison.Ordinal);

        var excerpt = TextDiffService.Excerpt(TextDiffService.Compare(old, changed), context: 2);

        Assert.Equal(TextDiffService.GapMarker, excerpt.First().Text);
        Assert.Equal(TextDiffService.GapMarker, excerpt.Last().Text);
        Assert.Contains(excerpt, l => l.Kind == DiffLineKind.Removed && l.Text == "línea 10");
        Assert.Contains(excerpt, l => l.Kind == DiffLineKind.Added && l.Text == "línea diez");
        Assert.Contains(excerpt, l => l.Text == "línea 8");
        Assert.DoesNotContain(excerpt, l => l.Text == "línea 7");
    }

    [Fact]
    public void Excerpt_OfIdenticalTexts_IsEmpty()
    {
        Assert.Empty(TextDiffService.Excerpt(TextDiffService.Compare("a\nb", "a\nb"), context: 2));
    }
}
