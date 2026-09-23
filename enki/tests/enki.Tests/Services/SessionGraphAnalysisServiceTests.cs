using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class SessionGraphAnalysisServiceTests
{
    private static SessionGraphNode Node(string session, string path) => new()
    {
        SessionId = session, SessionName = session, RelativePath = path, Title = path
    };

    private static SessionGraphEdge Edge(SessionGraphNode from, SessionGraphNode to) => new()
    {
        FromSessionId = from.SessionId, ToSessionId = to.SessionId,
        FromRelativePath = from.RelativePath, ToRelativePath = to.RelativePath
    };

    [Fact]
    public void Analyze_UsesBothDirections_DeduplicatesNeighbors_AndIgnoresSelfAndMissingLinks()
    {
        var a = Node("a", "A.md");
        var b = Node("b", "B.md");
        var orphan = Node("b", "C.md");
        var graph = new SessionGraph
        {
            Nodes = [a, b, orphan],
            Edges = [Edge(a, b), Edge(b, a), Edge(a, a), Edge(orphan, orphan), Edge(orphan, Node("a", "Missing.md"))]
        };

        SessionGraphAnalysisService.Analyze(graph);

        Assert.Equal(1, a.ConnectionCount);
        Assert.Equal(1, b.ConnectionCount);
        Assert.True(a.HasCrossSessionLinks);
        Assert.True(b.HasCrossSessionLinks);
        Assert.Equal(a.GlobalId, b.ClusterId);
        Assert.Equal(2, b.ClusterSize);
        Assert.Equal(0, orphan.ConnectionCount);
        Assert.Equal(1, orphan.ClusterSize);
        Assert.False(orphan.HasCrossSessionLinks);
    }

    [Fact]
    public void Analyze_RecomputesComponentsAfterAnEdgeIsRemoved_WithoutStaleMetadata()
    {
        var a = Node("a", "A.md");
        var b = Node("b", "B.md");
        var graph = new SessionGraph { Nodes = [b, a], Edges = [Edge(a, b)] };
        SessionGraphAnalysisService.Analyze(graph);
        Assert.Equal(a.GlobalId, b.ClusterId);

        graph.Edges.Clear();
        SessionGraphAnalysisService.Analyze(graph);

        Assert.All(graph.Nodes, n =>
        {
            Assert.Equal(0, n.ConnectionCount);
            Assert.Equal(n.GlobalId, n.ClusterId);
            Assert.Equal(1, n.ClusterSize);
            Assert.False(n.HasCrossSessionLinks);
        });
    }

    [Fact]
    public void Analyze_LongChain_UsesIterativeTraversal()
    {
        var graph = new SessionGraph { Nodes = Enumerable.Range(0, 10000).Select(i => Node("a", $"{i:D5}.md")).ToList() };
        graph.Edges = graph.Nodes.Zip(graph.Nodes.Skip(1), Edge).ToList();

        SessionGraphAnalysisService.Analyze(graph);

        Assert.All(graph.Nodes, n => Assert.Equal(10000, n.ClusterSize));
        Assert.Single(graph.Nodes.Select(n => n.ClusterId).Distinct());
        Assert.Equal(2, graph.Nodes[5000].ConnectionCount);
    }
}
