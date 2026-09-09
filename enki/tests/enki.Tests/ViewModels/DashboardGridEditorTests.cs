using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre el editor de rejilla de Ajustes › Dashboard (<c>SettingsViewModel.DashboardGrid.cs</c>)
/// contra servicios reales sobre un directorio desechable, igual que <see cref="SettingsViewModelTests"/>.
///
/// Lo que se afirma aquí es el contrato que el editor promete al usuario y que la vista no puede
/// garantizar por su cuenta: que una ficha nunca se coloca encima de otra, que lo que se ve en el editor
/// es exactamente lo que se guarda en el vault (y por tanto lo que pintará el Dashboard), y que cambiar
/// el tamaño de la rejilla o sacar y devolver una sección no descoloca al resto.</summary>
public class DashboardGridEditorTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (SettingsViewModel Settings, VaultSettingsService VaultSettings) CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var backupService = new VaultBackupService(vault, _storageDirectory);
        var restClient = new SupabaseRestClient(authService);
        var taskService = new PolarTaskService(_storageDirectory);
        var reminderService = new ReminderService(_storageDirectory);
        var syncService = new PolarSyncService(authService, restClient, taskService, reminderService, settingsService);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var customThemeService = new CustomThemeService(settingsService);
        var brandLogoService = new BrandLogoService(_storageDirectory);

        var vm = new SettingsViewModel(
            settingsService, customThemeService, brandLogoService, vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            onVaultChanged: () => { }, onTitleBarModeChanged: _ => { }, onNotificationsPanelChanged: _ => { },
            onRemindersPanelChanged: _ => { }, onCalendarShowSidebarScrollbarChanged: _ => { },
            onSidebarSectionChanged: (_, _) => { }, onSidebarSectionOrderChanged: () => { }, onCalendarSettingsChanged: () => { }, onDashboardShowScrollbarChanged: _ => { },
            onKanbanBoardOrientationChanged: _ => { },
            onPolarShowTaskTagsChanged: _ => { }, onUiZoomChanged: _ => { },
            onLanguageChanged: () => { });

        return (vm, vaultSettingsService);
    }

    /// <summary>Entra en la organización personalizada partiendo de una plantilla concreta, que es como
    /// llega el usuario: elige la que más se le parece y luego mueve fichas.</summary>
    private static SettingsViewModel EnterCustomFrom(SettingsViewModel vm, string style)
    {
        vm.SelectDashboardLayoutStyleCommand.Execute(style);
        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Custom);
        return vm;
    }

    private static DashboardSectionConfigViewModel Tile(SettingsViewModel vm, string key)
        => vm.DashboardSections.First(s => s.Key == key);

    private static void AssertNoOverlaps(SettingsViewModel vm)
    {
        var tiles = vm.DashboardSections.Where(s => s.IsVisible).ToList();
        for (var i = 0; i < tiles.Count; i++)
        {
            for (var j = i + 1; j < tiles.Count; j++)
            {
                var a = new DashboardGridSlot(tiles[i].GridRow, tiles[i].GridColumn, tiles[i].GridRowSpan, tiles[i].GridColumnSpan);
                var b = new DashboardGridSlot(tiles[j].GridRow, tiles[j].GridColumn, tiles[j].GridRowSpan, tiles[j].GridColumnSpan);
                Assert.False(DashboardLayoutEngine.Overlaps(a, b), $"'{tiles[i].Key}' se solapa con '{tiles[j].Key}'");
            }
        }
    }

    [Fact]
    public void SwitchingToCustom_SeedsTheGridWithTheTemplateThatWasActive()
    {
        var (vm, vaultSettings) = CreateViewModel();

        EnterCustomFrom(vm, DashboardLayoutEngine.ModularGrid);

        Assert.True(vm.IsCustomLayout);
        Assert.NotEmpty(vaultSettings.Settings.DashboardCustomLayout);

        // Misma geometría que la plantilla de partida: entrar en "personalizado" no debe mover nada de
        // sitio, solo volverlo editable.
        var template = DashboardLayoutEngine.Compute(
            vm.DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList(),
            DashboardLayoutEngine.ModularGrid);

        foreach (var (key, slot) in template)
        {
            var tile = Tile(vm, key);
            Assert.Equal(slot.Row, tile.GridRow);
            Assert.Equal(slot.Column, tile.GridColumn);
            Assert.Equal(slot.RowSpan, tile.GridRowSpan);
            Assert.Equal(slot.ColumnSpan, tile.GridColumnSpan);
        }
    }

    [Fact]
    public void PlacingATile_MovesItAndPersistsThatExactPosition()
    {
        var (vm, vaultSettings) = CreateViewModel();

        // Lateral ocupa 4 filas; ampliar la rejilla a 6 deja las dos últimas enteras libres, que es el
        // hueco al que se mueve la ficha aquí.
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);
        vm.DashboardGridRows = 6;

        var moved = vm.TryPlaceDashboardTile("Today", 5, 3, 1, 2);

        Assert.True(moved);
        var tile = Tile(vm, "Today");
        Assert.Equal((5, 3, 1, 2), (tile.GridRow, tile.GridColumn, tile.GridRowSpan, tile.GridColumnSpan));
        Assert.Equal(new[] { 5, 3, 1, 2 }, vaultSettings.Settings.DashboardCustomLayout["Today"]);
        AssertNoOverlaps(vm);
    }

    [Fact]
    public void TryingOutATemplateAndComingBack_RestoresTheHandMadeGrid()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);
        vm.DashboardGridRows = 6;
        Assert.True(vm.TryPlaceDashboardTile("Today", 5, 3, 1, 2));

        // Ir a mirar otra organización y volver no puede costar el trabajo hecho a mano.
        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Mosaic);
        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Custom);

        var tile = Tile(vm, "Today");
        Assert.Equal((5, 3, 1, 2), (tile.GridRow, tile.GridColumn, tile.GridRowSpan, tile.GridColumnSpan));

        // El botón "personalizar esta organización" sí rehace la rejilla desde la plantilla que se ve.
        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Mosaic);
        vm.CustomizeDashboardLayoutCommand.Execute(null);

        Assert.True(vm.IsCustomLayout);
        var mosaic = DashboardLayoutEngine.Compute(
            vm.DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList(),
            DashboardLayoutEngine.Mosaic)["Today"];
        Assert.Equal(
            (mosaic.Row, mosaic.Column, mosaic.RowSpan, mosaic.ColumnSpan),
            (tile.GridRow, tile.GridColumn, tile.GridRowSpan, tile.GridColumnSpan));
    }

    [Fact]
    public void PlacingATileOnTopOfAnother_IsRejectedAndChangesNothing()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);

        var target = Tile(vm, "QuickAccess");
        var moving = Tile(vm, "Today");
        var before = (moving.GridRow, moving.GridColumn, moving.GridRowSpan, moving.GridColumnSpan);

        var placed = vm.TryPlaceDashboardTile("Today", target.GridRow, target.GridColumn, 1, 1);

        Assert.False(placed);
        Assert.Equal(before, (moving.GridRow, moving.GridColumn, moving.GridRowSpan, moving.GridColumnSpan));
    }

    [Fact]
    public void PlacingATileOutsideTheGrid_IsRejected()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);
        vm.DashboardGridColumns = 6;
        vm.DashboardGridRows = 4;

        Assert.False(vm.TryPlaceDashboardTile("Today", 0, 5, 1, 2));
        Assert.False(vm.TryPlaceDashboardTile("Today", 4, 0, 1, 2));
        Assert.False(vm.TryPlaceDashboardTile("Today", -1, 0, 1, 2));
    }

    [Fact]
    public void KeyboardMoveAndResize_FollowTheSameRulesAsDragging()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Classic);
        vm.DashboardGridColumns = 6;
        vm.DashboardGridRows = 8;

        // Classic deja cada sección en una fila propia a todo lo ancho: para poder moverla en horizontal
        // primero hay que estrecharla, igual que haría el usuario.
        vm.DashboardSections.Where(s => s.IsVisible).ToList().ForEach(s => vm.TryPlaceDashboardTile(s.Key, s.GridRow, 0, 1, 1));

        var tile = Tile(vm, "Today");
        var row = tile.GridRow;

        vm.MoveDashboardTileRightCommand.Execute("Today");
        Assert.Equal(1, tile.GridColumn);

        vm.MoveDashboardTileLeftCommand.Execute("Today");
        Assert.Equal(0, tile.GridColumn);

        // Contra el borde izquierdo el comando no hace nada (y no lanza).
        vm.MoveDashboardTileLeftCommand.Execute("Today");
        Assert.Equal(0, tile.GridColumn);

        vm.GrowDashboardTileWidthCommand.Execute("Today");
        Assert.Equal(2, tile.GridColumnSpan);

        vm.ShrinkDashboardTileWidthCommand.Execute("Today");
        Assert.Equal(1, tile.GridColumnSpan);

        // No se puede encoger por debajo de una celda.
        vm.ShrinkDashboardTileWidthCommand.Execute("Today");
        Assert.Equal(1, tile.GridColumnSpan);
        Assert.Equal(row, tile.GridRow);
    }

    [Fact]
    public void TakingASectionOutOfTheGridAndPuttingItBack_LeavesEveryOtherTileWhereItWas()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.ModularGrid);

        var others = vm.DashboardSections
            .Where(s => s.IsVisible && s.Key != "Today")
            .ToDictionary(s => s.Key, s => (s.GridRow, s.GridColumn, s.GridRowSpan, s.GridColumnSpan));

        vm.RemoveDashboardSectionFromGridCommand.Execute("Today");
        Assert.False(Tile(vm, "Today").IsVisible);
        Assert.True(vm.HasHiddenDashboardSections);

        vm.ShowDashboardSectionCommand.Execute("Today");
        Assert.True(Tile(vm, "Today").IsVisible);

        foreach (var (key, position) in others)
        {
            var tile = Tile(vm, key);
            Assert.Equal(position, (tile.GridRow, tile.GridColumn, tile.GridRowSpan, tile.GridColumnSpan));
        }

        AssertNoOverlaps(vm);
    }

    [Fact]
    public void ShrinkingTheGrid_RepositionsTilesThatNoLongerFit_AndPersistsThatDecision()
    {
        var (vm, vaultSettings) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);

        vm.DashboardGridColumns = 2;

        Assert.All(vm.DashboardSections.Where(s => s.IsVisible), tile =>
        {
            Assert.InRange(tile.GridColumn, 0, 1);
            Assert.InRange(tile.GridColumn + tile.GridColumnSpan, 1, 2);
        });
        AssertNoOverlaps(vm);

        // Lo recolocado queda escrito: si solo se arreglara al pintar, la posición "de verdad" cambiaría
        // según qué secciones estuvieran visibles en cada arranque.
        foreach (var tile in vm.DashboardSections.Where(s => s.IsVisible))
        {
            var saved = vaultSettings.Settings.DashboardCustomLayout[tile.Key];
            Assert.Equal(new[] { tile.GridRow, tile.GridColumn, tile.GridRowSpan, tile.GridColumnSpan }, saved);
        }
    }

    [Fact]
    public void AutoArrange_CompactsEveryTileTowardsTheTopLeft()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Classic);
        vm.DashboardGridColumns = 6;
        vm.DashboardGridRows = 10;

        vm.AutoArrangeDashboardGridCommand.Execute(null);

        Assert.Contains(vm.DashboardSections.Where(s => s.IsVisible), t => t is { GridRow: 0, GridColumn: 0 });
        AssertNoOverlaps(vm);
    }

    [Fact]
    public void DragPreview_OnlyAppliesOnAValidDrop()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);
        vm.DashboardGridRows = 6;

        var tile = Tile(vm, "Today");
        var origin = (tile.GridRow, tile.GridColumn);

        // Soltar encima de otra ficha: la silueta se marca inválida y el soltar no cambia nada.
        vm.BeginDashboardTileDrag("Today");
        Assert.True(tile.IsDragging);
        var occupied = Tile(vm, "QuickAccess");
        vm.UpdateDashboardTileDrag("Today", occupied.GridRow, occupied.GridColumn, 1, 1);
        Assert.False(vm.IsDashboardDropValid);
        Assert.False(vm.EndDashboardTileDrag("Today", commit: true));
        Assert.Equal(origin, (tile.GridRow, tile.GridColumn));
        Assert.False(tile.IsDragging);

        // Soltar en un hueco libre sí se aplica.
        vm.RemoveDashboardSectionFromGridCommand.Execute("Habits");
        vm.BeginDashboardTileDrag("Today");
        vm.UpdateDashboardTileDrag("Today", 5, 0, 1, 1);
        Assert.True(vm.IsDashboardDropValid);
        Assert.True(vm.EndDashboardTileDrag("Today", commit: true));
        Assert.Equal((5, 0), (tile.GridRow, tile.GridColumn));

        // Cancelar (perder la captura del puntero) no aplica la silueta.
        vm.BeginDashboardTileDrag("Today");
        vm.UpdateDashboardTileDrag("Today", 5, 2, 1, 1);
        Assert.False(vm.EndDashboardTileDrag("Today", commit: false));
        Assert.Equal((5, 0), (tile.GridRow, tile.GridColumn));
    }

    [Fact]
    public void SelectingATile_MarksOnlyThatOne_AndTakingItOutClearsTheSelection()
    {
        var (vm, _) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Lateral);

        vm.SelectDashboardTileCommand.Execute("Today");
        Assert.Equal("Today", vm.SelectedDashboardTile?.Key);
        Assert.Single(vm.DashboardSections, s => s.IsSelected);

        vm.SelectDashboardTileCommand.Execute("Vault");
        Assert.Equal("Vault", vm.SelectedDashboardTile?.Key);
        Assert.Single(vm.DashboardSections, s => s.IsSelected);

        vm.RemoveDashboardSectionFromGridCommand.Execute("Vault");
        Assert.Null(vm.SelectedDashboardTile);

        vm.SelectDashboardTileCommand.Execute("Today");
        vm.ClearDashboardTileSelection();
        Assert.Null(vm.SelectedDashboardTile);
        Assert.DoesNotContain(vm.DashboardSections, s => s.IsSelected);
    }

    [Fact]
    public void ResetRestoresTheClassicArrangement_AndTheCardsAgreeWithIt()
    {
        var (vm, vaultSettings) = CreateViewModel();
        EnterCustomFrom(vm, DashboardLayoutEngine.Mosaic);
        vm.RemoveDashboardSectionFromGridCommand.Execute("Today");

        vm.ResetDashboardLayoutCommand.Execute(null);

        Assert.Equal(DashboardLayoutEngine.Classic, vm.DashboardLayoutStyle);
        Assert.Equal(DashboardLayoutEngine.Classic, vaultSettings.Settings.DashboardLayoutStyle);
        Assert.False(vm.IsCustomLayout);
        Assert.All(vm.DashboardSections, s => Assert.True(s.IsVisible));

        // La tarjeta marcada en Organización tiene que seguir a la organización real.
        Assert.Equal(
            DashboardLayoutEngine.Classic,
            vm.DashboardLayoutOptions.Single(o => o.IsSelected).Key);
    }

    [Fact]
    public void OpeningSettingsWithASavedTemplate_SelectsThatTemplatesCard()
    {
        var (vm, vaultSettings) = CreateViewModel();
        vm.SelectDashboardLayoutStyleCommand.Execute(DashboardLayoutEngine.Mosaic);

        // Segundo ViewModel sobre el mismo vault: es lo que ocurre al reabrir la app.
        var (reopened, _) = CreateViewModel();

        Assert.Equal(DashboardLayoutEngine.Mosaic, vaultSettings.Settings.DashboardLayoutStyle);
        Assert.Equal(DashboardLayoutEngine.Mosaic, reopened.DashboardLayoutStyle);
        Assert.Equal(DashboardLayoutEngine.Mosaic, reopened.DashboardLayoutOptions.Single(o => o.IsSelected).Key);
    }
}
