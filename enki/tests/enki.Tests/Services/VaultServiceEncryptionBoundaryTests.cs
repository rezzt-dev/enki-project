using System;
using System.IO;
using System.Linq;
using System.Text;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre los tres agujeros que quedaban en las sesiones protegidas después de las fases de
/// cifrado ya cerradas — todos ellos silenciosos, todos ellos con el contenido del usuario de por medio:
///
/// <list type="number">
/// <item>mover una nota o un adjunto <em>cruzando</em> la frontera de una sesión cifrada movía los bytes
/// sin convertirlos, dejando texto plano dentro de una sesión protegida (que se abría en blanco) o
/// ciphertext fuera de ella (que se abría como basura binaria);</item>
/// <item>un <c>.session.json</c> ilegible hacía que la sesión se tratase como texto plano, así que la
/// siguiente escritura guardaba la nota <em>sin cifrar</em> dentro de la carpeta protegida;</item>
/// <item>enumerar las notas de una sesión bloqueada lanzaba <see cref="SessionLockedException"/> desde
/// dentro de un <c>Select</c> de LINQ en vez de excluirla, que es lo que hace el resto de la app.</item>
/// </list>
///
/// Mismo patrón de directorio temporal por test que <see cref="VaultServiceEncryptionTests"/>.</summary>
public class VaultServiceEncryptionBoundaryTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public VaultServiceEncryptionBoundaryTests()
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

    // ===================== Mover cruzando la frontera de cifrado =====================

    [Fact]
    public void MoveNote_FromVaultIntoEncryptedSession_EncryptsItInstead_OfLeavingPlaintextInside()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Publica", _vaultPath);
        vault.WriteContent(note, "# Publica\ncontenido que no debe perderse");

        vault.MoveNote(note, session.RootPath);

        Assert.Equal(session.RootPath, Path.GetDirectoryName(note.FullPath));
        Assert.Equal("# Publica\ncontenido que no debe perderse", vault.ReadContent(note));

        var onDisk = Encoding.UTF8.GetString(File.ReadAllBytes(note.FullPath));
        Assert.DoesNotContain("contenido que no debe perderse", onDisk);
    }

    [Fact]
    public void MoveNote_OutOfEncryptedSession_DecryptsIt_InsteadOfLeavingCiphertextInTheVault()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "# Secreta\ntexto en claro tras salir");

        vault.MoveNote(note, _vaultPath);

        Assert.Equal(_vaultPath, Path.GetDirectoryName(note.FullPath));
        Assert.Equal("# Secreta\ntexto en claro tras salir", vault.ReadContent(note));
        Assert.Contains("texto en claro tras salir", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public void MoveNote_BetweenTwoEncryptedSessions_ReEncryptsUnderTheDestinationKey()
    {
        var (vault, sessions) = CreateServices();
        var (origen, _) = sessions.CreateEncrypted("Origen", "contrasena-larga-1");
        var (destino, _) = sessions.CreateEncrypted("Destino", "contrasena-larga-2");
        var note = vault.CreateNote("Secreta", origen.RootPath);
        vault.WriteContent(note, "contenido compartido");

        vault.MoveNote(note, destino.RootPath);

        Assert.Equal("contenido compartido", vault.ReadContent(note));

        // Bloquear el destino la vuelve ilegible; bloquear el origen no cambia nada — prueba de que el
        // contenido está realmente re-envuelto bajo la DEK del destino y no sigue dependiendo del origen.
        sessions.Lock(origen);
        Assert.Equal("contenido compartido", vault.ReadContent(note));

        sessions.Lock(destino);
        Assert.Throws<SessionLockedException>(() => vault.ReadContent(note));
    }

    [Fact]
    public void MoveNote_IntoALockedSession_IsRefused_AndLeavesTheNoteWhereItWas()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        sessions.Lock(session);
        var note = vault.CreateNote("Publica", _vaultPath);
        vault.WriteContent(note, "contenido intacto");
        var originalPath = note.FullPath;

        Assert.Throws<SessionLockedException>(() => vault.MoveNote(note, session.RootPath));

        Assert.True(File.Exists(originalPath));
        Assert.Equal("contenido intacto", File.ReadAllText(originalPath));
        Assert.Empty(Directory.GetFiles(session.RootPath, "*.md"));
    }

    [Fact]
    public void MoveFile_AttachmentCrossingIntoAnEncryptedSession_IsEncryptedAtTheDestination()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");

        var sourcePath = Path.Combine(_storageDirectory, "adjunto.txt");
        File.WriteAllText(sourcePath, "bytes originales del adjunto");
        vault.ImportFile(sourcePath);
        var file = vault.GetFiles().Single();

        vault.MoveFile(file, session.RootPath);

        Assert.Equal(session.RootPath, Path.GetDirectoryName(file.FullPath));
        Assert.Equal("bytes originales del adjunto",
            Encoding.UTF8.GetString(vault.ReadFileBytes(file.FullPath)));
        Assert.DoesNotContain("bytes originales del adjunto",
            Encoding.UTF8.GetString(File.ReadAllBytes(file.FullPath)));
    }

    [Fact]
    public void MoveNote_WithinTheSameEncryptedSession_KeepsWorking_WithoutNeedlessReEncryption()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "sigue cifrada");
        var subfolder = VaultService.CreateFolder(session.RootPath, "Subcarpeta");

        vault.MoveNote(note, subfolder.FullPath);

        Assert.Equal(subfolder.FullPath, Path.GetDirectoryName(note.FullPath));
        Assert.Equal("sigue cifrada", vault.ReadContent(note));
    }

    // ===================== Manifiesto ilegible: fallar cerrado =====================

    [Fact]
    public void WriteContent_WithAnUnreadableManifest_BlocksTheWrite_InsteadOfSavingPlaintext()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "contenido original");

        File.WriteAllText(Path.Combine(session.RootPath, ".session.json"), "{ esto no es json valido");

        Assert.Throws<SessionLockedException>(() => vault.WriteContent(note, "contenido nuevo"));
        Assert.DoesNotContain("contenido nuevo", File.ReadAllText(note.FullPath));
    }

    [Fact]
    public void ReadContent_WithAnUnreadableManifest_BlocksTheRead_InsteadOfReturningCiphertextAsText()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "contenido original");

        File.WriteAllText(Path.Combine(session.RootPath, ".session.json"), "{ esto no es json valido");

        // Servicios nuevos sobre el mismo vault = arrancar la app otra vez: ni caché de notas en memoria
        // ni DEK desbloqueada, que es el estado en el que un usuario se encuentra el manifiesto roto.
        var (freshVault, _) = CreateServices();

        // El bloqueo salta ya al leer la nota del disco (GetNote la lee para extraer título y preview),
        // que es exactamente el punto en el que antes se devolvía ciphertext como si fuera texto.
        Assert.Throws<SessionLockedException>(() => freshVault.ReadContent(freshVault.GetNote(note.FullPath)!));
    }

    [Fact]
    public void RepairingTheManifest_RestoresAccess_BecauseTheFailureIsNeverCached()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "contenido original");

        var manifestPath = Path.Combine(session.RootPath, ".session.json");
        var goodManifest = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, "{ roto");

        var (freshVault, freshSessions) = CreateServices();
        Assert.Throws<SessionLockedException>(() => freshVault.ReadContent(freshVault.GetNote(note.FullPath)!));

        File.WriteAllText(manifestPath, goodManifest);
        var repaired = freshSessions.List().Single(s => s.Name == "Privada");
        Assert.True(freshSessions.Unlock(repaired, "contrasena-larga-1"));
        Assert.Equal("contenido original", freshVault.ReadContent(freshVault.GetNote(note.FullPath)!));
    }

    [Fact]
    public void AFolderUnderSessionsWithNoManifestAtAll_IsStillTreatedAsPlainText()
    {
        var (vault, _) = CreateServices();
        var orphan = Path.Combine(_vaultPath, ".sessions", "huerfana");
        Directory.CreateDirectory(orphan);

        var note = vault.CreateNote("Suelta", orphan);
        vault.WriteContent(note, "sin manifiesto, sin cifrado");

        Assert.Equal("sin manifiesto, sin cifrado", File.ReadAllText(note.FullPath));
    }

    // ===================== Sesión bloqueada: excluir, no reventar =====================

    [Fact]
    public void GetAllNotesRecursive_OverALockedSession_ReturnsEmpty_InsteadOfThrowing()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        vault.WriteContent(vault.CreateNote("Secreta", session.RootPath), "contenido");
        sessions.Lock(session);

        Assert.Empty(vault.GetAllNotesRecursive(session.RootPath));
    }

    [Fact]
    public void GetNotes_OverALockedSession_ReturnsEmpty_InsteadOfThrowing()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        vault.WriteContent(vault.CreateNote("Secreta", session.RootPath), "contenido");
        sessions.Lock(session);

        Assert.Empty(vault.GetNotes(session.RootPath));
    }

    [Fact]
    public void GetAllNotesAcrossVault_SkipsOnlyTheLockedSession_AndKeepsEveryOtherNote()
    {
        var (vault, sessions) = CreateServices();
        var (bloqueada, _) = sessions.CreateEncrypted("Bloqueada", "contrasena-larga-1");
        var abierta = sessions.Create("Abierta");
        vault.WriteContent(vault.CreateNote("Secreta", bloqueada.RootPath), "# Secreta\n");
        vault.WriteContent(vault.CreateNote("Visible", abierta.RootPath), "# Visible\n");
        vault.WriteContent(vault.CreateNote("Raiz", _vaultPath), "# Raiz\n");
        sessions.Lock(bloqueada);

        var titles = vault.GetAllNotesAcrossVault().Select(n => n.Title).ToList();

        Assert.Contains("Visible", titles);
        Assert.Contains("Raiz", titles);
        Assert.DoesNotContain("Secreta", titles);
    }

    [Fact]
    public void IsPathInLockedSession_WithAnUnreadableManifest_ReportsLocked_InsteadOfThrowing()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "contenido");
        File.WriteAllText(Path.Combine(session.RootPath, ".session.json"), "{ roto");

        // Es la guarda que usa la restauración de pestañas al arrancar: si lanzase, el arranque de la app
        // se caería justo en el caso que esta guarda existe para evitar.
        Assert.True(vault.IsPathInLockedSession(note.FullPath));
    }

    [Fact]
    public void Delete_DropsTheUnlockedKey_SoANewSessionReusingTheFolderIsNotBornUnlocked()
    {
        var (vault, sessions) = CreateServices();
        var (session, _) = sessions.CreateEncrypted("Privada", "contrasena-larga-1");
        var rootPath = session.RootPath;
        Assert.True(sessions.IsUnlocked(session));

        Assert.True(sessions.Delete(session));

        Assert.False(vault.EncryptionService.IsUnlocked(rootPath));
    }
}
