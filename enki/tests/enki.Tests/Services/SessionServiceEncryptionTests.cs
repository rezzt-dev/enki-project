using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers the password-protected-session lifecycle end to end — <see cref="SessionService"/>'s
/// create/unlock/lock/reset methods talking to a real <see cref="VaultService"/> against a real temp-
/// directory vault, so these tests exercise the actual on-disk encryption
/// (<see cref="SessionEncryptionService"/> wired through both services), not just the pure-crypto layer
/// already covered by <c>SessionEncryptionServiceTests</c>. Same temp-directory-per-test pattern as
/// <see cref="SessionServiceTests"/>.</summary>
public class SessionServiceEncryptionTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionServiceEncryptionTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (VaultService Vault, SessionService Sessions) CreateServices()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return (vault, new SessionService(vault));
    }

    [Fact]
    public void CreateEncrypted_ManifestOnDisk_NeverContainsThePlaintextSecrets()
    {
        var (_, sessions) = CreateServices();

        var (session, codes) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");

        var manifestPath = Path.Combine(session.RootPath, ".session.json");
        var manifestJson = File.ReadAllText(manifestPath);

        Assert.DoesNotContain("super-secreta-123", manifestJson);
        foreach (var code in codes) Assert.DoesNotContain(code, manifestJson);
        Assert.True(session.IsEncrypted);
    }

    [Fact]
    public void CreateEncrypted_LeavesSessionUnlockedImmediately()
    {
        var (_, sessions) = CreateServices();

        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");

        Assert.True(sessions.IsUnlocked(session));
    }

    [Fact]
    public void WriteContent_UnderEncryptedSession_IsNotPlaintextOnDisk_ButReadsBackCorrectly()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");

        var note = vault.CreateNote("Nota secreta", session.RootPath);
        var plaintext = "# Nota secreta\n\nContenido que nadie más debería poder leer.";
        vault.WriteContent(note, plaintext);

        var rawBytes = File.ReadAllBytes(note.FullPath);
        var rawAsText = System.Text.Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("Contenido que nadie", rawAsText);

        Assert.Equal(plaintext, vault.ReadContent(note));
    }

    [Fact]
    public void Lock_ThenReadContent_ThrowsSessionLockedException()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");

        sessions.Lock(session);

        Assert.False(sessions.IsUnlocked(session));
        Assert.Throws<SessionLockedException>(() => vault.ReadContent(note));
    }

    [Fact]
    public void Unlock_WrongPassword_ReturnsFalse_SessionStaysLocked()
    {
        var (_, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        sessions.Lock(session);

        Assert.False(sessions.Unlock(session, "contraseña-incorrecta"));
        Assert.False(sessions.IsUnlocked(session));
    }

    [Fact]
    public void Unlock_CorrectPassword_AllowsReadingContentAgain()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido persistente");
        sessions.Lock(session);

        Assert.True(sessions.Unlock(session, "super-secreta-123"));
        Assert.Equal("contenido persistente", vault.ReadContent(note));
    }

    [Fact]
    public void LoadGraph_LockedSession_ReturnsEmptyGraph_UnlockedReturnsRealOne()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "super-secreta-123");
        vault.CreateNote("Nota secreta", session.RootPath);

        var unlockedGraph = sessions.LoadGraph(session);
        Assert.Single(unlockedGraph.Nodes);

        sessions.Lock(session);
        var lockedGraph = sessions.LoadGraph(session);
        Assert.Empty(lockedGraph.Nodes);
        Assert.Empty(lockedGraph.Edges);
    }

    [Fact]
    public void ResetPasswordWithRecoveryCode_EndToEnd_OldPasswordStopsWorking_NewOneWorks()
    {
        var (vault, sessions) = CreateServices();
        var (session, codes) = sessions.CreateEncrypted("Diario privado", "contraseña-vieja");
        var note = vault.CreateNote("Nota secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        sessions.Lock(session);

        var chosenCode = codes[3];
        Assert.True(sessions.ResetPasswordWithRecoveryCode(session, chosenCode, "contraseña-nueva"));

        // ResetPasswordWithRecoveryCode leaves the session unlocked.
        Assert.True(sessions.IsUnlocked(session));
        Assert.Equal("contenido", vault.ReadContent(note));

        sessions.Lock(session);
        Assert.False(sessions.Unlock(session, "contraseña-vieja"));
        Assert.True(sessions.Unlock(session, "contraseña-nueva"));

        // The consumed code no longer works for a future reset.
        sessions.Lock(session);
        Assert.False(sessions.ResetPasswordWithRecoveryCode(session, chosenCode, "otra-contraseña"));

        // A different, still-unused code does.
        Assert.True(sessions.ResetPasswordWithRecoveryCode(session, codes[0], "otra-contraseña"));
    }

    [Fact]
    public void Create_PlainSession_IsNeverEncrypted_AndUnaffectedByLockUnlock()
    {
        var (vault, sessions) = CreateServices();
        var session = sessions.Create("Sesión normal");

        Assert.False(session.IsEncrypted);
        Assert.True(sessions.IsUnlocked(session)); // an unencrypted session is always "unlocked"

        var note = vault.CreateNote("Nota", session.RootPath);
        vault.WriteContent(note, "texto plano de siempre");
        Assert.Equal("texto plano de siempre", File.ReadAllText(note.FullPath));
    }
}
