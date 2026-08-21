using System;

namespace enki.Models;

/// <summary>An isolated workspace inside the vault — its own folder tree of notes, separate from the
/// main vault root and from every other session, plus a "location file" (<see cref="SessionManifest"/>)
/// that lays its notes out as a graph.</summary>
public sealed class Session
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public int NoteCount { get; set; }

    /// <summary>The one session every vault is seeded with on creation — it can be renamed like any
    /// other, but never deleted, so there's always at least one workspace to fall back to.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Runtime mirror of <c>SessionManifest.Encryption is not null</c> — whether this session is
    /// password-protected. Doesn't say whether it's currently unlocked; see
    /// <see cref="Services.SessionEncryptionService.IsUnlocked"/> for that.</summary>
    public bool IsEncrypted { get; set; }
}
