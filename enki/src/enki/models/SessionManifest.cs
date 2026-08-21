using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>On-disk shape of a session's "fichero de localización" (<c>.session.json</c>), persisted at
/// the root of every session folder. Node positions are cached layout, not the source of truth for which
/// notes exist — <see cref="Services.SessionService"/> reconciles this against the files actually on disk
/// on every load. <see cref="ManualLinks"/> are the only relations a user has drawn explicitly; links
/// implied by <c>[[wikilinks]]</c> in note content are derived at load time and never stored here.</summary>
public sealed class SessionManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public bool IsDefault { get; set; }
    public List<SessionManifestNode> Nodes { get; set; } = [];
    public List<SessionManifestEdge> ManualLinks { get; set; } = [];
}

/// <summary>A note's cached 3D layout position, keyed by its path relative to the session root so the
/// manifest stays portable if the vault itself is moved or synced elsewhere.</summary>
public sealed class SessionManifestNode
{
    public string RelativePath { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

public sealed class SessionManifestEdge
{
    public string FromRelativePath { get; set; } = string.Empty;
    public string ToRelativePath { get; set; } = string.Empty;
}
