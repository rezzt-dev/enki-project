using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Dashboard › editor de rejilla: la mitad de la página que hace que la organización
/// "Personalizada" sea de verdad personalizable — cada sección se coloca en la celda que el usuario
/// quiera y se estira a las columnas y filas que quiera, arrastrando o con el teclado.
///
/// El estado real vive en un único sitio, <see cref="Models.VaultSettings.DashboardCustomLayout"/>, y la
/// geometría que se ve en pantalla la calcula siempre <see cref="DashboardLayoutEngine"/> a partir de él
/// — este archivo nunca escribe <c>GridRow</c>/<c>GridColumn</c> directamente sobre una ficha. Ese rodeo
/// es lo que garantiza que el editor y el Dashboard real no puedan discrepar: los dos leen lo mismo y lo
/// pasan por el mismo normalizador, así que lo que se ve aquí es exactamente lo que se verá allí.
///
/// Toda edición pasa por <see cref="TryPlaceDashboardTile"/>, que rechaza lo inválido (fuera de rejilla o
/// encima de otra ficha) en vez de aceptarlo y dejar que el normalizador lo arregle por detrás: mover una
/// ficha nunca desplaza a otra a tus espaldas.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Celdas vacías que dibujan el lienzo del editor por debajo de las fichas. Se reconstruyen
    /// enteras en cada cambio de tamaño de la rejilla — son a lo sumo 144 objetos de dos enteros, no
    /// compensa un diff incremental.</summary>
    public ObservableCollection<DashboardGridCellViewModel> DashboardGridCells { get; } = [];

    /// <summary>Dimensiones con las que se generó <see cref="DashboardGridCells"/>, para no rehacerlas en
    /// cada refresco (que ocurre tras cada movimiento de ficha) y sí rehacerlas cuando cambian de verdad.</summary>
    private int _dashboardGridCellRows;
    private int _dashboardGridCellColumns;

    /// <summary>Secciones ocultas, en la bandeja bajo el editor, listas para volver a la rejilla.</summary>
    public bool HasHiddenDashboardSections => DashboardSections.Any(s => !s.IsVisible);

    /// <summary>Ficha seleccionada en el editor (o <c>null</c>). Manda en qué ficha obedece a los botones
    /// de mover/redimensionar y al teclado.</summary>
    [ObservableProperty]
    private DashboardSectionConfigViewModel? _selectedDashboardTile;

    // ===================== Silueta de destino mientras se arrastra =====================
    // La pinta el editor como un rectángulo por encima de la rejilla. Vive aquí y no en la vista para que
    // la validación ("¿cabe aquí?") sea la misma que decide si el soltar se acepta — una sola regla.

    [ObservableProperty]
    private bool _isDashboardTileDragActive;

    [ObservableProperty]
    private bool _isDashboardDropValid;

    [ObservableProperty]
    private int _dashboardDragRow;

    [ObservableProperty]
    private int _dashboardDragColumn;

    [ObservableProperty]
    private int _dashboardDragRowSpan = 1;

    [ObservableProperty]
    private int _dashboardDragColumnSpan = 1;

    /// <summary>Reconstruye lo que depende del tamaño de la rejilla o de qué secciones están visibles.
    /// Lo llama <see cref="RefreshDashboardSummary"/>, que ya corre después de cada build, reorden,
    /// toggle, reset y cambio de organización — así el editor no necesita suscribirse a nada.</summary>
    private void RefreshDashboardGridEditor()
    {
        var rows = DashboardLayoutEngine.ClampDimension(DashboardGridRows);
        var cols = DashboardLayoutEngine.ClampDimension(DashboardGridColumns);

        // El editor enseña al menos las filas configuradas, pero si una ficha desbordó por abajo (layout
        // heredado, rejilla que se encogió) hay que enseñar también esas filas o la ficha se saldría del
        // lienzo y no habría forma de recuperarla arrastrando.
        foreach (var section in DashboardSections.Where(s => s.IsVisible))
            rows = Math.Max(rows, section.GridRow + section.GridRowSpan);

        // Comparar contra las dimensiones y no contra el número de celdas: 4×6 y 6×4 dan las mismas 24
        // celdas pero en coordenadas distintas, y reutilizarlas dejaría el fondo de la rejilla dibujado
        // con la forma anterior.
        if (_dashboardGridCellRows != rows || _dashboardGridCellColumns != cols)
        {
            _dashboardGridCellRows = rows;
            _dashboardGridCellColumns = cols;

            DashboardGridCells.Clear();
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                    DashboardGridCells.Add(new DashboardGridCellViewModel { Row = r, Column = c });
        }

        OnPropertyChanged(nameof(HasHiddenDashboardSections));
    }

    /// <summary>Las fichas colocadas ahora mismo, tal cual las calculó el motor — la fuente contra la que
    /// se valida cualquier movimiento.</summary>
    private Dictionary<string, DashboardGridSlot> CurrentTileSlots()
        => DashboardSections
            .Where(s => s.IsVisible)
            .ToDictionary(s => s.Key, s => new DashboardGridSlot(s.GridRow, s.GridColumn, s.GridRowSpan, s.GridColumnSpan));

    /// <summary>Foto del layout para persistir: lo que hay en pantalla más las posiciones recordadas de
    /// las secciones ocultas. Guardar también las ocultas es lo que hace que devolver una sección a la
    /// rejilla la lleve a donde estaba, si el hueco sigue libre.</summary>
    private Dictionary<string, int[]> SnapshotDashboardLayout()
    {
        var layout = new Dictionary<string, int[]>(_vaultSettingsService.Settings.DashboardCustomLayout ?? []);
        foreach (var section in DashboardSections.Where(s => s.IsVisible))
            layout[section.Key] = [section.GridRow, section.GridColumn, section.GridRowSpan, section.GridColumnSpan];
        return layout;
    }

    private void PersistDashboardLayout(Dictionary<string, int[]> layout)
    {
        _vaultSettingsService.Settings.DashboardCustomLayout = layout;
        _vaultSettingsService.Save();
        RefreshDashboardSummary();
    }

    /// <summary>Vuelve a pasar el layout guardado por el normalizador y guarda el resultado. Se llama al
    /// cambiar el tamaño de la rejilla: sin esto, encoger a 3 columnas dejaría en el JSON fichas en la
    /// columna 5 que el motor recolocaría en cada arranque, y la posición "de verdad" iría cambiando
    /// según qué secciones estuvieran visibles.</summary>
    private void NormalizeAndPersistCustomLayout()
    {
        if (!IsCustomLayout) return;

        var visibleKeys = DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList();
        var normalized = DashboardLayoutEngine.NormalizeCustom(
            visibleKeys,
            _vaultSettingsService.Settings.DashboardCustomLayout,
            DashboardGridRows,
            DashboardGridColumns);

        var layout = new Dictionary<string, int[]>(_vaultSettingsService.Settings.DashboardCustomLayout ?? []);
        foreach (var (key, slot) in normalized)
            layout[key] = [slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan];

        _vaultSettingsService.Settings.DashboardCustomLayout = layout;
        _vaultSettingsService.Save();
    }

    /// <summary>Siembra el layout personalizado con la geometría de <paramref name="fromStyle"/> y adopta
    /// el tamaño natural de esa plantilla, para que el salto de plantilla a Personalizado no cambie nada
    /// en pantalla: lo que había es exactamente el punto de partida.</summary>
    private void SeedCustomLayoutFrom(string fromStyle)
    {
        var visibleKeys = DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList();
        var (rows, cols) = DashboardLayoutEngine.NaturalSizeOf(fromStyle);

        // Classic no tiene tabla de tramos (una fila a todo lo ancho por sección), así que su "tamaño
        // natural" es tantas filas como secciones visibles — recortado al máximo de la rejilla.
        if (fromStyle == DashboardLayoutEngine.Classic)
            rows = DashboardLayoutEngine.ClampDimension(Math.Max(1, visibleKeys.Count));

        var seeded = DashboardLayoutEngine.BuildCustomLayoutFrom(visibleKeys, fromStyle, rows, cols);

        var layout = new Dictionary<string, int[]>(_vaultSettingsService.Settings.DashboardCustomLayout ?? []);
        foreach (var (key, pos) in seeded)
            layout[key] = pos;

        _vaultSettingsService.Settings.DashboardCustomLayout = layout;
        _vaultSettingsService.Settings.DashboardGridRows = rows;
        _vaultSettingsService.Settings.DashboardGridColumns = cols;
        _vaultSettingsService.Save();

        DashboardGridRows = rows;
        DashboardGridColumns = cols;
    }

    /// <summary>Botón "personalizar esta organización", junto a la vista previa de plantillas: pasa a
    /// Personalizado partiendo de la plantilla que se está viendo. A diferencia de pulsar la tarjeta
    /// "Personalizado" (que recupera la rejilla guardada, si la hay), esto siembra siempre desde la
    /// plantilla actual — es lo que el botón promete estando donde está.</summary>
    [RelayCommand]
    private void CustomizeDashboardLayout()
    {
        if (IsCustomLayout) return;

        SeedCustomLayoutFrom(DashboardLayoutStyle);
        SelectDashboardLayoutStyle(DashboardLayoutEngine.Custom);
    }

    // ===================== Colocar, mover y redimensionar =====================

    /// <summary>Si una colocación es legal: dentro de la rejilla y sin pisar a ninguna otra ficha
    /// visible. La usan por igual el arrastre (para colorear la silueta), el soltar y los botones de
    /// teclado, de modo que no puede haber un camino que acepte lo que otro rechaza.</summary>
    public bool CanPlaceDashboardTile(string key, int row, int column, int rowSpan, int columnSpan)
        => DashboardLayoutEngine.CanPlace(
            CurrentTileSlots(),
            key,
            new DashboardGridSlot(row, column, rowSpan, columnSpan),
            DashboardGridRows,
            DashboardGridColumns);

    /// <summary>Coloca una ficha, si cabe. Devuelve false sin tocar nada cuando no — el editor lo traduce
    /// en que la ficha vuelve a su sitio en vez de empujar a sus vecinas.</summary>
    public bool TryPlaceDashboardTile(string key, int row, int column, int rowSpan, int columnSpan)
    {
        var tile = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (tile is null || !tile.IsVisible) return false;
        if (!IsCustomLayout) return false;
        if (!CanPlaceDashboardTile(key, row, column, rowSpan, columnSpan)) return false;

        var layout = SnapshotDashboardLayout();
        layout[key] = [row, column, rowSpan, columnSpan];
        PersistDashboardLayout(layout);
        return true;
    }

    /// <summary>Desplaza una ficha una celda. El movimiento con teclado equivalente al arrastre, y la
    /// única forma de reorganizar sin ratón — de ahí que cada ficha del editor lleve estos cuatro botones
    /// con su nombre accesible en vez de dejarlo solo al puntero.</summary>
    private void NudgeDashboardTile(string key, int deltaRow, int deltaColumn)
    {
        var tile = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (tile is null) return;

        TryPlaceDashboardTile(key, tile.GridRow + deltaRow, tile.GridColumn + deltaColumn, tile.GridRowSpan, tile.GridColumnSpan);
    }

    [RelayCommand]
    private void MoveDashboardTileUp(string key) => NudgeDashboardTile(key, -1, 0);

    [RelayCommand]
    private void MoveDashboardTileDown(string key) => NudgeDashboardTile(key, 1, 0);

    [RelayCommand]
    private void MoveDashboardTileLeft(string key) => NudgeDashboardTile(key, 0, -1);

    [RelayCommand]
    private void MoveDashboardTileRight(string key) => NudgeDashboardTile(key, 0, 1);

    private void ResizeDashboardTile(string key, int deltaRowSpan, int deltaColumnSpan)
    {
        var tile = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (tile is null) return;

        var rowSpan = Math.Max(1, tile.GridRowSpan + deltaRowSpan);
        var columnSpan = Math.Max(1, tile.GridColumnSpan + deltaColumnSpan);
        TryPlaceDashboardTile(key, tile.GridRow, tile.GridColumn, rowSpan, columnSpan);
    }

    [RelayCommand]
    private void GrowDashboardTileWidth(string key) => ResizeDashboardTile(key, 0, 1);

    [RelayCommand]
    private void ShrinkDashboardTileWidth(string key) => ResizeDashboardTile(key, 0, -1);

    [RelayCommand]
    private void GrowDashboardTileHeight(string key) => ResizeDashboardTile(key, 1, 0);

    [RelayCommand]
    private void ShrinkDashboardTileHeight(string key) => ResizeDashboardTile(key, -1, 0);

    [RelayCommand]
    private void SelectDashboardTile(string key)
    {
        foreach (var section in DashboardSections)
            section.IsSelected = section.Key == key && section.IsVisible;

        SelectedDashboardTile = DashboardSections.FirstOrDefault(s => s.Key == key && s.IsVisible);
    }

    /// <summary>Deja la rejilla sin ficha seleccionada — clic en el lienzo vacío, Escape, o una ficha
    /// que acaba de salir de la rejilla.</summary>
    public void ClearDashboardTileSelection()
    {
        foreach (var section in DashboardSections)
            section.IsSelected = false;

        SelectedDashboardTile = null;
    }

    /// <summary>Devuelve una sección oculta a la rejilla. No hace falta buscarle sitio a mano: el
    /// normalizador la coloca en su posición recordada si sigue libre y, si no, en el primer hueco donde
    /// quepa — y <see cref="NormalizeAndPersistCustomLayout"/> fija esa decisión en el vault para que no
    /// se recalcule distinta más adelante.</summary>
    [RelayCommand]
    private void ShowDashboardSection(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null || item.IsVisible) return;

        item.IsVisible = true;
        SaveDashboardLayout();
        NormalizeAndPersistCustomLayout();
        RefreshDashboardSummary();
        SelectDashboardTile(key);
    }

    /// <summary>Saca una sección de la rejilla (la oculta del Dashboard) conservando su posición para
    /// cuando vuelva. Es el mismo camino que el interruptor de la lista de secciones, incluido el aviso
    /// de que no se puede quedar el Dashboard sin ninguna.</summary>
    [RelayCommand]
    private void RemoveDashboardSectionFromGrid(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null || !item.IsVisible) return;

        if (SelectedDashboardTile?.Key == key)
        {
            SelectedDashboardTile = null;
            item.IsSelected = false;
        }

        ToggleDashboardSectionVisibility(key);
    }

    /// <summary>Compacta la rejilla hacia arriba-izquierda conservando el tamaño de cada ficha — la
    /// salida de emergencia después de un rato moviendo cosas a mano, y la forma de recuperar una ficha
    /// que se quedó desbordada por debajo tras encoger la rejilla.</summary>
    [RelayCommand]
    private void AutoArrangeDashboardGrid()
    {
        if (!IsCustomLayout) return;

        var visibleKeys = DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList();
        var arranged = DashboardLayoutEngine.AutoArrange(
            visibleKeys,
            SnapshotDashboardLayout(),
            DashboardGridRows,
            DashboardGridColumns);

        var layout = new Dictionary<string, int[]>(_vaultSettingsService.Settings.DashboardCustomLayout ?? []);
        foreach (var (key, pos) in arranged)
            layout[key] = pos;

        PersistDashboardLayout(layout);

        _notificationService.Push(
            LocalizationService.Instance["Settings_Dashboard_Grid_AutoArranged_Title"],
            LocalizationService.Instance["Settings_Dashboard_Grid_AutoArranged_Message"],
            Models.NotificationSeverity.Info);
    }

    // ===================== Arrastre (lo conduce DashboardGridEditor.axaml.cs) =====================

    /// <summary>Empieza a arrastrar o redimensionar una ficha: la selecciona, la marca como "en vuelo"
    /// (el editor la atenúa) y arranca la silueta de destino en su posición actual.</summary>
    public void BeginDashboardTileDrag(string key)
    {
        var tile = DashboardSections.FirstOrDefault(s => s.Key == key && s.IsVisible);
        if (tile is null || !IsCustomLayout) return;

        SelectDashboardTile(key);
        tile.IsDragging = true;

        DashboardDragRow = tile.GridRow;
        DashboardDragColumn = tile.GridColumn;
        DashboardDragRowSpan = tile.GridRowSpan;
        DashboardDragColumnSpan = tile.GridColumnSpan;
        IsDashboardDropValid = true;
        IsDashboardTileDragActive = true;
    }

    /// <summary>Mueve la silueta de destino y recalcula si ahí cabe. No toca el layout: hasta que no se
    /// suelta no se guarda nada, así que soltar fuera de la rejilla o encima de otra ficha simplemente no
    /// hace nada.</summary>
    public void UpdateDashboardTileDrag(string key, int row, int column, int rowSpan, int columnSpan)
    {
        if (!IsDashboardTileDragActive) return;

        DashboardDragRow = row;
        DashboardDragColumn = column;
        DashboardDragRowSpan = rowSpan;
        DashboardDragColumnSpan = columnSpan;
        IsDashboardDropValid = CanPlaceDashboardTile(key, row, column, rowSpan, columnSpan);
    }

    /// <summary>Cierra el arrastre aplicando la silueta si era válida.</summary>
    public bool EndDashboardTileDrag(string key, bool commit)
    {
        var tile = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (tile is not null) tile.IsDragging = false;

        var wasActive = IsDashboardTileDragActive;
        IsDashboardTileDragActive = false;

        if (!wasActive || !commit || !IsDashboardDropValid) return false;

        return TryPlaceDashboardTile(key, DashboardDragRow, DashboardDragColumn, DashboardDragRowSpan, DashboardDragColumnSpan);
    }
}
