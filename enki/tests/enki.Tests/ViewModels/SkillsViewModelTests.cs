using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre el guardado forzado al cerrar el editor y la navegación por carpetas de la sección
/// (organizar la biblioteca en carpetas y elegir entre el modo explorador y el modo árbol). Como el
/// code-behind de una vista no se puede testear directamente, estos tests recorren los mismos caminos del
/// viewmodel que la vista dispara: <see cref="SkillsViewModel.CloseEditorCommand"/> es el que también
/// invoca el <c>Closing</c> de <c>MainWindow.axaml.cs</c> (Fase 1,
/// docs/agent-docs/analisis-proyecto/roadmap.md), y los de carpetas son los que atan los gestos de
/// arrastrar-y-soltar y el menú contextual del árbol.</summary>
public class SkillsViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SkillsViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (SkillsViewModel ViewModel, string SkillPath) CreateViewModelWithOneSkill()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);

        var skillsDirectory = Path.Combine(_vaultPath, ".enki", "skills");
        Directory.CreateDirectory(skillsDirectory);
        var skillPath = Path.Combine(skillsDirectory, "mi-skill.md");
        File.WriteAllText(skillPath, "---\nname: Mi skill\ndescription: Original\n---\nCuerpo original");

        var notifications = new NotificationService(settings, Path.Combine(_storageDirectory, "notifications"));
        var viewModel = new SkillsViewModel(vault, notifications, settings);
        return (viewModel, skillPath);
    }

    [Fact]
    public void CloseEditor_WhenSaveFails_PreservesEditorAndDraft()
    {
        var (viewModel, path) = CreateViewModelWithOneSkill();
        viewModel.EditSkillCommand.Execute(viewModel.Skills.Single());
        var editor = viewModel.ActiveEditor!;
        editor.Text = "Borrador pendiente";
        File.Delete(path);
        Directory.CreateDirectory(path); // El destino no se puede sustituir por un archivo.

        viewModel.CloseEditorCommand.Execute(null);

        Assert.Same(editor, viewModel.ActiveEditor);
        Assert.True(editor.IsDirty);
        Directory.Delete(path);
        File.WriteAllText(path, editor.Skill.Content);
        viewModel.CloseEditorCommand.Execute(null);
        Assert.Null(viewModel.ActiveEditor);
        Assert.Contains("Borrador pendiente", File.ReadAllText(path));
    }

    [Fact]
    public void CloseEditor_FlushesAPendingEdit_InsteadOfLosingIt()
    {
        var (viewModel, skillPath) = CreateViewModelWithOneSkill();
        var skill = viewModel.Skills.Single();

        viewModel.EditSkillCommand.Execute(skill);
        Assert.NotNull(viewModel.ActiveEditor);
        viewModel.ActiveEditor!.Text = "---\nname: Mi skill\ndescription: Original\n---\nCuerpo editado justo antes de cerrar";

        // No espera al debounce (600ms) — CloseEditor debe volcar el cambio pendiente de inmediato, igual
        // que MainWindow.axaml.cs hace ahora en su Closing.
        viewModel.CloseEditorCommand.Execute(null);

        Assert.Null(viewModel.ActiveEditor);
        Assert.Contains("Cuerpo editado justo antes de cerrar", File.ReadAllText(skillPath));
    }
    private SkillsViewModel CreateViewModel(out string skillsDirectory, out SettingsService settings)
    {
        settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);

        skillsDirectory = Path.Combine(_vaultPath, ".enki", "skills");
        Directory.CreateDirectory(skillsDirectory);

        var notifications = new NotificationService(settings, Path.Combine(_storageDirectory, "notifications"));
        return new SkillsViewModel(vault, notifications, settings);
    }

    [Fact]
    public void Reload_ShowsOnlyTheCurrentFolderContents_WithSubfoldersAsCards()
    {
        var viewModel = CreateViewModel(out var skillsDirectory, out _);
        Directory.CreateDirectory(Path.Combine(skillsDirectory, "escritura"));
        File.WriteAllText(Path.Combine(skillsDirectory, "raiz.md"), "---\nname: Raíz\n---\nCuerpo");
        File.WriteAllText(Path.Combine(skillsDirectory, "escritura", "dentro.md"), "---\nname: Dentro\n---\nCuerpo");
        viewModel.Reload();

        // En la raíz: la carpeta como tarjeta más la skill suelta, pero no la skill de dentro.
        Assert.Equal("Raíz", Assert.Single(viewModel.Skills).Name);
        Assert.Contains(viewModel.BrowserItems, i => i is SkillFolderCardViewModel { Name: "escritura" });

        var folder = Assert.Single(viewModel.RootFolders);
        viewModel.SelectFolderCommand.Execute(folder);

        // Dentro de la carpeta: solo su skill, más la tarjeta ".." para volver.
        Assert.Equal("Dentro", Assert.Single(viewModel.Skills).Name);
        Assert.Contains(viewModel.BrowserItems, i => i is SkillFolderCardViewModel { IsParent: true });
    }

    /// <summary>Con búsqueda activa la rejilla deja de ser "el contenido de esta carpeta" y pasa a ser
    /// "los resultados de toda la biblioteca" — si no, buscar dentro de una carpeta no encontraría lo que
    /// está en otra, que es justo el caso para el que sirve buscar.</summary>
    [Fact]
    public void Search_LooksAtTheWholeLibrary_EvenWhileInsideAFolder()
    {
        var viewModel = CreateViewModel(out var skillsDirectory, out _);
        Directory.CreateDirectory(Path.Combine(skillsDirectory, "escritura"));
        File.WriteAllText(Path.Combine(skillsDirectory, "escritura", "traductor.md"), "---\nname: Traductor\n---\nCuerpo");
        File.WriteAllText(Path.Combine(skillsDirectory, "otra.md"), "---\nname: Otra\n---\nCuerpo");
        viewModel.Reload();

        viewModel.SearchText = "traductor";

        Assert.True(viewModel.IsFlatResults);
        Assert.Equal("Traductor", Assert.Single(viewModel.Skills).Name);
        Assert.DoesNotContain(viewModel.BrowserItems, i => i is SkillFolderCardViewModel);
    }

    [Fact]
    public void Tree_MixesFoldersAndSkills_WithTheSkillsOfAFolderAsItsChildren()
    {
        var viewModel = CreateViewModel(out var skillsDirectory, out _);
        Directory.CreateDirectory(Path.Combine(skillsDirectory, "escritura"));
        File.WriteAllText(Path.Combine(skillsDirectory, "escritura", "dentro.md"), "---\nname: Dentro\n---\nCuerpo");
        File.WriteAllText(Path.Combine(skillsDirectory, "raiz.md"), "---\nname: Raíz\n---\nCuerpo");
        viewModel.Reload();

        Assert.True(viewModel.HasTreeItems);
        var folderNode = viewModel.SkillTreeRoots.Single(n => n.IsFolder);
        Assert.Equal("escritura", folderNode.Name);
        Assert.Equal("Dentro", Assert.Single(folderNode.Children).Name);
        Assert.Contains(viewModel.SkillTreeRoots, n => n.IsSkill && n.Name == "Raíz");
    }

    [Fact]
    public void MoveSkillToFolder_MovesTheFileAndKeepsItSelected()
    {
        var viewModel = CreateViewModel(out var skillsDirectory, out _);
        Directory.CreateDirectory(Path.Combine(skillsDirectory, "escritura"));
        File.WriteAllText(Path.Combine(skillsDirectory, "movible.md"), "---\nname: Movible\n---\nCuerpo");
        viewModel.Reload();

        var skill = viewModel.Skills.Single();
        var folder = viewModel.RootFolders.Single();
        viewModel.MoveSkillToFolder(skill, folder.FullPath);

        Assert.True(File.Exists(Path.Combine(skillsDirectory, "escritura", "movible.md")));
        Assert.False(File.Exists(Path.Combine(skillsDirectory, "movible.md")));
        Assert.Equal("escritura", viewModel.SelectedSkill!.RelativeFolderPath);
    }

    [Fact]
    public void NavigationMode_IsPersistedToSettings_AndReadBackOnTheNextSection()
    {
        var viewModel = CreateViewModel(out _, out var settings);

        Assert.False(viewModel.IsTreeView);
        viewModel.ShowTreeViewCommand.Execute(null);

        Assert.Equal("Tree", settings.Settings.SkillsViewMode);

        var reopened = new SkillsViewModel(new VaultService(settings),
            new NotificationService(settings, Path.Combine(_storageDirectory, "notifications")), settings);
        Assert.True(reopened.IsTreeView);
    }

    [Fact]
    public void DeleteFolder_AsksForConfirmationFirst_AndOnlyDeletesOnConfirm()
    {
        var viewModel = CreateViewModel(out var skillsDirectory, out _);
        Directory.CreateDirectory(Path.Combine(skillsDirectory, "temporal"));
        viewModel.Reload();

        var folder = viewModel.RootFolders.Single();
        viewModel.RequestDeleteFolderCommand.Execute(folder);

        Assert.Same(folder, viewModel.PendingDeleteFolder);
        Assert.True(Directory.Exists(folder.FullPath));

        viewModel.ConfirmDeleteFolderCommand.Execute(null);

        Assert.Null(viewModel.PendingDeleteFolder);
        Assert.False(Directory.Exists(folder.FullPath));
    }
    [Fact]
    public void NewSkill_UsesActiveFolderAndOpensEditor()
    {
        var vm = CreateViewModel(out var directory, out _);
        Directory.CreateDirectory(Path.Combine(directory, "custom"));
        vm.Reload();
        vm.SelectTreeItemCommand.Execute(vm.SkillTreeRoots.Single());
        vm.NewSkillCommand.Execute(null);
        vm.NamePrompt!.InputText = "Analista";
        vm.ConfirmNamePromptCommand.Execute(null);
        Assert.NotNull(vm.ActiveEditor);
        Assert.Equal("Analista", vm.ActiveEditor!.Title);
        Assert.True(File.Exists(Path.Combine(directory, "custom", "Analista.md")));
        vm.CloseEditorCommand.Execute(null);
    }

    [Fact]
    public void ImportConflict_KeepsDropDestinationWhenNavigationChanges()
    {
        var vm = CreateViewModel(out var directory, out _);
        var target = Path.Combine(directory, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "skill.md"), "Anterior");
        var source = Path.Combine(_storageDirectory, "skill.md");
        File.WriteAllText(source, "Importada");
        vm.Reload();
        vm.BeginImport([source], target);
        Assert.True(vm.HasImportConflicts);
        vm.SelectFolderCommand.Execute(null);
        vm.ResolveImportRenameCommand.Execute(null);
        Assert.Equal("Anterior", File.ReadAllText(Path.Combine(target, "skill.md")));
        Assert.Equal("Importada", File.ReadAllText(Path.Combine(target, "skill 2.md")));
        Assert.False(File.Exists(Path.Combine(directory, "skill.md")));
    }

    [Fact]
    public void RenameTreeFolder_ReentrantCommitDoesNotReloadAgain()
    {
        var vm = CreateViewModel(out var directory, out _);
        vm.NewTreeFolderAtRootCommand.Execute(null);
        var folder = vm.SkillTreeRoots.Single();
        folder.EditingName = "Renamed";
        // Simula LostFocus durante la reconstrucción provocada por Enter.
        vm.SkillTreeRoots.CollectionChanged += (_, _) => vm.CommitRenameTreeItemCommand.Execute(folder);
        vm.CommitRenameTreeItemCommand.Execute(folder);
        Assert.True(Directory.Exists(Path.Combine(directory, "Renamed")));
        Assert.False(folder.IsRenaming);
    }

    [Fact]
    public void MoveOpenSkill_FlushesDraftWithoutRecreatingOldPath()
    {
        var (vm, path) = CreateViewModelWithOneSkill();
        var skill = vm.Skills.Single();
        vm.EditSkillCommand.Execute(skill);
        vm.ActiveEditor!.Text = "Borrador";
        var target = Path.Combine(Path.GetDirectoryName(path)!, "target");
        vm.MoveSkillToFolder(skill, target);
        Assert.Null(vm.ActiveEditor);
        Assert.False(File.Exists(path));
        Assert.Contains("Borrador", File.ReadAllText(Path.Combine(target, Path.GetFileName(path))));
    }

    [Fact]
    public void EmptyFolder_ShowsEmptyStateDespiteParentNavigationCard()
    {
        var vm = CreateViewModel(out var directory, out _);
        Directory.CreateDirectory(Path.Combine(directory, "empty"));
        vm.Reload();
        vm.SelectFolderCommand.Execute(vm.RootFolders.Single());
        Assert.True(vm.IsCurrentFolderEmpty);
    }

    [Fact]
    public void Reload_PreservesExportSelection()
    {
        var (vm, _) = CreateViewModelWithOneSkill();
        vm.Skills.Single().IsCheckedForExport = true;
        vm.NewTreeFolderAtRootCommand.Execute(null);
        Assert.True(vm.Skills.Single().IsCheckedForExport);
    }

    [Fact]
    public void ExternalChange_ReloadRequiresExplicitConfirmationAndKeepsDraftUntilThen()
    {
        var (vm, path) = CreateViewModelWithOneSkill();
        vm.EditSkillCommand.Execute(vm.Skills.Single());
        var editor = vm.ActiveEditor!;
        editor.Text = "Borrador";
        File.WriteAllText(path, "Contenido externo");
        editor.FlushPendingSave();
        Assert.True(editor.IsDirty);
        Assert.NotNull(editor.SaveError);
        editor.RequestReloadCommand.Execute(null);
        Assert.Equal("Borrador", editor.Text);
        editor.CancelReloadCommand.Execute(null);
        Assert.Equal("Borrador", editor.Text);
        editor.RequestReloadCommand.Execute(null);
        editor.ConfirmReloadCommand.Execute(null);
        Assert.Equal("Contenido externo", editor.Text);
        Assert.False(editor.IsDirty);
        vm.CloseEditorCommand.Execute(null);
        Assert.Null(vm.ActiveEditor);
    }

    [Fact]
    public async System.Threading.Tasks.Task Export_FlushesPendingDraftBeforeOpeningPicker()
    {
        var (vm, path) = CreateViewModelWithOneSkill();
        vm.EditSkillCommand.Execute(vm.Skills.Single());
        vm.ActiveEditor!.Text = "Última edición";
        await vm.ExportSkillsCommand.ExecuteAsync(null);
        Assert.Contains("Última edición", File.ReadAllText(path));
        Assert.False(vm.ActiveEditor.IsDirty);
        vm.CloseEditorCommand.Execute(null);
    }

}
