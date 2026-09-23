using System;
using System.IO;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public sealed class QuickCaptureViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-quick-capture-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (QuickCaptureViewModel ViewModel, VaultService Vault, KanbanService Kanban,
        VaultSettingsService VaultSettings, NotificationService Notifications) CreateViewModel(bool withVault = true)
    {
        var settings = new SettingsService(Path.Combine(_root, "config"));
        var vault = new VaultService(settings);
        if (withVault) vault.InitializeVault(Path.Combine(_root, "vault"), seedWelcomeNote: false);
        var kanban = withVault
            ? new KanbanService(vault)
            : new KanbanService(Path.Combine(_root, "kanban"));
        var vaultSettings = new VaultSettingsService(vault);
        var notifications = new NotificationService(settings, Path.Combine(_root, "notifications"));
        var viewModel = new QuickCaptureViewModel(new QuickCaptureService(vault, kanban), vault, kanban,
            vaultSettings, notifications);
        return (viewModel, vault, kanban, vaultSettings, notifications);
    }

    [Fact]
    public void Cancel_ClosesAndPreservesDraftForNextOpening()
    {
        var (viewModel, _, _, _, _) = CreateViewModel();
        var closes = 0;
        viewModel.CloseRequested += () => closes++;
        viewModel.Draft = "No perder esta idea";

        viewModel.CancelCommand.Execute(null);
        viewModel.PrepareToShow();

        Assert.Equal(1, closes);
        Assert.Equal("No perder esta idea", viewModel.Draft);
    }

    [Fact]
    public void Submit_Inbox_ClearsDraftClosesAndNotifies()
    {
        var (viewModel, vault, _, _, notifications) = CreateViewModel();
        var closes = 0;
        viewModel.CloseRequested += () => closes++;
        viewModel.Draft = "Una idea capturada";

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.Draft);
        Assert.Equal(1, closes);
        Assert.Single(Directory.GetFiles(Path.Combine(vault.VaultPath, "Inbox"), "*.md"));
        Assert.Contains(notifications.Notifications, item => item.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public void DestinationAndColumn_ArePersistedPerVault()
    {
        var (viewModel, _, kanban, vaultSettings, _) = CreateViewModel();
        var kanbanDestination = Assert.Single(viewModel.Destinations,
            option => option.Value == QuickCaptureDestination.KanbanCard);
        var lastColumn = viewModel.KanbanColumns[^1];

        viewModel.SelectedDestination = kanbanDestination;
        viewModel.SelectedKanbanColumn = lastColumn;

        var reloaded = new VaultSettingsService(new VaultService(new SettingsService(Path.Combine(_root, "config"))));
        Assert.Equal(QuickCaptureDestination.KanbanCard, reloaded.Settings.QuickCaptureDestination);
        Assert.Equal(kanban.Board.Columns[^1].Id, reloaded.Settings.QuickCaptureKanbanColumnId);
    }

    /// <summary>El popup de la bandeja de entrada de la barra de título comparte este viewmodel con la
    /// mini-ventana global, así que guardar desde el popup tiene que cerrarlo — su IsOpen es lo único que
    /// lo cierra; CloseRequested solo lo escucha la ventana.</summary>
    [Fact]
    public void Toggle_OpensAndSubmitClosesTheTitleBarPopup()
    {
        var (viewModel, _, _, _, _) = CreateViewModel();

        viewModel.ToggleCommand.Execute(null);
        Assert.True(viewModel.IsOpen);

        viewModel.Draft = "Idea desde la barra de título";
        viewModel.SubmitCommand.Execute(null);

        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void Toggle_ClosesAnAlreadyOpenPopup_WithoutLosingTheDraft()
    {
        var (viewModel, _, _, _, _) = CreateViewModel();
        viewModel.ToggleCommand.Execute(null);
        viewModel.Draft = "A medio escribir";

        viewModel.ToggleCommand.Execute(null);

        Assert.False(viewModel.IsOpen);
        Assert.Equal("A medio escribir", viewModel.Draft);
    }

    [Fact]
    public void Submit_WithoutVault_KeepsDraftAndShowsReason()
    {
        var (viewModel, _, _, _, _) = CreateViewModel(withVault: false);
        viewModel.Draft = "Idea pendiente";

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal("Idea pendiente", viewModel.Draft);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
    }

    [Fact]
    public void Submit_KanbanWithoutColumns_KeepsDraftAndDoesNotClose()
    {
        var (viewModel, _, kanban, _, _) = CreateViewModel();
        kanban.Board.Columns.Clear();
        viewModel.ReloadDestinations();
        viewModel.SelectedDestination = Assert.Single(viewModel.Destinations,
            option => option.Value == QuickCaptureDestination.KanbanCard);
        viewModel.Draft = "Tarjeta pendiente";
        var closes = 0;
        viewModel.CloseRequested += () => closes++;

        viewModel.SubmitCommand.Execute(null);

        Assert.Equal("Tarjeta pendiente", viewModel.Draft);
        Assert.Equal(0, closes);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ErrorMessage));
    }
}
