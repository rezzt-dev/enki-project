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

/// <summary>Lo que la sección Archivos dibuja además de la rejilla y el árbol básicos: la miga de pan, el modal
/// de borrar carpeta, y los visores de la previsualización (código y panel de información) montados sobre el
/// XAML compilado de verdad. Es donde se cae una ruta de binding mal escrita o un DataType que no cuadra, que
/// ningún test de viewmodel ve.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class FilesSectionPolishUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public FilesSectionPolishUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (FilesViewModel ViewModel, Window Window) CreateView(bool treeMode = false)
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.FilesViewMode = treeMode ? "Tree" : "Browser";
        settings.Settings.ConfirmBeforeDelete = true;
        settings.Save();

        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _tempDir);
        var viewModel = new FilesViewModel(vault, sessions, notifications, settings);

        var window = new Window { Content = new FilesView { DataContext = viewModel }, Width = 1400, Height = 900 };
        window.Show();
        viewModel.Refresh();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (viewModel, window);
    }

    private void Seed(string relativePath, string content = "contenido")
    {
        var path = Path.Combine(_vaultPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void BrowserMode_InsideASubfolder_RendersTheBreadcrumbAndTheFolderCards()
    {
        Seed("diseno/logos/marca.txt");
        var (viewModel, window) = CreateView();

        viewModel.SelectFolderCommand.Execute(viewModel.RootFolders.Single(f => f.Name == "diseno"));
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("DISENO", text, StringComparison.Ordinal);
        Assert.Contains("logos", text, StringComparison.Ordinal);
        Assert.Contains(LocalizationService.Instance["Files_UpOneLevel"], text, StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void RequestDeleteFolder_OpensTheFolderDialog_WithWhereItEndsUp()
    {
        Seed("diseno/guion.txt");
        var (viewModel, window) = CreateView();

        viewModel.RequestDeleteFolderCommand.Execute(viewModel.RootFolders.Single(f => f.Name == "diseno"));
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains(LocalizationService.Instance["Files_DeleteFolder_Title"], text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LocalizationService.Instance["Files_DeleteFolder_Hint"], text, StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void PreviewOfATextFile_RendersTheCodeViewerAndItsToolbar()
    {
        Seed("script.py", "print('hola')\nprint('adios')");
        var (viewModel, window) = CreateView();

        viewModel.OpenCardCommand.Execute(viewModel.Files.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("script.py", text, StringComparison.Ordinal);
        Assert.Contains(viewModel.ActivePreview!.CodeLinesLabel, text, StringComparison.Ordinal);
        Assert.Contains("PY", text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>El menú de más acciones de la barra es un <see cref="MenuFlyout"/>, el único de la app: sus
    /// entradas tienen que heredar el viewmodel de la sección al abrirse, o sus comandos quedarían a null y el
    /// menú entero deshabilitado sin ningún error visible.</summary>
    [Fact]
    public void PreviewMoreActionsMenu_BindsItsCommandsToTheSection()
    {
        Seed("script.py", "print('hola')");
        var (viewModel, window) = CreateView();
        viewModel.OpenCardCommand.Execute(viewModel.Files.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var moreButton = window.GetVisualDescendants().OfType<Button>().Single(b =>
            Equals(Avalonia.Automation.AutomationProperties.GetName(b), LocalizationService.Instance["Files_MoreActionsTooltip"]));
        var flyout = Assert.IsType<MenuFlyout>(moreButton.Flyout);
        flyout.ShowAt(moreButton);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.NotNull(item.Command));
        Assert.True(items.Last().Command!.CanExecute(items.Last().CommandParameter));

        flyout.Hide();
        window.Close();
    }

    [Fact]
    public void PreviewOfAFileWithoutViewer_RendersTheInfoPanelWithBothWaysOut()
    {
        Seed("modelo.blend");
        var (viewModel, window) = CreateView(treeMode: true);

        viewModel.OpenCardCommand.Execute(viewModel.FindTreeCardByPath(Path.Combine(_vaultPath, "modelo.blend"))!);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains(LocalizationService.Instance["Files_NoPreviewAvailable"], text, StringComparison.Ordinal);
        Assert.Contains(LocalizationService.Instance["Files_RevealShortButton"], text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLEND", text, StringComparison.Ordinal);

        window.Close();
    }
}
