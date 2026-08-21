using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class KanbanColumnViewModelTests
{
    private static KanbanColumn NewColumn(string id = "c1", string title = "Columna", bool isCore = false) =>
        new() { Id = id, Title = title, IsCore = isCore };

    [Fact]
    public void Constructor_WrapsExistingCards()
    {
        var column = NewColumn();
        column.Cards.Add(new KanbanCard { Id = "1", Title = "Uno" });
        column.Cards.Add(new KanbanCard { Id = "2", Title = "Dos" });

        var vm = new KanbanColumnViewModel(column);

        Assert.Equal(2, vm.Cards.Count);
        Assert.Equal(2, vm.CardCount);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void IsEmpty_UpdatesWhenCardsCollectionChanges()
    {
        var vm = new KanbanColumnViewModel(NewColumn());
        Assert.True(vm.IsEmpty);

        vm.Cards.Add(new KanbanCardViewModel(new KanbanCard { Id = "1", Title = "Uno" }));

        Assert.False(vm.IsEmpty);
        Assert.Equal(1, vm.CardCount);
    }

    [Fact]
    public void ApplyTitle_UpdatesModelAndNotifies()
    {
        var vm = new KanbanColumnViewModel(NewColumn());
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(KanbanColumnViewModel.Title);

        vm.ApplyTitle("Nuevo título");

        Assert.Equal("Nuevo título", vm.Title);
        Assert.Equal("Nuevo título", vm.Column.Title);
        Assert.True(raised);
    }

    [Fact]
    public void IsCore_ReflectsModel()
    {
        var core = new KanbanColumnViewModel(NewColumn(isCore: true));
        var regular = new KanbanColumnViewModel(NewColumn(id: "c2", isCore: false));

        Assert.True(core.IsCore);
        Assert.False(regular.IsCore);
    }

    [Fact]
    public void ApplyFilters_HidesCardsWithoutMatchingTag()
    {
        var column = NewColumn();
        var matching = new KanbanCard { Id = "1", Title = "Uno", Tags = ["urgente"] };
        var nonMatching = new KanbanCard { Id = "2", Title = "Dos", Tags = ["otra"] };
        column.Cards.Add(matching);
        column.Cards.Add(nonMatching);
        var vm = new KanbanColumnViewModel(column);

        vm.ApplyFilters(["urgente"], string.Empty);

        Assert.True(vm.Cards[0].IsVisible);
        Assert.False(vm.Cards[1].IsVisible);
    }

    [Fact]
    public void ApplyFilters_HidesCardsNotMatchingSearchText()
    {
        var column = NewColumn();
        var matching = new KanbanCard { Id = "1", Title = "Revisar contrato" };
        var nonMatching = new KanbanCard { Id = "2", Title = "Comprar café" };
        column.Cards.Add(matching);
        column.Cards.Add(nonMatching);
        var vm = new KanbanColumnViewModel(column);

        vm.ApplyFilters([], "contrato");

        Assert.True(vm.Cards[0].IsVisible);
        Assert.False(vm.Cards[1].IsVisible);
    }

    [Fact]
    public void Sort_ByPriority_OrdersHighFirstInViewAndModel()
    {
        var column = NewColumn();
        var low = new KanbanCard { Id = "1", Title = "Baja", Priority = KanbanPriority.Low };
        var high = new KanbanCard { Id = "2", Title = "Alta", Priority = KanbanPriority.High };
        column.Cards.Add(low);
        column.Cards.Add(high);
        var vm = new KanbanColumnViewModel(column);

        vm.Sort(KanbanCardSortMode.Priority);

        Assert.Equal("2", vm.Cards[0].Id);
        Assert.Equal("2", column.Cards[0].Id);
    }

    [Fact]
    public void Sort_Manual_LeavesOrderUnchanged()
    {
        var column = NewColumn();
        var first = new KanbanCard { Id = "1", Title = "Uno", Priority = KanbanPriority.Low };
        var second = new KanbanCard { Id = "2", Title = "Dos", Priority = KanbanPriority.High };
        column.Cards.Add(first);
        column.Cards.Add(second);
        var vm = new KanbanColumnViewModel(column);

        vm.Sort(KanbanCardSortMode.Manual);

        Assert.Equal("1", vm.Cards[0].Id);
    }

    [Fact]
    public void BeginRename_SeedsEditingTitle_FromCurrentTitle()
    {
        var vm = new KanbanColumnViewModel(NewColumn(title: "Por hacer"));

        vm.BeginRenameCommand.Execute(null);

        Assert.True(vm.IsRenaming);
        Assert.Equal("Por hacer", vm.EditingTitle);
    }

    [Fact]
    public void CancelRename_ClearsIsRenaming()
    {
        var vm = new KanbanColumnViewModel(NewColumn());
        vm.BeginRenameCommand.Execute(null);

        vm.CancelRenameCommand.Execute(null);

        Assert.False(vm.IsRenaming);
    }
}
