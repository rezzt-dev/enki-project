using System;

namespace enki.Models;

/// <summary>Thrown when content under an encrypted session is read or written while that session has no
/// unlocked Data Encryption Key available (see <see cref="Services.SessionEncryptionService"/>). Kept as
/// its own type — distinct from a generic exception or an empty-string sentinel — so
/// <see cref="Services.VaultService"/>'s read/write choke points can tell "this session is locked" apart
/// from "this file doesn't exist" or "this note has no content", all of which would otherwise collapse to
/// the same observable "" result and let a locked session's absence of content pass as real (empty)
/// content instead of surfacing as blocked access.</summary>
public sealed class SessionLockedException : Exception
{
    public SessionLockedException(string sessionRootPath)
        : base($"Session at '{sessionRootPath}' is locked.")
    {
        SessionRootPath = sessionRootPath;
    }

    public string SessionRootPath { get; }
}
