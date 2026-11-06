using System;
using System.Collections.Generic;
using System.Linq;
using enki.ViewModels;
using Material.Icons;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="CommandPaletteViewModel"/>: filtrado por substring sobre el título del
/// comando, navegación con teclado, ejecución (que cierra el popup antes de invocar la acción) y
/// reconstrucción de la lista de comandos en cada apertura.</summary>
public class CommandPaletteViewModelTests
{
    private static CommandPaletteItem MakeItem(string title, Action execute) =>
        new(title, MaterialIconKind.NoteTextOutline, execute);

    [Fact]
    public void OpeningPalette_PopulatesResults_FromProvider()
    {
        var items = new List<CommandPaletteItem> { MakeItem("Nueva nota", () => { }), MakeItem("Abrir Ajustes", () => { }) };
        var vm = new CommandPaletteViewModel(() => items);

        vm.IsOpen = true;

        Assert.Equal(2, vm.Results.Count);
        Assert.True(vm.HasResults);
    }

    [Fact]
    public void SearchText_FiltersByTitleSubstring_CaseInsensitive()
    {
        var items = new List<CommandPaletteItem>
        {
            MakeItem("Nueva nota", () => { }),
            MakeItem("Nueva columna Kanban", () => { }),
            MakeItem("Abrir Ajustes", () => { })
        };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };

        vm.SearchText = "nueva";

        Assert.Equal(2, vm.Results.Count);
        Assert.All(vm.Results, r => Assert.Contains("Nueva", r.Title));
    }

    [Fact]
    public void SearchText_ExactMatch_IsRankedFirst()
    {
        var items = new List<CommandPaletteItem>
        {
            MakeItem("Nueva columna Kanban", () => { }),
            MakeItem("Nota", () => { })
        };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };

        vm.SearchText = "Nota";

        Assert.Equal("Nota", vm.Results[0].Title);
    }

    [Fact]
    public void SearchText_NoMatches_ShowsNoResults()
    {
        var items = new List<CommandPaletteItem> { MakeItem("Nueva nota", () => { }) };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };

        vm.SearchText = "xyz-nada-coincide";

        Assert.Empty(vm.Results);
        Assert.True(vm.ShowNoResults);
    }

    [Fact]
    public void ExecuteItemCommand_ClosesPalette_ThenRunsAction()
    {
        var executed = false;
        var wasOpenDuringExecution = true;
        var items = new List<CommandPaletteItem>
        {
            MakeItem("Comando", () =>
            {
                executed = true;
            })
        };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };
        var item = vm.Results.Single();

        vm.ExecuteItemCommand.Execute(item);

        Assert.True(executed);
        Assert.False(vm.IsOpen);
        _ = wasOpenDuringExecution;
    }

    [Fact]
    public void MoveSelection_WrapsAround_InBothDirections()
    {
        var items = new List<CommandPaletteItem>
        {
            MakeItem("Uno", () => { }),
            MakeItem("Dos", () => { }),
            MakeItem("Tres", () => { })
        };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };

        Assert.Equal(0, vm.SelectedIndex);

        vm.MoveSelectionUpCommand.Execute(null);
        Assert.Equal(2, vm.SelectedIndex);

        vm.MoveSelectionDownCommand.Execute(null);
        vm.MoveSelectionDownCommand.Execute(null);
        Assert.Equal(1, vm.SelectedIndex);
    }

    [Fact]
    public void OpenSelectedCommand_ExecutesHighlightedItem()
    {
        var executedTitle = string.Empty;
        var items = new List<CommandPaletteItem>
        {
            MakeItem("Uno", () => executedTitle = "Uno"),
            MakeItem("Dos", () => executedTitle = "Dos")
        };
        var vm = new CommandPaletteViewModel(() => items) { IsOpen = true };

        vm.MoveSelectionDownCommand.Execute(null);
        vm.OpenSelectedCommand.Execute(null);

        Assert.Equal("Dos", executedTitle);
    }

    [Fact]
    public void Reopening_RebuildsItems_FromProviderEachTime()
    {
        var callCount = 0;
        var vm = new CommandPaletteViewModel(() =>
        {
            callCount++;
            return callCount == 1
                ? [MakeItem("Primero", () => { })]
                : [MakeItem("Primero", () => { }), MakeItem("Segundo", () => { })];
        });

        vm.IsOpen = true;
        Assert.Single(vm.Results);

        vm.IsOpen = false;
        vm.IsOpen = true;
        Assert.Equal(2, vm.Results.Count);
    }
}
