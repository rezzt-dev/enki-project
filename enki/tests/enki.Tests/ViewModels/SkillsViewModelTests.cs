using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="SkillsViewModel.CloseEditorCommand"/> — the same
/// <c>ActiveEditor?.FlushPendingSave()</c> call <c>MainWindow.axaml.cs</c>'s <c>Closing</c> handler now also
/// makes (Fase 1, docs/analisis-proyecto/roadmap.md: "el editor de Skills nunca se fuerza a guardar al
/// cerrar la ventana"). Since a view's code-behind can't be unit tested directly, this exercises the same
/// view-model path the window's close handler goes through, confirming a pending edit inside the debounce
/// window is not lost.</summary>
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
}
