using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class NotesViewModelQueryTests
{
    [Fact]
    public void SavedQuery_FiltersNestedNotesAndSurvivesReopen()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var vaultPath = Path.Combine(temp, "vault");
        Directory.CreateDirectory(vaultPath);
        try
        {
            var nested = Path.Combine(vaultPath, "projects");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "A.md"), "---\ntags: [work]\nstatus: active\ndue: 2026-10-15\n---\n\n# A");
            File.WriteAllText(Path.Combine(nested, "B.md"), "---\ntags: [home]\nstatus: active\ndue: 2026-10-16\n---\n\n# B");
            var settings = new SettingsService(temp);
            var vault = new VaultService(settings);
            vault.SetVaultPath(vaultPath);
            var vaultSettings = new VaultSettingsService(vault);
            var sessions = new SessionService(vault);
            var vm = new NotesViewModel(vault, sessions, vaultSettingsService: vaultSettings);
            vm.IsQueryPanelOpen = true;
            vm.QueryIncludeSubfolders = true;
            vm.QueryTag = "work";
            vm.QueryPropertyKey = "status";
            vm.QueryPropertyValue = "active";
            vm.QueryExactPropertyValue = true;
            vm.QueryDatePropertyKey = "due";
            vm.QueryModifiedFrom = "2026-10-15";
            vm.QueryModifiedTo = "2026-10-15";
            vm.QueryName = "Active work";
            vm.Reload();
            vm.TagFilter.Items.Single(item => item.Tag == "work").ToggleCommand.Execute(null);
            Assert.Single(vm.Notes);
            Assert.Equal("A", vm.Notes[0].Title);
            vm.SaveQueryCommand.Execute(null);

            var restoredSettings = new VaultSettingsService(vault);
            var restored = new NotesViewModel(vault, sessions, vaultSettingsService: restoredSettings);
            Assert.Single(restored.SavedQueries);
            Assert.Equal("work", restored.SavedQueries[0].SelectedTags.Single());
            Assert.Equal("due", restored.SavedQueries[0].DatePropertyKey);
            Assert.True(restored.SavedQueries[0].ExactPropertyValue);
            restored.SelectedQuery = restored.SavedQueries.Single();
            restored.Reload();
            Assert.Single(restored.Notes);
            Assert.Equal("work", restored.TagFilter.SelectedTags.Single());
            Assert.Equal("A", restored.Notes[0].Title);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }
}
