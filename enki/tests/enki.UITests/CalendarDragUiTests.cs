using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

/// <summary>Gesto de puntero completo sobre las vistas reales y los tres almacenes de origen.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class CalendarDragUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-calendar-drag-{Guid.NewGuid():N}");
    private static readonly DateTime StartDate = new(2026, 9, 15, 2, 0, 0);

    public CalendarDragUiTests() => HeadlessApp.EnsureInitialized();
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private (CalendarView View, CalendarViewModel Vm, Window Window, Func<DateTime?> Date) Create(
        CalendarItemSourceType type, CalendarViewMode mode = CalendarViewMode.Month, bool unscheduled = false,
        bool recurring = false, int extraCards = 0)
    {
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings, baseUrl: "https://test.example", anonKey: "test-key");
        settings.Settings.PolarLinked = true;
        settings.Settings.PolarUserId = "user-1";
        var kanban = new KanbanService(_directory);
        foreach (var column in kanban.Board.Columns) column.Cards.Clear();
        var polar = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var due = unscheduled ? (DateTime?)null : StartDate.ToUniversalTime();
        Func<DateTime?> date;
        switch (type)
        {
            case CalendarItemSourceType.Kanban:
                var card = new KanbanCard { Id = "drag", Title = "Tarea de prueba", DueDateUtc = due, DurationMinutes = 60 };
                kanban.Board.Columns[0].Cards.Add(card);
                kanban.Save();
                date = () => card.DueDateUtc;
                break;
            case CalendarItemSourceType.PolarTask:
            case CalendarItemSourceType.PolarSubtask:
                var list = polar.CreateList("Lista", "ic_list", "#7F52FF", false);
                var task = polar.CreateTask(list, "Tarea de prueba", "", 0, type == CalendarItemSourceType.PolarSubtask ? null : due,
                    recurring ? PolarRecurrence.Daily : PolarRecurrence.None, 0, [], durationMinutes: 60);
                if (type == CalendarItemSourceType.PolarSubtask)
                {
                    var subtask = polar.AddSubtask(task, "Subtarea de prueba");
                    polar.UpdateSubtaskDueDate(subtask, due);
                    date = () => subtask.DueDateUtc;
                }
                else date = () => task.DueDateUtc;
                break;
            default:
                var reminder = reminders.Create("Recordatorio de prueba", "", due!.Value, "BellRingOutline", durationMinutes: 60);
                date = () => reminder.NotifyAtUtc;
                break;
        }
        for (var i = 0; i < extraCards; i++)
            kanban.Board.Columns[0].Cards.Add(new KanbanCard
            {
                Id = $"extra-{i}", Title = $"A extra {i}", DueDateUtc = StartDate.ToUniversalTime()
            });
        if (mode == CalendarViewMode.Agenda)
            reminders.Create("Destino", "", StartDate.AddDays(2).ToUniversalTime(), "BellRingOutline");
        var colors = new CalendarLayerColorService(kanban, polar, auth, settings);
        var aggregation = new CalendarAggregationService(kanban, polar, reminders, auth, colors);
        var vm = new CalendarViewModel(aggregation, colors, kanban, polar, reminders, auth, settings,
            new NotificationService(settings, _directory), _ => { })
        {
            ReferenceDate = StartDate.Date,
            IsUnscheduledExpanded = unscheduled
        };
        vm.SetViewCommand.Execute(mode == CalendarViewMode.Day ? CalendarViewMode.Week : CalendarViewMode.Day);
        vm.SetViewCommand.Execute(mode);
        Dispatcher.UIThread.RunJobs();
        var view = new CalendarView { DataContext = vm };
        var window = new Window { Content = view, Width = 1500, Height = 1000 };
        window.Show();
        // El gesto se verifica con las transiciones de entrada terminadas, como en una vista estable.
        foreach (var control in view.GetVisualDescendants().OfType<Control>()) control.Transitions = null;
        window.UpdateLayout();
        return (view, vm, window, date);
    }

    private static Control Source(CalendarView view) => view.GetVisualDescendants().OfType<Control>().First(c =>
        c.IsEffectivelyVisible && (c.Classes.Contains("calendar-chip") || c.Classes.Contains("calendar-unscheduled-item")
            || c.Classes.Contains("calendar-agenda-card")));

    private static Point PointIn(Control control, Window window, double x = 0.5, double y = 0.5) =>
        control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;

    private static void Drag(Window window, Point start, Point end)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.UpdateLayout();
    }

    [Theory]
    [InlineData(CalendarItemSourceType.Kanban)]
    [InlineData(CalendarItemSourceType.PolarTask)]
    [InlineData(CalendarItemSourceType.PolarSubtask)]
    [InlineData(CalendarItemSourceType.Reminder)]
    public void MonthDrag_ShowsOriginalChip_AndMovesRealItemOnlyOnRelease(CalendarItemSourceType type)
    {
        var (view, vm, window, date) = Create(type);
        try
        {
            var source = Source(view);
            window.MouseMove(PointIn(source, window));
            window.UpdateLayout();
            var start = PointIn(source, window);
            var size = source.Bounds.Size;
            var day = StartDate.Date.AddDays(2);
            var target = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("calendar-month-cell") && b.DataContext is CalendarMonthDayViewModel d && d.Date == day);
            var end = PointIn(target, window);
            var layer = view.FindControl<Canvas>("CalendarDragLayer")!;
            var preview = view.FindControl<Border>("CalendarDragPreview")!;
            var origin = source.TranslatePoint(default, layer)!.Value;
            Drag(window, start, end);

            Assert.True(layer.IsVisible);
            Assert.False(layer.IsHitTestVisible);
            Assert.IsType<RenderTargetBitmap>(view.FindControl<Image>("CalendarDragImage")!.Source);
            Assert.Equal(size, preview.Bounds.Size);
            Assert.Equal(origin.X + end.X - start.X, Canvas.GetLeft(preview), 4);
            Assert.Equal(origin.Y + end.Y - start.Y, Canvas.GetTop(preview), 4);
            Assert.True(source.Opacity < 0.3);
            Assert.Contains("drag-over", target.Classes);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(day.AddHours(2), date()!.Value.ToLocalTime());
            Assert.False(layer.IsVisible);
            Assert.Null(view.FindControl<Image>("CalendarDragImage")!.Source);
            Assert.Equal(1, source.Opacity);
            Assert.DoesNotContain("drag-over", target.Classes);
            Assert.Null(vm.QuickCreate);
            Assert.False(((ToggleButton)source).IsChecked == true);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(CalendarItemSourceType.Kanban, CalendarViewMode.Week)]
    [InlineData(CalendarItemSourceType.PolarTask, CalendarViewMode.Week)]
    [InlineData(CalendarItemSourceType.Reminder, CalendarViewMode.Day)]
    public void TimedDrag_SnapsToQuarterHour_EvenOverExistingChip(CalendarItemSourceType type, CalendarViewMode mode)
    {
        var (view, vm, window, date) = Create(type, mode);
        try
        {
            var source = Source(view);
            var column = source.GetVisualAncestors().OfType<Border>().Single(b => b.Classes.Contains("calendar-time-column"));
            // 02:23 cae dentro del chip existente: el destino sigue siendo la rejilla de debajo.
            var end = column.TranslatePoint(new Point(column.Bounds.Width / 2, 143), window)!.Value;
            Drag(window, PointIn(source, window, 0.3, 0.15), end);
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            Assert.True(view.FindControl<Border>("CalendarDropIndicator")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate.AddMinutes(30), date()!.Value.ToLocalTime());
            Assert.Null(vm.QuickCreate);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeOrInvalidDrop_RestoresChipAndDoesNotWrite(bool escape)
    {
        var (view, _, window, date) = Create(CalendarItemSourceType.Kanban);
        try
        {
            var source = Source(view);
            var end = new Point(15, 15);
            Drag(window, PointIn(source, window), end);
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            if (escape) window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
            Assert.Equal(1, source.Opacity);
            Assert.False(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            Assert.False(((ToggleButton)source).IsChecked == true);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Click_StillOpensDetail_AndGhostDoesNotStartDrag()
    {
        var (view, _, window, date) = Create(CalendarItemSourceType.PolarTask, recurring: true);
        try
        {
            var source = Source(view);
            var start = PointIn(source, window);
            window.MouseDown(start, MouseButton.Left);
            window.MouseUp(start, MouseButton.Left);
            Assert.True(((ToggleButton)source).IsChecked);
            ((ToggleButton)source).IsChecked = false;
            window.UpdateLayout();
            var ghost = view.GetVisualDescendants().OfType<ToggleButton>().First(b =>
                b.Classes.Contains("calendar-chip") && b.DataContext is CalendarItemViewModel { IsGhostOccurrence: true });
            Drag(window, PointIn(ghost, window), new Point(500, 500));
            Assert.False(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            window.MouseUp(new Point(500, 500), MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(CalendarItemSourceType.Kanban)]
    [InlineData(CalendarItemSourceType.PolarTask)]
    public void UnscheduledItem_CanBeScheduledByDragging(CalendarItemSourceType type)
    {
        var (view, _, window, date) = Create(type, unscheduled: true);
        try
        {
            var target = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("calendar-month-cell") && b.DataContext is CalendarMonthDayViewModel d && d.Date == StartDate.Date);
            var end = PointIn(target, window);
            Drag(window, PointIn(Source(view), window), end);
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate.Date, date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Fact]
    public void OverflowChip_DragsOutOfPopoverAndReschedules()
    {
        var (view, _, window, date) = Create(CalendarItemSourceType.Kanban, extraCards: 4);
        try
        {
            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(b =>
                b.Classes.Contains("calendar-overflow-trigger") && b.DataContext is CalendarMonthDayViewModel d && d.Date == StartDate.Date);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var popup = view.GetLogicalDescendants().OfType<Popup>().First(p => p.IsOpen);
            var source = popup.Child!.GetVisualDescendants().OfType<ToggleButton>().Single(b =>
                b.DataContext is CalendarItemViewModel item && item.SourceId == "drag");
            var target = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("calendar-month-cell") && b.DataContext is CalendarMonthDayViewModel d && d.Date == StartDate.Date.AddDays(2));
            var end = PointIn(target, window);
            var start = PointIn(source, window);
            Assert.True(source.IsEffectivelyVisible);
            Assert.Same(TopLevel.GetTopLevel(view), TopLevel.GetTopLevel(source));
            var hit = window.InputHitTest(start) as Visual;
            Assert.True(hit?.GetSelfAndVisualAncestors().Contains(source) == true,
                $"Punto {start}, origen {source.Bounds}, hit {hit?.GetType().Name}");
            Drag(window, start, end);
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            Assert.False(popup.IsOpen);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate.AddDays(2), date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ResizeHandle_ChangesDurationWithoutStartingMove()
    {
        var (view, vm, window, date) = Create(CalendarItemSourceType.Kanban, CalendarViewMode.Day);
        try
        {
            var handle = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.IsEffectivelyVisible && b.Classes.Contains("calendar-chip-resize-handle"));
            var start = PointIn(handle, window);
            var end = new Point(start.X, start.Y + 30);
            Drag(window, start, end);
            Assert.False(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
            Assert.Equal(90, vm.WeekColumns.Single().TimedItems.Single().Item.DurationMinutes);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ChangingViewDuringDrag_CancelsAndReleasesSnapshot()
    {
        var (view, vm, window, date) = Create(CalendarItemSourceType.Reminder);
        try
        {
            var source = Source(view);
            Drag(window, PointIn(source, window), new Point(900, 600));
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            vm.SetViewCommand.Execute(CalendarViewMode.Day);
            Assert.False(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            Assert.Null(view.FindControl<Image>("CalendarDragImage")!.Source);
            Assert.Equal(1, source.Opacity);
            window.MouseUp(new Point(900, 600), MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(CalendarItemSourceType.Kanban)]
    [InlineData(CalendarItemSourceType.PolarTask)]
    [InlineData(CalendarItemSourceType.Reminder)]
    public void AllDayDrop_PreservesExistingTime(CalendarItemSourceType type)
    {
        var (view, vm, window, date) = Create(type, CalendarViewMode.Week);
        try
        {
            var targetDate = StartDate.Date.AddDays(1);
            var target = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("calendar-week-column") && !b.Classes.Contains("calendar-time-column")
                && b.DataContext is CalendarWeekColumnViewModel c && c.Date == targetDate);
            var end = PointIn(target, window);
            Drag(window, PointIn(Source(view), window, 0.3, 0.2), end);
            Assert.Contains("drag-over", target.Classes);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate.AddDays(1), date()!.Value.ToLocalTime());
            Assert.Null(vm.QuickCreate);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void StationaryPointerAtEdge_AutoScrollsUntilCancelled()
    {
        var (view, _, window, date) = Create(CalendarItemSourceType.Kanban, CalendarViewMode.Day);
        try
        {
            var source = Source(view);
            var scroll = source.GetVisualAncestors().OfType<ScrollViewer>().First();
            var end = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height - 18), window)!.Value;
            Drag(window, PointIn(source, window, 0.3, 0.2), end);
            var preview = view.FindControl<Border>("CalendarDragPreview")!;
            var left = Canvas.GetLeft(preview);
            var top = Canvas.GetTop(preview);
            // Bombea el bucle real: dormir el hilo de UI no entrega los ticks del temporizador.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
                Dispatcher.UIThread.MainLoop(timeout.Token);
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(left, Canvas.GetLeft(preview));
            Assert.Equal(top, Canvas.GetTop(preview));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            var offset = scroll.Offset;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(80)))
                Dispatcher.UIThread.MainLoop(timeout.Token);
            Assert.Equal(offset, scroll.Offset);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Zoom_PreservesGrabPositionAndUsesScreenResolution()
    {
        var (view, _, window, date) = Create(CalendarItemSourceType.Reminder);
        try
        {
            window.Content = null;
            window.Content = new LayoutTransformControl { Child = view, LayoutTransform = new ScaleTransform(1.25, 1.25) };
            window.UpdateLayout();
            foreach (var control in view.GetVisualDescendants().OfType<Control>()) control.Transitions = null;
            var source = Source(view);
            window.MouseMove(PointIn(source, window));
            window.UpdateLayout();
            var start = PointIn(source, window);
            var layer = view.FindControl<Canvas>("CalendarDragLayer")!;
            var origin = source.TranslatePoint(default, layer)!.Value;
            var originalSize = source.Bounds.Size;
            var end = new Point(start.X + 125, start.Y + 50);
            Drag(window, start, end);
            var preview = view.FindControl<Border>("CalendarDragPreview")!;
            var snapshot = Assert.IsType<RenderTargetBitmap>(view.FindControl<Image>("CalendarDragImage")!.Source);
            Assert.Equal(origin.X + 100, Canvas.GetLeft(preview), 4);
            Assert.Equal(origin.Y + 40, Canvas.GetTop(preview), 4);
            Assert.Equal(originalSize, preview.Bounds.Size);
            Assert.Equal((int)Math.Ceiling(originalSize.Width * 1.25 * window.RenderScaling), snapshot.PixelSize.Width);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate, date()!.Value.ToLocalTime());
        }
        finally { window.Close(); }
    }

    [Fact]
    public void AgendaDrag_MovesToAnotherDayGroup()
    {
        var (view, vm, window, date) = Create(CalendarItemSourceType.Kanban, CalendarViewMode.Agenda);
        try
        {
            var target = view.GetVisualDescendants().OfType<Border>().Single(b =>
                b.Classes.Contains("calendar-agenda-day") && b.DataContext is CalendarAgendaGroupViewModel g && g.Date == StartDate.Date.AddDays(2));
            var end = PointIn(target, window);
            Drag(window, PointIn(Source(view), window), end);
            Assert.True(view.FindControl<Canvas>("CalendarDragLayer")!.IsVisible);
            Assert.True(view.FindControl<Border>("CalendarDropIndicator")!.IsVisible);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(StartDate.AddDays(2), date()!.Value.ToLocalTime());
            Assert.Null(vm.QuickCreate);
        }
        finally { window.Close(); }
    }
}
