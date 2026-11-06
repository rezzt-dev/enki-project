using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Verifica que el gestor reescribe las fuentes reales y que Polar conserva su marca de sync.</summary>
public sealed class TagManagerServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tags-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Merge_RewritesAllDomains_DeduplicatesAndPersists()
    {
        var (vault, kanban, polar, manager) = CreateServices();
        var notePath = Path.Combine(vault.VaultPath, "nota.md");
        File.WriteAllText(notePath, "---\naliases: [Nombre]\ntags: [curro, trabajo]\n---\n\n# Nota");

        var card = kanban.DefaultBoard.Columns[0].Cards[0];
        card.Tags = ["curro", "trabajo", "CURRO"];
        kanban.Save();

        var list = polar.CreateList("Lista", "ic_list", "#000", false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, enki.Models.PolarRecurrence.None, 0, ["curro", "trabajo"]);
        task.Dirty = false;
        polar.Save();

        var entry = manager.BuildEntries().Single(e => e.Name == "curro");
        Assert.Equal((1, 1, 1), (entry.NoteCount, entry.KanbanCount, entry.PolarCount));

        var changed = manager.ReplaceTag(["curro", "trabajo"], "TRABAJO");

        Assert.Equal((1, 1, 1), (changed.Notes, changed.Kanban, changed.Polar));
        Assert.Equal(["trabajo"], vault.GetNote(notePath)!.Tags);
        Assert.Contains("aliases: [Nombre]", File.ReadAllText(notePath));
        Assert.Equal(["trabajo"], new KanbanService(vault).DefaultBoard.Columns[0].Cards[0].Tags);
        var persistedTask = new PolarTaskService(vault).Store.Tasks.Single(t => t.Id == task.Id);
        Assert.Equal(["trabajo"], persistedTask.Tags);
        Assert.True(persistedTask.Dirty);
        Assert.Single(manager.BuildEntries());
    }

    [Fact]
    public void Delete_RemovesOnlyTheNamedTagAndKeepsOtherFrontmatter()
    {
        var (vault, kanban, polar, manager) = CreateServices();
        var path = Path.Combine(vault.VaultPath, "nota.md");
        File.WriteAllText(path, "---\naliases: [Nombre]\ntags: [malo, bueno]\n---\n\nTexto");
        kanban.DefaultBoard.Columns[0].Cards[0].Tags = ["malo", "bueno"];
        kanban.Save();
        var list = polar.CreateList("Lista", "ic_list", "#000", false);
        var task = polar.CreateTask(list, "Tarea", "", 0, null, enki.Models.PolarRecurrence.None, 0, ["malo", "bueno"]);

        manager.ReplaceTag(["malo"], null);

        Assert.Equal(["bueno"], vault.GetNote(path)!.Tags);
        Assert.Contains("aliases: [Nombre]", File.ReadAllText(path));
        Assert.Equal(["bueno"], kanban.DefaultBoard.Columns[0].Cards[0].Tags);
        Assert.Equal(["bueno"], task.Tags);
    }

    [Fact]
    public void InvalidTarget_IsRejectedBeforeAnyMutation()
    {
        var (vault, kanban, polar, manager) = CreateServices();
        kanban.DefaultBoard.Columns[0].Cards[0].Tags = ["original"];
        Assert.Throws<ArgumentException>(() => manager.ReplaceTag(["original"], "a,b"));
        Assert.Equal(["original"], kanban.DefaultBoard.Columns[0].Cards[0].Tags);
        Assert.False(TagManagerService.IsValidTagName("a\nb"));
        Assert.False(TagManagerService.IsValidTagName(" "));
    }

    private (VaultService Vault, KanbanService Kanban, PolarTaskService Polar, TagManagerService Manager) CreateServices()
    {
        var vault = new VaultService(new SettingsService(_directory));
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var kanban = new KanbanService(vault);
        var polar = new PolarTaskService(vault);
        return (vault, kanban, polar, new TagManagerService(vault, kanban, polar));
    }
}
