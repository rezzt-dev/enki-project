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
}
