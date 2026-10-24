using AvaloniaEdit.Document;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 6 change to <see cref="EditorOutlineViewModel"/>/<see cref="EditorStatsViewModel"/>
/// (docs/analisis-proyecto/roadmap.md, análisis §3): both used to recompute on every single
/// <see cref="TextDocument.TextChanged"/> event, i.e. every keystroke; they now debounce it the same way
/// <see cref="EditorViewModel"/>'s own autosave/backlinks refresh do, via <c>EditorDebounce.Delay</c>. Since
/// nothing in these tests ever lets the underlying <c>DispatcherTimer</c> tick, an assertion that a value
/// stayed unchanged after an edit is really asserting "the refresh didn't happen synchronously" — the same
/// bar the rest of this codebase's debounce coverage (e.g. autosave) is held to.</summary>
public class EditorOutlineAndStatsDebounceTests
{
    [Fact]
    public void Outline_TextChange_DoesNotImmediatelyRecomputeItems()
    {
        var document = new TextDocument("# Primer título\n\nCuerpo.");
        var outline = new EditorOutlineViewModel(document);
        Assert.Single(outline.Items);

        document.Text = "# Primer título\n\n## Segundo título\n\nCuerpo.";

        Assert.Single(outline.Items);
    }

    [Fact]
    public void Outline_Refresh_PicksUpTheLatestDocumentTextImmediately()
    {
        var document = new TextDocument("# Primer título\n\nCuerpo.");
        var outline = new EditorOutlineViewModel(document);
        document.Text = "# Primer título\n\n## Segundo título\n\nCuerpo.";

        outline.Refresh();

        Assert.Equal(2, outline.Items.Count);
    }

    [Fact]
    public void Stats_TextChange_DoesNotImmediatelyRecomputeWordCount()
    {
        var document = new TextDocument("una dos tres");
        var stats = new EditorStatsViewModel(document, fullPath: "/tmp/nota.md");
        Assert.Equal(3, stats.WordCount);

        document.Text = "una dos tres cuatro cinco";

        Assert.Equal(3, stats.WordCount);
    }

    [Fact]
    public void Stats_Refresh_PicksUpTheLatestDocumentTextImmediately()
    {
        var document = new TextDocument("una dos tres");
        var stats = new EditorStatsViewModel(document, fullPath: "/tmp/nota.md");
        document.Text = "una dos tres cuatro cinco";

        stats.Refresh();

        Assert.Equal(5, stats.WordCount);
    }
}
