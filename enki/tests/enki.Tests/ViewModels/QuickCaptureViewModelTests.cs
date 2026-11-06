using System;
using System.IO;
using System.Linq;
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
    public void Submit_ToAnotherBoard_UsesExplicitDestination_AndRejectsDeletionDuringDraft()
    {
        var (vm, _, service, settings, _) = CreateViewModel();
        var board = service.CreateBoard("Estudios");
        vm.PrepareToShow();
        vm.SelectedDestination = vm.Destinations.Single(d => d.Value == QuickCaptureDestination.KanbanCard);
        vm.SelectedKanbanBoard = board;
        vm.Draft = "Examen";
        vm.SubmitCommand.Execute(null);
        Assert.Equal(board.Id, settings.Settings.QuickCaptureKanbanBoardId);
        Assert.Equal("Examen", Assert.Single(board.Columns[0].Cards).Title);
        board.Columns[0].Cards.Clear();
        vm.Draft = "Conservar borrador";
        service.DeleteEmptyBoard(board.Id);
        vm.SubmitCommand.Execute(null);
        Assert.Equal("Conservar borrador", vm.Draft);
        Assert.NotNull(vm.ErrorMessage);
        vm.PrepareToShow();
        Assert.Same(service.DefaultBoard, vm.SelectedKanbanBoard);
        Assert.NotNull(vm.SelectedKanbanColumn);
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
        Assert.Equal(kanban.DefaultBoard.Columns[^1].Id, reloaded.Settings.QuickCaptureKanbanColumnId);
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
        kanban.DefaultBoard.Columns.Clear();
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

    [Fact]
    public void TryCaptureExternal_UsesTheRememberedDestination_AndKeepsTheDraftUntouched()
    {
        var (vm, vault, kanban, settings, notifications) = CreateViewModel();
        vm.Draft = "Borrador a medias";
        settings.Settings.QuickCaptureDestination = QuickCaptureDestination.InboxNote;

        var saved = vm.TryCaptureExternal("Llamar a Marta\ncon el presupuesto");

        Assert.True(saved);
        Assert.Equal("Borrador a medias", vm.Draft);
        var note = Assert.Single(vault.GetAllNotesRecursive(Path.Combine(vault.VaultPath, "Inbox")));
        Assert.Equal("Llamar a Marta", note.Title);
        Assert.Contains("con el presupuesto", File.ReadAllText(note.FullPath));
        Assert.NotEmpty(notifications.Notifications);

        settings.Settings.QuickCaptureDestination = QuickCaptureDestination.KanbanCard;
        Assert.True(vm.TryCaptureExternal("Tarjeta desde el terminal"));
        Assert.Contains(kanban.AllColumns.SelectMany(c => c.Cards), c => c.Title == "Tarjeta desde el terminal");
    }

    [Fact]
    public void TryCaptureExternal_WithAStaleKanbanColumn_FallsBackToTheDefaultBoard()
    {
        var (vm, _, kanban, settings, _) = CreateViewModel();
        settings.Settings.QuickCaptureDestination = QuickCaptureDestination.KanbanCard;
        settings.Settings.QuickCaptureKanbanBoardId = "tablero-borrado";
        settings.Settings.QuickCaptureKanbanColumnId = "columna-borrada";

        Assert.True(vm.TryCaptureExternal("Idea que no se pierde"));

        Assert.Contains(kanban.DefaultBoard.Columns.SelectMany(c => c.Cards), c => c.Title == "Idea que no se pierde");
    }

    [Fact]
    public void TryCaptureExternal_WithoutAVault_FailsAndNotifies()
    {
        var (vm, _, _, _, notifications) = CreateViewModel(withVault: false);

        Assert.False(vm.TryCaptureExternal("Sin dónde guardar"));
        Assert.NotEmpty(notifications.Notifications);
    }
}
