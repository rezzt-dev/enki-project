using System;
using System.Runtime.CompilerServices;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace enki.Views;

/// <summary>Centra los iconos con la tinta real de su etiqueta, no con el espacio de la fuente.
/// Recalcula tras el layout para respetar fuente, tamaño, traducción, ajuste de línea y zoom.</summary>
public sealed class IconTextAlignment : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<IconTextAlignment, Control, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<Control, AlignmentState> States = new();

    static IconTextAlignment()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((icon, _) =>
            States.GetValue(icon, i => new AlignmentState(i)).SetEnabled(GetIsEnabled(icon)));
    }

    public static bool GetIsEnabled(Control icon) => icon.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control icon, bool value) => icon.SetValue(IsEnabledProperty, value);

    private sealed class AlignmentState
    {
        private readonly Control _icon;
        private readonly TranslateTransform _translation = new();
        private bool _enabled;
        private ITransform? _originalTransform;

        public AlignmentState(Control icon)
        {
            _icon = icon;
            icon.AttachedToVisualTree += (_, _) => Subscribe();
            icon.DetachedFromVisualTree += (_, _) => Unsubscribe();
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            Unsubscribe();
            if (enabled) Subscribe();
        }

        private void Subscribe()
        {
            if (!_enabled || TopLevel.GetTopLevel(_icon) is null) return;
            _icon.LayoutUpdated -= Update;
            _icon.LayoutUpdated += Update;
        }

        private void Unsubscribe()
        {
            _icon.LayoutUpdated -= Update;
            if (ReferenceEquals(_icon.RenderTransform, _translation)) _icon.RenderTransform = _originalTransform;
            _translation.Y = 0;
        }

        private void Update(object? sender, EventArgs args)
        {
            // Los objetos inline del editor y las animaciones con transformación propia
            // conservan su composición; allí la línea del documento gobierna la posición.
            if (!_icon.IsEffectivelyVisible ||
                (_icon.RenderTransform is { } transform && !transform.Value.IsIdentity &&
                 !ReferenceEquals(transform, _translation))) return;

            var label = FindLabel(_icon);
            if (label is null || !TryGetInkBounds(label, label, out var top, out var bottom))
            {
                _translation.Y = 0;
                return;
            }

            // TranslatePoint incluye la corrección anterior: restarla evita acumularla.
            var center = _icon.TranslatePoint(new Point(0, _icon.Bounds.Height / 2), label);
            if (center is null) return;
            var offset = (top + bottom) / 2 - (center.Value.Y - _translation.Y);
            if (!double.IsFinite(offset)) return;
            if (!ReferenceEquals(_icon.RenderTransform, _translation))
            {
                _originalTransform = _icon.RenderTransform;
                _icon.RenderTransform = _translation;
            }
            if (Math.Abs(_translation.Y - offset) > 0.01) _translation.Y = offset;
        }
    }

    private static Control? FindLabel(Control icon)
    {
        if (icon.Parent is MenuItem menu)
            return menu.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => Equals(text.Text, menu.Header));

        Control branch = icon;
        // Admite el contenedor del icono con una insignia superpuesta del menú lateral.
        while (branch.Parent is Border || branch.Parent?.GetType() == typeof(Panel))
            branch = (Control)branch.Parent;

        if (branch.Parent is not Panel row ||
            (row is not Grid && row is not StackPanel { Orientation: Orientation.Horizontal })) return null;

        Control? best = null;
        var distance = int.MaxValue;
        var position = row is Grid ? Grid.GetColumn(branch) : row.Children.IndexOf(branch);
        foreach (var child in row.Children)
        {
            if (ReferenceEquals(child, branch) || !child.IsEffectivelyVisible || !IsLabel(child)) continue;
            if (row is Grid && (Grid.GetRow(child) != Grid.GetRow(branch) ||
                               Grid.GetColumn(child) == Grid.GetColumn(branch))) continue;
            var candidatePosition = row is Grid ? Grid.GetColumn(child) : row.Children.IndexOf(child);
            // A igual distancia se prefiere el texto que sigue al icono.
            var candidateDistance = Math.Abs(candidatePosition - position) * 2 + (candidatePosition < position ? 1 : 0);
            if (candidateDistance >= distance) continue;
            distance = candidateDistance;
            best = child;
        }
        return best;
    }

    private static bool IsLabel(Control control)
    {
        if (control is TextBlock) return true;
        if (control is Border { Child: { } content }) return IsLabel(content);
        // Ignorar adornos y botones auxiliares sin atravesar controles interactivos.
        return control is Panel panel && panel.Children.Any(IsLabel);
    }

    private static bool TryGetInkBounds(Control control, Control relativeTo, out double top, out double bottom)
    {
        top = double.PositiveInfinity;
        bottom = double.NegativeInfinity;
        if (!control.IsEffectivelyVisible) return false;
        if (control is TextBlock text)
        {
            var layout = text.TextLayout;
            if (layout.Extent <= 0 || layout.Width <= 0) return false;
            var origin = text.TranslatePoint(new Point(0, text.Padding.Top), relativeTo);
            if (origin is null) return false;
            var renderOffset = text.Bounds.Height < layout.Height && text.VerticalAlignment == VerticalAlignment.Center
                ? (text.Bounds.Height - layout.Height) / 2 : 0;
            bottom = origin.Value.Y + renderOffset + layout.Height + layout.OverhangAfter;
            top = bottom - layout.Extent;
            return double.IsFinite(top) && double.IsFinite(bottom);
        }
        if (control is Border { Child: { } content })
            return TryGetInkBounds(content, relativeTo, out top, out bottom);
        if (control is not Panel panel) return false;
        foreach (var child in panel.Children)
        {
            if (!TryGetInkBounds(child, relativeTo, out var childTop, out var childBottom)) continue;
            top = Math.Min(top, childTop);
            bottom = Math.Max(bottom, childBottom);
        }
        return top <= bottom;
    }
}
