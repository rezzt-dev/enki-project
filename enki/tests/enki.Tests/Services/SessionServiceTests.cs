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
    public void ToggleGlobalManualLink_BetweenTwoDifferentSessions_AddsThenRemovesTheSameEdge()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "# Origen");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Destino.md"), "# Destino");

        var nowLinked = service.ToggleGlobalManualLink(sessionA.Id, "Origen.md", sessionB.Id, "Destino.md");
        Assert.True(nowLinked);
        var edge = Assert.Single(service.LoadGlobalGraph().Edges);
        Assert.True(edge.IsManual);
        Assert.Equal(sessionA.Id, edge.FromSessionId);
        Assert.Equal(sessionB.Id, edge.ToSessionId);

        var nowUnlinked = service.ToggleGlobalManualLink(sessionA.Id, "Origen.md", sessionB.Id, "Destino.md");
        Assert.False(nowUnlinked);
        Assert.Empty(service.LoadGlobalGraph().Edges);
    }

    [Fact]
    public void ToggleGlobalManualLink_BetweenASessionNoteAndAVaultRootNote_Persists()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Origen.md"), "# Origen");
        File.WriteAllText(Path.Combine(_vaultPath, "Suelta.md"), "# Suelta");

        service.ToggleGlobalManualLink(session.Id, "Origen.md", SessionGraph.RootGroupId, "Suelta.md");

        var edge = Assert.Single(service.LoadGlobalGraph().Edges);
        Assert.True(edge.IsManual);
        Assert.Equal(SessionGraph.RootGroupId, edge.ToSessionId);
    }

    [Fact]
    public void ToggleGlobalManualLink_BetweenTwoVaultRootNotes_Persists()
    {
        var service = CreateService();
        File.WriteAllText(Path.Combine(_vaultPath, "Uno.md"), "# Uno");
        File.WriteAllText(Path.Combine(_vaultPath, "Dos.md"), "# Dos");

        service.ToggleGlobalManualLink(SessionGraph.RootGroupId, "Uno.md", SessionGraph.RootGroupId, "Dos.md");

        var edge = Assert.Single(service.LoadGlobalGraph().Edges);
        Assert.True(edge.IsManual);
        Assert.Equal(SessionGraph.RootGroupId, edge.FromSessionId);
        Assert.Equal(SessionGraph.RootGroupId, edge.ToSessionId);
    }

    /// <summary>A cross-scope manual link whose target note has since been deleted must not survive forever
    /// in <c>.graph.json</c> — same reconciliation a within-session <c>ManualLinks</c> entry already gets in
    /// <see cref="ToggleManualLink_AddsThenRemoves_TheSameEdge"/>'s session-scoped counterpart.</summary>
    [Fact]
    public void LoadGlobalGraph_PrunesCrossScopeManualLink_WhenTargetNoteIsDeleted()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        var destinoPath = Path.Combine(sessionB.RootPath, "Destino.md");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "# Origen");
        File.WriteAllText(destinoPath, "# Destino");
        service.ToggleGlobalManualLink(sessionA.Id, "Origen.md", sessionB.Id, "Destino.md");
        Assert.Single(service.LoadGlobalGraph().Edges);

        File.Delete(destinoPath);

        Assert.Empty(service.LoadGlobalGraph().Edges);
    }

    [Fact]
    public void ListAllNotesByWorkspace_IncludesRootAndEverySessionNote_LabeledByWorkspace()
    {
        var service = CreateService();
        var session = service.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "En sesión.md"), "# En sesión");
        File.WriteAllText(Path.Combine(_vaultPath, "Suelta.md"), "# Suelta");

        var results = service.ListAllNotesByWorkspace().ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Note.Title == "En sesión" && r.WorkspaceLabel == "Sesión");
        Assert.Contains(results, r => r.Note.Title == "Suelta");
    }

    /// <summary>Companion regression test to <c>KanbanCardEditorViewModelTests</c>'s at the ViewModel layer —
    /// this asserts the same fix at the source: a locked encrypted session must be skipped, not enumerated
    /// (which would throw <see cref="SessionLockedException"/> partway through reading its notes).</summary>
    [Fact]
    public void ListAllNotesByWorkspace_SkipsALockedSession_WithoutThrowing()
    {
        var service = CreateService();
        var (locked, _) = service.CreateEncrypted("Privada", "super-secreta-123");
        File.WriteAllText(Path.Combine(_vaultPath, "Suelta.md"), "# Suelta");
        service.Lock(locked);

        var results = service.ListAllNotesByWorkspace().ToList();

        Assert.Single(results);
        Assert.Equal("Suelta", results[0].Note.Title);
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

    /// <summary>Covers the ambiguity-aware branch of <c>ComputeGlobalWikiLinkEdges</c> that couldn't be
    /// exercised before the global graph existed — two notes in different sessions can now share the same
    /// title (every fresh note starts as "# Nueva nota"), and a title held by more than one note vault-wide
    /// must resolve to nothing rather than an arbitrary guess (docs/version-alfa-3/01-analisis-detallado.md
    /// §4.1, Media).</summary>
    [Fact]
    public void LoadGlobalGraph_DoesNotResolveWikiLink_WhenTargetTitleIsAmbiguousAcrossSessions()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        var sessionC = service.Create("C");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Nota1.md"), "# Destino");
        File.WriteAllText(Path.Combine(sessionC.RootPath, "Nota2.md"), "# Destino");

        var graph = service.LoadGlobalGraph();

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Empty(graph.Edges);
    }

    /// <summary>Same ambiguity guard as above, but for the filename fallback — a filename held by more than
    /// one note vault-wide (not just within one session) must also resolve to nothing.</summary>
    [Fact]
    public void LoadGlobalGraph_DoesNotResolveWikiLink_WhenTargetFileNameIsAmbiguousAcrossSessions()
    {
        var service = CreateService();
        var sessionA = service.Create("A");
        var sessionB = service.Create("B");
        var sessionC = service.Create("C");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Destino.md"), "# Título distinto B");
        File.WriteAllText(Path.Combine(sessionC.RootPath, "Destino.md"), "# Título distinto C");

        var graph = service.LoadGlobalGraph();

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Empty(graph.Edges);
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
