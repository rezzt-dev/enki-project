using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class EditorStatsServiceTests : IDisposable
{
    private readonly string _tempFile;

    public EditorStatsServiceTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); }
        catch { /* no-op */ }
    }

    [Fact]
    public void Compute_CountsWordsAndChars()
    {
        var markdown = "Primera frase.\nSegunda frase.";
        File.WriteAllText(_tempFile, markdown);

        var stats = EditorStatsService.Compute(markdown, _tempFile);

        Assert.Equal(4, stats.WordCount);
        Assert.Equal(markdown.Length, stats.CharCount);
        Assert.True(stats.CharCountNoSpaces < stats.CharCount);
    }

    [Fact]
    public void Compute_StripsFrontMatterBeforeCounting()
    {
        var markdown = "---\ntags: [a, b]\n---\n\nSolo esta línea.";
        File.WriteAllText(_tempFile, markdown);

        var stats = EditorStatsService.Compute(markdown, _tempFile);

        Assert.Equal(3, stats.WordCount);
        Assert.DoesNotContain("tags", stats.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Assertion", "XUnit2013:Do not use equality check to check for collection size.", Justification = "Scalar property, not collection.")]
    public void Compute_ParagraphsIgnoreHeadingsAndEmptyLines()
    {
        var markdown = "# Título\n\nPárrafo uno.\n\nPárrafo dos.\n\n";

        var stats = EditorStatsService.Compute(markdown, _tempFile);

        Assert.Equal(2, stats.ParagraphCount);
        Assert.Equal(7, stats.LineCount);
    }

    [Fact]
    public void Compute_ReadingTime_IsAtLeastOneMinute()
    {
        var markdown = "Palabra.";

        var stats = EditorStatsService.Compute(markdown, _tempFile);

        Assert.Equal(1, stats.ReadingTimeMinutes);
    }

    [Fact]
    public void Compute_InvalidPath_ReturnsMinDates()
    {
        var stats = EditorStatsService.Compute("Texto", "/ruta/que/no/existe.md");

        Assert.Equal(DateTime.MinValue, stats.CreatedUtc);
        Assert.Equal(DateTime.MinValue, stats.ModifiedUtc);
    }
}
