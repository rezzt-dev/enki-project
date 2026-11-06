using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Modo "árbol" de la sección Archivos (docs/agent-docs/seccion-archivos.md §4): la forma del
/// árbol, la búsqueda de su panel, el conmutador de navegación y el estado que sobrevive a un reinicio.
/// Mismo montaje sobre directorio temporal que el resto de tests de viewmodel de esta sección.
///
/// Todos entran primero en el modo árbol (<c>ShowTreeViewCommand</c>) porque el árbol solo se mantiene al
/// día mientras ese modo está activo —montarlo enumera carpeta por carpeta toda la raíz efectiva, y
/// <c>Reload</c> corre en cada tecla de la búsqueda de la rejilla (ver <c>FilesViewModel.Reload</c>)—, así
/// que mirarlo desde el modo explorador sería mirar algo que la app tampoco está enseñando.</summary>
public class FilesViewModelTreeTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public FilesViewModelTreeTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (FilesViewModel Vm, SettingsService Settings, VaultService Vault, SessionService Sessions) CreateViewModel(
        SettingsService? existingSettings = null)
    {
        var settings = existingSettings ?? new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new FilesViewModel(vault, sessions, notifications, settings);
        return (vm, settings, vault, sessions);
    }

    /// <summary>Deja en el vault una carpeta "diseno" con "logo.png" dentro, un "notas.md" que Archivos no
    /// debe ver, y un "suelto.pdf" en la raíz.</summary>
    private void SeedVault(VaultService vault)
    {
        var folder = Path.Combine(_vaultPath, "diseno");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "logo.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(folder, "notas.md"), "# no es un archivo de esta seccion");
        File.WriteAllBytes(Path.Combine(_vaultPath, "suelto.pdf"), [5, 6, 7, 8]);
    }

    // ===================== Forma del árbol =====================

    [Fact]
    public void Tree_MixesFoldersAndFiles_WithSubfoldersFirst()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        Assert.Collection(vm.FileTreeRoots,
            node => Assert.True(node.IsFolder && node.Name == "diseno"),
            node => Assert.True(node.IsFile && node.Name == "suelto.pdf"));

        var folderNode = vm.FileTreeRoots[0];
        Assert.Equal(["logo.png"], folderNode.Children.Select(c => c.Name));
    }

    /// <summary>La frontera entre "Documentos" y "Archivos": el árbol se compone de llamadas a
    /// <c>VaultService.GetFiles</c>, que excluye los <c>.md</c>, así que una nota nunca aparece aquí.</summary>
    [Fact]
    public void Tree_NeverListsMarkdownNotes()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var names = Flatten(vm).Select(n => n.Name).ToList();
        Assert.Contains("logo.png", names);
        Assert.DoesNotContain("notas.md", names);
    }

    /// <summary>El filtro por tipo es del modo explorador y no debe podar el árbol: si lo hiciera, una
    /// carpeta con archivos de otro tipo se vería vacía y la jerarquía mentiría sobre lo que hay en disco.</summary>
    [Fact]
    public void Tree_IgnoresTheFileKindFilter()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var pdfFilter = vm.FilterOptions.First(o => o.Kind == enki.Models.VaultFileKind.Pdf);
        vm.SelectedFilterOption = pdfFilter;

        Assert.DoesNotContain(vm.Files, card => card.FileName == "logo.png");
        Assert.Contains(Flatten(vm), node => node.Name == "logo.png");
    }

    [Fact]
    public void Tree_UnderALockedSession_ListsNothing()
    {
        var (vm, _, vault, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "una-contraseña-larga");

        var sourcePath = Path.Combine(_storageDirectory, "privado.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        vault.ImportFile(sourcePath, session.RootPath);

        sessions.Lock(session);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadSessions();
        vm.SelectSession(session.Id);

        Assert.True(vm.IsSelectedSessionLocked);
        Assert.Empty(vm.FileTreeRoots);
        Assert.False(vm.HasTreeItems);
        Assert.False(vm.ShowTreeEmptyState); // el aviso de sesión bloqueada sustituye al de "vacío"
    }

    // ===================== Búsqueda del panel =====================

    [Fact]
    public void TreeSearch_FindsFilesAtAnyDepth_AndReportsWhereTheyLive()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        vm.TreeSearchText = "LOGO";

        var result = Assert.Single(vm.TreeSearchResults);
        Assert.Equal("logo.png", result.Name);
        Assert.Equal("diseno", result.LocationLabel);
    }

    /// <summary>La búsqueda del panel no toca <see cref="FilesViewModel.SearchText"/>, que es la de la
    /// rejilla y lleva su propio debounce de 200 ms.</summary>
    [Fact]
    public void TreeSearch_DoesNotTouchTheGridSearch()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.Reload();

        vm.TreeSearchText = "logo";

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Contains(vm.Files, card => card.FileName == "suelto.pdf");
    }

    [Fact]
    public void TreeSearch_WithNoMatches_ShowsItsOwnEmptyState()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.Reload();
        vm.ShowTreeSearchCommand.Execute(null);

        Assert.False(vm.ShowTreeSearchEmptyState); // cuadro vacío: todavía no se ha buscado nada

        vm.TreeSearchText = "no-existe";

        Assert.True(vm.ShowTreeSearchEmptyState);
    }

    // ===================== Acciones =====================

    [Fact]
    public void SelectTreeItem_OnAFile_OpensItsPreview_AndOnAFolderTogglesIt()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var folderNode = vm.FileTreeRoots.First(n => n.IsFolder);
        vm.SelectTreeItemCommand.Execute(folderNode);
        Assert.True(folderNode.IsExpanded);

        var fileNode = vm.FileTreeRoots.First(n => n.IsFile);
        vm.SelectTreeItemCommand.Execute(fileNode);

        Assert.Same(fileNode.Card, vm.ActivePreview);
        Assert.True(fileNode.IsSelected);
    }

    [Fact]
    public void CommitRenameTreeItem_RenamesFilesAndFolders_ThroughTheExistingCommands()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var fileNode = vm.FileTreeRoots.First(n => n.IsFile);
        fileNode.EditingName = "renombrado.pdf";
        vm.CommitRenameTreeItemCommand.Execute(fileNode);

        Assert.True(File.Exists(Path.Combine(_vaultPath, "renombrado.pdf")));

        var folderNode = vm.FileTreeRoots.First(n => n.IsFolder);
        folderNode.EditingName = "identidad";
        vm.CommitRenameTreeItemCommand.Execute(folderNode);

        Assert.True(Directory.Exists(Path.Combine(_vaultPath, "identidad")));
        Assert.Contains(vm.FileTreeRoots, n => n.IsFolder && n.Name == "identidad");
    }

    [Fact]
    public void DropOnTreeNode_MovesTheFileIntoThatFolder_AndDropOnRootBringsItBack()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var folderNode = vm.FileTreeRoots.First(n => n.IsFolder);
        vm.DropOnTreeNode(folderNode, Path.Combine(_vaultPath, "suelto.pdf"));

        Assert.True(File.Exists(Path.Combine(_vaultPath, "diseno", "suelto.pdf")));

        vm.DropOnTreeRoot(Path.Combine(_vaultPath, "diseno", "suelto.pdf"));

        Assert.True(File.Exists(Path.Combine(_vaultPath, "suelto.pdf")));
    }

    [Fact]
    public void DeleteTreeItem_OnAFile_GoesThroughTheConfirmationDialog()
    {
        var (vm, settings, vault, _) = CreateViewModel();
        settings.Settings.ConfirmBeforeDelete = true;
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.Reload();

        var fileNode = vm.FileTreeRoots.First(n => n.IsFile);
        vm.DeleteTreeItemCommand.Execute(fileNode);

        Assert.NotNull(vm.FilePendingDelete);
        Assert.True(File.Exists(Path.Combine(_vaultPath, "suelto.pdf")));

        vm.ConfirmDeleteFileCommand.Execute(null);

        Assert.False(File.Exists(Path.Combine(_vaultPath, "suelto.pdf")));
        Assert.True(File.Exists(Path.Combine(_vaultPath, ".trash", "suelto.pdf")));
    }

    [Fact]
    public void CollapseTree_ClosesEveryFolder()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();
        vm.FileTreeRoots.First(n => n.IsFolder).IsExpanded = true;

        vm.CollapseTreeCommand.Execute(null);

        Assert.All(vm.FileTreeRoots.Where(n => n.IsFolder), node => Assert.False(node.IsExpanded));
    }

    // ===================== Conmutador y estado persistido =====================

    [Fact]
    public void NavigationMode_IsPersisted_AndReReadWhenTheSectionIsRebuilt()
    {
        var (vm, settings, _, _) = CreateViewModel();
        Assert.False(vm.IsTreeView);

        vm.ShowTreeViewCommand.Execute(null);

        Assert.Equal("Tree", settings.Settings.FilesViewMode);

        var reopened = CreateViewModel(new SettingsService(_storageDirectory));
        Assert.True(reopened.Vm.IsTreeView);

        reopened.Vm.ShowBrowserViewCommand.Execute(null);
        Assert.Equal("Browser", reopened.Settings.Settings.FilesViewMode);
    }

    [Fact]
    public void SidebarVisibilityAndWidth_SurviveARestart_AndTheWidthIsClampedWhenRead()
    {
        var (vm, settings, _, _) = CreateViewModel();

        vm.ToggleTreeSidebarCommand.Execute(null);
        vm.TreeSidebarWidth = 9000;
        vm.PersistTreeSidebarWidth();

        Assert.False(settings.Settings.FilesTreeSidebarVisible);
        Assert.Equal(FilesViewModel.TreeSidebarMaxWidth, settings.Settings.FilesTreeSidebarWidth);

        settings.Settings.FilesTreeSidebarWidth = 10; // por debajo del mínimo, p. ej. editado a mano
        settings.Save();

        var reopened = CreateViewModel(new SettingsService(_storageDirectory));
        Assert.False(reopened.Vm.IsTreeSidebarVisible);
        Assert.Equal(FilesViewModel.TreeSidebarMinWidth, reopened.Vm.TreeSidebarWidth);
    }

    /// <summary>Los dos árboles de la sección guardan su expansión por separado: el del explorador no
    /// incluye archivos y el del modo árbol sí, así que compartir el estado reorganizaría uno al navegar
    /// por el otro.</summary>
    [Fact]
    public void ExpandedFolders_ArePersistedSeparatelyFromTheBrowserPanel()
    {
        var (vm, settings, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        vm.FileTreeRoots.First(n => n.IsFolder).IsExpanded = true;

        Assert.Equal([Path.Combine(_vaultPath, "diseno")], settings.Settings.FilesTreeExpandedFolderPaths);
        Assert.Empty(settings.Settings.FilesExpandedFolderPaths);

        var reopened = CreateViewModel(new SettingsService(_storageDirectory));
        reopened.Vm.ReloadFolders();
        reopened.Vm.Reload();

        Assert.True(reopened.Vm.FileTreeRoots.First(n => n.IsFolder).IsExpanded);
        Assert.False(reopened.Vm.RootFolders.Single().IsExpanded);
    }

    /// <summary>Cambiar de modo reordena la pantalla, nunca reinicia lo que se estaba haciendo: es el mismo
    /// viewmodel, así que la sesión, la carpeta actual y la previsualización abierta siguen ahí.</summary>
    [Fact]
    public void SwitchingNavigationMode_KeepsTheOpenPreviewAndTheCurrentFolder()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ReloadFolders();
        vm.Reload();

        vm.SelectFolderCommand.Execute(vm.RootFolders.Single());
        var card = vm.Files.Single();
        vm.OpenCardCommand.Execute(card);

        vm.ShowTreeViewCommand.Execute(null);

        Assert.Same(card, vm.ActivePreview);
        Assert.Equal("diseno", vm.CurrentFolder?.Name);

        vm.ShowBrowserViewCommand.Execute(null);

        Assert.Same(card, vm.ActivePreview);
        Assert.Equal("diseno", vm.CurrentFolder?.Name);
    }

    /// <summary>Recargar (renombrar, importar, mover) no debe cerrar lo que hay abierto: la tarjeta que se
    /// está previsualizando se reutiliza tal cual al reconstruir el árbol en vez de recrearse.</summary>
    [Fact]
    public void RebuildingTheTree_ReusesTheCardOfTheOpenPreview()
    {
        var (vm, _, vault, _) = CreateViewModel();
        SeedVault(vault);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var fileNode = vm.FileTreeRoots.First(n => n.IsFile);
        vm.SelectTreeItemCommand.Execute(fileNode);
        var openCard = vm.ActivePreview;

        vm.Reload();

        Assert.Same(openCard, vm.ActivePreview);
        Assert.Same(openCard, vm.FileTreeRoots.First(n => n.IsFile).Card);
    }

    /// <summary>Renombrar en línea el archivo que se está previsualizando no debe vaciar el panel derecho:
    /// <c>FileCardViewModel.UpdateFile</c> descarta miniatura, PDF y código de la tarjeta (el archivo de
    /// debajo ha cambiado), así que la previsualización abierta hay que rearmarla. En el modo árbol se
    /// renombra con la previsualización delante, que es donde esto se ve.</summary>
    [Fact]
    public void RenamingTheFileBeingPreviewed_KeepsItsContentLoaded()
    {
        var (vm, _, vault, _) = CreateViewModel();
        var sourcePath = Path.Combine(_storageDirectory, "script.py");
        File.WriteAllText(sourcePath, "print('hola')");
        vault.ImportFile(sourcePath, _vaultPath);
        vm.ShowTreeViewCommand.Execute(null);
        vm.ReloadFolders();
        vm.Reload();

        var fileNode = vm.FileTreeRoots.First(n => n.IsFile);
        vm.SelectTreeItemCommand.Execute(fileNode);
        Assert.NotNull(vm.ActivePreview?.CodeContent);

        fileNode.EditingName = "renombrado.py";
        vm.CommitRenameTreeItemCommand.Execute(fileNode);

        Assert.Equal("renombrado.py", vm.ActivePreview?.FileName);
        Assert.NotNull(vm.ActivePreview?.CodeContent);
    }

    private static System.Collections.Generic.IEnumerable<FileTreeItemViewModel> Flatten(FilesViewModel vm) =>
        Flatten(vm.FileTreeRoots);

    private static System.Collections.Generic.IEnumerable<FileTreeItemViewModel> Flatten(
        System.Collections.Generic.IEnumerable<FileTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
