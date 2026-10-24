using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the delete-confirmation flow Fase 3 de docs/analisis-proyecto/roadmap.md added to
/// <see cref="SessionsViewModel"/> (top 10 #10 del análisis: borrar una sesión movía su carpeta entera de
/// notas a la papelera con un solo clic). El <c>Reload</c> del constructor corre sobre un vault todavía sin
/// sesiones, así que los tests operan sobre <see cref="SessionListItemViewModel"/>s construidos a mano —
/// los comandos de confirmación trabajan contra el parámetro, no contra la lista.</summary>
public class SessionsViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionsViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    /// <summary>Espera al <c>Reload</c> que el constructor lanza en segundo plano antes de devolver el
    /// viewmodel: recorre el vault entero, y dejarlo vivo mientras el test crea y borra carpetas de sesión
    /// delante es exactamente la carrera que hacía fallar a
    /// <see cref="ConfirmDeleteSession_MovesTheWholeFolderToTrash"/> de vez en cuando y solo con la máquina
    /// cargada (que es cuando la carga tarda lo bastante como para seguir viva aquí).</summary>
    private async Task<(SessionsViewModel Vm, SessionService Sessions)> CreateViewModelAsync()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        await vm.PendingLoad;
        return (vm, sessions);
    }

    [Fact]
    public async Task RequestDeleteSession_OpensDialog_WithoutDeleting()
    {
        var (vm, sessions) = await CreateViewModelAsync();
        var session = sessions.Create("Borrable");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);

        Assert.Same(item, vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
        Assert.Empty(sessions.ListTrashedSessions());
    }

    [Fact]
    public async Task RequestDeleteSession_OnDefaultSession_IsNoOp()
    {
        var (vm, sessions) = await CreateViewModelAsync();
        var session = sessions.EnsureDefaultSession();
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);

        Assert.Null(vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
    }

    [Fact]
    public async Task CancelDeleteSession_ClearsPending_WithoutDeleting()
    {
        var (vm, sessions) = await CreateViewModelAsync();
        var session = sessions.Create("Borrable");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);
        vm.CancelDeleteSessionCommand.Execute(null);

        Assert.Null(vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
    }

    [Fact]
    public async Task ConfirmDeleteSession_MovesTheWholeFolderToTrash()
    {
        var (vm, sessions) = await CreateViewModelAsync();
        var session = sessions.Create("Borrable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);
        vm.ConfirmDeleteSessionCommand.Execute(null);
        await vm.PendingLoad; // el borrado relanza Reload: que no quede recorriendo el vault tras el test

        Assert.Null(vm.SessionPendingDelete);
        Assert.False(Directory.Exists(session.RootPath));
        var trashed = sessions.ListTrashedSessions().Single();
        Assert.True(File.Exists(Path.Combine(trashed.RootPath, "Nota.md")));
    }
}
