using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace enki.Controls;

/// <summary>Estado abierto de un popover cuyo viewmodel comparten varias vistas vivas a la vez. El asistente es el
/// caso: la sección y el panel lateral son el mismo <c>AssistantViewModel</c>, y la sección se queda construida y
/// oculta al navegar (ver <c>SectionHost</c>). Con <c>IsOpen="{Binding …}"</c> directo, abrir el menú desde el panel
/// abría también el de la sección oculta, anclado donde estuvo su botón. Con
/// <c>controls:SharedPopover.IsOpen="{Binding …, Mode=TwoWay}"</c> en su lugar, solo se abre el popover de la vista
/// que se está viendo; cerrarlo (clic fuera, Escape) sigue avisando al viewmodel.</summary>
public static class SharedPopover
{
    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>("IsOpen", typeof(SharedPopover), defaultBindingMode: BindingMode.TwoWay);

    static SharedPopover()
    {
        IsOpenProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            popup.Closed -= OnClosed;
            popup.Closed += OnClosed;
            popup.IsOpen = change.NewValue is true && popup.IsEffectivelyVisible;
        });
    }

    public static bool GetIsOpen(Popup popup) => popup.GetValue(IsOpenProperty);

    public static void SetIsOpen(Popup popup, bool value) => popup.SetValue(IsOpenProperty, value);

    /// <summary>El popover visible se ha cerrado por su cuenta: el estado compartido pasa a cerrado. Los popovers
    /// ocultos nunca llegan a abrirse, así que no pasan por aquí.</summary>
    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup && GetIsOpen(popup)) SetIsOpen(popup, false);
    }
}
