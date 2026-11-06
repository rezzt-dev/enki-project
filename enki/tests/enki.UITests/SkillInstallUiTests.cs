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

/// <summary>Addon de instalación en el sistema montado sobre el XAML real: el detalle con un paquete, el
/// diálogo de instalación y el de skills del sistema. La carpeta personal del instalador es temporal.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SkillInstallUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private string Home => Path.Combine(_tempDir, "home");

    public SkillInstallUiTests() => HeadlessApp.EnsureInitialized();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SkillsViewModel ViewModel, Window Window) CreateView(bool treeMode)
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "config"));
        settings.Settings.SkillsViewMode = treeMode ? "Tree" : "Browser";
        settings.Settings.SkillSystemIntegrationEnabled = true;
        settings.Save();

        var vaultDir = Path.Combine(_tempDir, "vault");
        var vault = new VaultService(settings);
        vault.InitializeVault(vaultDir, seedWelcomeNote: false);
        var package = Path.Combine(vaultDir, ".enki", "skills", "informe");
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        File.WriteAllText(Path.Combine(package, "SKILL.md"), "---\nname: Informe\ndescription: Genera informes\n---\nCuerpo");
        File.WriteAllText(Path.Combine(package, "scripts", "run.sh"), "echo");

        var notifications = new NotificationService(settings, Path.Combine(_tempDir, "notifications"));
        var viewModel = new SkillsViewModel(vault, notifications, settings, installService: new SkillInstallService(Home, _ => null));
        var window = new Window { Content = new SkillsView { DataContext = viewModel }, Width = 1400, Height = 900 };
        window.Show();
        window.GetLayoutManager()!.ExecuteLayoutPass();
        return (viewModel, window);
    }

    private static string RenderedText(Window window) => string.Join(" | ",
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallDialog_RendersOneRowPerAssistantAndInstalls(bool treeMode)
    {
        var (viewModel, window) = CreateView(treeMode);
        var skill = viewModel.Skills.Single();
        viewModel.SelectSkillCommand.Execute(skill);
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.Contains("Package", RenderedText(window));
        Assert.Equal(["scripts/run.sh"], viewModel.SelectedPackageFiles);

        viewModel.OpenInstallDialogCommand.Execute(skill);
        window.GetLayoutManager()!.ExecuteLayoutPass();
        var text = RenderedText(window);
        Assert.Contains("Claude Code", text);
        Assert.Contains("Codex", text);
        Assert.Contains("Gemini CLI", text);
        Assert.Contains("informe", text);

        viewModel.InstallToTargetCommand.Execute(viewModel.InstallDialog!.Targets.First());
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.True(File.Exists(Path.Combine(Home, ".claude", "skills", "informe", "scripts", "run.sh")));

        viewModel.CloseInstallDialogCommand.Execute(null);
        viewModel.OpenSystemSkillsCommand.Execute(null);
        window.GetLayoutManager()!.ExecuteLayoutPass();
        Assert.Contains("Genera informes", RenderedText(window));
        window.Close();
    }
}
