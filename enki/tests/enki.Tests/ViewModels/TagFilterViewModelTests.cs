using System.Collections.Generic;
using System.Linq;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class TagFilterViewModelTests
{
    [Fact]
    public void Reload_PopulatesItemsFromTags()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["casa", "urgente"]);

        Assert.Equal(2, vm.Items.Count);
        Assert.Contains("casa", vm.Items.Select(i => i.Tag));
        Assert.Contains("urgente", vm.Items.Select(i => i.Tag));
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void SelectedTags_ReturnsOnlySelected()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["casa", "urgente"]);

        vm.Items[0].ToggleCommand.Execute(null);

        Assert.Equal(["casa"], vm.SelectedTags);
    }

    [Fact]
    public void Changed_RaisesOnToggle()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["casa"]);

        var raised = false;
        vm.Changed += () => raised = true;

        vm.Items[0].ToggleCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void Reload_KeepsExistingSelection()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["casa", "urgente"]);
        vm.Items.Single(i => i.Tag == "urgente").IsSelected = true;

        vm.Reload(["urgente", "trabajo"]);

        Assert.Equal(2, vm.Items.Count);
        Assert.DoesNotContain("casa", vm.Items.Select(i => i.Tag));
        Assert.True(vm.Items.Single(i => i.Tag == "urgente").IsSelected);
        Assert.False(vm.Items.Single(i => i.Tag == "trabajo").IsSelected);
    }

    [Fact]
    public void Reload_IgnoresEmptyTags()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["casa", "", "   ", "urgente"]);

        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public void Reload_DeduplicatesCaseInsensitive()
    {
        var vm = new TagFilterViewModel();
        vm.Reload(["Casa", "casa", "CASA", "urgente"]);

        Assert.Equal(2, vm.Items.Count);
    }
}
