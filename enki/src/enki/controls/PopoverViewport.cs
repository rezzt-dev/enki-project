using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace enki.Controls;

/// <summary>Limita el contenido emergente al área de la ventana, también al cambiar su tamaño.
/// Restaura los límites originales al cerrar para no acumular restricciones entre aperturas.</summary>
public static class PopoverViewport
{
    public static readonly AttachedProperty<bool> ConstrainProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("Constrain", typeof(PopoverViewport));

    private sealed class Constraint
    {
        public Control Child = null!;
        public double MaxWidth;
        public double MaxHeight;
        public IDisposable? Subscription;
    }

    private static readonly ConditionalWeakTable<Popup, Constraint> Constraints = new();

    static PopoverViewport()
    {
        ConstrainProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            popup.Opened -= OnOpened;
            popup.Closed -= OnClosed;
            if (change.NewValue is true)
            {
                popup.Opened += OnOpened;
                popup.Closed += OnClosed;
            }
            else Release(popup);
        });
    }

    public static bool GetConstrain(Popup popup) => popup.GetValue(ConstrainProperty);
    public static void SetConstrain(Popup popup, bool value) => popup.SetValue(ConstrainProperty, value);

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: { } child, PlacementTarget: { } target } popup ||
            TopLevel.GetTopLevel(target) is not { } window) return;
        Release(popup);
        var constraint = new Constraint { Child = child, MaxWidth = child.MaxWidth, MaxHeight = child.MaxHeight };
        Constraints.Add(popup, constraint);
        constraint.Subscription = window.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
        {
            child.MaxWidth = Math.Min(constraint.MaxWidth, Math.Max(1, bounds.Width - 32));
            child.MaxHeight = Math.Min(constraint.MaxHeight, Math.Max(1, bounds.Height - 32));
        });
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup) Release(popup);
    }

    private static void Release(Popup popup)
    {
        if (!Constraints.TryGetValue(popup, out var constraint)) return;
        constraint.Subscription?.Dispose();
        constraint.Child.MaxWidth = constraint.MaxWidth;
        constraint.Child.MaxHeight = constraint.MaxHeight;
        Constraints.Remove(popup);
    }
}
