using System;
using System.IO;
using System.Text;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Targets <see cref="VaultService"/>'s content choke points directly against an encrypted,
/// unlocked session — note creation/write, the rename wikilink-rewrite cascade, and importing a
/// non-<c>.md</c> attachment. Same temp-directory-per-test pattern used across this project's other
/// <c>Services</c> tests.</summary>
public class VaultServiceEncryptionTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public VaultServiceEncryptionTests()
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
    public void ImportFile_IntoEncryptedSession_ProducesCiphertextOnDisk_AndDecryptsBackToOriginalBytes()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "pw-adjuntos");

        var sourcePath = Path.Combine(_storageDirectory, "source.bin");
        var originalBytes = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252, 0, 0, 9 };
        File.WriteAllBytes(sourcePath, originalBytes);

        vault.ImportFile(sourcePath, session.RootPath);
        var importedPath = Path.Combine(session.RootPath, "source.bin");

        var onDiskBytes = File.ReadAllBytes(importedPath);
        Assert.NotEqual(originalBytes, onDiskBytes);

        var decrypted = vault.EncryptionService.Decrypt(session.RootPath, onDiskBytes);
        Assert.Equal(originalBytes, decrypted);
    }

    [Fact]
    public void RenameNote_WikilinkCascade_ReEncryptsTheOtherLinkedNote()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Enlaces privados", "pw-enlaces");

        var target = vault.CreateNote("Nota destino", session.RootPath);
        vault.WriteContent(target, "# Nota destino\n\ncontenido");

        var linker = vault.CreateNote("Nota origen", session.RootPath);
        vault.WriteContent(linker, "# Nota origen\n\nVer [[Nota destino]] para más.");

        var renamed = vault.RenameNote(target, "Nota destino renombrada");

        // The renamed note itself, and the note linking to it, must both still round-trip correctly.
        Assert.Equal("# Nota destino\n\ncontenido", vault.ReadContent(renamed));

        var linkerContentAfter = vault.ReadContent(linker);
        Assert.Contains("[[Nota destino renombrada]]", linkerContentAfter);

        // And the bytes actually on disk for the linking note must not be plaintext.
        var rawBytes = File.ReadAllBytes(linker.FullPath);
        var rawAsText = Encoding.UTF8.GetString(rawBytes);
        Assert.DoesNotContain("Nota destino renombrada", rawAsText);
    }

    [Fact]
    public void DeleteNote_ThenRestore_KeepsContentDecryptable()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Sesión con papelera", "pw-papelera");

        var note = vault.CreateNote("Nota protegida", session.RootPath);
        vault.WriteContent(note, "# Nota protegida\n\nsecreto");

        vault.DeleteNote(note);

        // While trashed, the note must still be recognized as belonging to the (unlocked) session — not
        // read back as garbage plain-text ciphertext.
        var trashed = Assert.Single(vault.GetTrashedNotes());
        Assert.Equal("# Nota protegida\n\nsecreto", vault.ReadContent(trashed));

        vault.RestoreNote(trashed);

        // Restored back under the session root, not flattened to the vault root, so it keeps resolving as
        // encrypted content going forward.
        Assert.StartsWith(session.RootPath, trashed.FullPath);
        Assert.Equal("# Nota protegida\n\nsecreto", vault.ReadContent(trashed));

        var rawBytes = File.ReadAllBytes(trashed.FullPath);
        Assert.DoesNotContain("secreto", Encoding.UTF8.GetString(rawBytes));
    }

    [Fact]
    public void DeleteFile_ThenRestore_KeepsBytesDecryptable()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Adjuntos con papelera", "pw-adjuntos-papelera");

        var sourcePath = Path.Combine(_storageDirectory, "source.bin");
        var originalBytes = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
        File.WriteAllBytes(sourcePath, originalBytes);
        var imported = vault.ImportFile(sourcePath, session.RootPath);

        var file = new enki.Models.VaultFile { FullPath = imported.FullPath };
        vault.DeleteFile(file);

        var trashed = Assert.Single(vault.GetTrashedFiles());
        Assert.Equal(originalBytes, vault.ReadFileBytes(trashed.FullPath));

        vault.RestoreFile(trashed);

        Assert.StartsWith(session.RootPath, trashed.FullPath);
        Assert.Equal(originalBytes, vault.ReadFileBytes(trashed.FullPath));
    }

    [Fact]
    public void GetTrashedNotes_ExcludesItemsFromALockedSession_InsteadOfThrowing()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Sesión que se bloquea", "pw-bloqueo");

        var note = vault.CreateNote("Nota a bloquear", session.RootPath);
        vault.WriteContent(note, "# Nota a bloquear\n\ncontenido");
        vault.DeleteNote(note);

        sessions.Lock(session);

        var trashedWhileLocked = vault.GetTrashedNotes();
        Assert.Empty(trashedWhileLocked);

        Assert.True(sessions.Unlock(session, "pw-bloqueo"));
        var trashedAfterUnlock = Assert.Single(vault.GetTrashedNotes());
        Assert.Equal("# Nota a bloquear\n\ncontenido", vault.ReadContent(trashedAfterUnlock));
    }
}
