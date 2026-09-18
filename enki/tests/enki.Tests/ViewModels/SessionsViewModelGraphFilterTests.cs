using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the graph group-filter popover (docs/version-alfa-3/01-analisis-detallado.md §4.1,
/// Media: "es la funcionalidad menos revisada de todo el árbol de trabajo") —
/// <see cref="SessionsViewModel.GraphGroupFilters"/>/<see cref="GraphGroupFilterItemViewModel"/>, the
/// private <c>RebuildGraphGroupFilters</c>/<c>PublishGraph</c> pair that back it, reached here only through
/// the public surface (<see cref="SessionsViewModel.LoadGraph"/>, the <see cref="SessionsViewModel.GraphReady"/>/
/// <see cref="SessionsViewModel.GraphUpdated"/> events, and each filter row's own <c>IsActive</c> toggle).
/// Same temp-directory-per-test pattern as the other Sessions tests.</summary>
public class SessionsViewModelGraphFilterTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionsViewModelGraphFilterTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    /// <summary>El constructor de <see cref="SessionsViewModel"/> lanza un <c>Reload</c> en segundo plano
    /// (recorre el vault entero y arma el grafo), y esperar a que termine antes de devolver el viewmodel no
    /// es cortesía: sin eso el test y esa carga compiten. Los dos síntomas eran reales y de los que solo
    /// aparecen con la máquina cargada, que es cuando la carga tarda lo bastante como para seguir viva aquí:
    /// el test escribe sus ficheros mientras la carga recorre el árbol, y —peor— la carga del constructor
    /// puede publicar su <c>GraphReady</c> justo después de que <see cref="LoadGraphAsync"/> se haya
    /// suscrito, de modo que el test se quedaba con un grafo anterior a las sesiones y notas que él
    /// mismo acababa de crear y contaba cero grupos donde esperaba dos.</summary>
    private async Task<(SessionsViewModel Vm, SessionService Sessions, VaultService Vault)> CreateViewModelAsync()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        await vm.PendingLoad;
        return (vm, sessions, vault);
    }

    /// <summary>Awaits the next full graph (re)load. <see cref="SessionsViewModel.LoadGraph"/> es "dispara y
    /// olvida" (una acción de UI, o el constructor), así que hacen falta las dos mitades: esperar a
    /// <see cref="SessionsViewModel.PendingLoad"/> para saber que el recorrido de disco terminó de verdad
    /// —y que un fallo salga como fallo aquí, en vez de como un plantón de diez segundos—, y engancharse a
    /// <see cref="SessionsViewModel.GraphReady"/> para quedarse con el grafo que publicó.</summary>
    private static async Task<SessionGraph> LoadGraphAsync(SessionsViewModel vm)
    {
        var tcs = new TaskCompletionSource<SessionGraph>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(SessionGraph g) => tcs.TrySetResult(g);
        vm.GraphReady += OnReady;
        try
        {
            vm.LoadGraph();
            await vm.PendingLoad;
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            vm.GraphReady -= OnReady;
        }
    }

    /// <summary>Awaits the in-place graph update a filter toggle triggers (<see cref="SessionsViewModel.GraphUpdated"/>,
    /// not <see cref="SessionsViewModel.GraphReady"/> — toggling a filter doesn't rebuild the document).</summary>
    private static async Task<SessionGraph> ToggleFilterAsync(SessionsViewModel vm, GraphGroupFilterItemViewModel filter)
    {
        var tcs = new TaskCompletionSource<SessionGraph>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(SessionGraph g) => tcs.TrySetResult(g);
        vm.GraphUpdated += OnUpdated;
        try
        {
            filter.IsActive = !filter.IsActive;
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            vm.GraphUpdated -= OnUpdated;
        }
    }

    [Fact]
    public async Task RebuildGraphGroupFilters_CreatesOneRowPerGroup_VaultRootFirst_AllActiveByDefault()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var sessionA = sessions.Create("Sesión A");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Nota.md"), "# Nota");
        File.WriteAllText(Path.Combine(vault.VaultPath, "Suelta.md"), "# Suelta");

        await LoadGraphAsync(vm);

        Assert.Equal(2, vm.GraphGroupFilters.Count);
        Assert.Equal(SessionGraph.RootGroupId, vm.GraphGroupFilters[0].GroupId);
        Assert.All(vm.GraphGroupFilters, f => Assert.True(f.IsActive));
        Assert.False(vm.IsGraphFiltered);
    }

    [Fact]
    public async Task TogglingAFilterOff_HidesThatGroupsNodes_FromThePublishedGraph()
    {
        var (vm, sessions, _) = await CreateViewModelAsync();
        var sessionA = sessions.Create("Sesión A");
        var sessionB = sessions.Create("Sesión B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "A.md"), "# A");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "B.md"), "# B");

        await LoadGraphAsync(vm);
        Assert.Equal(2, vm.NodeCount);

        var filterA = vm.GraphGroupFilters.Single(f => f.GroupId == sessionA.Id);
        var updated = await ToggleFilterAsync(vm, filterA);

        Assert.False(filterA.IsActive);
        Assert.True(vm.IsGraphFiltered);
        Assert.Single(updated.Nodes);
        Assert.Equal(sessionB.Id, updated.Nodes[0].SessionId);
        Assert.Equal(1, vm.NodeCount);
    }

    [Fact]
    public async Task TogglingAFilterOff_HidesCrossGroupEdges_WhereEitherEndpointIsHidden()
    {
        var (vm, sessions, _) = await CreateViewModelAsync();
        var sessionA = sessions.Create("Sesión A");
        var sessionB = sessions.Create("Sesión B");
        // A wikilink from A's note to B's note only resolves cross-scope via the global graph pass
        // (SessionService.ComputeGlobalWikiLinkEdges) — same target note title, unique across the vault.
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "Ver [[Destino]].");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Destino.md"), "# Destino");

        await LoadGraphAsync(vm);
        Assert.Equal(1, vm.EdgeCount);

        var filterA = vm.GraphGroupFilters.Single(f => f.GroupId == sessionA.Id);
        await ToggleFilterAsync(vm, filterA);

        Assert.Equal(0, vm.EdgeCount);
    }

    [Fact]
    public async Task TogglingAFilterBackOn_RestoresItsGroupsNodes_AndClearsIsGraphFiltered()
    {
        var (vm, sessions, _) = await CreateViewModelAsync();
        var sessionA = sessions.Create("Sesión A");
        var sessionB = sessions.Create("Sesión B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "A.md"), "# A");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "B.md"), "# B");

        await LoadGraphAsync(vm);
        var filterA = vm.GraphGroupFilters.Single(f => f.GroupId == sessionA.Id);
        await ToggleFilterAsync(vm, filterA);
        Assert.Equal(1, vm.NodeCount);

        var restored = await ToggleFilterAsync(vm, filterA);

        Assert.True(filterA.IsActive);
        Assert.False(vm.IsGraphFiltered);
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Equal(2, vm.NodeCount);
    }

    [Fact]
    public async Task RebuildGraphGroupFilters_WithOnlyRootNotesAndNoSessions_CreatesSingleRootRow()
    {
        var (vm, _, vault) = await CreateViewModelAsync();
        File.WriteAllText(Path.Combine(vault.VaultPath, "Suelta.md"), "# Suelta");

        await LoadGraphAsync(vm);

        var onlyFilter = Assert.Single(vm.GraphGroupFilters);
        Assert.Equal(SessionGraph.RootGroupId, onlyFilter.GroupId);
        Assert.True(onlyFilter.IsActive);
    }

    [Fact]
    public async Task RebuildGraphGroupFilters_PreservesInactiveSelection_AcrossAReload()
    {
        var (vm, sessions, _) = await CreateViewModelAsync();
        var sessionA = sessions.Create("Sesión A");
        var sessionB = sessions.Create("Sesión B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "A.md"), "# A");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "B.md"), "# B");

        await LoadGraphAsync(vm);
        var filterA = vm.GraphGroupFilters.Single(f => f.GroupId == sessionA.Id);
        filterA.IsActive = false;

        await LoadGraphAsync(vm);

        var filterAAfterReload = vm.GraphGroupFilters.Single(f => f.GroupId == sessionA.Id);
        Assert.False(filterAAfterReload.IsActive);
        Assert.True(vm.IsGraphFiltered);
    }

    [Fact]
    public async Task RootOnlyVault_ShowsGraphAndIncludesFrontmatterTags()
    {
        var (vm, _, vault) = await CreateViewModelAsync();
        File.WriteAllText(Path.Combine(vault.VaultPath, "Suelta.md"), "---\ntags: [ideas]\n---\n# Suelta");

        var graph = await LoadGraphAsync(vm);

        Assert.False(vm.IsEmpty);
        Assert.Equal("ideas", Assert.Single(Assert.Single(graph.Nodes).Tags));
        Assert.Equal("ideas", Assert.Single(vm.GraphTagFilters).GroupId);
    }

    [Fact]
    public async Task Tags_CombineWithSessions_AndNeverPublishDanglingEdges()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var session = sessions.Create("Trabajo");
        File.WriteAllText(Path.Combine(session.RootPath, "A.md"), "---\ntags: [ideas, ciencia]\n---\n# A\n[[B]]");
        File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "---\ntags: [IDEAS]\n---\n# B\n[[C]]");
        File.WriteAllText(Path.Combine(vault.VaultPath, "C.md"), "# C");
        await LoadGraphAsync(vm);

        var ideas = vm.GraphTagFilters.Single(f => f.GroupId.Equals("ideas", StringComparison.OrdinalIgnoreCase));
        var filtered = await ToggleFilterAsync(vm, ideas);
        Assert.Equal(2, filtered.Nodes.Count);
        Assert.Single(filtered.Edges);
        Assert.Equal(2, vm.GraphTagFilters.Count);

        filtered = await ToggleFilterAsync(vm, vm.GraphGroupFilters.Single(f => f.GroupId == session.Id));
        Assert.Equal("B", Assert.Single(filtered.Nodes).Title);
        Assert.Empty(filtered.Edges);
        Assert.Equal(2, vm.GraphTagFilters.Count);
    }

    [Fact]
    public async Task MultipleTags_MatchAny_AndResetPublishesOnce()
    {
        var (vm, _, vault) = await CreateViewModelAsync();
        File.WriteAllText(Path.Combine(vault.VaultPath, "A.md"), "---\ntags: [uno]\n---\n# A");
        File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "---\ntags: [dos]\n---\n# B");
        File.WriteAllText(Path.Combine(vault.VaultPath, "C.md"), "# C");
        await LoadGraphAsync(vm);
        await ToggleFilterAsync(vm, vm.GraphTagFilters[0]);
        var graph = await ToggleFilterAsync(vm, vm.GraphTagFilters[1]);
        Assert.Equal(2, graph.Nodes.Count);

        vm.GraphExplorationIndex = 1;
        var updates = 0;
        vm.GraphUpdated += _ => updates++;
        vm.ResetGraphFiltersCommand.Execute(null);
        Assert.Equal(1, updates);
        Assert.Equal(3, vm.NodeCount);
        Assert.False(vm.IsGraphFiltered);
    }

    [Fact]
    public async Task Orphans_UseGlobalConnectivity_NotFilteredConnectivity()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var session = sessions.Create("Trabajo");
        File.WriteAllText(Path.Combine(session.RootPath, "A.md"), "# A\n[[B]]");
        File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "# B");
        File.WriteAllText(Path.Combine(vault.VaultPath, "C.md"), "# C");
        await LoadGraphAsync(vm);
        await ToggleFilterAsync(vm, vm.GraphGroupFilters.Single(f => f.GroupId == session.Id));
        SessionGraph? published = null;
        vm.GraphUpdated += graph => published = graph;

        vm.GraphExplorationIndex = 1;

        Assert.Equal("C", Assert.Single(published!.Nodes).Title);
        Assert.Empty(published.Edges);
    }

    [Fact]
    public async Task CrossSessionMode_OnlyIncludesVisibleCrossLinksAndTheirEndpoints()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var session = sessions.Create("Trabajo");
        File.WriteAllText(Path.Combine(session.RootPath, "A.md"), "# A\n[[B]]\n[[C]]");
        File.WriteAllText(Path.Combine(session.RootPath, "C.md"), "# C");
        File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "# B");
        await LoadGraphAsync(vm);
        SessionGraph? published = null;
        vm.GraphUpdated += graph => published = graph;

        vm.GraphExplorationIndex = 2;
        Assert.Equal(2, published!.Nodes.Count);
        Assert.Single(published.Edges);
        Assert.DoesNotContain(published.Nodes, n => n.Title == "C");

        await ToggleFilterAsync(vm, vm.GraphGroupFilters.Single(f => f.GroupId == session.Id));
        Assert.True(vm.IsGraphEmpty);
        Assert.Empty(published.Nodes);
        Assert.Empty(published.Edges);
    }

    [Fact]
    public async Task Clusters_CrossSessionBoundaries_AndSelectionSurvivesReload()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var session = sessions.Create("Trabajo");
        File.WriteAllText(Path.Combine(session.RootPath, "A.md"), "# A\n[[B]]");
        File.WriteAllText(Path.Combine(vault.VaultPath, "B.md"), "# B");
        File.WriteAllText(Path.Combine(vault.VaultPath, "C.md"), "# C\n[[D]]");
        File.WriteAllText(Path.Combine(vault.VaultPath, "D.md"), "# D");
        File.WriteAllText(Path.Combine(vault.VaultPath, "E.md"), "# E");
        var all = await LoadGraphAsync(vm);
        Assert.Equal(3, vm.GraphClusters.Count);
        var clusterId = all.Nodes.Single(n => n.Title == "A").ClusterId;
        vm.SelectedGraphCluster = vm.GraphClusters.Single(c => c.Id == clusterId);
        var filtered = await LoadGraphAsync(vm);
        Assert.Equal(2, filtered.Nodes.Count);
        Assert.Contains(filtered.Nodes, n => n.Title == "B");
        Assert.Equal(clusterId, vm.SelectedGraphCluster!.Id);

        File.Delete(Path.Combine(session.RootPath, "A.md"));
        await LoadGraphAsync(vm);
        Assert.Equal(string.Empty, vm.SelectedGraphCluster!.Id);
        Assert.False(vm.IsGraphFiltered);
    }

    [Fact]
    public async Task Tags_PreserveSelectionOnReload_AndDropDeletedTags()
    {
        var (vm, _, vault) = await CreateViewModelAsync();
        var path = Path.Combine(vault.VaultPath, "A.md");
        File.WriteAllText(path, "---\ntags: [ideas]\n---\n# A");
        await LoadGraphAsync(vm);
        await ToggleFilterAsync(vm, vm.GraphTagFilters[0]);
        await LoadGraphAsync(vm);
        Assert.True(Assert.Single(vm.GraphTagFilters).IsActive);
        File.Delete(path);
        await LoadGraphAsync(vm);
        Assert.Empty(vm.GraphTagFilters);
        Assert.False(vm.IsGraphFiltered);
    }

    [Fact]
    public async Task Filtering_ClearsPendingManualLink()
    {
        var (vm, _, vault) = await CreateViewModelAsync();
        File.WriteAllText(Path.Combine(vault.VaultPath, "A.md"), "# A");
        var graph = await LoadGraphAsync(vm);
        vm.ToggleLinkModeCommand.Execute(null);
        vm.HandleNodeClicked(graph.Nodes[0].GlobalId);
        string? pending = "unchanged";
        vm.PendingLinkChanged += id => pending = id;
        vm.GraphExplorationIndex = 1;
        Assert.Null(pending);
    }

    [Fact]
    public async Task LockedSession_DoesNotExposeTagsOrClusterOptions()
    {
        var (vm, sessions, vault) = await CreateViewModelAsync();
        var (session, _) = sessions.CreateEncrypted("Privada", "super-secreta-123");
        var note = vault.CreateNote("Secreta", session.RootPath);
        vault.WriteContent(note, "---\ntags: [privada]\n---\n# Secreta");
        await LoadGraphAsync(vm);
        Assert.Equal("privada", Assert.Single(vm.GraphTagFilters).Label);

        sessions.Lock(session);
        var graph = await LoadGraphAsync(vm);
        Assert.Empty(graph.Nodes);
        Assert.Empty(vm.GraphTagFilters);
        Assert.False(vm.HasGraphClusters);
    }
}
