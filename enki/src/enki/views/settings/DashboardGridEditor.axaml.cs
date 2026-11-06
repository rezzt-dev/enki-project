using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind del editor de rejilla del Dashboard (Ajustes › Dashboard, organización
/// Personalizada). Su único trabajo es traducir gestos del puntero y del teclado a celdas: qué celda hay
/// bajo el ratón, cuántas columnas y filas abarcaría la ficha si se soltara ahí. Decidir si esa
/// colocación vale, aplicarla y guardarla es cosa de <see cref="SettingsViewModel"/>
/// (<c>SettingsViewModel.DashboardGrid.cs</c>), que es también quien responde a los botones de la barra
/// inferior — así el arrastre y el teclado no pueden acabar aplicando reglas distintas.
///
/// El arrastre es por captura de puntero y no por <c>DragDrop.DoDragDropAsync</c> (lo que sí usa la lista
/// de secciones de al lado para reordenarla) a propósito: aquí hace falta seguimiento continuo — la
/// silueta del destino se recalcula en cada movimiento del ratón — y el arrastre del sistema solo avisa
/// al entrar y salir de un control con <c>AllowDrop</c>, que obligaría a sembrar de zonas de destino cada
/// celda de la rejilla.</summary>
public partial class DashboardGridEditor : UserControl
{
    /// <summary>Qué se está haciendo con la ficha capturada: moverla entera o estirar su esquina.</summary>
    private enum TileDragMode
    {
        None,
        Move,
        Resize
    }

    private TileDragMode _mode;
    private string? _dragKey;

    /// <summary>Celda agarrada dentro de la ficha, en celdas desde su esquina superior izquierda. Sin
    /// esto, arrastrar una ficha ancha desde su mitad derecha la saltaría hacia la izquierda al soltar:
    /// la ficha se colocaría con su esquina donde está el puntero en vez de conservar el punto por el que
    /// se la agarró.</summary>
    private int _grabRowOffset;
    private int _grabColumnOffset;

    public DashboardGridEditor()
    {
        InitializeComponent();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>El panel real que coloca las fichas — el mismo cuyo <c>GetCellBounds</c>/<c>TryGetCell</c>
    /// se usa para la silueta, así que las coordenadas siempre son las suyas.</summary>
    private Controls.DashboardGridPanel? TilePanel => TileLayer.ItemsPanelRoot as Controls.DashboardGridPanel;

    private void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel tile } control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (Vm is not { } vm || !vm.IsCustomLayout) return;

        control.Focus();
        vm.BeginDashboardTileDrag(tile.Key);

        _mode = TileDragMode.Move;
        _dragKey = tile.Key;

        if (TilePanel is { } panel && panel.TryGetCell(e.GetPosition(panel), out var row, out var column))
        {
            _grabRowOffset = Math.Max(0, row - tile.GridRow);
            _grabColumnOffset = Math.Max(0, column - tile.GridColumn);
        }
        else
        {
            _grabRowOffset = 0;
            _grabColumnOffset = 0;
        }

        e.Pointer.Capture(control);
        UpdateGhost();
        e.Handled = true;
    }

    /// <summary>El tirador es hijo de la ficha, así que marcar el evento como tratado aquí es lo que
    /// impide que el mismo clic arranque además un movimiento.</summary>
    private void ResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel tile } control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        if (Vm is not { } vm || !vm.IsCustomLayout) return;

        vm.BeginDashboardTileDrag(tile.Key);

        _mode = TileDragMode.Resize;
        _dragKey = tile.Key;
        _grabRowOffset = 0;
        _grabColumnOffset = 0;

        e.Pointer.Capture(control);
        UpdateGhost();
        e.Handled = true;
    }

    private void Tile_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mode == TileDragMode.None || _dragKey is null) return;
        if (Vm is not { } vm || TilePanel is not { } panel) return;

        var tile = FindTile(vm, _dragKey);
        if (tile is null) return;

        // Fuera de la rejilla no se recalcula nada: la silueta se queda en el último destino válido, de
        // modo que soltar con el ratón fuera del lienzo deja la ficha donde el usuario ya la veía.
        if (!panel.TryGetCell(e.GetPosition(panel), out var row, out var column)) return;

        if (_mode == TileDragMode.Move)
        {
            vm.UpdateDashboardTileDrag(
                _dragKey,
                Math.Max(0, row - _grabRowOffset),
                Math.Max(0, column - _grabColumnOffset),
                tile.GridRowSpan,
                tile.GridColumnSpan);
        }
        else
        {
            vm.UpdateDashboardTileDrag(
                _dragKey,
                tile.GridRow,
                tile.GridColumn,
                Math.Max(1, row - tile.GridRow + 1),
                Math.Max(1, column - tile.GridColumn + 1));
        }

        UpdateGhost();
        e.Handled = true;
    }

    private void Tile_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode == TileDragMode.None || _dragKey is null) return;

        Vm?.EndDashboardTileDrag(_dragKey, commit: true);
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Si el sistema retira la captura a media faena (la ventana pierde el foco, otro control
    /// se lleva el puntero), el arrastre se cancela sin aplicar nada: quedarse con <c>_mode</c> colgado
    /// dejaría la siguiente pasada del ratón moviendo una ficha que nadie está arrastrando.</summary>
    private void Tile_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_mode == TileDragMode.None || _dragKey is null) return;

        Vm?.EndDashboardTileDrag(_dragKey, commit: false);
        EndDrag(null);
    }

    /// <summary>Clic en el lienzo, fuera de cualquier ficha: deselecciona. Solo llega aquí si ninguna
    /// ficha trató el evento antes.</summary>
    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
        => Vm?.ClearDashboardTileSelection();

    /// <summary>Teclado sobre la ficha enfocada: flechas para moverla, Mayús+flechas para redimensionarla,
    /// Supr para sacarla de la rejilla y Esc para deseleccionar. Es la ruta accesible completa — todo lo
    /// que se puede hacer arrastrando se puede hacer sin ratón.</summary>
    private void Tile_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: DashboardSectionConfigViewModel tile }) return;
        if (Vm is not { } vm || !vm.IsCustomLayout) return;

        var resize = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        switch (e.Key)
        {
            case Key.Left:
                Run(resize ? vm.ShrinkDashboardTileWidthCommand : vm.MoveDashboardTileLeftCommand, tile.Key);
                break;
            case Key.Right:
                Run(resize ? vm.GrowDashboardTileWidthCommand : vm.MoveDashboardTileRightCommand, tile.Key);
                break;
            case Key.Up:
                Run(resize ? vm.ShrinkDashboardTileHeightCommand : vm.MoveDashboardTileUpCommand, tile.Key);
                break;
            case Key.Down:
                Run(resize ? vm.GrowDashboardTileHeightCommand : vm.MoveDashboardTileDownCommand, tile.Key);
                break;
            case Key.Delete or Key.Back:
                vm.RemoveDashboardSectionFromGridCommand.Execute(tile.Key);
                break;
            case Key.Escape:
                vm.ClearDashboardTileSelection();
                break;
            default:
                return;
        }

        e.Handled = true;
        return;

        static void Run(System.Windows.Input.ICommand command, string key) => command.Execute(key);
    }

    private static DashboardSectionConfigViewModel? FindTile(SettingsViewModel vm, string key)
    {
        foreach (var section in vm.DashboardSections)
        {
            if (section.Key == key) return section;
        }
        return null;
    }

    private void EndDrag(IPointer? pointer)
    {
        _mode = TileDragMode.None;
        _dragKey = null;
        _grabRowOffset = 0;
        _grabColumnOffset = 0;
        pointer?.Capture(null);
        Ghost.IsVisible = false;
    }

    /// <summary>Coloca la silueta sobre el destino actual. La geometría sale del propio panel, no de un
    /// cálculo paralelo, para que lo prometido y lo aplicado sean el mismo rectángulo.</summary>
    private void UpdateGhost()
    {
        if (Vm is not { IsDashboardTileDragActive: true } vm || TilePanel is not { } panel)
        {
            Ghost.IsVisible = false;
            return;
        }

        var bounds = panel.GetCellBounds(vm.DashboardDragRow, vm.DashboardDragColumn, vm.DashboardDragRowSpan, vm.DashboardDragColumnSpan);
        var origin = panel.TranslatePoint(bounds.Position, GhostLayer) ?? bounds.Position;

        Canvas.SetLeft(Ghost, origin.X);
        Canvas.SetTop(Ghost, origin.Y);
        Ghost.Width = bounds.Width;
        Ghost.Height = bounds.Height;
        Ghost.Classes.Set("invalid", !vm.IsDashboardDropValid);
        Ghost.IsVisible = true;
    }
}
