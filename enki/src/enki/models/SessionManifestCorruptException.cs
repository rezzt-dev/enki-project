using System;

namespace enki.Models;

/// <summary>Thrown when a password-protected session's <c>.session.json</c> carries encryption metadata
/// that can't be used at all — invalid base64, a salt or wrapped key of an impossible length, a PBKDF2
/// iteration count outside the accepted range, or a <see cref="SessionEncryptionInfo.FormatVersion"/> this
/// build doesn't know.
///
/// <para>Kept as its own type, next to <see cref="SessionLockedException"/> and for the same reason: "the
/// secret you typed is wrong" and "this manifest is damaged" must not collapse into the same
/// <c>false</c>. They look identical to a user staring at an unlock dialog, but one is fixed by typing
/// more carefully and the other never is — someone who is told "wrong password" about a corrupted
/// manifest will retype it forever instead of going for their backup. See
/// <see cref="Services.SessionEncryptionService"/> for the validation that raises this, and
/// .docs/auditoria-seguridad-y-fallos-criticos.md, SEC-06.</para>
///
/// <para>The message is English and technical on purpose — it goes to the log. What the user reads is
/// the translated <c>Sessions_Unlock_CorruptManifest</c>, resolved at the viewmodel.</para></summary>
public sealed class SessionManifestCorruptException : Exception
{
    public SessionManifestCorruptException(string reason)
        : base($"The session's encryption manifest is unusable: {reason}")
    {
    }
}
