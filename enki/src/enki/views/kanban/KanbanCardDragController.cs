using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>
/// Gesto local del tablero: captura el puntero y mueve una instantánea de la tarjeta, sin modificar
/// las colecciones hasta soltar. La persistencia y las automatizaciones siguen pasando por MoveCard.
/// </summary>
internal sealed class KanbanCardDragController
{
    private readonly KanbanView _view;
    private readonly Canvas _layer;
    private readonly Grid _preview;
    private readonly Image _image;
    private readonly Border _indicator;
    private readonly DispatcherTimer _scrollTimer;
    private Button? _source;
    private IPointer? _pointer;
    private Window? _window;
    private Point _pressPoint;
    private Point _position;
    private Vector _grabOffset;
    private double _sourceOpacity;
    private Cursor? _previousCursor;
    private RenderTargetBitmap? _snapshot;
    private KanbanColumnViewModel? _targetColumn;
    private KanbanCardViewModel? _beforeCard;
    private bool _dragging;

    public KanbanCardDragController(KanbanView view, Canvas layer, Grid preview, Image image, Border indicator)
    {
        _view = view;
        _layer = layer;
        _preview = preview;
        _image = image;
        _indicator = indicator;
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += (_, _) => AutoScroll();
        view.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        view.PointerCaptureLost += (_, e) =>
        {
            if (_dragging && e.Pointer == _pointer && e.Pointer.Captured != _view) Cancel();
        };
        view.AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(view) as Window;
            if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        };
        view.DetachedFromVisualTree += (_, _) =>
        {
            Cancel();
            if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        };
    }

    private KanbanViewModel? Vm => _view.DataContext as KanbanViewModel;
    private bool Horizontal => Vm?.BoardOrientation == KanbanBoardOrientation.Horizontal;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragging || !e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed) return;
        Cancel();
        // Los botones anidados (prioridad, editar, borrar, etiquetas) conservan su propio gesto.
        for (var visual = e.Source as Visual; visual is not null && visual != _view; visual = visual.GetVisualParent())
        {
            if (visual is not Button button) continue;
            if (!button.Classes.Contains("kanban-card") || button.DataContext is not KanbanCardViewModel) return;
            _source = button;
            _pointer = e.Pointer;
            _pressPoint = e.GetPosition(_layer);
            _grabOffset = e.GetPosition(button) - default(Point);
            return;
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_source is null || _pointer != e.Pointer) return;
        if (!e.GetCurrentPoint(_view).Properties.IsLeftButtonPressed || !SourceIsValid())
        {
            Cancel();
            return;
        }

        _position = e.GetPosition(_layer);
        if (!_dragging)
        {
            var delta = _position - _pressPoint;
            if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
            BeginDrag();
        }
        e.Handled = true;
        UpdateFeedback();
        // La primera pasada debe responder al instante; el temporizador mantiene el desplazamiento
        // cuando el puntero se queda quieto junto al borde.
        AutoScroll();
    }

    private bool SourceIsValid() =>
        _source is { IsEffectivelyVisible: true, DataContext: KanbanCardViewModel { IsVisible: true } card }
        && Vm is { DisplayMode: KanbanDisplayMode.Board } vm
        && vm.Columns.Any(column => column.Cards.Contains(card));

    private void BeginDrag()
    {
        var source = _source!;
        var topLevel = TopLevel.GetTopLevel(_view)!;
        // Incluye escala de pantalla y zoom de la aplicación; se rasteriza una sola vez por gesto.
        var transform = source.TransformToVisual(topLevel);
        var scale = topLevel.RenderScaling * Math.Abs(transform?.M11 ?? 1);
        var size = source.Bounds.Size;
        var snapshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        try { snapshot.Render(source); }
        catch { snapshot.Dispose(); throw; }
        _snapshot = snapshot;
        _image.Source = snapshot;
        _preview.Width = size.Width;
        _preview.Height = size.Height;
        _sourceOpacity = source.Opacity;
        source.SetCurrentValue(Visual.OpacityProperty, 0.22);
        _previousCursor = _view.Cursor;
        _dragging = true;
        _layer.IsVisible = true;
        // Capturar en la vista cancela el estado pulsado del Button: soltar no abre el detalle.
        _pointer!.Capture(_view);
        _view.Focus();
        _scrollTimer.Start();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        var vm = Vm;
        var card = _source?.DataContext as KanbanCardViewModel;
        var commit = _dragging && SourceIsValid();
        if (commit)
        {
            _position = e.GetPosition(_layer);
            UpdateFeedback();
        }
        var column = _targetColumn;
        var before = _beforeCard;
        e.Handled = _dragging;
        Cancel();
        if (commit && vm is not null && card is not null && column is not null)
            vm.MoveCard(card, column, before);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        if (e.Key == Key.Escape) Cancel();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Cancel();

    public void Cancel()
    {
        _scrollTimer.Stop();
        if (_targetColumn is not null) _targetColumn.IsDropTarget = false;
        if (_dragging)
        {
            _source?.SetCurrentValue(Visual.OpacityProperty, _sourceOpacity);
            _view.SetCurrentValue(InputElement.CursorProperty, _previousCursor);
        }
        _dragging = false;
        _source = null;
        _targetColumn = null;
        _beforeCard = null;
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == _view) pointer.Capture(null);
        _layer.IsVisible = false;
        _indicator.IsVisible = false;
        _image.Source = null;
        _snapshot?.Dispose();
        _snapshot = null;
    }

    private Control? HitControl() => _view.InputHitTest(_position) as Control;

    private void UpdateFeedback()
    {
        Canvas.SetLeft(_preview, _position.X - _grabOffset.X);
        Canvas.SetTop(_preview, _position.Y - _grabOffset.Y);
        var hit = HitControl();
        var columnControl = hit?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("kanban-column"));
        var column = columnControl?.DataContext as KanbanColumnViewModel;
        if (_targetColumn != column)
        {
            if (_targetColumn is not null) _targetColumn.IsDropTarget = false;
            _targetColumn = column;
            if (column is not null) column.IsDropTarget = true;
        }
        _beforeCard = null;
        _indicator.IsVisible = false;
        _view.SetCurrentValue(InputElement.CursorProperty,
            new Cursor(column is null ? StandardCursorType.No : StandardCursorType.SizeAll));
        if (columnControl is null || column is null) return;

        var cards = columnControl.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("kanban-card") && b.IsEffectivelyVisible && b != _source).ToArray();
        Button? before = null;
        foreach (var card in cards)
        {
            var rect = BoundsInLayer(card);
            if (Horizontal ? _position.X < rect.Center.X : _position.Y < rect.Center.Y)
            {
                before = card;
                break;
            }
        }
        _beforeCard = before?.DataContext as KanbanCardViewModel;
        var scroll = columnControl.GetVisualDescendants().OfType<ScrollViewer>().First();
        var viewport = BoundsInLayer(scroll);
        var anchor = before ?? cards.LastOrDefault();
        var bounds = anchor is null ? viewport.Deflate(14) : BoundsInLayer(anchor);
        var x = Horizontal ? (before is null && anchor is not null ? bounds.Right + 4 : bounds.Left - 6) : bounds.Left;
        var y = Horizontal ? bounds.Top : (before is null && anchor is not null ? bounds.Bottom + 4 : bounds.Top - 6);
        var indicator = new Rect(x, y, Horizontal ? 2 : bounds.Width, Horizontal ? bounds.Height : 2);
        // No dibujar la marca sobre la cabecera, el pie ni fuera de los scrolls anidados.
        foreach (var ancestor in scroll.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
            indicator = indicator.Intersect(BoundsInLayer(ancestor));
        if (indicator.Width <= 0 || indicator.Height <= 0) return;
        Canvas.SetLeft(_indicator, indicator.X);
        Canvas.SetTop(_indicator, indicator.Y);
        _indicator.Width = indicator.Width;
        _indicator.Height = indicator.Height;
        _indicator.IsVisible = true;
    }

    private Rect BoundsInLayer(Control control) =>
        new(control.TranslatePoint(default, _layer) ?? default, control.Bounds.Size);

    /// <summary>Desplaza tanto el cuerpo de la columna como el tablero al acercarse a sus bordes.</summary>
    private void AutoScroll()
    {
        if (!_dragging) return;
        if (!SourceIsValid()) { Cancel(); return; }
        var hit = HitControl();
        if (hit is null) return;
        var changed = false;
        foreach (var scroll in hit.GetSelfAndVisualAncestors().OfType<ScrollViewer>())
        {
            var rect = BoundsInLayer(scroll);
            var dx = scroll.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                ? 0 : EdgeStep(_position.X, rect.Left, rect.Right);
            var dy = scroll.VerticalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                ? 0 : EdgeStep(_position.Y, rect.Top, rect.Bottom);
            var offset = new Vector(
                Math.Clamp(scroll.Offset.X + dx, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)),
                Math.Clamp(scroll.Offset.Y + dy, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
            if (offset == scroll.Offset) continue;
            scroll.Offset = offset;
            changed = true;
        }
        if (changed)
        {
            // El destino depende de la nueva posición de las tarjetas después del layout.
            _view.UpdateLayout();
            UpdateFeedback();
        }
    }

    private static double EdgeStep(double value, double min, double max)
    {
        const double edge = 40;
        const double speed = 12;
        if (value < min || value > max) return 0;
        if (value < min + edge) return -speed * (1 - (value - min) / edge);
        if (value > max - edge) return speed * (1 - (max - value) / edge);
        return 0;
    }
}
