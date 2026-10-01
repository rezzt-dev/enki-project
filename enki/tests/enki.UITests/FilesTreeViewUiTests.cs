using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Las dos disposiciones de la sección Archivos montadas sobre el XAML compilado de verdad y un
/// viewmodel real. Es la única capa donde se cae una ruta de binding mal escrita, un converter que no está
/// registrado en <c>App.axaml</c> o un <c>DataType</c> que no cuadra — nada de eso lo puede ver un test de
/// solo viewmodel, y esta sección tiene ahora dos árboles de plantillas distintos sobre el mismo estado.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class FilesTreeViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public FilesTreeViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (FilesView View, FilesViewModel ViewModel, VaultService Vault, SessionService Sessions, Window Window)
        CreateView(bool treeMode)
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.FilesViewMode = treeMode ? "Tree" : "Browser";
        settings.Save();

        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var viewModel = new FilesViewModel(vault, sessions, notifications, settings);

        var view = new FilesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (view, viewModel, vault, sessions, window);
    }

    [Fact]
    public void CollapsedRail_SearchOverlaysPreview_AndPinningReservesSpace()
    {
        var (view, vm, _, _, window) = CreateView(treeMode: true);
        try
        {
            vm.IsTreeSidebarVisible = false;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var tree = view.GetVisualDescendants().OfType<FilesTreeView>().Single();
            var preview = tree.GetVisualDescendants().OfType<FilePreviewView>().Single();
            var previewColumn = (Grid)preview.Parent!;
            var originalBounds = previewColumn.Bounds;
            var search = tree.FindControl<Button>("RailSearchButton")!;
            Assert.True(search.IsEffectivelyVisible);
            search.Command!.Execute(search.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.True(vm.IsTreeSearchMode);
            Assert.True(tree.FindControl<Border>("TreeNavigator")!.IsEffectivelyVisible);
            Assert.True(tree.FindControl<TextBox>("TreeSearchBox")!.IsEffectivelyVisible);
            Assert.Equal(originalBounds, previewColumn.Bounds);
            Assert.False(vm.IsTreeSidebarVisible);

            vm.ToggleTreeSidebarCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.False(vm.IsTreeNavigatorOpen);
            Assert.True(vm.IsTreeSidebarVisible);
            Assert.True(previewColumn.Bounds.X > originalBounds.X);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void CollapsedRail_NewFolderOpensForRenaming_AndSessionMenuDismissesNavigator()
    {
        var (view, vm, _, _, window) = CreateView(treeMode: true);
        try
        {
            vm.IsTreeSidebarVisible = false;
            var tree = view.GetVisualDescendants().OfType<FilesTreeView>().Single();
            var create = tree.FindControl<Button>("RailNewFolderButton")!;
            create.Command!.Execute(create.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.True(vm.IsTreeNavigatorOpen);
            Assert.False(vm.IsTreeSidebarVisible);
            Assert.True(vm.FileTreeRoots.Single().IsRenaming);
            Assert.True(tree.FindControl<Border>("TreeNavigator")!.IsEffectivelyVisible);

            var session = tree.FindControl<Button>("RailSessionButton")!;
            session.Command!.Execute(session.CommandParameter);
            Assert.False(vm.IsTreeNavigatorOpen);
            Assert.True(vm.IsTreeSessionMenuOpen);
            vm.SelectTreeSessionOptionCommand.Execute(vm.SessionOptions[0]);
            Assert.False(vm.IsTreeSessionMenuOpen);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TemporarySearch_EscapeAndOpeningResultCloseIt_WithoutPinning()
    {
        SeedVault();
        var (view, vm, _, _, window) = CreateView(treeMode: true);
        try
        {
            vm.ReloadFolders();
            vm.Reload();
            vm.IsTreeSidebarVisible = false;
            vm.ToggleTreeSearchPeekCommand.Execute(null);
            var tree = view.GetVisualDescendants().OfType<FilesTreeView>().Single();
            tree.FindControl<TextBox>("TreeSearchBox")!.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                Key = Avalonia.Input.Key.Escape
            });
            Assert.False(vm.IsTreeNavigatorOpen);

            vm.ToggleTreeSearchPeekCommand.Execute(null);
            vm.TreeSearchText = "suelto";
            vm.OpenFirstTreeSearchResult();
            Assert.NotNull(vm.ActivePreview);
            Assert.False(vm.IsTreeNavigatorOpen);
            Assert.False(vm.IsTreeSidebarVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>Una carpeta "diseno" con "logo.png" dentro y un "suelto.pdf" en la raíz del vault.</summary>
    private void SeedVault()
    {
        var folder = Path.Combine(_vaultPath, "diseno");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "logo.png"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(_vaultPath, "suelto.pdf"), [5, 6, 7, 8]);
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void TreeMode_RendersFoldersAndFilesInTheSameSidebar()
    {
        SeedVault();
        var (_, viewModel, _, _, window) = CreateView(treeMode: true);
        viewModel.ReloadFolders();
        viewModel.Reload();
        // Desplegada la carpeta, sus archivos cuelgan de ella en el mismo árbol.
        viewModel.FileTreeRoots.First(n => n.IsFolder).IsExpanded = true;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("diseno", text, StringComparison.Ordinal);
        Assert.Contains("logo.png", text, StringComparison.Ordinal);
        Assert.Contains("suelto.pdf", text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Cambiar de disposición intercambia la vista montada en vez de dejar las dos vivas — cada
    /// una lleva su propia previsualización, que instala TextMate sobre un AvaloniaEdit y rasteriza PDFs
    /// con PDFium, y tener dos vivas duplicaría ambas cosas.</summary>
    [Fact]
    public void SwitchingNavigationMode_SwapsTheLayoutInsteadOfKeepingBothAlive()
    {
        SeedVault();
        var (view, viewModel, _, _, window) = CreateView(treeMode: false);

        Assert.Single(view.GetVisualDescendants().OfType<FilesBrowserView>());
        Assert.Empty(view.GetVisualDescendants().OfType<FilesTreeView>());
        Assert.Single(view.GetVisualDescendants().OfType<FilePreviewView>());

        viewModel.ShowTreeViewCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Single(view.GetVisualDescendants().OfType<FilesTreeView>());
        Assert.Empty(view.GetVisualDescendants().OfType<FilesBrowserView>());
        Assert.Single(view.GetVisualDescendants().OfType<FilePreviewView>());

        window.Close();
    }

    [Fact]
    public void TreeMode_SelectingAFile_RendersItsPreview()
    {
        SeedVault();
        var (_, viewModel, _, _, window) = CreateView(treeMode: true);
        viewModel.ReloadFolders();
        viewModel.Reload();

        var fileNode = viewModel.FileTreeRoots.First(n => n.IsFile);
        viewModel.SelectTreeItemCommand.Execute(fileNode);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Same(fileNode.Card, viewModel.ActivePreview);
        // La barra de la previsualización (compartida con el modo explorador) enseña el nombre y el botón
        // de abrir con la aplicación del sistema.
        var text = RenderedText(window);
        Assert.Contains("suelto.pdf", text, StringComparison.Ordinal);
        Assert.Contains(
            LocalizationService.Instance["Files_OpenWithSystemButton"], text, StringComparison.OrdinalIgnoreCase);

        window.Close();
    }

    /// <summary>Sin nada abierto, el panel derecho invita a elegir algo en vez de quedarse en blanco.</summary>
    [Fact]
    public void TreeMode_WithNoPreviewOpen_RendersTheEmptyState()
    {
        SeedVault();
        var (_, viewModel, _, _, window) = CreateView(treeMode: true);
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Null(viewModel.ActivePreview);
        Assert.Contains(
            LocalizationService.Instance["Files_Tree_NoSelection"], RenderedText(window), StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Una sesión protegida y bloqueada no lista ni nombres de archivo en ninguna de las dos
    /// disposiciones: aquí, el árbol se queda vacío y en su sitio aparece el aviso de desbloquearla.</summary>
    [Fact]
    public void TreeMode_WithALockedSession_ListsNothingAndSaysWhy()
    {
        var (_, viewModel, vault, sessions, window) = CreateView(treeMode: true);
        var (session, _) = sessions.CreateEncrypted("Adjuntos privados", "una-contraseña-larga");

        var sourcePath = Path.Combine(_tempDir, "privado.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        vault.ImportFile(sourcePath, session.RootPath);

        sessions.Lock(session);
        viewModel.ReloadSessions();
        viewModel.SelectSession(session.Id);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.DoesNotContain("privado.png", text, StringComparison.Ordinal);
        Assert.Contains(
            LocalizationService.Instance["Files_LockedSession_Message"], text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>El modo búsqueda del panel monta su propia lista plana, con la carpeta de cada
    /// resultado.</summary>
    [Fact]
    public void TreeMode_SearchPanel_RendersTheMatchesWithTheirFolder()
    {
        SeedVault();
        var (_, viewModel, _, _, window) = CreateView(treeMode: true);
        viewModel.ReloadFolders();
        viewModel.Reload();

        viewModel.ShowTreeSearchCommand.Execute(null);
        viewModel.TreeSearchText = "logo";
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("logo.png", text, StringComparison.Ordinal);
        Assert.Contains("diseno", text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>El modo explorador sigue siendo el de siempre, con el conmutador añadido a su barra.</summary>
    [Fact]
    public void BrowserMode_StillRendersTheGridAndTheNavigationSwitch()
    {
        SeedVault();
        var (view, viewModel, _, _, window) = CreateView(treeMode: false);
        viewModel.ReloadFolders();
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("suelto.pdf", text, StringComparison.Ordinal);
        Assert.Contains("diseno", text, StringComparison.Ordinal);

        var switchButtons = view.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(Avalonia.Automation.AutomationProperties.GetName(b),
                               LocalizationService.Instance["Files_Nav_Tree"]));
        Assert.Single(switchButtons);

        window.Close();
    }
}
