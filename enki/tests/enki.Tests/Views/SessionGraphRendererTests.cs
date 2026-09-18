using System.Text.Json;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.Tests.Views;

/// <summary>Covers the JSON payload <see cref="SessionGraphRenderer"/> hands to the WebView-hosted graph
/// script (03-problemas-y-deuda-tecnica.md §8) — only the pure-data entry points
/// (<see cref="SessionGraphRenderer.BuildSetGraphDataScript"/> and friends), not
/// <see cref="SessionGraphRenderer.RenderDocument"/> itself: that one reads the vendored sigma/d3 bundles
/// through Avalonia's <c>avares://</c> asset loader and the app's theme resources, neither of which a
/// plain xunit host sets up (same constraint documented on <c>PdfRenderHelperTests</c> for
/// Avalonia-Bitmap-producing code — would need Avalonia.Headless to exercise safely).</summary>
public class SessionGraphRendererTests
{
    private static SessionGraph OneEdgeGraph() => new()
    {
        Nodes =
        [
            new SessionGraphNode { SessionId = "s1", SessionName = "Sesión", RelativePath = "A.md", Title = "Nota A", X = 1, Y = 2, Z = 3 },
            new SessionGraphNode { SessionId = "s1", SessionName = "Sesión", RelativePath = "B.md", Title = "Nota B", X = 4, Y = 5, Z = 6 }
        ],
        Edges =
        [
            new SessionGraphEdge { FromSessionId = "s1", ToSessionId = "s1", FromRelativePath = "A.md", ToRelativePath = "B.md", IsManual = true }
        ]
    };

    [Fact]
    public void BuildSetGraphDataScript_IncludesNodeGlobalIdsAndTitles()
    {
        var script = SessionGraphRenderer.BuildSetGraphDataScript(OneEdgeGraph());

        Assert.Contains("window.__enkiSetGraphData", script);
        Assert.Contains("s1::A.md", script);
        Assert.Contains("Nota A", script);
        Assert.Contains("s1::B.md", script);
    }

    [Fact]
    public void BuildSetGraphDataScript_IncludesEdgeSourceTargetAndManualFlag()
    {
        // JsonSerializer.Serialize(string) HTML-escapes quotes as " (default encoder), so the
        // literal survives as a single-line JS string argument rather than raw '"source":"..."'.
        var script = SessionGraphRenderer.BuildSetGraphDataScript(OneEdgeGraph());

        Assert.Contains("source", script);
        Assert.Contains("target", script);
        Assert.Contains("manual", script);
        Assert.Contains("s1::A.md", script);
        Assert.Contains("s1::B.md", script);
        Assert.Contains("true", script);
    }

    [Fact]
    public void BuildSetGraphDataScript_EmptyGraph_StillProducesAValidCall()
    {
        var script = SessionGraphRenderer.BuildSetGraphDataScript(new SessionGraph());

        Assert.Contains("window.__enkiSetGraphData", script);
        Assert.Contains("nodes", script);
        Assert.Contains("links", script);
        Assert.Contains("[]", script);
    }

    [Fact]
    public void BuildSetPendingLinkScript_WithGlobalId_EmbedsItAsAQuotedLiteral()
    {
        var script = SessionGraphRenderer.BuildSetPendingLinkScript("s1::A.md");

        Assert.Equal("window.__enkiSetPendingLink && window.__enkiSetPendingLink(\"s1::A.md\");", script);
    }

    [Fact]
    public void BuildSetPendingLinkScript_WithNull_EmbedsJavaScriptNull()
    {
        var script = SessionGraphRenderer.BuildSetPendingLinkScript(null);

        Assert.Equal("window.__enkiSetPendingLink && window.__enkiSetPendingLink(null);", script);
    }

    [Fact]
    public void BuildSetSessionColorScript_EmbedsSessionAndColorAsQuotedLiterals()
    {
        var script = SessionGraphRenderer.BuildSetSessionColorScript("s1", "#2F80ED");

        Assert.Equal(
            "window.__enkiSetSessionColor && window.__enkiSetSessionColor(\"s1\", \"#2F80ED\");",
            script);
    }

    [Fact]
    public void BuildLinkRejectedScript_EmbedsBothGlobalIds()
    {
        var script = SessionGraphRenderer.BuildLinkRejectedScript("s1::A.md", "s2::B.md");

        Assert.Equal(
            "window.__enkiLinkRejected && window.__enkiLinkRejected(\"s1::A.md\", \"s2::B.md\");",
            script);
    }

    [Fact]
    public void BuildSetGraphDataScript_PreservesTagsAsData_IncludingScriptLikeText()
    {
        var graph = OneEdgeGraph();
        graph.Nodes[0].Tags = ["ideas", "</script><script>alert('tag')</script>"];
        var script = SessionGraphRenderer.BuildSetGraphDataScript(graph);
        var argument = script[(script.IndexOf('(') + 1)..^2];
        using var payload = JsonDocument.Parse(JsonSerializer.Deserialize<string>(argument)!);

        var tags = payload.RootElement.GetProperty("nodes")[0].GetProperty("tags");
        Assert.Equal("ideas", tags[0].GetString());
        Assert.Equal(graph.Nodes[0].Tags[1], tags[1].GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("nodes")[1].GetProperty("tags").GetArrayLength());
    }
}
