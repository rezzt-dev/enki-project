using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Comprueba el contrato de navegación del grafo: un doble clic entrega al shell tanto el espacio
/// de trabajo que debe seleccionar en Notas como el archivo que debe abrir en el editor.</summary>
public sealed class SessionsViewModelNavigationTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionsViewModelNavigationTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public async Task HandleNodeDoubleClicked_OpensItsSessionAndNote_AndClearsPendingLink()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var session = sessions.Create("Proyecto");
        var notePath = Path.Combine(session.RootPath, "carpeta", "Objetivo.md");
        Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
        File.WriteAllText(notePath, "# Objetivo");

        Session? openedSession = null;
        NoteFile? openedNote = null;
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(
            vault,
            sessions,
            notifications,
            (targetSession, targetNote) => (openedSession, openedNote) = (targetSession, targetNote),
            () => { });
        await vm.PendingLoad;

        var globalId = $"{session.Id}::carpeta/Objetivo.md";
        var pendingChanges = new List<string?>();
        vm.PendingLinkChanged += pendingChanges.Add;
        vm.ToggleLinkModeCommand.Execute(null);
        vm.HandleNodeClicked(globalId);

        vm.HandleNodeDoubleClicked(globalId);

        Assert.Equal(session.Id, openedSession?.Id);
        Assert.NotNull(openedNote);
        Assert.True(VaultService.PathsEqual(notePath, openedNote.FullPath));
        Assert.Equal(new string?[] { null, globalId, null }, pendingChanges);
    }

    [Fact]
    public async Task HandleNodeDoubleClicked_OnVaultRootNote_SelectsVaultAndOpensNote()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var notePath = Path.Combine(vault.VaultPath, "Suelta.md");
        File.WriteAllText(notePath, "# Suelta");
        var sessions = new SessionService(vault);

        Session? openedSession = new() { Id = "sentinel", Name = "sentinel", RootPath = "sentinel" };
        NoteFile? openedNote = null;
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(
            vault,
            sessions,
            notifications,
            (targetSession, targetNote) => (openedSession, openedNote) = (targetSession, targetNote),
            () => { });
        await vm.PendingLoad;

        vm.HandleNodeDoubleClicked($"{SessionGraph.RootGroupId}::Suelta.md");

        Assert.Null(openedSession);
        Assert.NotNull(openedNote);
        Assert.True(VaultService.PathsEqual(notePath, openedNote.FullPath));
    }
}
