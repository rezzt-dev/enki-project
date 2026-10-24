using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    [Fact]
    public void CollapsedTreeRail_ExposesActions_AndOpensBrowseOrSearch()
    {
        var (view, vm, window) = CreateView(treeMode: true);
        try
        {
            vm.IsTreeSidebarVisible = false;
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var tree = view.GetVisualDescendants().OfType<SkillsTreeView>().Single();
            var rail = tree.FindControl<Grid>("CollapsedRail")!;
            Assert.True(rail.IsEffectivelyVisible);
            Assert.Equal(52, rail.Bounds.Width);

            foreach (var name in new[] { "RailTreeButton", "RailSearchButton", "RailNewSkillButton",
                         "RailNewFolderButton", "RailImportButton", "RailExportButton",
                         "RailCollapseButton", "RailCommunityButton", "RailBrowserButton" })
            {
                var button = tree.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible, name);
                Assert.NotNull(button.Command);
            }

            var search = tree.FindControl<Button>("RailSearchButton")!;
            search.Command!.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(vm.IsTreeSidebarVisible);
            Assert.True(vm.IsTreeSearchMode);
            Assert.True(tree.FindControl<TextBox>("TreeSearchBox")!.IsEffectivelyVisible);

            vm.IsTreeSidebarVisible = false;
            var browse = tree.FindControl<Button>("RailTreeButton")!;
            browse.Command!.Execute(null);
            Assert.True(vm.IsTreeSidebarVisible);
            Assert.False(vm.IsTreeSearchMode);
        }
        finally { window.Close(); }
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreatingAndRenamingFolder_WithMountedView_DoesNotReenter(bool treeMode)
    {
        var (_, vm, window) = CreateView(treeMode);
        if (treeMode)
        {
            vm.NewTreeFolderAtRootCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var node = vm.SkillTreeRoots.Single();
            node.EditingName = "Organizada";
            vm.CommitRenameTreeItemCommand.Execute(node);
        }
        else
        {
            vm.NewFolderCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var folder = vm.RootFolders.Single();
            folder.EditingName = "Organizada";
            vm.CommitRenameFolderCommand.Execute(folder);
        }
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.True(Directory.Exists(Path.Combine(_skillsDir, "Organizada")));
        Assert.Contains("Organizada", RenderedText(window), StringComparison.Ordinal);
        window.Close();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detail_SourceToggleShowsFrontmatterWithoutChangingTheFile(bool treeMode)
    {
        SeedLibrary();
        var (view, vm, window) = CreateView(treeMode);
        vm.SelectSkillCommand.Execute(vm.Skills.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();
        var preview = view.GetVisualDescendants().OfType<SkillEditorView>().Single(v => v.IsReadOnly);
        var editor = preview.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
        Assert.True(editor.IsReadOnly);
        Assert.False(preview.FindControl<Border>("EditorStatus")!.IsVisible);
        Assert.DoesNotContain("name:", editor.Text);
        var detail = preview.FindAncestorOfType<SkillDetailView>()!;
        var sourceToggle = detail.FindControl<Avalonia.Controls.Primitives.ToggleButton>("DetailSourceToggle")!;
        Assert.True(sourceToggle.IsEffectivelyVisible);
        Assert.False(preview.FindControl<Avalonia.Controls.Primitives.ToggleButton>("SourceToggle")!.IsEffectivelyVisible);
        sourceToggle.IsChecked = true;
        Assert.Contains("name: Suelta", editor.Text);
        sourceToggle.IsChecked = false;
        Assert.DoesNotContain("name:", editor.Text);
        Assert.False(editor.ShowLineNumbers);
        Assert.Equal(vm.SelectedSkill!.Model.Content, File.ReadAllText(vm.SelectedSkill.FilePath));
        window.Close();
    }

    [Fact]
    public void SwitchingEditedSkill_ClearsUndoHistory()
    {
        SeedLibrary();
        var (view, vm, window) = CreateView(true);
        vm.EditSkillCommand.Execute(vm.Skills.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();
        var editorView = view.GetVisualDescendants().OfType<SkillEditorView>().Single(v => !v.IsReadOnly);
        var editor = editorView.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
        editor.AppendText("\nCambio");
        vm.EditSkillCommand.Execute(vm.SkillTreeRoots.Single(n => n.IsFolder).Children.Single().Skill!);
        Assert.Contains("name: Redactor", editor.Text);
        Assert.False(editor.Document.UndoStack.CanUndo);
        vm.CloseEditorCommand.Execute(null);
        window.Close();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task MarkdownDrop_ImportsOnlyIntoTargetFolder(bool treeMode, bool cardTarget)
    {
        SeedLibrary();
        var external = Path.Combine(_tempDir, "external.md");
        File.WriteAllText(external, "# Externa");
        var (view, _, window) = CreateView(treeMode);
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile((await window.StorageProvider.TryGetFileFromPathAsync(external))!));
        Interactive target = cardTarget
            ? view.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is SkillFolderCardViewModel { Name: "escritura" })
            : view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("folder-row") &&
                (b.DataContext is SkillFolderViewModel { Name: "escritura" } || b.DataContext is SkillTreeItemViewModel { Name: "escritura" }));
        var over = new DragEventArgs(DragDrop.DragOverEvent, transfer, target, default, KeyModifiers.None);
        target.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Copy, over.DragEffects);
        target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, target, default, KeyModifiers.None));
        Assert.True(File.Exists(Path.Combine(_skillsDir, "escritura", "external.md")));
        Assert.False(File.Exists(Path.Combine(_skillsDir, "external.md")));
        Assert.True(File.Exists(external));
        window.Close();
    }

    [Fact]
    public void InternalDropOnTreeFolder_DoesNotBubbleBackToRoot()
    {
        SeedLibrary();
        var (view, _, window) = CreateView(true);
        var path = Path.Combine(_skillsDir, "suelta.md");
        var target = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("folder-row") && b.DataContext is SkillTreeItemViewModel { Name: "escritura" });
        using var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.CreateStringApplicationFormat("com.enki.skill-path"), path));
        target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, target, default, KeyModifiers.None));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_skillsDir, "escritura", "suelta.md")));
        window.Close();
    }

    [Fact]
    public void EditorFormatting_UsesSelectionAndPersistsMarkdown()
    {
        SeedLibrary();
        var (view, vm, window) = CreateView(true);
        vm.EditSkillCommand.Execute(vm.Skills.Single());
        window.GetLayoutManager()!.ExecuteLayoutPass();
        var editorView = view.GetVisualDescendants().OfType<SkillEditorView>().Single(v => !v.IsReadOnly);
        var editor = editorView.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
        editor.Select(editor.Text.IndexOf("Cuerpo", StringComparison.Ordinal), 6);
        var bold = editorView.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, "bold"));
        bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("**Cuerpo**", vm.ActiveEditor!.Text);
        vm.CloseEditorCommand.Execute(null);
        Assert.Contains("**Cuerpo**", File.ReadAllText(Path.Combine(_skillsDir, "suelta.md")));
        window.Close();
    }

}
