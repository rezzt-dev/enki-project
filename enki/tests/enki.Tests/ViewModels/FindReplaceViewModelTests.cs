using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 8 change to <see cref="FindReplaceViewModel"/> (análisis §4.2): it used to
/// re-scan the whole document on every keystroke in the search box via <c>RefreshStatus</c>; now it
/// debounces that the same way the rest of the editor's refresh timers do (<c>EditorDebounce.Delay</c>).
/// Since nothing in these tests lets the underlying <c>DispatcherTimer</c> tick, an assertion that
/// <c>OperationRequested</c> didn't fire synchronously is really asserting "the refresh didn't happen
/// synchronously" — same bar as <see cref="EditorOutlineAndStatsDebounceTests"/>. Opening/closing the panel
/// (<c>IsOpen</c>) is a discrete, non-keystroke event and stays immediate.</summary>
public class FindReplaceViewModelTests
{
    [Fact]
    public void SearchTextChanged_DoesNotImmediatelyRaiseOperationRequested()
    {
        var vm = new FindReplaceViewModel();
        var raised = false;
        vm.OperationRequested += (_, _, _, _) => raised = true;

        vm.SearchText = "hola";

        Assert.False(raised);
    }

    [Fact]
    public void MatchCaseChanged_DoesNotImmediatelyRaiseOperationRequested()
    {
        var vm = new FindReplaceViewModel();
        var raised = false;
        vm.OperationRequested += (_, _, _, _) => raised = true;

        vm.MatchCase = true;

        Assert.False(raised);
    }

    [Fact]
    public void OpeningThePanel_ImmediatelyRaisesRefreshStatus()
    {
        var vm = new FindReplaceViewModel();
        FindReplaceOperation? operation = null;
        vm.OperationRequested += (op, _, _, _) => operation = op;

        vm.IsOpen = true;

        Assert.Equal(FindReplaceOperation.RefreshStatus, operation);
    }

    [Fact]
    public void ClosingThePanel_ImmediatelyRaisesRefreshStatus()
    {
        var vm = new FindReplaceViewModel { IsOpen = true };
        FindReplaceOperation? operation = null;
        vm.OperationRequested += (op, _, _, _) => operation = op;

        vm.IsOpen = false;

        Assert.Equal(FindReplaceOperation.RefreshStatus, operation);
    }

    [Fact]
    public void RefreshStatus_RaisesOperationRequestedWithCurrentFields()
    {
        var vm = new FindReplaceViewModel { SearchText = "foo", ReplaceText = "bar", MatchCase = true };
        FindReplaceOperation? operation = null;
        string? searchText = null;
        string? replaceText = null;
        bool? matchCase = null;
        vm.OperationRequested += (op, s, r, m) => (operation, searchText, replaceText, matchCase) = (op, s, r, m);

        vm.RefreshStatus();

        Assert.Equal(FindReplaceOperation.RefreshStatus, operation);
        Assert.Equal("foo", searchText);
        Assert.Equal("bar", replaceText);
        Assert.True(matchCase);
    }
}
