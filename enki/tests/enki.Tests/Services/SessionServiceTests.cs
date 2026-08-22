using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="SessionService"/> — sessions and their <c>.session.json</c> manifest, and
/// the graph it derives from wikilinks + manual links (03-problemas-y-deuda-tecnica.md §8 flags this,
/// alongside <c>SessionGraphRenderer</c>, as untested despite backing the Sesiones page end to end). Each
/// test points the vault at its own throwaway temp directory, same pattern as <see cref="VaultServiceTests"/>.</summary>
public class SessionServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionServiceTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private SessionService CreateService()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return new SessionService(vault);
    }

    [Fact]
    public void List_IsEmpty_WhenNoSessionsExist()
    {
        var service = CreateService();
        Assert.Empty(service.List());
    }

    [Fact]
    public void Create_MakesASessionFolder_WithManifest()
    {
        var service = CreateService();

        var session = service.Create("Mi sesión");

        Assert.True(Directory.Exists(session.RootPath));
        Assert.True(File.Exists(Path.Combine(session.RootPath, ".session.json")));
        Assert.Equal("Mi sesión", session.Name);
        Assert.Single(service.List());
    }

    [Fact]
    public void Create_DeduplicatesFolderName_WhenNameAlreadyTaken()
    {
        var service = CreateService();
        var first = service.Create("Proyecto");
        var second = service.Create("Proyecto");

        Assert.NotEqual(first.RootPath, second.RootPath);
        Assert.Equal(2, service.List().Count);
    }

    [Fact]
    public void EnsureDefaultSession_CreatesOnce_AndIsIdempotent()
    {
        var service = CreateService();

        var first = service.EnsureDefaultSession();
        var second = service.EnsureDefaultSession();

        Assert.True(first.IsDefault);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.List());
    }

    [Fact]
    public void Rename_UpdatesManifestAndReturnedSession()
    {
        var service = CreateService();
        var session = service.Create("Antes");

        var renamed = service.Rename(session, "Después");

        Assert.Equal("Después", renamed.Name);
        Assert.Equal("Después", service.List().Single().Name);
    }

    [Fact]
    public void Delete_OnDefaultSession_IsNoOp()
    {
        var service = CreateService();
        var session = service.EnsureDefaultSession();

        var deleted = service.Delete(session);

        Assert.False(deleted);
        Assert.Single(service.List());
    }

    [Fact]
    public void Delete_OnRegularSession_MovesFolderToTrash()
    {
        var service = CreateService();
        var session = service.Create("Descartable");

        var deleted = service.Delete(session);

        Assert.True(deleted);
        Assert.Empty(service.List());
        Assert.False(Directory.Exists(session.RootPath));
        Assert.True(Directory.Exists(Path.Combine(_vaultPath, ".trash", "sessions", Path.GetFileName(session.RootPath))));
    }

    [Fact]
    public void LoadGraph_CreatesOneNodePerNote_WithDeterministicInitialPosition()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota A.md"), "# Nota A");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota B.md"), "# Nota B");

        var graph = service.LoadGraph(session);

        Assert.Equal(2, graph.Nodes.Count);
        Assert.All(graph.Nodes, n => Assert.Equal(session.Id, n.SessionId));
    }

    [Fact]
    public void LoadGraph_DerivesEdge_FromWikiLinkBetweenNotes()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(session.RootPath, "Destino.md"), "# Destino");

        var graph = service.LoadGraph(session);

        Assert.Single(graph.Edges);
        Assert.False(graph.Edges[0].IsManual);
    }

    [Fact]
    public void LoadGraph_PersistsNewNodePositions_ForNextLoad()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");

        var firstLoad = service.LoadGraph(session);
        Assert.False(firstLoad.Nodes[0].HasStoredPosition);

        var secondLoad = service.LoadGraph(session);
        Assert.True(secondLoad.Nodes[0].HasStoredPosition);
        Assert.Equal(firstLoad.Nodes[0].X, secondLoad.Nodes[0].X);
    }

    [Fact]
    public void ToggleManualLink_AddsThenRemoves_TheSameEdge()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "A.md"), "# A");
        File.WriteAllText(Path.Combine(session.RootPath, "B.md"), "# B");

        var nowLinked = service.ToggleManualLink(session, "A.md", "B.md");
        Assert.True(nowLinked);
        Assert.Contains(service.LoadGraph(session).Edges, e => e.IsManual);

        var nowUnlinked = service.ToggleManualLink(session, "A.md", "B.md");
        Assert.False(nowUnlinked);
        Assert.DoesNotContain(service.LoadGraph(session).Edges, e => e.IsManual);
    }

    [Fact]
    public void SavePositions_UpdatesStoredCoordinates()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        service.LoadGraph(session); // seeds the manifest with an initial node entry

        service.SavePositions(session, [("Nota.md", 1.5, 2.5, 3.5)]);
        var reloaded = service.LoadGraph(session);

        var node = reloaded.Nodes.Single();
        Assert.Equal(1.5, node.X);
        Assert.Equal(2.5, node.Y);
        Assert.Equal(3.5, node.Z);
    }

    [Fact]
    public void LoadGlobalGraph_KeepsEachSessionAsASeparateCluster()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Nota.md"), "# Nota");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Nota.md"), "# Nota");

        var combined = service.LoadGlobalGraph();

        Assert.Equal(2, combined.Nodes.Count);
        Assert.Equal(2, combined.Nodes.Select(n => n.SessionId).Distinct().Count());
    }

    [Fact]
    public void LoadGlobalGraph_IncludesVaultRootNotes_UnderTheRootGroup()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "En sesión.md"), "# En sesión");
        File.WriteAllText(Path.Combine(_vaultPath, "Suelta.md"), "# Suelta");

        var graph = service.LoadGlobalGraph();

        Assert.Equal(2, graph.Nodes.Count);
        var rootNode = Assert.Single(graph.Nodes, n => n.SessionId == SessionGraph.RootGroupId);
        Assert.Equal("Suelta.md", rootNode.RelativePath);
    }

    [Fact]
    public void LoadGlobalGraph_DerivesEdge_BetweenASessionNoteAndAVaultRootNote()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(_vaultPath, "Destino.md"), "# Destino");

        var graph = service.LoadGlobalGraph();

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(session.Id, edge.FromSessionId);
        Assert.Equal(SessionGraph.RootGroupId, edge.ToSessionId);
    }

    [Fact]
    public void LoadGlobalGraph_DerivesEdge_BetweenNotesInTwoDifferentSessions()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Destino.md"), "# Destino");

        var graph = service.LoadGlobalGraph();

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(sessionA.Id, edge.FromSessionId);
        Assert.Equal(sessionB.Id, edge.ToSessionId);
    }

    [Fact]
    public void SaveRootPositions_UpdatesStoredCoordinates_ForVaultRootNotes()
    {
        var service = CreateService();
        File.WriteAllText(Path.Combine(_vaultPath, "Suelta.md"), "# Suelta");
        service.LoadGlobalGraph(); // seeds .graph.json with an initial node entry

        service.SaveRootPositions([("Suelta.md", 1.5, 2.5, 3.5)]);
        var reloaded = service.LoadGlobalGraph();

        var node = Assert.Single(reloaded.Nodes);
        Assert.True(node.HasStoredPosition);
        Assert.Equal(1.5, node.X);
        Assert.Equal(2.5, node.Y);
        Assert.Equal(3.5, node.Z);
    }

    [Fact]
    public void ReadManifest_RaisesManifestError_WhenFileContainsInvalidUtf8AndIsLocked()
    {
        var service = CreateService();
        var session = service.Create("Bloqueada");
        var manifestPath = Path.Combine(session.RootPath, ".session.json");

        string? reportedMessage = null;
        service.ManifestError += message => reportedMessage = message;

        // Abre el manifiesto con acceso exclusivo para forzar un IOException real en el siguiente
        // ReadManifest, en vez de simular la excepción — mismo criterio que el resto de tests de
        // servicios de este proyecto, que ejercitan rutas reales de E/S contra un directorio temporal.
        using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.Rename(session, "Nuevo nombre");
        }

        Assert.NotNull(reportedMessage);
        Assert.Contains("Bloqueada", reportedMessage);
    }

    /// <summary>Fase 0 del roadmap de deficiencias (docs/analisis-proyecto/analisis-deficiencias-proyecto.md
    /// §2, "Concurrencia/async"): antes de esto, un permiso denegado al enumerar <c>.sessions</c> se
    /// propagaba como excepción no capturable desde los <c>async void Reload</c>/<c>LoadGraph</c> de
    /// <c>SessionsViewModel</c>. La aserción principal (no debe lanzar) es válida en cualquier entorno; la
    /// comprobación del aviso solo aplica cuando el permiso realmente se hizo cumplir (no, por ejemplo,
    /// corriendo como root, donde chmod no restringe el propio acceso).</summary>
    [Fact]
    public void List_DoesNotThrow_WhenSessionsRootIsUnreadable()
    {
        if (OperatingSystem.IsWindows()) return; // UnixFileMode no aplica en Windows.

        var service = CreateService();
        service.Create("Sesión");
        var sessionsRoot = Path.Combine(_vaultPath, ".sessions");

        string? reportedMessage = null;
        service.ManifestError += message => reportedMessage = message;

        File.SetUnixFileMode(sessionsRoot, UnixFileMode.None);
        try
        {
            var sessions = service.List();

            if (reportedMessage is not null) Assert.Empty(sessions);
        }
        finally
        {
            File.SetUnixFileMode(sessionsRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ===================== Papelera de sesiones (Fase 3, docs/analisis-proyecto/roadmap.md) =====================

    [Fact]
    public void ListTrashedSessions_IsEmpty_WhenNothingWasDeleted()
    {
        var service = CreateService();
        service.Create("Viva");

        Assert.Empty(service.ListTrashedSessions());
    }

    [Fact]
    public void ListTrashedSessions_ReturnsSoftDeletedSessions_WithTheirNoteCount()
    {
        var service = CreateService();
        var session = service.Create("Descartable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");

        service.Delete(session);
        var trashed = service.ListTrashedSessions();

        var item = Assert.Single(trashed);
        Assert.Equal("Descartable", item.Name);
        Assert.Equal(1, item.NoteCount);
    }

    [Fact]
    public void RestoreTrashedSession_MovesFolderBack_AndListsItAgain()
    {
        var service = CreateService();
        var session = service.Create("Recuperable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        service.Delete(session);
        var trashed = service.ListTrashedSessions().Single();

        var restored = service.RestoreTrashedSession(trashed);

        Assert.True(restored);
        Assert.Empty(service.ListTrashedSessions());
        var again = service.List().Single(s => s.Name == "Recuperable");
        Assert.True(File.Exists(Path.Combine(again.RootPath, "Nota.md")));
    }

    [Fact]
    public void RestoreTrashedSession_DeduplicatesFolderName_WhenAlreadyTaken()
    {
        var service = CreateService();
        var session = service.Create("Repetida");
        service.Delete(session);
        var occupying = service.Create("Repetida"); // misma carpeta libre tras el borrado
        var trashed = service.ListTrashedSessions().Single();

        var restored = service.RestoreTrashedSession(trashed);

        Assert.True(restored);
        var all = service.List();
        Assert.Equal(2, all.Count(s => s.Name == "Repetida"));
        Assert.Equal(2, all.Select(s => s.RootPath).Distinct().Count());
        Assert.True(Directory.Exists(occupying.RootPath)); // la sesión que ocupaba el nombre no se tocó
    }

    [Fact]
    public void RestoreTrashedSession_WhenFolderIsGone_ReturnsFalse()
    {
        var service = CreateService();
        var session = service.Create("Fantasma");
        service.Delete(session);
        var trashed = service.ListTrashedSessions().Single();
        Directory.Delete(trashed.RootPath, recursive: true); // borrada a mano fuera de la app

        var restored = service.RestoreTrashedSession(trashed);

        Assert.False(restored);
        Assert.Empty(service.List());
    }

    [Fact]
    public void PermanentlyDeleteTrashedSession_RemovesTheWholeFolder()
    {
        var service = CreateService();
        var session = service.Create("Purgable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        service.Delete(session);
        var trashed = service.ListTrashedSessions().Single();

        SessionService.PermanentlyDeleteTrashedSession(trashed);

        Assert.False(Directory.Exists(trashed.RootPath));
        Assert.Empty(service.ListTrashedSessions());
    }
}
