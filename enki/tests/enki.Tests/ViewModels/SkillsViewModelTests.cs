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
}
