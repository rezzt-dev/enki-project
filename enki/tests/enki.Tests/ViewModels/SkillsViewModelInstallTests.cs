using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Addon de instalación en el sistema desde la sección de skills. El instalador apunta a una
/// carpeta personal temporal; sin instalador (la construcción por defecto de los demás tests) la sección
/// nunca ofrece instalar.</summary>
public sealed class SkillsViewModelInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-skills-install-vm-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (SkillsViewModel ViewModel, SettingsService Settings) Create(bool withInstaller, string instance = "a")
    {
        var settings = new SettingsService(Path.Combine(_root, instance, "config"));
        var vault = new VaultService(settings);
        var vaultPath = Path.Combine(_root, instance, "vault");
        vault.InitializeVault(vaultPath, seedWelcomeNote: false);
        var skills = Path.Combine(vaultPath, ".enki", "skills");
        Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills, "revisor.md"), "---\nname: Revisor\ndescription: Revisa cambios\n---\nCuerpo original");

        var notifications = new NotificationService(settings, Path.Combine(_root, instance, "notifications"));
        var installer = withInstaller ? new SkillInstallService(Home, _ => null) : null;
        return (new SkillsViewModel(vault, notifications, settings, installService: installer), settings);
    }

    [Fact]
    public void WithoutInstallerOrSetting_InstallIsUnavailable()
    {
        var (withoutInstaller, settings) = Create(withInstaller: false);
        settings.Settings.SkillSystemIntegrationEnabled = true;
        settings.Save();
        Assert.False(withoutInstaller.IsSystemInstallAvailable);

        var (withInstaller, _) = Create(withInstaller: true, instance: "b");
        Assert.False(withInstaller.IsSystemInstallAvailable);
        withInstaller.OpenInstallDialogCommand.Execute(withInstaller.Skills.Single());
        Assert.Null(withInstaller.InstallDialog);
        Assert.False(Directory.Exists(Home));
    }

    [Fact]
    public void InstallFromDialog_ThenSavingInEditorUpdatesInstalledCopy()
    {
        var (viewModel, settings) = Create(withInstaller: true);
        settings.Settings.SkillSystemIntegrationEnabled = true;
        settings.Save();
        Assert.True(viewModel.IsSystemInstallAvailable);

        var skill = viewModel.Skills.Single();
        viewModel.SelectSkillCommand.Execute(skill);
        viewModel.OpenInstallDialogCommand.Execute(skill);
        var dialog = viewModel.InstallDialog!;
        Assert.Equal("revisor", dialog.InstallName);
        var claude = dialog.Targets.Single(t => t.Status.Target.Kind == SkillInstallTargetKind.ClaudeCode);

        viewModel.InstallToTargetCommand.Execute(claude);

        var installed = Path.Combine(Home, ".claude", "skills", "revisor", "SKILL.md");
        Assert.True(File.Exists(installed));
        Assert.Equal(SkillInstallState.Installed, dialog.Targets.Single(t => t.Status.Target.Kind == SkillInstallTargetKind.ClaudeCode).Status.State);
        viewModel.CloseInstallDialogCommand.Execute(null);
        Assert.Equal("Claude Code", viewModel.SelectedSkillInstallSummary);

        viewModel.EditSkillCommand.Execute(viewModel.Skills.Single());
        viewModel.ActiveEditor!.Text = "---\nname: Revisor\ndescription: Revisa cambios\n---\nCuerpo editado";
        viewModel.CloseEditorCommand.Execute(null);

        Assert.Contains("Cuerpo editado", File.ReadAllText(installed));
        Assert.False(Directory.Exists(Path.Combine(Home, ".agents")));
    }

    [Fact]
    public void AutoSyncDisabled_LeavesInstalledCopyOutdated()
    {
        var (viewModel, settings) = Create(withInstaller: true);
        settings.Settings.SkillSystemIntegrationEnabled = true;
        settings.Settings.SkillSystemAutoSync = false;
        settings.Save();
        var skill = viewModel.Skills.Single();
        viewModel.OpenInstallDialogCommand.Execute(skill);
        viewModel.InstallToTargetCommand.Execute(viewModel.InstallDialog!.Targets.First());
        viewModel.CloseInstallDialogCommand.Execute(null);

        viewModel.EditSkillCommand.Execute(viewModel.Skills.Single());
        viewModel.ActiveEditor!.Text = "---\nname: Revisor\ndescription: Revisa cambios\n---\nOtro cuerpo";
        viewModel.CloseEditorCommand.Execute(null);

        Assert.DoesNotContain("Otro cuerpo", File.ReadAllText(Path.Combine(Home, ".claude", "skills", "revisor", "SKILL.md")));
    }

    [Fact]
    public void SystemSkills_ImportBringsPackageIntoCurrentFolder()
    {
        var (viewModel, settings) = Create(withInstaller: true);
        settings.Settings.SkillSystemIntegrationEnabled = true;
        settings.Save();
        var external = Path.Combine(Home, ".claude", "skills", "externa");
        Directory.CreateDirectory(Path.Combine(external, "scripts"));
        File.WriteAllText(Path.Combine(external, "SKILL.md"), "---\nname: externa\ndescription: Hecha a mano\n---\nCuerpo");
        File.WriteAllText(Path.Combine(external, "scripts", "a.sh"), "echo");

        viewModel.OpenSystemSkillsCommand.Execute(null);
        var row = viewModel.SystemSkillsDialog!.Skills.Single();
        Assert.False(row.IsManagedByEnki);
        viewModel.ImportSystemSkillCommand.Execute(row);

        var imported = viewModel.Skills.Single(s => s.IsPackage);
        Assert.Equal("externa", imported.Id);
        Assert.True(File.Exists(Path.Combine(imported.Model.PackageDirectory!, "scripts", "a.sh")));
        Assert.True(File.Exists(Path.Combine(external, "SKILL.md")));
    }
}
