using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public sealed class KanbanMultipleBoardsViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-board-vm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private (KanbanViewModel Vm, KanbanService Service, SettingsService Settings) Create()
    {
        var settings = new SettingsService(_directory);
        settings.Settings.KanbanMultipleBoardsEnabled = true;
        var auth = new PolarAuthService(settings);
        var tasks = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), tasks, reminders, settings);
        var service = new KanbanService(_directory);
        var vm = new KanbanViewModel(service, auth, new PolarTaskManagementViewModel(tasks, sync), settingsService: settings);
        return (vm, service, settings);
    }
    private static void Select(KanbanViewModel vm, KanbanBoard board)
    {
        vm.Reload();
        vm.SelectBoardCommand.Execute(vm.BoardOptions.Single(b => b.Board == board));
    }

    [Fact]
    public void Selection_PersistsAndRemembersFilters_WithoutDataEvents()
    {
        var (vm, service, settings) = Create();
        var original = service.DefaultBoard;
        var second = service.CreateBoard("Estudios");
        vm.SearchText = "borrador";
        vm.QuickFilter = KanbanQuickFilter.HighPriority;
        var changes = 0;
        service.BoardChanged += () => changes++;
        Select(vm, second);
        Assert.Empty(vm.SearchText);
        Assert.Equal(second.Id, settings.Settings.KanbanLastSelectedBoardId);
        Assert.Equal(original, service.DefaultBoard);
        vm.SearchText = "examen";
        Select(vm, original);
        Assert.Equal("borrador", vm.SearchText);
        Assert.Equal(KanbanQuickFilter.HighPriority, vm.QuickFilter);
        Select(vm, second);
        Assert.Equal("examen", vm.SearchText);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void GlobalNavigation_ResolvesArchivedOwner_AndCancelPreservesDraft()
    {
        var (vm, service, _) = Create();
        var second = service.CreateBoard("Personal");
        var archived = new KanbanCard { Id = "archived", Title = "Histórico", ArchivedAtUtc = DateTime.UtcNow, CompletedAtUtc = DateTime.UtcNow };
        second.Columns[2].Cards.Add(archived);
        service.Save();
        vm.OpenCreateCardCommand.Execute(vm.Columns[0]);
        vm.CardEditor!.Title = "No perder";
        var editor = vm.CardEditor;
        vm.OpenCardById(archived.Id);
        Assert.True(vm.IsBoardSwitchPending);
        vm.CancelBoardSwitchCommand.Execute(null);
        Assert.Same(editor, vm.CardEditor);
        Assert.Equal(service.DefaultBoard, vm.CurrentBoard);
        vm.OpenCardById(archived.Id);
        vm.DiscardAndSwitchBoardCommand.Execute(null);
        Assert.Equal(second, vm.CurrentBoard);
        Assert.Same(archived, vm.CardDetail!.SourceCard.Card);
        Assert.NotNull(archived.ArchivedAtUtc);
    }

    [Fact]
    public void SaveAndSwitch_PersistsDraftInOriginalBoard()
    {
        var (vm, service, _) = Create();
        var original = vm.CurrentBoard;
        var second = service.CreateBoard("Destino");
        vm.OpenCreateCardCommand.Execute(vm.Columns[0]);
        vm.CardEditor!.Title = "Guardar aquí";
        Select(vm, second);
        vm.SaveAndSwitchBoardCommand.Execute(null);
        Assert.Equal(second, vm.CurrentBoard);
        Assert.Contains(original.Columns.SelectMany(c => c.Cards), c => c.Title == "Guardar aquí");
        Assert.Empty(second.Columns.SelectMany(c => c.Cards));
    }

    [Fact]
    public void FailedSaveAndSwitch_KeepsDraftAndOriginalSelection()
    {
        var (vm, service, _) = Create();
        var second = service.CreateBoard("Destino");
        vm.OpenCreateCardCommand.Execute(vm.Columns[0]);
        vm.CardEditor!.Title = "Conservar";
        Select(vm, second);
        File.Delete(Path.Combine(_directory, "kanban.json"));
        Directory.CreateDirectory(Path.Combine(_directory, "kanban.json"));
        vm.SaveAndSwitchBoardCommand.Execute(null);
        Assert.Equal(service.DefaultBoard, vm.CurrentBoard);
        Assert.Equal("Conservar", vm.CardEditor!.Title);
        Assert.True(vm.IsBoardSwitchPending);
        Assert.DoesNotContain(service.AllColumns.SelectMany(c => c.Cards), c => c.Title == "Conservar");
    }

    [Fact]
    public void InconsistentPreference_RecoversMultipleMode_AndVaultChangeClearsTransientState()
    {
        var (vm, service, settings) = Create();
        var second = service.CreateBoard("Estudios");
        settings.Settings.KanbanMultipleBoardsEnabled = false;
        settings.Save();
        Assert.True(settings.Settings.KanbanMultipleBoardsEnabled);
        Assert.True(vm.MultipleBoardsEnabled);
        Select(vm, second);
        vm.SearchText = "no conservar";
        vm.CreateBoardCommand.Execute(null);
        vm.CloseForVaultChange();
        Assert.Empty(vm.SearchText);
        Assert.False(vm.IsBoardNameEditorOpen);
        Assert.False(vm.IsBoardSwitchPending);
        Assert.Null(vm.CardEditor);
    }

    [Fact]
    public void MoveForm_RequiresExplicitColumnForCustomRole_AndCancelDoesNotMutate()
    {
        var (vm, service, _) = Create();
        var second = service.CreateBoard("Otro");
        var custom = new KanbanColumn { Id = "custom", Title = "Revisar" };
        var card = new KanbanCard { Id = "card", Title = "Tarjeta" };
        custom.Cards.Add(card);
        service.DefaultBoard.Columns.Add(custom);
        vm.Reload();
        vm.OpenMoveCardCommand.Execute(vm.Columns.Last().Cards[0]);
        Assert.Equal(second, vm.MoveTargetBoard);
        Assert.Null(vm.MoveTargetColumn);
        vm.CancelMoveCardCommand.Execute(null);
        Assert.Same(card, custom.Cards[0]);
        Assert.Empty(second.Columns.SelectMany(c => c.Cards));
    }
}
