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

    /// <summary>Null for a plain-text session (the default, and every session created before this field
    /// existed — old manifests deserialize with this null, so nothing migrates). Present only for a
    /// password-protected session. Always readable without unlocking (the sidebar needs the session's
    /// name/note count even while locked), so it must never hold the password, a recovery code in the
    /// clear, or the raw content-encryption key — only KDF salts and AES-GCM-wrapped copies of that key.
    /// See <see cref="Services.SessionEncryptionService"/> for how these fields are produced/consumed.</summary>
    public SessionEncryptionInfo? Encryption { get; set; }
}

/// <summary>"Envelope encryption" metadata for a session: a single random Data Encryption Key (DEK)
/// encrypts the session's note/attachment content, and is itself wrapped once per secret that should be
/// able to recover it — the user's password, plus 5 independent recovery codes generated alongside it.
/// Any one of the six slots below unwraps the same DEK.</summary>
public sealed class SessionEncryptionInfo
{
    /// <summary>Bumped only if the wrap/KDF scheme itself ever changes, so a future version can detect
    /// and reject (or migrate) a manifest written by an older/newer scheme instead of guessing.</summary>
    public int FormatVersion { get; set; } = 1;

    public KeySlot Password { get; set; } = new();

    /// <summary>Always exactly 5 entries, created together at session-creation time and never regenerated
    /// implicitly — only a successful password reset marks one <see cref="KeySlot.Used"/>.</summary>
    public List<KeySlot> RecoveryCodes { get; set; } = [];
}

/// <summary>One password- or recovery-code-derived wrapping of the session's DEK: the PBKDF2 salt and
/// iteration count used to derive a key-wrapping key from that secret, plus the DEK itself encrypted
/// with AES-GCM under that wrapping key (nonce + ciphertext + tag, base64-encoded together) — wrapping
/// fails loudly on the wrong secret (AEAD tag mismatch), so no separate "is this the right password"
/// check is needed anywhere this is consumed.</summary>
public sealed class KeySlot
{
    public string SaltBase64 { get; set; } = string.Empty;
    public int Iterations { get; set; } = 210_000;
    public string WrappedDekBase64 { get; set; } = string.Empty;

    /// <summary>Only meaningful for a <see cref="SessionEncryptionInfo.RecoveryCodes"/> entry — the
    /// <see cref="SessionEncryptionInfo.Password"/> slot's Used is always false. Single-use, matching the
    /// "backup code" convention this closely resembles: a code that already reset the password once
    /// can't reset it again.</summary>
    public bool Used { get; set; }
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
