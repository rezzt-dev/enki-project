using System.Collections.Generic;

namespace enki.Models;

/// <summary>Resolved, render-ready view of a graph — every note currently on disk under one or more
/// session roots as a node, plus the edges between them (derived from <c>[[wikilinks]]</c> and/or drawn
/// manually), regardless of how the underlying manifest is stored. A graph can span a single session or
/// every session in the vault at once (see <c>SessionService.LoadCombinedGraph</c>) — nodes and edges
/// always carry their owning <see cref="SessionGraphNode.SessionId"/> so the two cases share one shape.</summary>
public sealed class SessionGraph
{
    public List<SessionGraphNode> Nodes { get; set; } = [];
    public List<SessionGraphEdge> Edges { get; set; } = [];
}

public sealed class SessionGraphNode
{
    public required string SessionId { get; set; }
    public required string SessionName { get; set; }
    public required string RelativePath { get; set; }
    public required string Title { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool HasStoredPosition { get; set; }

    /// <summary>Vault-wide unique node id — plain relative paths collide across sessions since each
    /// session is its own note tree, so anything that crosses session boundaries (the graph's JS payload,
    /// click/position messages coming back from it) must key off this instead.</summary>
    public string GlobalId => $"{SessionId}::{RelativePath}";
}

public sealed class SessionGraphEdge
{
    public required string SessionId { get; set; }
    public required string FromRelativePath { get; set; }
    public required string ToRelativePath { get; set; }
    public bool IsManual { get; set; }

    public string FromGlobalId => $"{SessionId}::{FromRelativePath}";
    public string ToGlobalId => $"{SessionId}::{ToRelativePath}";
}
