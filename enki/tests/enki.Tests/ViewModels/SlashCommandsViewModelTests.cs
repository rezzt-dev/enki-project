using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Would have caught the "/tabla" bug before it reached production
/// (docs/version-alfa-3/01-analisis-detallado.md §4.2): <c>SlashCommandsViewModel.AllCommands</c>
/// referenced <c>Editor_SlashCommand_Table_InsertText</c> as an <see cref="Models.SlashCommand.InsertTextKey"/>,
/// but that key didn't exist in any <c>.resx</c> — <see cref="Models.SlashCommand.InsertText"/> silently
/// resolved to a resource-key-shaped fallback string (bracketed in Debug, the bare key in Release) instead
/// of real Markdown, and that fallback got inserted verbatim into the user's note. Iterates
/// <c>AllCommands</c> directly rather than a hardcoded list of keys, so a future command with the same
/// mistake fails here instead of shipping.</summary>
public class SlashCommandsViewModelTests
{
    private static readonly string s_sourceResxPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "localization", "Strings.resx"));

    [Fact]
    public void InsertText_ResolvesToRealContent_ForEveryCommand()
    {
        var resourceKeys = LoadResourceKeys();

        foreach (var command in SlashCommandsViewModel.AllCommands)
        {
            if (command.InsertTextKey is { } key)
            {
                Assert.True(resourceKeys.Contains(key),
                    $"Comando '{command.Key}': InsertTextKey '{key}' no existe en Strings.resx.");
            }

            var resolved = command.InsertText;
            Assert.DoesNotContain("[[", resolved);
            Assert.NotEqual(command.InsertTextKey, resolved);
        }
    }

    /// <summary>A command's <see cref="Models.SlashCommand.CursorOffset"/> must never land past the end of
    /// its own resolved <see cref="Models.SlashCommand.InsertText"/> — exactly the second half of the
    /// "/tabla" bug (<c>CursorOffset = 53</c> against a resolved fallback far shorter than that), which
    /// would have thrown from <c>TextEditor.CaretOffset</c> once the missing-key half of the bug above was
    /// fixed but this one wasn't.</summary>
    [Fact]
    public void CursorOffset_NeverExceedsInsertTextLength_ForEveryCommand()
    {
        foreach (var command in SlashCommandsViewModel.AllCommands)
        {
            Assert.InRange(command.CursorOffset, 0, command.InsertText.Length);
        }
    }

    private static HashSet<string> LoadResourceKeys()
    {
        var document = XDocument.Load(s_sourceResxPath);
        return document.Root!.Elements("data")
            .Select(e => e.Attribute("name")?.Value!)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();
    }

    // ===================== Comportamiento de la lista filtrada =====================

    [Fact]
    public void OnIsOpenChanged_ToOpen_ResetsFilterAndListsUpToTenCommands()
    {
        var vm = new SlashCommandsViewModel { Filter = "tabla" };

        vm.IsOpen = true;

        Assert.Equal(string.Empty, vm.Filter);
        Assert.Equal(0, vm.SelectedIndex);
        // Refresh() caps FilteredCommands at 10 entries even with an empty filter — AllCommands currently
        // has 11, so this also guards against the cap silently hiding a real command if the list shrinks.
        Assert.Equal(Math.Min(10, SlashCommandsViewModel.AllCommands.Count), vm.FilteredCommands.Count);
    }

    [Fact]
    public void Refresh_FiltersByKeyOrLabel_CaseInsensitive()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };

        vm.Filter = "TABLA";

        var match = Assert.Single(vm.FilteredCommands);
        Assert.Equal("tabla", match.Key);
    }

    [Fact]
    public void Refresh_WithNoMatches_LeavesFilteredCommandsEmptyAndSelectedCommandNull()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };

        vm.Filter = "no-existe-este-comando";

        Assert.Empty(vm.FilteredCommands);
        Assert.Null(vm.SelectedCommand);
    }

    [Fact]
    public void MoveDown_WrapsAroundToFirstCommandAfterTheLast()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };
        vm.SelectedIndex = vm.FilteredCommands.Count - 1;

        vm.MoveDownCommand.Execute(null);

        Assert.Equal(0, vm.SelectedIndex);
        Assert.True(vm.FilteredCommands[0].IsSelected);
    }

    [Fact]
    public void MoveUp_WrapsAroundToLastCommandBeforeTheFirst()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };
        vm.SelectedIndex = 0;

        vm.MoveUpCommand.Execute(null);

        Assert.Equal(vm.FilteredCommands.Count - 1, vm.SelectedIndex);
    }

    [Fact]
    public void MoveDown_WithNoFilteredCommands_DoesNotThrow()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };
        vm.Filter = "no-existe-este-comando";

        var exception = Record.Exception(() => vm.MoveDownCommand.Execute(null));

        Assert.Null(exception);
    }

    [Fact]
    public void ConfirmSelected_RaisesCommandSelectedWithTheHighlightedCommandAndCloses()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true, Filter = "tabla" };
        SlashCommand? selected = null;
        vm.CommandSelected += cmd => selected = cmd;

        vm.ConfirmSelectedCommand.Execute(null);

        Assert.NotNull(selected);
        Assert.Equal("tabla", selected!.Key);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void SelectCommand_RaisesCommandSelectedForThatSpecificItemRegardlessOfSelectedIndex()
    {
        var vm = new SlashCommandsViewModel { IsOpen = true };
        var target = vm.FilteredCommands.Single(c => c.Key == "h2");
        SlashCommand? selected = null;
        vm.CommandSelected += cmd => selected = cmd;

        vm.SelectCommand.Execute(target);

        Assert.Equal("h2", selected!.Key);
        Assert.False(vm.IsOpen);
    }
}
