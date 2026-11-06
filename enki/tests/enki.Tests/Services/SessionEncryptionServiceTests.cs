using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="SessionEncryptionService"/> in isolation — pure crypto plus the in-memory
/// unlocked-DEK cache, no filesystem involved. See docs/agent-docs (Sesiones protegidas con contraseña)
/// for the envelope-encryption design this implements: one random DEK, wrapped once per password/recovery
/// code, so any one of the six secrets can recover it.</summary>
public class SessionEncryptionServiceTests
{
    private const string RootPath = "/fake/vault/.sessions/secret";

    private static SessionEncryptionService CreateService() => new();

    [Fact]
    public void CreateEncryptionInfo_Produces5UniqueRecoveryCodesOfLength20()
    {
        SessionEncryptionService.CreateEncryptionInfo("hunter2-password", out var codes);

        Assert.Equal(5, codes.Count);
        Assert.All(codes, c => Assert.Equal(20, c.Length));
        Assert.Equal(5, codes.Distinct().Count());
        Assert.All(codes, c => Assert.DoesNotContain(c, "0O1Il".Select(ch => ch.ToString())));
    }

    [Fact]
    public void CreateEncryptionInfo_StoresNoPlaintextSecrets()
    {
        var info = SessionEncryptionService.CreateEncryptionInfo("correct horse battery staple", out var codes);

        Assert.DoesNotContain("correct horse battery staple", info.Password.WrappedDekBase64);
        foreach (var code in codes)
        {
            Assert.All(info.RecoveryCodes, slot => Assert.DoesNotContain(code, slot.WrappedDekBase64));
        }
    }

    [Fact]
    public void TryUnlockWithPassword_CorrectPassword_Succeeds()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("my-password", out _);

        Assert.True(service.TryUnlockWithPassword(RootPath, info, "my-password"));
        Assert.True(service.IsUnlocked(RootPath));
    }

    [Fact]
    public void TryUnlockWithPassword_WrongPassword_FailsWithoutThrowing()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("my-password", out _);

        Assert.False(service.TryUnlockWithPassword(RootPath, info, "wrong-password"));
        Assert.False(service.IsUnlocked(RootPath));
    }

    [Fact]
    public void EveryRecoveryCode_IndependentlyUnlocksTheSameDek()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("my-password", out var codes);

        // Unlock via the password, encrypt a known plaintext.
        Assert.True(service.TryUnlockWithPassword(RootPath, info, "my-password"));
        var plaintext = Encoding.UTF8.GetBytes("# Nota secreta\n\nContenido sensible.");
        var ciphertext = service.Encrypt(RootPath, plaintext);
        service.Lock(RootPath);

        // Every one of the 5 codes should recover the exact same DEK and decrypt correctly.
        foreach (var code in codes)
        {
            var freshInfo = Clone(info);
            Assert.True(service.TryUnlockWithRecoveryCode(RootPath, freshInfo, code, out var slotIndex));
            Assert.NotNull(slotIndex);

            var decrypted = service.Decrypt(RootPath, ciphertext);
            Assert.Equal(plaintext, decrypted);
            service.Lock(RootPath);
        }
    }

    [Fact]
    public void TryUnlockWithRecoveryCode_WrongCode_FailsCleanly()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("my-password", out _);

        Assert.False(service.TryUnlockWithRecoveryCode(RootPath, info, "NOTAREALRECOVERYCODEXX", out var slotIndex));
        Assert.Null(slotIndex);
        Assert.False(service.IsUnlocked(RootPath));
    }

    [Fact]
    public void ResetPassword_ConsumesOnlyTheUsedSlot_OldPasswordStopsWorking()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("old-password", out var codes);

        Assert.True(service.TryUnlockWithRecoveryCode(RootPath, info, codes[2], out var usedSlot));
        info = service.ResetPassword(RootPath, info, usedSlot!.Value, "new-password");

        Assert.True(info.RecoveryCodes[2].Used);
        Assert.DoesNotContain(info.RecoveryCodes, s => s != info.RecoveryCodes[2] && s.Used);

        service.Lock(RootPath);
        Assert.False(service.TryUnlockWithPassword(RootPath, info, "old-password"));
        Assert.True(service.TryUnlockWithPassword(RootPath, info, "new-password"));
    }

    [Fact]
    public void ResetPassword_UsedRecoveryCode_NoLongerUnlocks()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("old-password", out var codes);

        Assert.True(service.TryUnlockWithRecoveryCode(RootPath, info, codes[0], out var usedSlot));
        info = service.ResetPassword(RootPath, info, usedSlot!.Value, "new-password");
        service.Lock(RootPath);

        Assert.False(service.TryUnlockWithRecoveryCode(RootPath, info, codes[0], out var retryIndex));
        Assert.Null(retryIndex);

        // The other 4 codes remain usable for a future reset.
        Assert.True(service.TryUnlockWithRecoveryCode(RootPath, info, codes[1], out var otherIndex));
        Assert.Equal(1, otherIndex);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsArbitraryBytes()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("pw", out _);
        service.TryUnlockWithPassword(RootPath, info, "pw");

        var plaintext = RandomNumberGenerator.GetBytes(4096);
        var ciphertext = service.Encrypt(RootPath, plaintext);
        var roundTripped = service.Decrypt(RootPath, ciphertext);

        Assert.Equal(plaintext, roundTripped);
        Assert.NotEqual(plaintext, ciphertext[12..^16].ToArray()); // ciphertext body must not equal plaintext
    }

    [Fact]
    public void Decrypt_WhileLocked_ThrowsSessionLockedException()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("pw", out _);
        service.TryUnlockWithPassword(RootPath, info, "pw");
        var ciphertext = service.Encrypt(RootPath, "hola"u8.ToArray());
        service.Lock(RootPath);

        Assert.Throws<SessionLockedException>(() => service.Decrypt(RootPath, ciphertext));
    }

    [Fact]
    public void Encrypt_WithoutEverUnlocking_ThrowsSessionLockedException()
    {
        var service = CreateService();
        Assert.Throws<SessionLockedException>(() => service.Encrypt(RootPath, "hola"u8.ToArray()));
    }

    [Fact]
    public void LockAll_ClearsEveryUnlockedSession()
    {
        var service = CreateService();
        var infoA = SessionEncryptionService.CreateEncryptionInfo("pw-a", out _);
        var infoB = SessionEncryptionService.CreateEncryptionInfo("pw-b", out _);
        service.TryUnlockWithPassword(RootPath, infoA, "pw-a");
        service.TryUnlockWithPassword(RootPath + "-b", infoB, "pw-b");

        service.LockAll();

        Assert.False(service.IsUnlocked(RootPath));
        Assert.False(service.IsUnlocked(RootPath + "-b"));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var service = CreateService();
        var info = SessionEncryptionService.CreateEncryptionInfo("pw", out _);
        service.TryUnlockWithPassword(RootPath, info, "pw");
        var ciphertext = service.Encrypt(RootPath, "contenido original"u8.ToArray());

        ciphertext[^1] ^= 0xFF; // flip a bit inside the GCM tag

        // AesGcm surfaces a CryptographicException subtype (AuthenticationTagMismatchException on newer
        // runtimes) for a bad AEAD tag — ThrowsAny matches either without pinning to one concrete type.
        Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(RootPath, ciphertext));
    }

    /// <summary>Deep-enough clone for these tests: a fresh <see cref="SessionEncryptionInfo"/> instance
    /// with the same salts/wrapped keys, simulating "read the manifest back from disk" between unlock
    /// attempts instead of reusing the exact same in-memory object every time.</summary>
    private static SessionEncryptionInfo Clone(SessionEncryptionInfo info) => new()
    {
        FormatVersion = info.FormatVersion,
        Password = new KeySlot
        {
            SaltBase64 = info.Password.SaltBase64,
            Iterations = info.Password.Iterations,
            WrappedDekBase64 = info.Password.WrappedDekBase64,
            Used = info.Password.Used
        },
        RecoveryCodes = info.RecoveryCodes.Select(s => new KeySlot
        {
            SaltBase64 = s.SaltBase64,
            Iterations = s.Iterations,
            WrappedDekBase64 = s.WrappedDekBase64,
            Used = s.Used
        }).ToList()
    };
}
