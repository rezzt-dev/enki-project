using System.Collections.Generic;

namespace enki.Models;

/// <summary>Resolved, render-ready view of a graph — every note currently on disk under one or more
/// session roots, plus every note living directly at the vault root outside any session, as nodes, with
/// the edges between them (derived from <c>[[wikilinks]]</c> and/or drawn manually), regardless of how
/// the underlying manifest is stored. A graph spans the whole vault at once (see
/// <c>SessionService.LoadGlobalGraph</c>) — nodes and edges always carry their owning
/// <see cref="SessionGraphNode.SessionId"/> so every group (a real session, or the vault-root pseudo-group
/// <see cref="RootGroupId"/>) shares one shape.</summary>
public sealed class SessionGraph
{
    /// <summary>Sentinel <see cref="SessionGraphNode.SessionId"/>/<see cref="SessionGraphEdge.FromSessionId"/>
    /// value for notes living directly at the vault root — outside every session folder, so they have no
    /// backing <c>Session</c>/<c>SessionManifest</c> of their own. Never collides with a real session id
    /// (those are <c>Guid.ToString("N")</c>, hex-only).</summary>
    public const string RootGroupId = "__vault_root__";

    public List<SessionGraphNode> Nodes { get; set; } = [];
    public List<SessionGraphEdge> Edges { get; set; } = [];
}

public sealed class SessionGraphNode
{
    public required string SessionId { get; set; }
    public required string SessionName { get; set; }
    public required string RelativePath { get; set; }
    public required string Title { get; set; }
    /// <summary>The session's display color (either custom from <see cref="SessionManifest.Color"/> or
    /// auto-derived by <see cref="Views.Sessions.NodeBrightness.ColorFor"/>). Null means the node's owning
    /// session has no explicit color. Stored per-node so the renderer's JSON payload carries it without
    /// needing a separate session-color lookup.</summary>
    public string? SessionColor { get; set; }

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
    /// <summary>Owning group of each endpoint — equal for an edge derived within one session (or within
    /// the vault-root group) exactly as before; different for a "global" edge, one whose two notes live in
    /// different sessions (or a session and the vault root), which only cross-scope wikilink resolution
    /// can produce (see <c>SessionService.LoadGlobalGraph</c>). A single shared <c>SessionId</c> used to be
    /// enough back when every edge stayed inside one session's own graph.</summary>
    public required string FromSessionId { get; set; }
    public required string ToSessionId { get; set; }
    public required string FromRelativePath { get; set; }
    public required string ToRelativePath { get; set; }
    public bool IsManual { get; set; }

    public string FromGlobalId => $"{FromSessionId}::{FromRelativePath}";
    public string ToGlobalId => $"{ToSessionId}::{ToRelativePath}";
}
