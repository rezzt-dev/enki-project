using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Arrastre local con el mismo gesto visual que Kanban. Solo MoveItem modifica el origen real
/// al soltar; la copia flotante, el resaltado y el desplazamiento son estado transitorio de la vista.</summary>
internal sealed class CalendarItemDragController
{
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor InvalidCursor = new(StandardCursorType.No);
    private readonly CalendarView _view;
    private readonly Canvas _layer;
    private readonly Border _preview;
    private readonly Image _image;
    private readonly Border _indicator;
    private readonly Func<bool> _isResizing;
    private readonly DispatcherTimer _timer;
    private Control? _source;
    private Popup? _sourcePopup;
    private IPointer? _pointer;
    private Window? _window;
    private Point _start;
    private Point _position;
    private Point _grab;
    private RenderTargetBitmap? _snapshot;
    private double _opacity;
    private Cursor? _cursor;
    private Border? _target;
    private DateTime _date;
    private TimeSpan? _time;
    private bool _dragging;
    private (CalendarItemSourceType Type, string Id)? _item;

    public CalendarItemDragController(CalendarView view, Canvas layer, Border preview, Image image,
        Border indicator, Func<bool> isResizing)
    {
        _view = view;
        _layer = layer;
        _preview = preview;
        _image = image;
        _indicator = indicator;
        _isResizing = isResizing;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => ScrollAtEdge();
        view.AddHandler(InputElement.PointerMovedEvent, Move, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.PointerReleasedEvent, Release, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel, true);
        view.PointerCaptureLost += (_, e) =>
        {
            if (_dragging && e.Pointer == _pointer && e.Pointer.Captured != view) Cancel();
        };
        view.AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(view) as Window;
            if (_window is not null) _window.Deactivated += Deactivated;
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            Cancel();
            if (_window is not null) _window.Deactivated -= Deactivated;
            _window = null;
        };
    }

    public void Press(Control source, PointerPressedEventArgs e)
    {
        if (_dragging || _isResizing()) return;
        Cancel();
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        // No tomar los botones de acciones de un popover ni el asa de duración como origen.
        for (var child = e.Source as Visual; child is not null && child != source; child = child.GetVisualParent())
            if (child is Button || child is Control c && c.Classes.Contains("calendar-chip-resize-handle")) return;
        _item = source.DataContext switch
        {
            CalendarItemViewModel { IsGhostOccurrence: false } item => (item.SourceType, item.SourceId),
            CalendarTimedItemViewModel { Item.IsGhostOccurrence: false } timed => (timed.Item.SourceType, timed.Item.SourceId),
            CalendarUnscheduledItemViewModel item => (item.SourceType, item.SourceId),
            _ => null
        };
        if (_item is null) return;
        _source = source;
        _pointer = e.Pointer;
        _start = e.GetPosition(_layer);
        _grab = e.GetPosition(source);
    }

    public void Move(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer || _source is null || _isResizing()) return;
        if (!e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed || !SourceIsValid()) { Cancel(); return; }
        _position = e.GetPosition(_layer);
        if (!_dragging)
        {
            var delta = _position - _start;
            if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
            Begin();
        }
        e.Handled = true;
        UpdateFeedback();
    }

    private bool SourceIsValid() => _view.IsEffectivelyVisible && _source is not null
        && ((_dragging && _sourcePopup is not null) || (_source.IsEffectivelyVisible
            && TopLevel.GetTopLevel(_source) == TopLevel.GetTopLevel(_view)));

    private void Begin()
    {
        var source = _source!;
        var top = TopLevel.GetTopLevel(_view)!;
        var scale = top.RenderScaling * Math.Abs(_layer.TransformToVisual(top)?.M11 ?? 1);
        var size = source.Bounds.Size;
        // Se conserva el tamaño y el color real de cada chip, sin reconstruir una plantilla distinta.
        var snapshot = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale),
            (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        try
        {
            // Componer sobre el fondo del tema evita artefactos de texto subpíxel al trasladar un
            // chip translúcido, especialmente con texto negro en el tema claro.
            using var context = snapshot.CreateDrawingContext();
            context.FillRectangle(_preview.Background!, new Rect(size));
            context.FillRectangle(new VisualBrush(source) { Stretch = Stretch.Fill }, new Rect(size));
        }
        catch { snapshot.Dispose(); throw; }
        _snapshot = snapshot;
        _image.Source = snapshot;
        _preview.Width = size.Width;
        _preview.Height = size.Height;
        _opacity = source.Opacity;
        _cursor = _view.Cursor;
        source.SetCurrentValue(Visual.OpacityProperty, 0.22);
        // El desplegable de desbordamiento no debe tapar la copia ni interceptar el destino.
        _sourcePopup = source.GetLogicalAncestors().OfType<Popup>().FirstOrDefault();
        if (_sourcePopup is not null) _sourcePopup.IsOpen = false;
        _dragging = true;
        _layer.IsVisible = true;
        // Capturar cancela el clic del ToggleButton y evita abrir su detalle al soltar.
        _pointer!.Capture(_view);
        _view.Focus();
        _timer.Start();
    }

    public void Release(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        if (!_dragging) { Cancel(); return; }
        e.Handled = true;
        if (!SourceIsValid()) { Cancel(); return; }
        _position = e.GetPosition(_layer);
        UpdateFeedback();
        var item = _item;
        var valid = _target is not null;
        var date = _date;
        var time = _time;
        Cancel();
        if (valid && item is { } origin && _view.DataContext is CalendarViewModel vm)
            vm.MoveItem(origin.Type, origin.Id, date, time);
    }

    private Control? Hit() => _view.InputHitTest(_layer.TranslatePoint(_position, _view) ?? default) as Control;

    private void UpdateFeedback()
    {
        Canvas.SetLeft(_preview, _position.X - _grab.X);
        Canvas.SetTop(_preview, _position.Y - _grab.Y);
        var previousTarget = _target;
        _target = null;
        _time = null;
        _indicator.IsVisible = false;
        var hit = Hit();
        foreach (var border in hit?.GetSelfAndVisualAncestors().OfType<Border>() ?? [])
        {
            if (border.DataContext is CalendarAgendaGroupViewModel group && border.Classes.Contains("calendar-agenda-day"))
            {
                _target = border;
                _date = group.Date;
                ShowTimeIndicator(border, 0);
                break;
            }
            if (border.DataContext is CalendarMonthDayViewModel day && border.Classes.Contains("calendar-month-cell"))
            {
                _target = border;
                _date = day.Date;
                break;
            }
            if (border.DataContext is CalendarWeekColumnViewModel column && border.Classes.Contains("calendar-week-column"))
            {
                _target = border;
                _date = column.Date;
                if (border.Classes.Contains("calendar-time-column"))
                {
                    var point = _layer.TranslatePoint(_position, border) ?? default;
                    var minutes = Math.Clamp(Math.Round(point.Y / CalendarWeekColumnViewModel.HourRowHeight * 4) * 15, 0, 1425);
                    _time = TimeSpan.FromMinutes(minutes);
                    ShowTimeIndicator(border, minutes);
                }
                break;
            }
        }
        if (previousTarget != _target)
        {
            previousTarget?.Classes.Set("drag-over", false);
            _target?.Classes.Set("drag-over", true);
        }
        _view.SetCurrentValue(InputElement.CursorProperty, _target is null ? InvalidCursor : MoveCursor);
    }

    private Rect Bounds(Control control) => new(control.TranslatePoint(default, _layer) ?? default, control.Bounds.Size);

    private void ShowTimeIndicator(Border column, double minutes)
    {
        var rect = Bounds(column);
        var marker = new Rect(rect.X + 2, rect.Y + minutes / 60 * CalendarWeekColumnViewModel.HourRowHeight,
            Math.Max(0, rect.Width - 4), 2);
        foreach (var scroll in column.GetVisualAncestors().OfType<ScrollViewer>()) marker = marker.Intersect(Bounds(scroll));
        if (marker.Width <= 0 || marker.Height <= 0) return;
        Canvas.SetLeft(_indicator, marker.X);
        Canvas.SetTop(_indicator, marker.Y);
        _indicator.Width = marker.Width;
        _indicator.Height = marker.Height;
        _indicator.IsVisible = true;
    }

    private void ScrollAtEdge()
    {
        if (!_dragging) return;
        if (!SourceIsValid()) { Cancel(); return; }
        var hit = Hit();
        var changed = false;
        foreach (var scroll in hit?.GetSelfAndVisualAncestors().OfType<ScrollViewer>() ?? [])
        {
            if (scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var rect = Bounds(scroll);
            var step = _position.Y < rect.Top + 40 ? -12 * (1 - (_position.Y - rect.Top) / 40)
                : _position.Y > rect.Bottom - 40 ? 12 * (1 - (rect.Bottom - _position.Y) / 40) : 0;
            var next = Math.Clamp(scroll.Offset.Y + step, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            if (Math.Abs(next - scroll.Offset.Y) < 0.01) continue;
            scroll.Offset = new Vector(scroll.Offset.X, next);
            changed = true;
        }
        if (changed) { _view.UpdateLayout(); UpdateFeedback(); }
    }

    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        if (e.Key == Key.Escape) Cancel();
    }

    private void Deactivated(object? sender, EventArgs e) => Cancel();

    public void Cancel()
    {
        _timer.Stop();
        _target?.Classes.Set("drag-over", false);
        if (_dragging)
        {
            _source?.SetCurrentValue(Visual.OpacityProperty, _opacity);
            _view.SetCurrentValue(InputElement.CursorProperty, _cursor);
        }
        _dragging = false;
        _source = null;
        _sourcePopup = null;
        _item = null;
        _target = null;
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == _view) pointer.Capture(null);
        _layer.IsVisible = false;
        _indicator.IsVisible = false;
        _image.Source = null;
        _snapshot?.Dispose();
        _snapshot = null;
    }
}
