using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class NotesViewModelTagFilterTests
{
    [Fact]
    public void Reload_FiltersNotesByTag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var vaultDir = Path.Combine(tempDir, "vault");
        Directory.CreateDirectory(vaultDir);

        try
        {
            var settings = new SettingsService(tempDir);
            var vault = new VaultService(settings);
            vault.SetVaultPath(vaultDir);

            File.WriteAllText(
                Path.Combine(vaultDir, "Nota Casa.md"),
                NoteTagHelper.SetTags("# Nota Casa\n\nContenido de la nota.", ["casa", "urgente"]));
            File.WriteAllText(
                Path.Combine(vaultDir, "Nota Trabajo.md"),
                NoteTagHelper.SetTags("# Nota Trabajo\n\nContenido de la nota.", ["trabajo"]));
            File.WriteAllText(
                Path.Combine(vaultDir, "Nota Sin Tags.md"),
                "# Nota Sin Tags\n\nContenido de la nota.");

            var session = new SessionService(vault);
            var vm = new NotesViewModel(vault, session);

            Assert.Equal(3, vm.Notes.Count);
            Assert.Contains("casa", vm.TagFilter.Items.Select(i => i.Tag));
            Assert.Contains("urgente", vm.TagFilter.Items.Select(i => i.Tag));
            Assert.Contains("trabajo", vm.TagFilter.Items.Select(i => i.Tag));

            var casaTag = vm.TagFilter.Items.First(i => i.Tag == "casa");
            casaTag.ToggleCommand.Execute(null);

            Assert.Single(vm.Notes);
            Assert.Equal("Nota Casa", vm.Notes[0].Title);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Reload_ShowsAllNotes_WhenNoTagSelected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var vaultDir = Path.Combine(tempDir, "vault");
        Directory.CreateDirectory(vaultDir);

        try
        {
            var settings = new SettingsService(tempDir);
            var vault = new VaultService(settings);
            vault.SetVaultPath(vaultDir);

            File.WriteAllText(
                Path.Combine(vaultDir, "A.md"),
                NoteTagHelper.SetTags("# A\n\nContenido.", ["x"]));
            File.WriteAllText(
                Path.Combine(vaultDir, "B.md"),
                NoteTagHelper.SetTags("# B\n\nContenido.", ["y"]));

            var session = new SessionService(vault);
            var vm = new NotesViewModel(vault, session);

            Assert.Equal(2, vm.Notes.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
