using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class KanbanMultipleBoardsUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-boards-ui-" + Guid.NewGuid().ToString("N"));
    public KanbanMultipleBoardsUiTests() => HeadlessApp.EnsureInitialized();
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private (KanbanView View, KanbanViewModel Vm, KanbanService Service, Window Window, SettingsService Settings) Create(double width = 1100)
    {
        var settings = new SettingsService(_directory);
        settings.Settings.KanbanMultipleBoardsEnabled = true;
        var auth = new PolarAuthService(settings);
        var tasks = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), tasks, reminders, settings);
        var service = new KanbanService(_directory);
        service.CreateBoard("Estudios");
        var vm = new KanbanViewModel(service, auth, new PolarTaskManagementViewModel(tasks, sync), settingsService: settings);
        var view = new KanbanView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (view, vm, service, window, settings);
    }

    [Theory]
    [InlineData(480)]
    [InlineData(900)]
    [InlineData(1400)]
    public void Toolbar_RightActionsAlignAndSettingsPersist(double width)
    {
        var (view, vm, service, window, settings) = Create(width);
        try
        {
            var board = service.DefaultBoard;
            service.CreateCard(board.Id, board.Columns[0].Id, new KanbanCard
            {
                Id = Guid.NewGuid().ToString(), Title = "Pendiente",
                Priority = KanbanPriority.High, DueDateUtc = DateTime.UtcNow.AddDays(-2)
            });
            service.CreateCard(board.Id, board.Columns[0].Id, new KanbanCard
            {
                Id = Guid.NewGuid().ToString(), Title = "Archivada", ArchivedAtUtc = DateTime.UtcNow
            });
            vm.Reload();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var toolbar = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("board-toolbar"));
            var archive = view.FindControl<Button>("KanbanArchiveTrigger")!;
            var sort = view.FindControl<Button>("KanbanSortTrigger")!;
            var sortOrigin = sort.TranslatePoint(default, toolbar)!.Value;
            Assert.InRange(Math.Abs(toolbar.Bounds.Width - sortOrigin.X - sort.Bounds.Width), 0, 1);
            var archiveOrigin = archive.TranslatePoint(default, toolbar)!.Value;
            Assert.True(archiveOrigin.X + archive.Bounds.Width < sortOrigin.X);
            var filters = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Where(b => b.Classes.Contains("toolbar-chip")).ToArray();
            Assert.Equal(2, filters.Length);
            foreach (var filter in filters)
            {
                Assert.True(filter.IsVisible);
                Assert.Single(filter.GetVisualDescendants().OfType<TextBlock>());
                var origin = filter.TranslatePoint(default, toolbar)!.Value;
                Assert.True(origin.X + filter.Bounds.Width < archiveOrigin.X);
            }

            settings.Settings.KanbanShowHighPriorityButton = false;
            settings.Settings.KanbanShowOverdueButton = false;
            settings.Settings.KanbanShowArchiveButton = false;
            settings.Settings.KanbanShowSortButton = false;
            settings.Save();
            Dispatcher.UIThread.RunJobs();
            Assert.All(filters, filter => Assert.False(filter.IsVisible));
            Assert.False(archive.IsVisible);
            Assert.False(sort.IsEffectivelyVisible);
            var reloaded = new SettingsService(_directory).Settings;
            Assert.False(reloaded.KanbanShowHighPriorityButton);
            Assert.False(reloaded.KanbanShowOverdueButton);
            Assert.False(reloaded.KanbanShowArchiveButton);
            Assert.False(reloaded.KanbanShowSortButton);
            settings.Settings.KanbanShowHighPriorityButton = true;
            settings.Settings.KanbanShowOverdueButton = true;
            settings.Settings.KanbanShowArchiveButton = true;
            settings.Settings.KanbanShowSortButton = true;
            settings.Save();
            Dispatcher.UIThread.RunJobs();
            Assert.All(filters, filter => Assert.True(filter.IsVisible));
            Assert.True(archive.IsVisible);
            Assert.True(sort.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(480)]
    [InlineData(900)]
    [InlineData(1400)]
    public void Header_ActionsDoNotOverlap_AndTitleAlignsWithContent(double width)
    {
        var (view, vm, _, window, _) = Create(width);
        try
        {
            var selector = view.FindControl<Button>("BoardSelectorButton")!;
            var heading = view.GetVisualDescendants().OfType<Grid>()
                .Single(p => p.Classes.Contains("board-heading"));
            var origin = selector.TranslatePoint(default, window)!.Value;
            var headingOrigin = heading.TranslatePoint(default, window)!.Value;
            Assert.Equal(headingOrigin.X, origin.X, 1);

            var controls = view.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsVisible && (c.Classes.Contains("board-tool") ||
                    c is RadioButton && c.Parent is StackPanel panel && panel.Classes.Contains("board-modes")))
                .ToArray();
            var bounds = controls.Select(c =>
                new Rect(c.TranslatePoint(default, window)!.Value, c.Bounds.Size)).ToArray();
            foreach (var rect in bounds)
            {
                Assert.InRange(Math.Abs(origin.Y + selector.Bounds.Height / 2 - rect.Center.Y), 0, 1);
                Assert.True(rect.Left >= origin.X + selector.Bounds.Width);
                Assert.InRange(rect.Right, 0, width);
            }
            for (var i = 0; i < bounds.Length; i++)
                for (var j = i + 1; j < bounds.Length; j++)
                    Assert.False(bounds[i].Intersects(bounds[j]), $"Acciones solapadas: {bounds[i]} / {bounds[j]}");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Selector_SearchAndKeyboardChooseBoard_EscapeReturnsFocus()
    {
        var (view, vm, service, window, _) = Create();
        try
        {
            vm.ToggleBoardSelectorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var search = view.FindControl<TextBox>("BoardSearchBox")!;
            Assert.True(search.IsFocused);
            search.Text = "inexistente";
            Assert.Empty(vm.BoardOptions);
            Assert.False(vm.HasBoardSearchResults);
            search.Text = "estud";
            Assert.Single(vm.BoardOptions);
            Assert.True(vm.HasBoardSearchResults);
            var list = view.FindControl<ListBox>("BoardList")!;
            list.SelectedIndex = 0;
            list.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Estudios", vm.BoardName);
            Assert.False(vm.IsBoardSelectorOpen);
            Assert.All(vm.Columns, c => Assert.Empty(c.Cards));
            Assert.Equal(service.Boards[1], vm.CurrentBoard);
            vm.ToggleBoardSelectorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsBoardSelectorOpen);
            Assert.True(view.FindControl<Button>("BoardSelectorButton")!.IsFocused);
        }
        finally { window.Close(); }
    }

    /// <summary>El selector limita la lista y mantiene accesible el pie con muchos tableros.</summary>
    [Theory]
    [InlineData(480)]
    [InlineData(1100)]
    public void Selector_LongNamesAndManyBoards_KeepActionsWithinPopover(double width)
    {
        var (view, vm, service, window, _) = Create(width);
        try
        {
            for (var i = 0; i < 12; i++) service.CreateBoard($"{i} {new string('W', 70)}");
            vm.Reload();
            vm.ToggleBoardSelectorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var list = view.FindControl<ListBox>("BoardList")!;
            var popover = list.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("dropdown-popover"));
            Assert.InRange(popover.Bounds.Width, 1, width);
            Assert.InRange(list.Bounds.Height, 1, 300);
            var create = popover.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.CreateBoardCommand);
            var createOrigin = create.TranslatePoint(default, popover)!.Value;
            Assert.True(createOrigin.Y >= list.TranslatePoint(default, popover)!.Value.Y + list.Bounds.Height);
            Assert.True(createOrigin.Y + create.Bounds.Height <= popover.Bounds.Height);
            foreach (var button in list.GetVisualDescendants().OfType<Button>())
            {
                var origin = button.TranslatePoint(default, popover)!.Value;
                Assert.InRange(origin.X, 0, popover.Bounds.Width);
                Assert.True(origin.X + button.Bounds.Width <= popover.Bounds.Width);
            }
            create.Command!.Execute(create.CommandParameter);
            Assert.True(vm.IsBoardNameEditorOpen);
            Assert.False(vm.IsBoardSelectorOpen);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void NameDialog_ReceivesFocus_AndCreatesEmptyBoard()
    {
        var (view, vm, service, window, _) = Create();
        try
        {
            vm.CreateBoardCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var name = view.FindControl<TextBox>("BoardNameBox")!;
            Assert.True(name.IsFocused);
            name.Text = "Proyecto nuevo";
            vm.SaveBoardNameCommand.Execute(null);
            Assert.False(vm.IsBoardNameEditorOpen);
            Assert.Equal("Proyecto nuevo", vm.BoardName);
            Assert.Equal(3, service.Boards.Count);
            Assert.True(vm.IsBoardEmpty);
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Command == vm.CreateFirstCardCommand);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(480, KanbanBoardOrientation.Vertical)]
    [InlineData(600, KanbanBoardOrientation.Vertical)]
    [InlineData(900, KanbanBoardOrientation.Horizontal)]
    public void LongBoardName_SelectorRemainsWithinWindow(double width, KanbanBoardOrientation orientation)
    {
        var (view, vm, service, window, _) = Create(width);
        try
        {
            var board = service.CreateBoard(new string('W', 80));
            vm.Reload();
            vm.SelectBoardCommand.Execute(vm.BoardOptions.Single(b => b.Board == board));
            vm.BoardOrientation = orientation;
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var button = view.FindControl<Button>("BoardSelectorButton")!;
            var origin = button.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.X, 0, width);
            Assert.True(origin.X + button.Bounds.Width <= width);
            Assert.InRange(button.Bounds.Width, 1, 460);
            var addColumn = view.GetVisualDescendants().OfType<Button>().First(b => b.Command == vm.AddColumnCommand);
            var addColumnOrigin = addColumn.TranslatePoint(default, window)!.Value;
            Assert.True(addColumnOrigin.X + addColumn.Bounds.Width <= width);
            Assert.Equal(board.Name, vm.BoardName);
        }
        finally { window.Close(); }
    }
}
