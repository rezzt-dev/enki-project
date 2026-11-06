using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace enki.Controls;

/// <summary>Invierte solo la distribución del panel y el contenido. Cada hijo conserva su dirección
/// de lectura; las columnas, los bindings y el estado del navegador siguen siendo los originales.</summary>
public sealed class SidePanelGrid : Grid
{
    public static readonly StyledProperty<bool> IsPanelOnRightProperty =
        AvaloniaProperty.Register<SidePanelGrid, bool>(nameof(IsPanelOnRight));

    public bool IsPanelOnRight
    {
        get => GetValue(IsPanelOnRightProperty);
        set => SetValue(IsPanelOnRightProperty, value);
    }

    public SidePanelGrid()
    {
        // También cubre las capas añadidas después de cargar el XAML, como la etiqueta de arrastre.
        Children.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is null) return;
            foreach (Control child in change.NewItems)
            {
                // El splitter usa la dirección del Grid para invertir también el gesto de arrastre.
                if (child is not GridSplitter) child.FlowDirection = FlowDirection.LeftToRight;
            }
        };
        ApplyPosition();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPanelOnRightProperty) ApplyPosition();
    }

    private void ApplyPosition()
    {
        FlowDirection = IsPanelOnRight ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Classes.Set("panel-right", IsPanelOnRight);
    }

    /// <summary>Convierte el arrastre físico al crecimiento del panel en cualquiera de los lados.</summary>
    public static double GetResizeDelta(Control grip, double delta) =>
        grip.FindAncestorOfType<SidePanelGrid>()?.IsPanelOnRight == true ? -delta : delta;
}
