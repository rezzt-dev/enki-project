using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="SessionsViewModel.HandleLinkToggled"/> now that a manual link is no longer
/// restricted to two notes in the same session — the graph's "modo enlace" used to reject (and flash) any
/// pair spanning two sessions, or touching a vault-root note, treating it as an expected error
/// (docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md, gap 24). Same temp-directory-per-test
/// pattern, and the same <c>GraphUpdated</c>-await helper, as <c>SessionsViewModelGraphFilterTests</c>.</summary>
public class SessionsViewModelLinkModeTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionsViewModelLinkModeTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (SessionsViewModel Vm, SessionService Sessions, VaultService Vault) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        return (vm, sessions, vault);
    }

    private static async Task<SessionGraph> ToggleLinkAsync(SessionsViewModel vm, string fromGlobalId, string toGlobalId)
    {
        var tcs = new TaskCompletionSource<SessionGraph>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(SessionGraph g) => tcs.TrySetResult(g);
        vm.GraphUpdated += OnUpdated;
        try
        {
            vm.HandleLinkToggled(fromGlobalId, toGlobalId);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            vm.GraphUpdated -= OnUpdated;
        }
    }

    [Fact]
    public async Task HandleLinkToggled_BetweenNotesInDifferentSessions_CreatesTheLinkInsteadOfRejectingIt()
    {
        var (vm, sessions, _) = CreateViewModel();
        var sessionA = sessions.Create("A");
        var sessionB = sessions.Create("B");
        File.WriteAllText(Path.Combine(sessionA.RootPath, "Origen.md"), "# Origen");
        File.WriteAllText(Path.Combine(sessionB.RootPath, "Destino.md"), "# Destino");

        var rejected = false;
        vm.LinkRejected += (_, _) => rejected = true;

        var graph = await ToggleLinkAsync(vm, $"{sessionA.Id}::Origen.md", $"{sessionB.Id}::Destino.md");

        Assert.False(rejected);
        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsManual);
        Assert.Equal(sessionA.Id, edge.FromSessionId);
        Assert.Equal(sessionB.Id, edge.ToSessionId);
    }

    [Fact]
    public async Task HandleLinkToggled_BetweenASessionNoteAndAVaultRootNote_CreatesTheLinkInsteadOfRejectingIt()
    {
        var (vm, sessions, vault) = CreateViewModel();
        var session = sessions.Create("Sesión");
        File.WriteAllText(Path.Combine(session.RootPath, "Origen.md"), "# Origen");
        File.WriteAllText(Path.Combine(vault.VaultPath, "Suelta.md"), "# Suelta");

        var rejected = false;
        vm.LinkRejected += (_, _) => rejected = true;

        var graph = await ToggleLinkAsync(vm, $"{session.Id}::Origen.md", $"{SessionGraph.RootGroupId}::Suelta.md");

        Assert.False(rejected);
        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsManual);
        Assert.Equal(SessionGraph.RootGroupId, edge.ToSessionId);
    }

    [Fact]
    public void HandleLinkToggled_WithAnUnparsableGlobalId_RaisesLinkRejected()
    {
        var (vm, _, _) = CreateViewModel();

        string? rejectedFrom = null;
        vm.LinkRejected += (from, _) => rejectedFrom = from;

        vm.HandleLinkToggled("no-tiene-separador", "tampoco");

        Assert.Equal("no-tiene-separador", rejectedFrom);
    }

    [Fact]
    public void HandleLinkToggled_NamingASessionThatDoesNotExist_RaisesLinkRejected()
    {
        var (vm, _, _) = CreateViewModel();
        var unknownSessionId = Guid.NewGuid().ToString("N");

        var rejected = false;
        vm.LinkRejected += (_, _) => rejected = true;

        vm.HandleLinkToggled($"{unknownSessionId}::A.md", $"{unknownSessionId}::B.md");

        Assert.True(rejected);
    }
}
