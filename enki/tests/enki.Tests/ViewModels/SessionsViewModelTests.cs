using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the delete-confirmation flow Fase 3 de docs/analisis-proyecto/roadmap.md added to
/// <see cref="SessionsViewModel"/> (top 10 #10 del análisis: borrar una sesión movía su carpeta entera de
/// notas a la papelera con un solo clic). El <c>Reload</c> del constructor es <c>async void</c> sobre un
/// vault sin sesiones, así que los tests operan sobre <see cref="SessionListItemViewModel"/>s construidos
/// a mano — los comandos de confirmación trabajan contra el parámetro, no contra la lista.</summary>
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

    private (SessionsViewModel Vm, SessionService Sessions) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        return (vm, sessions);
    }

    [Fact]
    public void RequestDeleteSession_OpensDialog_WithoutDeleting()
    {
        var (vm, sessions) = CreateViewModel();
        var session = sessions.Create("Borrable");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);

        Assert.Same(item, vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
        Assert.Empty(sessions.ListTrashedSessions());
    }

    [Fact]
    public void RequestDeleteSession_OnDefaultSession_IsNoOp()
    {
        var (vm, sessions) = CreateViewModel();
        var session = sessions.EnsureDefaultSession();
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);

        Assert.Null(vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
    }

    [Fact]
    public void CancelDeleteSession_ClearsPending_WithoutDeleting()
    {
        var (vm, sessions) = CreateViewModel();
        var session = sessions.Create("Borrable");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);
        vm.CancelDeleteSessionCommand.Execute(null);

        Assert.Null(vm.SessionPendingDelete);
        Assert.True(Directory.Exists(session.RootPath));
    }

    [Fact]
    public void ConfirmDeleteSession_MovesTheWholeFolderToTrash()
    {
        var (vm, sessions) = CreateViewModel();
        var session = sessions.Create("Borrable");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        var item = new SessionListItemViewModel(session);

        vm.RequestDeleteSessionCommand.Execute(item);
        vm.ConfirmDeleteSessionCommand.Execute(null);

        Assert.Null(vm.SessionPendingDelete);
        Assert.False(Directory.Exists(session.RootPath));
        var trashed = sessions.ListTrashedSessions().Single();
        Assert.True(File.Exists(Path.Combine(trashed.RootPath, "Nota.md")));
    }
}
