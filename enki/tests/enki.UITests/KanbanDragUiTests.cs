using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Recorre el gesto real de puntero, la capa flotante y la persistencia al soltar.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class KanbanDragUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-drag-{Guid.NewGuid():N}");

    public KanbanDragUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private (KanbanView View, KanbanViewModel Vm, Window Window) CreateView(KanbanBoardOrientation orientation, int cardCount = 3)
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var tasks = new PolarTaskService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), tasks, new ReminderService(_directory), settings);
        var service = new KanbanService(_directory);
        foreach (var column in service.Board.Columns) column.Cards.Clear();
        for (var i = 0; i < cardCount; i++)
            service.Board.Columns[0].Cards.Add(new KanbanCard
            {
                Id = $"source-{i}", Title = $"Tarea {i}", Description = "Descripción de la tarea que se arrastra.",
                Tags = ["desarrollo", "documentación"], DueDateUtc = DateTime.UtcNow.AddDays(2)
            });
        service.Board.Columns[1].Cards.Add(new KanbanCard { Id = "target", Title = "Destino" });
        service.Save();
        var vm = new KanbanViewModel(service, auth, new PolarTaskManagementViewModel(tasks, sync))
        {
            BoardOrientation = orientation
        };
        var view = new KanbanView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 1100 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (view, vm, window);
    }

    private static Button CardControl(KanbanView view, KanbanCardViewModel card) =>
        view.GetVisualDescendants().OfType<Button>().Single(b =>
            b.Classes.Contains("kanban-card") && b.IsEffectivelyVisible && b.DataContext == card);

    private static Point PointIn(Control control, Window window, double x = 0.5, double y = 0.5) =>
        control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;

    private static void Begin(Window window, Point start, Point end)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.GetLayoutManager()!.ExecuteLayoutPass();
    }

    [Theory]
    [InlineData(KanbanBoardOrientation.Vertical)]
    [InlineData(KanbanBoardOrientation.Horizontal)]
    public void Drag_ShowsFullSizePreviewAtGrabPoint_AndPersistsOnlyOnRelease(KanbanBoardOrientation orientation)
    {
        var (view, vm, window) = CreateView(orientation);
        try
        {
            var source = vm.Columns[0].Cards[0];
            var button = CardControl(view, source);
            var start = PointIn(button, window);
            var target = CardControl(view, vm.Columns[1].Cards[0]);
            var end = PointIn(target, window, 0.3, 0.3);
            var origin = button.TranslatePoint(default, view)!.Value;
            Begin(window, start, end);

            var layer = view.FindControl<Canvas>("CardDragLayer")!;
            var preview = view.FindControl<Grid>("CardDragPreview")!;
            var image = view.FindControl<Image>("CardDragImage")!;
            Assert.True(layer.IsVisible);
            Assert.False(layer.IsHitTestVisible);
            Assert.Equal(button.Bounds.Size, preview.Bounds.Size);
            Assert.IsType<RenderTargetBitmap>(image.Source);
            Assert.Equal(origin.X + end.X - start.X, Canvas.GetLeft(preview), 5);
            Assert.Equal(origin.Y + end.Y - start.Y, Canvas.GetTop(preview), 5);
            Assert.True(button.Opacity < 0.3);
            Assert.True(vm.Columns[1].IsDropTarget);
            Assert.True(view.FindControl<Border>("CardDropIndicator")!.IsVisible);
            Assert.Contains(source, vm.Columns[0].Cards);
            Assert.Contains(new KanbanService(_directory).Board.Columns[0].Cards, c => c.Id == source.Id);

            var moved = new Point(end.X + 8, end.Y + 8);
            window.MouseMove(moved, RawInputModifiers.LeftMouseButton);
            Assert.Equal(origin.X + moved.X - start.X, Canvas.GetLeft(preview), 5);
            window.MouseUp(moved, MouseButton.Left);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.Same(source, vm.Columns[1].Cards[0]);
            Assert.DoesNotContain(source, vm.Columns[0].Cards);
            Assert.Contains(new KanbanService(_directory).Board.Columns[1].Cards, c => c.Id == source.Id);
            Assert.False(layer.IsVisible);
            Assert.Null(image.Source);
            Assert.Equal(1, button.Opacity);
            Assert.All(vm.Columns, column => Assert.False(column.IsDropTarget));
            Assert.Null(vm.CardDetail);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(KanbanBoardOrientation.Vertical)]
    [InlineData(KanbanBoardOrientation.Horizontal)]
    public void Drag_PastCardMidpoint_InsertsAfter_AndCanReorderBack(KanbanBoardOrientation orientation)
    {
        var (view, vm, window) = CreateView(orientation);
        try
        {
            var column = vm.Columns[0];
            var source = column.Cards[0];
            var last = column.Cards[2];
            var end = PointIn(CardControl(view, last), window, 0.8, 0.8);
            Begin(window, PointIn(CardControl(view, source), window), end);
            window.MouseUp(end, MouseButton.Left);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Same(source, column.Cards.Last());

            end = PointIn(CardControl(view, column.Cards[0]), window, 0.3, 0.3);
            Begin(window, PointIn(CardControl(view, source), window), end);
            window.MouseUp(end, MouseButton.Left);
            Assert.Same(source, column.Cards.First());
            Assert.Equal(3, column.Cards.Select(c => c.Id).Distinct().Count());
            Assert.Null(vm.CardDetail);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeOrDropOutside_CancelsWithoutOpeningDetailOrChangingData(bool escape)
    {
        var (view, vm, window) = CreateView(KanbanBoardOrientation.Vertical);
        try
        {
            var source = vm.Columns[0].Cards[0];
            var button = CardControl(view, source);
            var end = escape ? PointIn(CardControl(view, vm.Columns[1].Cards[0]), window) : new Point(20, 20);
            Begin(window, PointIn(button, window), end);
            Assert.True(view.FindControl<Canvas>("CardDragLayer")!.IsVisible);
            if (escape) window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.MouseUp(end, MouseButton.Left);
            Assert.Same(source, vm.Columns[0].Cards[0]);
            Assert.Equal(1, button.Opacity);
            Assert.False(view.FindControl<Canvas>("CardDragLayer")!.IsVisible);
            Assert.All(vm.Columns, column => Assert.False(column.IsDropTarget));
            Assert.Null(vm.CardDetail);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SmallMovement_StillOpensCard_AndNestedActionDoesNotStartDrag()
    {
        var (view, vm, window) = CreateView(KanbanBoardOrientation.Vertical);
        try
        {
            var card = vm.Columns[0].Cards[0];
            var button = CardControl(view, card);
            var start = PointIn(button, window);
            var end = new Point(start.X + 2, start.Y + 1);
            Begin(window, start, end);
            Assert.False(view.FindControl<Canvas>("CardDragLayer")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.NotNull(vm.CardDetail);
            vm.CloseCardDetailCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var priority = button.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.CyclePriorityCommand);
            start = PointIn(priority, window);
            Begin(window, start, new Point(start.X + 30, start.Y + 30));
            Assert.False(view.FindControl<Canvas>("CardDragLayer")!.IsVisible);
            window.MouseUp(new Point(start.X + 30, start.Y + 30), MouseButton.Left);
            Assert.Null(vm.CardDetail);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void RemovingViewDuringDrag_RestoresSourceAndClearsPreview()
    {
        var (view, vm, window) = CreateView(KanbanBoardOrientation.Vertical);
        try
        {
            var button = CardControl(view, vm.Columns[0].Cards[0]);
            Begin(window, PointIn(button, window), PointIn(CardControl(view, vm.Columns[1].Cards[0]), window));
            window.Content = null;
            Assert.Equal(1, button.Opacity);
            Assert.False(view.FindControl<Canvas>("CardDragLayer")!.IsVisible);
            Assert.Null(view.FindControl<Image>("CardDragImage")!.Source);
            Assert.All(vm.Columns, column => Assert.False(column.IsDropTarget));
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(KanbanBoardOrientation.Vertical, false)]
    [InlineData(KanbanBoardOrientation.Horizontal, false)]
    [InlineData(KanbanBoardOrientation.Vertical, true)]
    [InlineData(KanbanBoardOrientation.Horizontal, true)]
    public void EmptyOrFilteredColumn_AcceptsCardWithoutLosingHiddenCards(KanbanBoardOrientation orientation, bool filtered)
    {
        var (view, vm, window) = CreateView(orientation);
        try
        {
            if (filtered) vm.SearchText = "Tarea";
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var target = vm.Columns[filtered ? 1 : 2];
            var columnControl = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("kanban-column") && b.IsEffectivelyVisible && b.DataContext == target);
            var scroll = columnControl.GetVisualDescendants().OfType<ScrollViewer>().First();
            var end = PointIn(scroll, window, 0.3, 0.2);
            var source = vm.Columns[0].Cards[0];
            Begin(window, PointIn(CardControl(view, source), window), end);
            Assert.True(target.IsDropTarget);
            Assert.True(view.FindControl<Border>("CardDropIndicator")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.Same(source, target.Cards.Last());
            Assert.Equal(filtered ? 2 : 1, target.Cards.Count);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void HoldingPointerNearVerticalEdge_ScrollsColumnAndKeepsPreviewAttached()
    {
        var (view, vm, window) = CreateView(KanbanBoardOrientation.Vertical, cardCount: 15);
        try
        {
            var source = CardControl(view, vm.Columns[0].Cards[0]);
            var scroll = source.GetVisualAncestors().OfType<ScrollViewer>().First();
            var end = scroll.TranslatePoint(new Point(90, scroll.Bounds.Height - 15), window)!.Value;
            Begin(window, PointIn(source, window), end);
            var preview = view.FindControl<Grid>("CardDragPreview")!;
            var left = Canvas.GetLeft(preview);
            var top = Canvas.GetTop(preview);
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(left, Canvas.GetLeft(preview));
            Assert.Equal(top, Canvas.GetTop(preview));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            var offset = scroll.Offset;
            Assert.Equal(offset, scroll.Offset);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Zoom_KeepsGrabPointAndRasterResolutionAligned()
    {
        var (view, vm, window) = CreateView(KanbanBoardOrientation.Vertical);
        try
        {
            window.Content = null;
            window.Content = new LayoutTransformControl { LayoutTransform = new ScaleTransform(1.25, 1.25), Child = view };
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var source = CardControl(view, vm.Columns[0].Cards[0]);
            var start = PointIn(source, window);
            var end = new Point(start.X + 120, start.Y + 40);
            var origin = source.TranslatePoint(default, view)!.Value;
            Begin(window, start, end);
            var preview = view.FindControl<Grid>("CardDragPreview")!;
            var snapshot = Assert.IsType<RenderTargetBitmap>(view.FindControl<Image>("CardDragImage")!.Source);
            Assert.Equal(origin.X + 120 / 1.25, Canvas.GetLeft(preview), 5);
            Assert.Equal(origin.Y + 40 / 1.25, Canvas.GetTop(preview), 5);
            var scale = Math.Abs(source.TransformToVisual(window)?.M11 ?? 1) * window.RenderScaling;
            Assert.InRange(snapshot.PixelSize.Width, (int)Math.Floor(source.Bounds.Width * scale) - 10,
                (int)Math.Ceiling(source.Bounds.Width * scale) + 10);
            Assert.Equal(source.Bounds.Size, preview.Bounds.Size);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        }
        finally { window.Close(); }
    }
}
