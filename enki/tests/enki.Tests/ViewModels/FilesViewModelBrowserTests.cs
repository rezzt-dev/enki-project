using System;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Modo explorador de la sección Archivos (docs/agent-docs/seccion-archivos.md §3 y §9): la miga de
/// pan y la selección del panel, la búsqueda que baja a las subcarpetas, el orden, los estados vacíos, y que
/// renombrar, mover o borrar carpetas y archivos no deje la pantalla apuntando a rutas que ya no existen.
/// También la navegación con teclado del modo árbol y la previsualización anterior/siguiente, que comparten
/// viewmodel. Mismo montaje sobre directorio temporal que el resto de tests de viewmodel de esta sección.</summary>
public class FilesViewModelBrowserTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public FilesViewModelBrowserTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (FilesViewModel Vm, SettingsService Settings, VaultService Vault) CreateViewModel(
        SettingsService? existingSettings = null, bool confirmBeforeDelete = true)
    {
        var settings = existingSettings ?? new SettingsService(_storageDirectory);
        settings.Settings.ConfirmBeforeDelete = confirmBeforeDelete;
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new FilesViewModel(vault, sessions, notifications, settings);
        vm.ReloadFolders();
        vm.Reload();
        return (vm, settings, vault);
    }

    /// <summary>vault/diseno/logos/marca.png, vault/diseno/guion.txt, vault/diseno/apuntes.md (que Archivos no
    /// debe contar), vault/informe.pdf y vault/notas.txt.</summary>
    private void SeedVault()
    {
        var logos = Path.Combine(_vaultPath, "diseno", "logos");
        Directory.CreateDirectory(logos);
        File.WriteAllBytes(Path.Combine(logos, "marca.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_vaultPath, "diseno", "guion.txt"), "hola");
        File.WriteAllText(Path.Combine(_vaultPath, "diseno", "apuntes.md"), "# nota");
        File.WriteAllBytes(Path.Combine(_vaultPath, "informe.pdf"), [5, 6, 7, 8]);
        File.WriteAllText(Path.Combine(_vaultPath, "notas.txt"), "notas");
    }

    private void WriteText(string relativePath, string content)
    {
        var path = Path.Combine(_vaultPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void CutFileAndFolder_ShowPendingStateUntilPasteOrCancel()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        var folder = Folder(vm, "diseno");

        vm.CutEntryCommand.Execute(folder);
        Assert.True(folder.IsCut);
        Assert.True(vm.BrowserItems.OfType<FileFolderCardViewModel>().Single(c => c.Name == "diseno").IsCut);
        vm.CancelClipboardCommand.Execute(null);
        Assert.False(folder.IsCut);

        var file = vm.Files.Single(c => c.FileName == "notas.txt");
        vm.CutEntryCommand.Execute(file);
        Assert.True(file.IsCut);
        vm.PasteEntryCommand.Execute(folder);
        Assert.False(file.IsCut);
        Assert.True(File.Exists(Path.Combine(_vaultPath, "diseno", "notas.txt")));
    }

    private static FileFolderViewModel Folder(FilesViewModel vm, string name) =>
        Flatten(vm.RootFolders).First(f => f.Name == name);

    private static System.Collections.Generic.IEnumerable<FileFolderViewModel> Flatten(
        System.Collections.Generic.IEnumerable<FileFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;
            foreach (var child in Flatten(folder.Children)) yield return child;
        }
    }

    // ===================== Navegación =====================

    /// <summary>La fila de la carpeta actual tenía el estilo de seleccionada pero nadie la marcaba, así que el
    /// panel nunca decía dónde se estaba; y entrar desde una tarjeta de la rejilla dejaba la carpeta escondida
    /// dentro de su padre plegado.</summary>
    [Fact]
    public void EnteringAFolder_HighlightsItsSidebarRow_AndOpensItsAncestors()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();

        vm.SelectFolderCommand.Execute(Folder(vm, "logos"));

        Assert.True(Folder(vm, "logos").IsSelected);
        Assert.False(Folder(vm, "diseno").IsSelected);
        Assert.True(Folder(vm, "diseno").IsExpanded);
    }

    [Fact]
    public void Breadcrumbs_ListEveryLevelDownToTheCurrentFolder_AndNavigateBack()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();

        vm.SelectFolderCommand.Execute(Folder(vm, "logos"));

        Assert.Equal(
            [null, Path.Combine(_vaultPath, "diseno"), Path.Combine(_vaultPath, "diseno", "logos")],
            vm.Breadcrumbs.Select(b => b.FullPath));
        Assert.True(vm.Breadcrumbs[^1].IsLast);

        vm.NavigateToBreadcrumbCommand.Execute(vm.Breadcrumbs[1]);
        Assert.Equal("diseno", vm.CurrentFolder?.Name);

        vm.NavigateUpCommand.Execute(null);
        Assert.Null(vm.CurrentFolder);
    }

    [Fact]
    public void FolderCards_CountWhatTheFolderShows_WithoutNotes()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();

        var card = vm.BrowserItems.OfType<FileFolderCardViewModel>().Single(c => c.Name == "diseno");

        // logos/ y guion.txt; apuntes.md es cosa de Documentos.
        Assert.Equal(2, card.EntryCount);
    }

    // ===================== Búsqueda, orden y estados vacíos =====================

    /// <summary>Buscar se limitaba a la carpeta abierta: un archivo de una subcarpeta respondía "sin resultados".</summary>
    [Fact]
    public void Search_LooksIntoSubfolders_AndSaysWhereEachResultLives()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();

        vm.SearchText = "marca";
        vm.Reload();

        var card = Assert.Single(vm.Files);
        Assert.Equal("marca.png", card.FileName);
        Assert.Equal("diseno › logos", card.LocationLabel);
        Assert.True(vm.IsSearchActive);
        Assert.DoesNotContain(vm.BrowserItems, item => item is FileFolderCardViewModel);
    }

    [Fact]
    public void Search_WithNoMatches_ShowsItsOwnEmptyState()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();

        vm.SearchText = "no-existe";
        vm.Reload();

        Assert.True(vm.ShowSearchEmptyState);
        Assert.False(vm.ShowEmptyState);
        Assert.False(vm.ShowFolderEmptyState);
    }

    [Fact]
    public void AnEmptySubfolder_ShowsTheFolderEmptyState_NotTheEmptyVaultOne()
    {
        Directory.CreateDirectory(Path.Combine(_vaultPath, "vacia"));
        var (vm, _, _) = CreateViewModel();

        vm.SelectFolderCommand.Execute(Folder(vm, "vacia"));

        Assert.True(vm.ShowFolderEmptyState);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public void Sort_OrdersFilesByTheChosenCriterion_AndIsRemembered()
    {
        File.WriteAllBytes(Path.Combine(_vaultPath.EnsureDirectory(), "b-grande.pdf"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_vaultPath, "a-pequeno.pdf"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_vaultPath, "c-medio.png"), new byte[100]);
        var (vm, settings, _) = CreateViewModel();

        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Mode == FileSortMode.Name);
        Assert.Equal(["a-pequeno.pdf", "b-grande.pdf", "c-medio.png"], vm.Files.Select(f => f.FileName));

        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Mode == FileSortMode.Type);
        Assert.Equal(["c-medio.png", "a-pequeno.pdf", "b-grande.pdf"], vm.Files.Select(f => f.FileName));

        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Mode == FileSortMode.Size);
        Assert.Equal(["b-grande.pdf", "c-medio.png", "a-pequeno.pdf"], vm.Files.Select(f => f.FileName));
        Assert.Equal("Size", settings.Settings.FilesSortMode);

        var reopened = CreateViewModel(new SettingsService(_storageDirectory));
        Assert.Equal(FileSortMode.Size, reopened.Vm.SelectedSortOption?.Mode);
    }

    // ===================== Carpetas: crear, renombrar, borrar =====================

    [Fact]
    public void NewFolder_StartsRenamingItsCardInTheGrid()
    {
        var (vm, _, _) = CreateViewModel();

        vm.NewFolderCommand.Execute(null);

        var card = vm.BrowserItems.OfType<FileFolderCardViewModel>().Single(c => c.IsRenaming);
        Assert.True(Directory.Exists(card.FullPath));
    }

    /// <summary>Renombrar la carpeta en la que se estaba devolvía al usuario a la raíz.</summary>
    [Fact]
    public void RenamingTheCurrentFolder_KeepsTheUserInsideIt()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        vm.SelectFolderCommand.Execute(Folder(vm, "diseno"));

        var row = Folder(vm, "diseno");
        row.EditingName = "identidad";
        vm.CommitRenameFolderCommand.Execute(row);

        Assert.True(Directory.Exists(Path.Combine(_vaultPath, "identidad")));
        Assert.Equal("identidad", vm.CurrentFolder?.Name);
        Assert.Contains(vm.Files, f => f.FileName == "guion.txt");
    }

    /// <summary>Renombrar la carpeta del archivo abierto dejaba la previsualización leyendo de una ruta muerta.</summary>
    [Fact]
    public void RenamingAFolder_KeepsTheOpenPreviewPointingAtItsFile()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        vm.SelectFolderCommand.Execute(Folder(vm, "diseno"));
        vm.OpenCardCommand.Execute(vm.Files.Single(f => f.FileName == "guion.txt"));

        var row = Folder(vm, "diseno");
        row.EditingName = "identidad";
        vm.CommitRenameFolderCommand.Execute(row);

        Assert.Equal(Path.Combine(_vaultPath, "identidad", "guion.txt"), vm.ActivePreview?.File.FullPath);
        Assert.Equal("hola", vm.ActivePreview?.CodeContent);
    }

    [Fact]
    public void DeletingAFolder_AsksFirst_AndClosesAPreviewThatLivedInside()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel(confirmBeforeDelete: true);
        vm.SelectFolderCommand.Execute(Folder(vm, "diseno"));
        vm.OpenCardCommand.Execute(vm.Files.Single(f => f.FileName == "guion.txt"));

        vm.RequestDeleteFolderCommand.Execute(Folder(vm, "diseno"));

        Assert.Equal("diseno", vm.FolderPendingDelete?.Name);
        Assert.True(Directory.Exists(Path.Combine(_vaultPath, "diseno")));

        vm.ConfirmDeleteFolderCommand.Execute(null);

        Assert.Null(vm.FolderPendingDelete);
        Assert.False(Directory.Exists(Path.Combine(_vaultPath, "diseno")));
        Assert.True(Directory.Exists(Path.Combine(_vaultPath, ".trash", "diseno")));
        Assert.Null(vm.ActivePreview);
        Assert.Null(vm.CurrentFolder);
    }

    [Fact]
    public void RequestDeleteFolder_WithConfirmationOff_DeletesStraightAway_FromACardToo()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel(confirmBeforeDelete: false);

        vm.RequestDeleteFolderCommand.Execute(vm.BrowserItems.OfType<FileFolderCardViewModel>().Single(c => c.Name == "diseno"));

        Assert.Null(vm.FolderPendingDelete);
        Assert.False(Directory.Exists(Path.Combine(_vaultPath, "diseno")));
    }

    // ===================== Archivos: mover y borrar con la previsualización abierta =====================

    [Fact]
    public void MovingTheOpenPreview_KeepsItsCardReadableAtTheNewLocation()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        var card = vm.Files.Single(f => f.FileName == "notas.txt");
        vm.OpenCardCommand.Execute(card);

        vm.MoveFileToFolder(card, Path.Combine(_vaultPath, "diseno"));

        var newPath = Path.Combine(_vaultPath, "diseno", "notas.txt");
        Assert.True(File.Exists(newPath));
        Assert.Equal(newPath, vm.ActivePreview?.File.FullPath);
        Assert.Equal("notas", vm.ActivePreview?.CodeContent);
    }

    /// <summary>Un fallo de disco al mover (aquí, un destino que no existe) se avisa en vez de salir como
    /// excepción de un manejador de arrastre.</summary>
    [Fact]
    public void AVaultFailureWhileMoving_IsReportedInsteadOfThrown()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        var card = vm.Files.Single(f => f.FileName == "notas.txt");

        var exception = Record.Exception(() =>
            vm.MoveFileToFolder(card, Path.Combine(_storageDirectory, "no-existe", "tampoco")));

        Assert.Null(exception);
        Assert.True(File.Exists(Path.Combine(_vaultPath, "notas.txt")));
    }

    [Fact]
    public void PreviewPreviousAndNext_WalkTheFilesOfTheGridInTheirOrder()
    {
        WriteText("a.txt", "a");
        WriteText("b.txt", "b");
        WriteText("c.txt", "c");
        var (vm, _, _) = CreateViewModel();
        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Mode == FileSortMode.Name);

        vm.OpenCardCommand.Execute(vm.Files[0]);
        Assert.False(vm.CanPreviewPrevious);
        Assert.Equal("1 / 3", vm.PreviewPositionLabel);

        vm.PreviewNextCommand.Execute(null);
        Assert.Equal("b.txt", vm.ActivePreview?.FileName);
        Assert.Equal("2 / 3", vm.PreviewPositionLabel);

        vm.PreviewNextCommand.Execute(null);
        Assert.Equal("c.txt", vm.ActivePreview?.FileName);
        Assert.False(vm.CanPreviewNext);

        vm.PreviewPreviousCommand.Execute(null);
        Assert.Equal("b.txt", vm.ActivePreview?.FileName);
    }

    /// <summary>Borrar mientras se repasa una carpeta archivo a archivo no expulsa de la previsualización.</summary>
    [Fact]
    public void DeletingTheOpenPreview_OpensTheNextFileInstead()
    {
        WriteText("a.txt", "a");
        WriteText("b.txt", "b");
        WriteText("c.txt", "c");
        var (vm, _, _) = CreateViewModel(confirmBeforeDelete: false);
        vm.SelectedSortOption = vm.SortOptions.Single(o => o.Mode == FileSortMode.Name);
        vm.OpenCardCommand.Execute(vm.Files.Single(f => f.FileName == "b.txt"));

        vm.RequestDeleteFileCommand.Execute(vm.ActivePreview);

        Assert.False(File.Exists(Path.Combine(_vaultPath, "b.txt")));
        Assert.Equal("c.txt", vm.ActivePreview?.FileName);
    }

    // ===================== Modo árbol: contador y teclado =====================

    /// <summary>El pie del árbol enseñaba el contador de la carpeta abierta en el otro modo.</summary>
    [Fact]
    public void TreeFooterCount_CountsEveryFileInTheTree()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        vm.ShowTreeViewCommand.Execute(null);

        // marca.png, guion.txt, informe.pdf y notas.txt — la nota .md no.
        Assert.Equal(Plural.Get("Files_CountLabel", 4, 4), vm.TreeFilesCountLabel);
    }

    [Fact]
    public void TreeKeyboard_MovesThroughVisibleNodes_OpeningFilesAndSelectingFolders()
    {
        SeedVault();
        var (vm, _, _) = CreateViewModel();
        vm.ShowTreeViewCommand.Execute(null);
        vm.CollapseTreeCommand.Execute(null);

        // Raíces: diseno/, informe.pdf, notas.txt.
        var first = vm.MoveTreeSelection(1);
        Assert.Equal("diseno", first?.Name);
        Assert.True(first?.IsSelected);
        Assert.Null(vm.ActivePreview);

        vm.ExpandTreeSelection();
        Assert.True(first?.IsExpanded);

        var child = vm.ExpandTreeSelection();
        Assert.Equal("logos", child?.Name);

        var parent = vm.CollapseTreeSelection();
        Assert.Same(first, parent);

        vm.CollapseTreeSelection();
        Assert.False(first?.IsExpanded);

        var file = vm.MoveTreeSelection(2);
        Assert.Equal("notas.txt", file?.Name);
        Assert.Equal("notas.txt", vm.ActivePreview?.FileName);
        Assert.True(file?.IsSelected);
        Assert.False(first?.IsSelected);
    }
}

internal static class PathTestExtensions
{
    public static string EnsureDirectory(this string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
