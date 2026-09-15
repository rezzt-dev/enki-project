using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Comprueba que editar el color de una sesión actualiza la entidad persistida, la muestra del
/// listado y el payload incremental que recolorea el grafo sin reconstruir su documento.</summary>
public sealed class SessionsViewModelColorTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public async Task ConfirmSessionColor_PersistsAndPublishesTheNewColorIncrementally()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var session = sessions.Create("Proyecto");
        File.WriteAllText(Path.Combine(session.RootPath, "Nota.md"), "# Nota");
        var notifications = new NotificationService(settings, _storageDirectory);
        var sessionsChanged = 0;
        var vm = new SessionsViewModel(
            vault,
            sessions,
            notifications,
            (_, _) => { },
            () => sessionsChanged++);
        await vm.PendingLoad;
        var item = vm.Sessions.Single(s => s.Session.Id == session.Id);

        string? changedSessionId = null;
        string? changedColor = null;
        vm.SessionColorChanged += (sessionId, color) => (changedSessionId, changedColor) = (sessionId, color);
        vm.BeginChangeSessionColorCommand.Execute(item);
        vm.SessionColorHex = "#2f80ed";

        vm.ConfirmSessionColorCommand.Execute(null);

        Assert.Null(vm.SessionPendingColor);
        Assert.Equal("#2F80ED", sessions.List().Single().Color);
        Assert.Equal(Color.Parse("#2F80ED"), item.SessionColorBrush.Color);
        Assert.Equal(session.Id, changedSessionId);
        Assert.Equal("#2F80ED", changedColor);

        SessionGraph? republished = null;
        vm.GraphReady += graph => republished = graph;
        vm.RepublishGraph();
        Assert.Equal("#2F80ED", Assert.Single(republished!.Nodes).SessionColor);
        Assert.Equal(1, sessionsChanged);
    }

    [Fact]
    public async Task UseAutomaticSessionColor_ClearsTheExplicitColor()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var session = sessions.Create("Proyecto");
        sessions.SetColor(session, "#2F80ED");
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        await vm.PendingLoad;
        var item = vm.Sessions.Single(s => s.Session.Id == session.Id);

        vm.BeginChangeSessionColorCommand.Execute(item);
        vm.UseAutomaticSessionColorCommand.Execute(null);

        Assert.Null(vm.SessionPendingColor);
        Assert.Null(sessions.List().Single().Color);
        Assert.Null(item.Session.Color);
    }
}
