using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Controls;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Renderiza de verdad el editor de rejilla de Ajustes › Dashboard y su panel
/// (<see cref="DashboardGridPanel"/>) contra ViewModels reales.
///
/// Estas afirmaciones no las puede hacer un test de ViewModel: el ViewModel dice en qué celda va cada
/// ficha, pero que esa celda se traduzca en un rectángulo que no pise al de al lado, y que la capa de
/// celdas vacías cuadre al píxel con la de fichas (van superpuestas), depende del panel — que es código
/// de medición y colocación, y solo se comprueba midiendo y colocando.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class DashboardGridEditorUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public DashboardGridEditorUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private SettingsViewModel CreateSettingsViewModel(string layoutStyle)
    {
        var settingsService = new SettingsService(_tempDir);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(Path.Combine(_tempDir, "vault"), seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var backupService = new VaultBackupService(vault, _tempDir);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_tempDir);
        var reminderService = new ReminderService(_tempDir);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var notificationService = new NotificationService(settingsService, _tempDir);
        var customThemeService = new CustomThemeService(settingsService);

        var vm = new SettingsViewModel(
            settingsService, customThemeService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            () => { }, _ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { });

        // Igual que el usuario: primero una plantilla, y desde ella a personalizado.
        vm.SelectDashboardLayoutStyleCommand.Execute(layoutStyle);
        if (layoutStyle != DashboardLayoutEngine.Custom)
            vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Custom);

        return vm;
    }

    private static (Window Window, DashboardGridEditor Editor) ShowEditor(SettingsViewModel vm)
    {
        var editor = new DashboardGridEditor { DataContext = vm };
        var window = new Window { Content = editor, Width = 900, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (window, editor);
    }

    [Fact]
    public void EveryVisibleSection_GetsItsOwnRectangle_AndNoTwoOverlap()
    {
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.ModularGrid);
        var (window, editor) = ShowEditor(vm);

        var panels = editor.GetVisualDescendants().OfType<DashboardGridPanel>().ToList();
        Assert.Equal(2, panels.Count); // capa de celdas + capa de fichas

        var tiles = panels[1].Children.Where(c => c.IsVisible).ToList();
        Assert.Equal(vm.DashboardSections.Count(s => s.IsVisible), tiles.Count);

        foreach (var tile in tiles)
        {
            Assert.True(tile.Bounds.Width > 0, "una ficha se colocó con ancho 0");
            Assert.True(tile.Bounds.Height > 0, "una ficha se colocó con alto 0");
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var intersection = tiles[i].Bounds.Intersect(tiles[j].Bounds);
                Assert.True(
                    intersection.Width <= 0.01 || intersection.Height <= 0.01,
                    $"dos fichas se pintan una encima de otra: {tiles[i].Bounds} ∩ {tiles[j].Bounds}");
            }
        }

        window.Close();
    }

    [Fact]
    public void TheEmptyCellLayer_LinesUpExactlyWithTheTileLayer()
    {
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.Lateral);
        var (window, editor) = ShowEditor(vm);

        var panels = editor.GetVisualDescendants().OfType<DashboardGridPanel>().ToList();
        var cellPanel = panels[0];
        var tilePanel = panels[1];

        // La ficha de la esquina superior izquierda y la celda (0,0) tienen que empezar en el mismo
        // punto: si no, la rejilla de fondo se ve descuadrada respecto a las fichas.
        var firstCell = cellPanel.Children.First();
        var topLeftTile = tilePanel.Children
            .Where(c => c.IsVisible)
            .OrderBy(c => c.Bounds.Y)
            .ThenBy(c => c.Bounds.X)
            .First();

        Assert.Equal(firstCell.Bounds.X, topLeftTile.Bounds.X, 1);
        Assert.Equal(firstCell.Bounds.Y, topLeftTile.Bounds.Y, 1);

        window.Close();
    }

    [Fact]
    public void AWiderTile_IsExactlyAsManyColumnsWideAsItsSpanSays()
    {
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.Lateral);
        var (window, editor) = ShowEditor(vm);

        var tilePanel = editor.GetVisualDescendants().OfType<DashboardGridPanel>().ElementAt(1);
        var spacing = tilePanel.ColumnSpacing;
        var columnWidth = (tilePanel.Bounds.Width - (tilePanel.Columns - 1) * spacing) / tilePanel.Columns;

        foreach (var tile in tilePanel.Children.Where(c => c.IsVisible))
        {
            var span = Grid.GetColumnSpan(tile);
            var expected = span * columnWidth + (span - 1) * spacing;

            // Un píxel de margen: Avalonia redondea cada rectángulo colocado a píxeles del dispositivo
            // (UseLayoutRounding), así que un ancho de columna fraccionario no cuadra al decimal.
            Assert.True(
                Math.Abs(expected - tile.Bounds.Width) <= 1.01,
                $"una ficha de {span} columnas mide {tile.Bounds.Width}px y debería medir ~{expected}px");
        }

        window.Close();
    }

    [Fact]
    public void ResizingTheGridToOneColumn_KeepsEveryTileInsideTheCanvas()
    {
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.Mosaic);
        var (window, editor) = ShowEditor(vm);

        vm.DashboardGridColumns = 1;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var tilePanel = editor.GetVisualDescendants().OfType<DashboardGridPanel>().ElementAt(1);
        foreach (var tile in tilePanel.Children.Where(c => c.IsVisible))
        {
            Assert.True(tile.Bounds.Right <= tilePanel.Bounds.Width + 0.01,
                $"una ficha se sale del lienzo: {tile.Bounds} en un panel de {tilePanel.Bounds.Width}px");
        }

        window.Close();
    }

    [Fact]
    public void TheSelectedTileBar_HasRealCommandsWiredUp()
    {
        // Los botones de mover/redimensionar viven dentro del ContentTemplate de la ficha seleccionada y
        // llegan al ViewModel por $parent[UserControl] — una ruta que, si se rompiera, no fallaría al
        // compilar: los botones se pintarían igual y no harían nada.
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.Lateral);
        var (window, editor) = ShowEditor(vm);

        // La ficha más baja de la rejilla, con las filas ampliadas: así el hueco de debajo está libre y el
        // botón tiene efecto (moverse sobre una ficha ocupada se rechaza, que es otro test).
        vm.DashboardGridRows = 6;
        var lowest = vm.DashboardSections.Where(s => s.IsVisible).OrderByDescending(s => s.GridRow).First();
        vm.SelectDashboardTileCommand.Execute(lowest.Key);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var moveDown = editor.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), LocalizationService.Instance["Settings_Dashboard_Grid_MoveDown"]));

        Assert.NotNull(moveDown.Command);

        var tile = lowest;
        var rowBefore = tile.GridRow;
        moveDown.Command!.Execute(moveDown.CommandParameter);

        Assert.Equal(rowBefore + 1, tile.GridRow);

        window.Close();
    }

    [Fact]
    public void TheCustomEditorReplacesTheTemplatePreview_AndTakesTheWholeWidth()
    {
        var vm = CreateSettingsViewModel(DashboardLayoutEngine.ModularGrid);

        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 1000 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.True(vm.IsCustomLayout);
        Assert.Equal(3, vm.DashboardSectionListColumnSpan);

        // El editor está montado en la página, no solo declarado.
        Assert.Single(view.GetVisualDescendants().OfType<DashboardGridEditor>());

        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Classic);
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.Equal(1, vm.DashboardSectionListColumnSpan);

        window.Close();
    }
}
