using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Sección IA Skills, sus dos disposiciones montadas sobre el XAML compilado de verdad y un
/// viewmodel real. Es la única capa donde se cae una ruta de binding mal escrita, un converter que no está
/// registrado en <c>App.axaml</c> o un <c>DataType</c> que no cuadra — nada de eso lo puede ver un test de
/// solo viewmodel, y esta sección tiene dos árboles de plantillas distintos sobre el mismo estado.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class SkillsViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;
    private readonly string _skillsDir;

    public SkillsViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        _skillsDir = Path.Combine(_vaultDir, ".enki", "skills");
        Directory.CreateDirectory(_skillsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SkillsView View, SkillsViewModel ViewModel, Window Window) CreateView(bool treeMode)
    {
        var settings = new SettingsService(_tempDir);
        settings.Settings.SkillsViewMode = treeMode ? "Tree" : "Browser";
        settings.Save();

        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultDir, seedWelcomeNote: false);
        var notifications = new NotificationService(settings, _tempDir);
        var viewModel = new SkillsViewModel(vault, notifications, settings);
        viewModel.Reload();

        var view = new SkillsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        return (view, viewModel, window);
    }

    private void SeedLibrary()
    {
        Directory.CreateDirectory(Path.Combine(_skillsDir, "escritura"));
        File.WriteAllText(Path.Combine(_skillsDir, "escritura", "redactor.md"), "---\nname: Redactor\ndescription: Escribe\n---\nCuerpo");
        File.WriteAllText(Path.Combine(_skillsDir, "suelta.md"), "---\nname: Suelta\ndescription: Sin carpeta\n---\nCuerpo");
    }

    private static string RenderedText(Window window) => string.Join(
        " | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Fact]
    public void BrowserMode_RendersTheFolderPanelAndTheFolderCardBesideTheRootSkill()
    {
        SeedLibrary();
        var (_, viewModel, window) = CreateView(treeMode: false);
        viewModel.Reload();
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("escritura", text, StringComparison.Ordinal);
        Assert.Contains("Suelta", text, StringComparison.Ordinal);
        // La skill de dentro de la carpeta no se pinta hasta entrar en ella.
        Assert.DoesNotContain("Redactor", text, StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void BrowserMode_EnteringAFolder_RendersItsSkill()
    {
        SeedLibrary();
        var (_, viewModel, window) = CreateView(treeMode: false);
        viewModel.Reload();

        viewModel.SelectFolderCommand.Execute(viewModel.RootFolders.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Contains("Redactor", RenderedText(window), StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void TreeMode_RendersFoldersAndSkillsInTheSameSidebar()
    {
        SeedLibrary();
        var (_, viewModel, window) = CreateView(treeMode: true);
        viewModel.Reload();
        // Desplegada la carpeta, sus skills cuelgan de ella en el mismo árbol.
        viewModel.SkillTreeRoots.Single(n => n.IsFolder).IsExpanded = true;
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("escritura", text, StringComparison.Ordinal);
        Assert.Contains("Redactor", text, StringComparison.Ordinal);
        Assert.Contains("Suelta", text, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>Cambiar de disposición intercambia la vista montada en vez de dejar las dos vivas — cada
    /// una lleva su propio editor de skills, y dos editores sobre el mismo viewmodel escribirían los dos
    /// sobre su texto (mismo motivo que en <c>NotesView</c>).</summary>
    [Fact]
    public void SwitchingNavigationMode_SwapsTheLayoutInsteadOfKeepingBothAlive()
    {
        SeedLibrary();
        var (view, viewModel, window) = CreateView(treeMode: false);

        Assert.Single(view.GetVisualDescendants().OfType<SkillsBrowserView>());
        Assert.Empty(view.GetVisualDescendants().OfType<SkillsTreeView>());

        viewModel.ShowTreeViewCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.Single(view.GetVisualDescendants().OfType<SkillsTreeView>());
        Assert.Empty(view.GetVisualDescendants().OfType<SkillsBrowserView>());

        window.Close();
    }

    [Fact]
    public void SelectingASkillInTreeMode_RendersItsDetailPanelWithItsLocation()
    {
        SeedLibrary();
        var (_, viewModel, window) = CreateView(treeMode: true);
        viewModel.Reload();

        var skill = viewModel.SkillTreeRoots.Single(n => n.IsFolder).Children.Single().Skill!;
        viewModel.SelectSkillCommand.Execute(skill);
        window.GetLayoutManager()!.ExecuteLayoutPass();

        var text = RenderedText(window);
        Assert.Contains("Escribe", text, StringComparison.Ordinal);
        Assert.Contains("escritura", text, StringComparison.Ordinal);

        window.Close();
    }

    [Fact]
    public void RequestDeleteFolder_OpensTheFolderDialogFromEitherLayout()
    {
        SeedLibrary();
        var (_, viewModel, window) = CreateView(treeMode: false);
        viewModel.Reload();

        viewModel.RequestDeleteFolderCommand.Execute(viewModel.RootFolders.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();

        Assert.NotNull(viewModel.PendingDeleteFolder);
        Assert.Contains(
            enki.Localization.LocalizationService.Instance["Skills_DeleteFolderModal_Warning"],
            RenderedText(window), StringComparison.Ordinal);

        window.Close();
    }
}
