using System.Text.Json;
using enki.Models;
using enki.Views.Sessions;
using Xunit;

namespace enki.Tests.Views;

/// <summary>Covers the JSON payload <see cref="SessionGraphRenderer"/> hands to the WebView-hosted graph
/// script (03-problemas-y-deuda-tecnica.md §8) — only the pure-data entry points
/// (<see cref="SessionGraphRenderer.BuildSetGraphDataScript"/> and friends), not
/// <see cref="SessionGraphRenderer.RenderDocument"/> itself: that one reads the embedded font through
/// Avalonia's <c>avares://</c> asset loader and the app's theme resources, neither of which a plain xunit
/// host sets up (it is exercised under Avalonia.Headless in <c>GraphPresentationUiTests</c>).</summary>
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

    private static string Script(SessionGraph graph) =>
        SessionGraphRenderer.BuildSetGraphDataScript(graph, new SessionSphereLayout().Build(graph));

    private static JsonElement Payload(SessionGraph graph)
    {
        var script = Script(graph);
        var argument = script[(script.IndexOf('(') + 1)..^2];
        using var document = JsonDocument.Parse(JsonSerializer.Deserialize<string>(argument)!);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Payload_CarriesRealLocal3DPositions_SpheresAndOnlyRealLinks()
    {
        var graph = OneEdgeGraph();
        var scene = new SessionSphereLayout().Build(graph);
        var payload = Payload(graph);

        var nodes = payload.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        foreach (var node in nodes.EnumerateArray())
        {
            var local = scene.Local[node.GetProperty("id").GetString()!];
            Assert.Equal(local.X, node.GetProperty("x").GetDouble(), 3);
            Assert.Equal(local.Y, node.GetProperty("y").GetDouble(), 3);
            Assert.Equal(local.Z, node.GetProperty("z").GetDouble(), 3);
        }
        Assert.NotEqual(nodes[0].GetProperty("z").GetDouble(), nodes[1].GetProperty("z").GetDouble());
        var sphere = Assert.Single(payload.GetProperty("spheres").EnumerateArray());
        Assert.Equal("s1", sphere.GetProperty("id").GetString());
        Assert.Equal(scene.Spheres["s1"].Radius, sphere.GetProperty("r").GetDouble(), 3);
        Assert.Single(payload.GetProperty("links").EnumerateArray());
        Assert.All(payload.GetProperty("mesh").EnumerateArray(), pair => Assert.All(pair.EnumerateArray(),
            index => Assert.InRange(index.GetInt32(), 0, 1)));
    }

    [Fact]
    public void BuildSetRotationScript_TogglesEverySphereAtOnce()
    {
        Assert.Equal("window.__enkiSetRotation && window.__enkiSetRotation(true);", SessionGraphRenderer.BuildSetRotationScript(true));
        Assert.Equal("window.__enkiSetRotation && window.__enkiSetRotation(false);", SessionGraphRenderer.BuildSetRotationScript(false));
    }

    [Fact]
    public void BuildSetSelectedSessionScript_EmbedsIdOrNull()
    {
        Assert.Equal("window.__enkiSetSelectedSession && window.__enkiSetSelectedSession(\"s1\");",
            SessionGraphRenderer.BuildSetSelectedSessionScript("s1"));
        Assert.Equal("window.__enkiSetSelectedSession && window.__enkiSetSelectedSession(null);",
            SessionGraphRenderer.BuildSetSelectedSessionScript(null));
    }

    [Fact]
    public void BuildSetGraphDataScript_IncludesNodeGlobalIdsAndTitles()
    {
        var script = Script(OneEdgeGraph());

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
        var script = Script(OneEdgeGraph());

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
        var script = Script(new SessionGraph());

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
        var payload = Payload(graph);

        var tags = payload.GetProperty("nodes")[0].GetProperty("tags");
        Assert.Equal("ideas", tags[0].GetString());
        Assert.Equal(graph.Nodes[0].Tags[1], tags[1].GetString());
        Assert.Equal(0, payload.GetProperty("nodes")[1].GetProperty("tags").GetArrayLength());
    }
}
