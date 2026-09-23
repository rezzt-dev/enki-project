using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Envelope encryption for password-protected sessions: a random per-session Data Encryption Key
/// (DEK) encrypts note/attachment content, and is itself wrapped once per secret that should be able to
/// recover it — the user's password, plus 5 independent recovery codes generated alongside it (see
/// <see cref="SessionEncryptionInfo"/>). Deliberately has no dependency on <see cref="VaultService"/> or
/// <see cref="SessionService"/> — it's pure cryptography plus one in-memory cache of currently-unlocked
/// DEKs, injected into both of those services instead of depending on either, so nothing here needs to
/// know what a "session" or a "vault" is beyond a root path used as a cache key.
///
/// <para>All wrapping/encryption uses AES-GCM (authenticated — a wrong password/code fails via AEAD tag
/// mismatch, no separate correctness check needed) with PBKDF2-SHA256 deriving the wrapping key from a
/// password or recovery code. Content is only ever decrypted into memory here, on demand, for a session
/// whose DEK is currently unlocked; nothing is cached to disk.</para></summary>
public sealed class SessionEncryptionService
{
    private const int DekSizeBytes = 32; // AES-256
    private const int SaltSizeBytes = 16;
    private const int NonceSizeBytes = 12; // AES-GCM standard nonce size
    private const int TagSizeBytes = 16;
    private const int DefaultIterations = 210_000; // OWASP 2023 guidance for PBKDF2-SHA256
    private const int RecoveryCodeLength = 20;
    private const int RecoveryCodeCount = 5;

    /// <summary>The only <see cref="SessionEncryptionInfo.FormatVersion"/> this build knows how to unwrap.
    /// The field was declared from day one precisely so an unknown scheme could be rejected instead of
    /// guessed at, and until now nothing ever read it. Only 1 has ever been written, so demanding exactly 1
    /// locks out no existing manifest.</summary>
    private const int SupportedFormatVersion = 1;

    /// <summary>Bounds on the PBKDF2 cost factor read out of <c>.session.json</c>, which is a file on disk
    /// and therefore untrusted input, not a constant.
    ///
    /// <para><b>Por qué el techo.</b> Un manifiesto con <c>"Iterations": 2000000000</c> —corrupción, una
    /// edición a mano, un disco con bits volteados— congelaba el hilo de desbloqueo durante horas, sin
    /// forma de cancelar. PBKDF2-SHA256 va del orden de 2–3 millones de iteraciones por segundo y por
    /// núcleo en un equipo de escritorio actual, así que 10 millones son unos pocos segundos: lo peor que
    /// puede costar un valor disparatado que aun así esté dentro del rango.</para>
    ///
    /// <para><b>Por qué el suelo es tan bajo.</b> No es una política de seguridad, es un detector de
    /// basura. Toda versión de Enki ha escrito siempre <see cref="DefaultIterations"/> (210.000), así que
    /// cualquier suelo por debajo de esa cifra deja pasar el 100 % de los manifiestos reales; subirlo hasta
    /// "lo que hoy se considera seguro" solo serviría para dejar a alguien fuera de sus propios datos
    /// cifrados, sin ninguna vía de recuperación, por una decisión que tomó el programa y no él. 1.000 está
    /// muy por encima de los valores que delatan corrupción (0, negativos) y muy por debajo de cualquier
    /// configuración real, que es justo lo que se quiere distinguir.</para></summary>
    private const int MinIterations = 1_000;
    private const int MaxIterations = 10_000_000;

    /// <summary>Longitud mínima aceptable de los dos campos base64 de una ranura, medida sobre lo que
    /// <see cref="WrapWithSecret"/> produce: la sal son exactamente <see cref="SaltSizeBytes"/> bytes, y la
    /// DEK envuelta es el formato de <see cref="EncryptBytes"/> (nonce + ciphertext + tag) sobre una clave
    /// de <see cref="DekSizeBytes"/>. Se acepta "igual o mayor" y no "exactamente": mayor lo rechazará de
    /// todas formas el AEAD, y un rechazo por tamaño en esta capa solo tiene sentido para lo que no puede
    /// descifrarse de ninguna manera.</summary>
    private const int MinWrappedDekBytes = NonceSizeBytes + DekSizeBytes + TagSizeBytes;

    // No 0/O/1/I/l — avoids characters a user could misread when copying a code down by hand.
    private const string RecoveryCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _unlockedDeks = new(PathComparer());

    private static StringComparer PathComparer() =>
        VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Generates a fresh DEK and wraps it under the given password plus 5 freshly-generated
    /// recovery codes — called once, from <see cref="SessionService"/> when a session is first created as
    /// password-protected. The plaintext recovery codes exist only in <paramref name="recoveryCodes"/>;
    /// nothing here persists them anywhere.</summary>
    public static SessionEncryptionInfo CreateEncryptionInfo(string password, out IReadOnlyList<string> recoveryCodes)
    {
        var dek = RandomNumberGenerator.GetBytes(DekSizeBytes);
        try
        {
            var info = new SessionEncryptionInfo { Password = WrapWithSecret(dek, password) };

            var codes = new List<string>(RecoveryCodeCount);
            var slots = new List<KeySlot>(RecoveryCodeCount);
            for (var i = 0; i < RecoveryCodeCount; i++)
            {
                var code = GenerateRecoveryCode();
                codes.Add(code);
                slots.Add(WrapWithSecret(dek, code));
            }
            info.RecoveryCodes = slots;

            recoveryCodes = codes;
            return info;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Unwraps <paramref name="info"/>'s DEK using the password slot and, on success, caches it
    /// as unlocked for <paramref name="sessionRootPath"/>. Returns false on a wrong password — never
    /// throws for that case. Throws <see cref="SessionManifestCorruptException"/> when the manifest itself
    /// is unusable, which is a different answer and must reach the user as one.</summary>
    public bool TryUnlockWithPassword(string sessionRootPath, SessionEncryptionInfo info, string password)
    {
        ValidateFormatVersion(info);
        var dek = TryUnwrapSlot(info.Password, password);
        if (dek is null) return false;

        StoreUnlocked(sessionRootPath, dek);
        return true;
    }

    /// <summary>Tries every not-yet-used recovery slot against <paramref name="recoveryCode"/> — the
    /// caller has no way to know in advance which of the 5 codes the user is holding. On success, caches
    /// the recovered DEK as unlocked (same as <see cref="TryUnlockWithPassword"/>) and reports which slot
    /// matched via <paramref name="usedSlotIndex"/>, so a subsequent <see cref="ResetPassword"/> call can
    /// mark that specific slot consumed.
    ///
    /// <para>A slot whose metadata is corrupt is skipped rather than fatal: the 5 slots are independent
    /// wrappings of the same DEK, so one damaged entry must not cost the user the other four codes, which
    /// may be the only way back into their notes. The corruption is only reported —as
    /// <see cref="SessionManifestCorruptException"/>— if no slot at all could be tried successfully, since
    /// at that point "wrong code" would be the wrong thing to tell them.</para></summary>
    public bool TryUnlockWithRecoveryCode(string sessionRootPath, SessionEncryptionInfo info, string recoveryCode, out int? usedSlotIndex)
    {
        ValidateFormatVersion(info);
        SessionManifestCorruptException? firstCorruption = null;

        for (var i = 0; i < info.RecoveryCodes.Count; i++)
        {
            var slot = info.RecoveryCodes[i];
            if (slot.Used) continue;

            byte[]? dek;
            try
            {
                dek = TryUnwrapSlot(slot, recoveryCode);
            }
            catch (SessionManifestCorruptException ex)
            {
                LogService.Warning($"Ranura de recuperación {i} ilegible en una sesión protegida", ex);
                firstCorruption ??= ex;
                continue;
            }

            if (dek is null) continue;

            StoreUnlocked(sessionRootPath, dek);
            usedSlotIndex = i;
            return true;
        }

        usedSlotIndex = null;
        if (firstCorruption is not null) throw firstCorruption;
        return false;
    }

    /// <summary>Rewraps the DEK already recovered by the <see cref="TryUnlockWithRecoveryCode"/> call that
    /// must precede this one, under a fresh salt for <paramref name="newPassword"/>, and marks the
    /// consumed recovery slot used — the other 4 codes stay valid for future resets. Mutates and returns
    /// <paramref name="info"/> in place; the caller persists it back to the session manifest.</summary>
    public SessionEncryptionInfo ResetPassword(string sessionRootPath, SessionEncryptionInfo info, int usedSlotIndex, string newPassword)
    {
        var dek = GetUnlockedDek(sessionRootPath);
        info.Password = WrapWithSecret(dek, newPassword);
        info.RecoveryCodes[usedSlotIndex].Used = true;
        return info;
    }

    public void Lock(string sessionRootPath)
    {
        lock (_lock)
        {
            if (_unlockedDeks.Remove(sessionRootPath, out var dek))
                CryptographicOperations.ZeroMemory(dek);
        }
    }

    public bool IsUnlocked(string sessionRootPath)
    {
        lock (_lock)
        {
            return _unlockedDeks.ContainsKey(sessionRootPath);
        }
    }

    /// <summary>Clears every unlocked DEK — called on app shutdown so nothing decrypted survives the
    /// process, and available for an explicit "lock everything" action if the UI ever wants one.</summary>
    public void LockAll()
    {
        lock (_lock)
        {
            foreach (var dek in _unlockedDeks.Values) CryptographicOperations.ZeroMemory(dek);
            _unlockedDeks.Clear();
        }
    }

    /// <summary>Encrypts arbitrary content (note text as UTF-8 bytes, or an attachment's raw bytes) under
    /// the DEK currently unlocked for <paramref name="sessionRootPath"/>. Throws
    /// <see cref="SessionLockedException"/> if that session isn't unlocked — callers must not treat that
    /// as "empty content", only as "access blocked".</summary>
    public byte[] Encrypt(string sessionRootPath, ReadOnlySpan<byte> plaintext) =>
        EncryptBytes(GetUnlockedDek(sessionRootPath), plaintext);

    /// <summary>Inverse of <see cref="Encrypt"/>. Throws <see cref="SessionLockedException"/> if locked,
    /// or <see cref="CryptographicException"/> if the payload's AEAD tag doesn't match (corrupted or
    /// tampered file) — the two failures are deliberately distinguishable so a caller can log/report them
    /// differently instead of collapsing both to the same generic error.</summary>
    public byte[] Decrypt(string sessionRootPath, ReadOnlySpan<byte> ciphertext) =>
        DecryptBytes(GetUnlockedDek(sessionRootPath), ciphertext);

    private byte[] GetUnlockedDek(string sessionRootPath)
    {
        lock (_lock)
        {
            if (_unlockedDeks.TryGetValue(sessionRootPath, out var dek)) return dek;
        }
        throw new SessionLockedException(sessionRootPath);
    }

    private void StoreUnlocked(string sessionRootPath, byte[] dek)
    {
        lock (_lock)
        {
            _unlockedDeks[sessionRootPath] = dek;
        }
    }

    private static KeySlot WrapWithSecret(byte[] dek, string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var wrappingKey = DeriveWrappingKey(secret, salt, DefaultIterations);
        try
        {
            return new KeySlot
            {
                SaltBase64 = Convert.ToBase64String(salt),
                Iterations = DefaultIterations,
                WrappedDekBase64 = Convert.ToBase64String(EncryptBytes(wrappingKey, dek))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    /// <summary>Null means the secret was wrong (AEAD tag mismatch) — deliberately not an exception, since
    /// "wrong password" is an expected, routine outcome here, not an error condition. A
    /// <see cref="SessionManifestCorruptException"/> means the opposite: nothing about this slot could be
    /// used, whatever the user types.</summary>
    private static byte[]? TryUnwrapSlot(KeySlot slot, string secret)
    {
        // Todo lo que sigue sale de .session.json, que es un archivo del disco y no una constante del
        // programa: se valida entero antes de derivar nada. Antes no se validaba nada, así que un base64
        // roto lanzaba una FormatException que el catch de abajo no cubría y que salía por encima de
        // TryUnlockWithPassword —documentado como "nunca lanza"— hasta tumbar la app, y un "Iterations"
        // enorme se pagaba íntegro antes de descubrir que el manifiesto no servía
        // (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-06).
        var salt = DecodeBase64(slot.SaltBase64, nameof(slot.SaltBase64));
        if (salt.Length < SaltSizeBytes)
            throw new SessionManifestCorruptException(
                $"the KDF salt is {salt.Length} bytes, below the {SaltSizeBytes} this scheme writes");

        var wrapped = DecodeBase64(slot.WrappedDekBase64, nameof(slot.WrappedDekBase64));
        if (wrapped.Length < MinWrappedDekBytes)
            throw new SessionManifestCorruptException(
                $"the wrapped key is {wrapped.Length} bytes, below the {MinWrappedDekBytes} this scheme writes");

        if (slot.Iterations is < MinIterations or > MaxIterations)
            throw new SessionManifestCorruptException(
                $"the PBKDF2 iteration count {slot.Iterations} is outside the accepted "
                + $"{MinIterations}–{MaxIterations} range");

        var wrappingKey = DeriveWrappingKey(secret, salt, slot.Iterations);
        try
        {
            return DecryptBytes(wrappingKey, wrapped);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    private static byte[] DecodeBase64(string value, string fieldName)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new SessionManifestCorruptException($"{fieldName} is not valid base64 ({ex.Message})");
        }
    }

    private static void ValidateFormatVersion(SessionEncryptionInfo info)
    {
        if (info.FormatVersion != SupportedFormatVersion)
            throw new SessionManifestCorruptException(
                $"format version {info.FormatVersion} is not supported "
                + $"(this build writes and reads {SupportedFormatVersion})");
    }

    private static byte[] DeriveWrappingKey(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, DekSizeBytes);

    private static byte[] EncryptBytes(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using (var aes = new AesGcm(key, TagSizeBytes))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(result.AsSpan(0));
        ciphertext.CopyTo(result.AsSpan(NonceSizeBytes));
        tag.CopyTo(result.AsSpan(NonceSizeBytes + ciphertext.Length));
        return result;
    }

    private static byte[] DecryptBytes(byte[] key, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Ciphertext payload is too short to contain a nonce and tag.");

        var nonce = payload[..NonceSizeBytes];
        var ciphertextLength = payload.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = payload.Slice(NonceSizeBytes, ciphertextLength);
        var tag = payload[^TagSizeBytes..];

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static string GenerateRecoveryCode()
    {
        Span<char> buffer = stackalloc char[RecoveryCodeLength];
        for (var i = 0; i < RecoveryCodeLength; i++)
            buffer[i] = RecoveryCodeAlphabet[RandomNumberGenerator.GetInt32(RecoveryCodeAlphabet.Length)];
        return new string(buffer);
    }
}
