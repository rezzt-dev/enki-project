using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace enki.Controls;

/// <summary>Comportamiento de teclado de los popovers (<c>Popup</c> + <c>Border.dropdown-popover</c>): Escape
/// cierra el popover y devuelve el foco al botón que lo abrió (<see cref="Popup.PlacementTarget"/>), para que
/// el recorrido con teclado siga donde estaba. Se activa con <c>controls:PopoverKeyboard.CloseOnEscape="True"</c>;
/// cerrar pone <see cref="Popup.IsOpen"/> a falso, así que un enlace TwoWay avisa también al viewmodel.</summary>
public static class PopoverKeyboard
{
    public static readonly AttachedProperty<bool> CloseOnEscapeProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("CloseOnEscape", typeof(PopoverKeyboard));

    static PopoverKeyboard()
    {
        CloseOnEscapeProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            popup.Opened -= OnOpened;
            if (change.NewValue is true) popup.Opened += OnOpened;
        });
    }

    public static bool GetCloseOnEscape(Popup popup) => popup.GetValue(CloseOnEscapeProperty);

    public static void SetCloseOnEscape(Popup popup, bool value) => popup.SetValue(CloseOnEscapeProperty, value);

    private static void OnOpened(object? sender, EventArgs e)
    {
        // El contenido vive en su propia raíz de entrada: el manejador va en él y no en el Popup.
        if (sender is not Popup { Child: { } child }) return;
        child.RemoveHandler(InputElement.KeyDownEvent, OnChildKeyDown);
        child.AddHandler(InputElement.KeyDownEvent, OnChildKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnChildKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Control { Parent: Popup { IsOpen: true } popup }) return;
        e.Handled = true;
        popup.IsOpen = false;
        if (popup.PlacementTarget is InputElement trigger) trigger.Focus(NavigationMethod.Tab);
    }
}
