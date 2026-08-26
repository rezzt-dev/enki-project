using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="NoteLinkPickerViewModel"/> — the editor's "enlazar nota" popover, same shape
/// as <c>SlashCommandsViewModel</c> (filter-as-you-type, arrow-key navigation, Enter to confirm) but backed
/// by an arbitrary note set instead of a fixed command list.</summary>
public class NoteLinkPickerViewModelTests
{
    private static NoteFile Note(string path, string title) => new() { FullPath = path, Title = title };

    private static NoteLinkPickerViewModel CreateOpenedPicker()
    {
        var vm = new NoteLinkPickerViewModel();
        vm.Open(
        [
            new NotePickerItemViewModel(Note("/vault/Reunión.md", "Reunión"), "Vault"),
            new NotePickerItemViewModel(Note("/vault/.sessions/a/Proyecto Alfa.md", "Proyecto Alfa"), "A"),
            new NotePickerItemViewModel(Note("/vault/.sessions/b/Proyecto Beta.md", "Proyecto Beta"), "B")
        ]);
        return vm;
    }

    [Fact]
    public void Open_PopulatesFilteredResults_WithEveryNote_AndSelectsTheFirstOne()
    {
        var vm = CreateOpenedPicker();

        Assert.True(vm.IsOpen);
        Assert.Equal(3, vm.FilteredResults.Count);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.True(vm.FilteredResults[0].IsSelected);
    }

    [Fact]
    public void Filter_NarrowsResultsByTitle_CaseInsensitive()
    {
        var vm = CreateOpenedPicker();

        vm.Filter = "proyecto";

        Assert.Equal(2, vm.FilteredResults.Count);
        Assert.All(vm.FilteredResults, r => Assert.Contains("Proyecto", r.Title));
    }

    [Fact]
    public void Filter_MatchingNothing_LeavesFilteredResultsEmpty()
    {
        var vm = CreateOpenedPicker();

        vm.Filter = "no existe";

        Assert.Empty(vm.FilteredResults);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public void MoveDownCommand_WrapsAroundToTheFirstRow_PastTheLastOne()
    {
        var vm = CreateOpenedPicker();
        vm.MoveDownCommand.Execute(null);
        vm.MoveDownCommand.Execute(null);
        Assert.Equal(2, vm.SelectedIndex);

        vm.MoveDownCommand.Execute(null);

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void MoveUpCommand_WrapsAroundToTheLastRow_BeforeTheFirstOne()
    {
        var vm = CreateOpenedPicker();

        vm.MoveUpCommand.Execute(null);

        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public void ConfirmSelectedCommand_RaisesNoteSelected_WithTheHighlightedRow_AndCloses()
    {
        var vm = CreateOpenedPicker();
        vm.MoveDownCommand.Execute(null);
        NotePickerItemViewModel? selected = null;
        vm.NoteSelected += item => selected = item;

        vm.ConfirmSelectedCommand.Execute(null);

        Assert.NotNull(selected);
        Assert.Equal("Proyecto Alfa", selected!.Title);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void SelectCommand_RaisesNoteSelected_WithTheClickedItem_RegardlessOfSelectedIndex()
    {
        var vm = CreateOpenedPicker();
        NotePickerItemViewModel? selected = null;
        vm.NoteSelected += item => selected = item;

        vm.SelectCommand.Execute(vm.FilteredResults[2]);

        Assert.Equal("Proyecto Beta", selected!.Title);
        Assert.False(vm.IsOpen);
    }
}
