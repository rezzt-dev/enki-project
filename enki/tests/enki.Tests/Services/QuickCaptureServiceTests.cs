using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class QuickCaptureServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-quick-capture-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (VaultService Vault, KanbanService Kanban, QuickCaptureService Capture) CreateServices()
    {
        var settings = new SettingsService(Path.Combine(_root, "config"));
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_root, "vault"), seedWelcomeNote: false);
        var kanban = new KanbanService(vault);
        return (vault, kanban, new QuickCaptureService(vault, kanban));
    }

    [Fact]
    public void Capture_Inbox_CreatesFolderAndMarkdownNote()
    {
        var (vault, _, service) = CreateServices();

        var result = service.Capture("Idea principal\n\nDetalles importantes", QuickCaptureDestination.InboxNote);

        Assert.Equal("Idea principal", result.Title);
        Assert.NotNull(result.Path);
        Assert.Equal(Path.Combine(vault.VaultPath, "Inbox"), Path.GetDirectoryName(result.Path));
        Assert.Equal("# Idea principal\n\nDetalles importantes\n", File.ReadAllText(result.Path!));
    }

    [Fact]
    public void Capture_Inbox_AvoidsFilenameCollisions()
    {
        var (_, _, service) = CreateServices();

        var first = service.Capture("La misma idea", QuickCaptureDestination.InboxNote);
        var second = service.Capture("La misma idea", QuickCaptureDestination.InboxNote);

        Assert.NotEqual(first.Path, second.Path);
        Assert.EndsWith("La misma idea 2.md", second.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_Kanban_AddsRealCardToChosenColumnAndPersistsIt()
    {
        var (vault, kanban, service) = CreateServices();
        var target = kanban.Board.Columns.Last();
        var changed = 0;
        kanban.BoardChanged += () => changed++;

        var result = service.Capture("Preparar demo\nCon capturas y guion", QuickCaptureDestination.KanbanCard, target.Id);

        var card = Assert.Single(target.Cards, candidate => candidate.Id == result.CardId);
        Assert.Equal("Preparar demo", card.Title);
        Assert.Equal("Con capturas y guion", card.Description);
        Assert.Equal(1, changed);
        var reloaded = new KanbanService(vault);
        Assert.Contains(reloaded.Board.Columns.SelectMany(column => column.Cards), candidate => candidate.Id == card.Id);
    }

    [Fact]
    public void Capture_Kanban_UsesFirstColumnWhenSavedColumnNoLongerExists()
    {
        var (_, kanban, service) = CreateServices();
        var first = kanban.Board.Columns[0];

        var result = service.Capture("Recoger paquete", QuickCaptureDestination.KanbanCard, "missing");

        Assert.Contains(first.Cards, card => card.Id == result.CardId);
    }

    [Fact]
    public void Capture_WithoutVault_RejectsWithoutDiscardingAnything()
    {
        var settings = new SettingsService(Path.Combine(_root, "config"));
        var vault = new VaultService(settings);
        var service = new QuickCaptureService(vault, new KanbanService(Path.Combine(_root, "kanban")));

        Assert.Throws<VaultUnavailableException>(() =>
            service.Capture("Idea", QuickCaptureDestination.InboxNote));
    }

    [Fact]
    public void HotkeyKeyMap_MapsDefaultGestureOnBothNativeBackends()
    {
        var gesture = KeyGesture.Parse("Ctrl+Alt+Space");

        Assert.True(HotkeyKeyMap.TryToWindowsVirtualKey(gesture.Key, out var windowsKey));
        Assert.Equal(0x20u, windowsKey);
        Assert.True(HotkeyKeyMap.TryToX11KeysymName(gesture.Key, out var keysym));
        Assert.Equal("space", keysym);
        Assert.True(HotkeyKeyMap.TryToMacKeyCode(gesture.Key, out var macKey));
        Assert.Equal(49u, macKey);
    }
}
