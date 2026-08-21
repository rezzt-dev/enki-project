using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Fase 4 §"Editor de notas" gap 17 ("Toolbar fija sin personalización") — each of the five
/// formatting-toolbar groups can be collapsed/expanded independently via
/// <see cref="EditorViewModel.ToggleToolbarGroupCommand"/>, and the choice is remembered: mirrored into the
/// shared <see cref="EditorPreferences"/> statics (so other open tabs pick it up — see
/// <see cref="EditorViewModel.ApplyToolbarGroupState"/>) and persisted to <see cref="AppSettings"/>.</summary>
[Collection("LocalizationMutatingTests")]
public class EditorViewModelToolbarGroupsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public EditorViewModelToolbarGroupsTests()
    {
        // Every group defaults to expanded — reset the shared statics before each test so one test's
        // toggle can't leak into the next (EditorPreferences is a process-wide static, same caveat as
        // every other test file that touches it).
        EditorPreferences.ToolbarTextStyleExpanded = true;
        EditorPreferences.ToolbarHeadingsExpanded = true;
        EditorPreferences.ToolbarListsExpanded = true;
        EditorPreferences.ToolbarInsertExpanded = true;
        EditorPreferences.ToolbarCodeExpanded = true;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    private (VaultService Vault, SettingsService Settings) CreateVault(out string vaultDir)
    {
        vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(vaultDir);
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(vaultDir);
        return (vault, settings);
    }

    private EditorViewModel CreateEditor(VaultService vault, SettingsService settings)
    {
        File.WriteAllText(Path.Combine(vault.VaultPath, "Nota.md"), "Contenido");
        var note = vault.GetNotes().Single();
        return new EditorViewModel(vault, note, onBack: () => { }, onChanged: () => { }, settingsService: settings);
    }

    [Theory]
    [InlineData("TextStyle")]
    [InlineData("Headings")]
    [InlineData("Lists")]
    [InlineData("Insert")]
    [InlineData("Code")]
    public void ToggleToolbarGroup_CollapsesOnlyTheTargetedGroup(string group)
    {
        var (vault, settings) = CreateVault(out _);
        var editor = CreateEditor(vault, settings);

        editor.ToggleToolbarGroupCommand.Execute(group);

        Assert.Equal(group != "TextStyle", editor.IsToolbarTextStyleExpanded);
        Assert.Equal(group != "Headings", editor.IsToolbarHeadingsExpanded);
        Assert.Equal(group != "Lists", editor.IsToolbarListsExpanded);
        Assert.Equal(group != "Insert", editor.IsToolbarInsertExpanded);
        Assert.Equal(group != "Code", editor.IsToolbarCodeExpanded);
    }

    [Fact]
    public void ToggleToolbarGroup_Twice_RestoresExpandedState()
    {
        var (vault, settings) = CreateVault(out _);
        var editor = CreateEditor(vault, settings);

        editor.ToggleToolbarGroupCommand.Execute("Lists");
        Assert.False(editor.IsToolbarListsExpanded);

        editor.ToggleToolbarGroupCommand.Execute("Lists");
        Assert.True(editor.IsToolbarListsExpanded);
    }

    [Fact]
    public void ToggleToolbarGroup_PersistsToSettings_AndSurvivesReload()
    {
        var (vault, settings) = CreateVault(out _);
        var editor = CreateEditor(vault, settings);

        editor.ToggleToolbarGroupCommand.Execute("Code");

        Assert.False(settings.Settings.EditorToolbarCodeExpanded);

        var reloaded = new SettingsService(_tempDir);
        Assert.False(reloaded.Settings.EditorToolbarCodeExpanded);
    }

    [Fact]
    public void ToggleToolbarGroup_UpdatesSharedPreference_SoAnotherTabPicksItUp()
    {
        var (vault, settings) = CreateVault(out _);
        var tabOne = CreateEditor(vault, settings);
        File.WriteAllText(Path.Combine(vault.VaultPath, "Otra.md"), "Otra");
        var otherNote = vault.GetNotes().First(n => n.FullPath.EndsWith("Otra.md", StringComparison.Ordinal));
        var tabTwo = new EditorViewModel(vault, otherNote, onBack: () => { }, onChanged: () => { }, settingsService: settings);

        tabOne.ToggleToolbarGroupCommand.Execute("Insert");
        Assert.True(tabTwo.IsToolbarInsertExpanded); // tabTwo was constructed before the toggle

        tabTwo.ApplyToolbarGroupState();

        Assert.False(tabTwo.IsToolbarInsertExpanded);
    }
}
