using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class AiChatServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private string ChatsDirectory => Path.Combine(_storageDirectory, "ai", "chats");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Sessions_WithoutFolder_IsEmptyAndCreatesNothing()
    {
        var service = new AiChatService(_storageDirectory);

        Assert.Empty(service.Sessions);
        Assert.False(Directory.Exists(ChatsDirectory));
    }

    [Fact]
    public void Save_PersistsOneFilePerChat_AndRoundTrips()
    {
        var service = new AiChatService(_storageDirectory);
        var changes = 0;
        service.ChatsChanged += () => changes++;
        var session = new AiChatSession { Title = "Plan", CliSessionId = "cli-1", Model = "opus", SkillName = "Redactor" };
        session.Messages.Add(new AiChatMessage
        {
            Role = AiChatRole.User,
            Text = "hola",
            Attachments = [new AiContextAttachment { Title = "Nota", RelativePath = "a/b.md", SentChars = 10, WasTruncated = true }]
        });

        service.Save(session);

        Assert.Equal(1, changes);
        Assert.True(File.Exists(Path.Combine(ChatsDirectory, session.Id + ".json")));
        var reloaded = new AiChatService(_storageDirectory).Sessions.Single();
        Assert.Equal("Plan", reloaded.Title);
        Assert.Equal("cli-1", reloaded.CliSessionId);
        Assert.Equal("opus", reloaded.Model);
        var attachment = reloaded.Messages.Single().Attachments.Single();
        Assert.Equal("a/b.md", attachment.RelativePath);
        Assert.True(attachment.WasTruncated);
    }

    [Fact]
    public void Sessions_AreOrderedByMostRecent()
    {
        var service = new AiChatService(_storageDirectory);
        var older = new AiChatSession { Title = "vieja", UpdatedUtc = DateTime.UtcNow.AddDays(-2) };
        var newer = new AiChatSession { Title = "nueva", UpdatedUtc = DateTime.UtcNow };
        service.Save(newer);
        service.Save(older);

        Assert.Equal(["nueva", "vieja"], service.Sessions.Select(s => s.Title));
        Assert.Equal(["nueva", "vieja"], new AiChatService(_storageDirectory).Sessions.Select(s => s.Title));
    }

    [Fact]
    public void Delete_RemovesFileAndEntry()
    {
        var service = new AiChatService(_storageDirectory);
        var session = new AiChatSession { Title = "x" };
        service.Save(session);

        service.Delete(session.Id);

        Assert.Empty(service.Sessions);
        Assert.Empty(Directory.EnumerateFiles(ChatsDirectory));
    }

    [Fact]
    public void DeleteAll_EmptiesHistory()
    {
        var service = new AiChatService(_storageDirectory);
        service.Save(new AiChatSession());
        service.Save(new AiChatSession());

        service.DeleteAll();

        Assert.Empty(service.Sessions);
        Assert.Empty(new AiChatService(_storageDirectory).Sessions);
    }

    [Fact]
    public void Load_SkipsCorruptOrMismatchedFiles_WithoutTouchingThem()
    {
        Directory.CreateDirectory(ChatsDirectory);
        var corrupt = Path.Combine(ChatsDirectory, "abc.json");
        File.WriteAllText(corrupt, "{ no es json");
        File.WriteAllText(Path.Combine(ChatsDirectory, "otronombre.json"), """{"Id":"distinto","Title":"x"}""");

        var service = new AiChatService(_storageDirectory);

        Assert.Empty(service.Sessions);
        Assert.Equal(2, service.UnreadableFileCount);
        Assert.Equal("{ no es json", File.ReadAllText(corrupt));
    }

    [Fact]
    public void Save_RejectsIdsThatCouldEscapeTheFolder()
    {
        var service = new AiChatService(_storageDirectory);

        Assert.Throws<ArgumentException>(() => service.Save(new AiChatSession { Id = "../fuera" }));
    }

    // ===================== Historial por vault =====================

    [Fact]
    public void AssignToVault_AssociatesAnOldChatOnce_AndPersists()
    {
        var service = new AiChatService(_storageDirectory);
        var old = new AiChatSession { Title = "Antiguo" };
        service.Save(old);
        Assert.Single(service.UnassignedSessions);

        service.AssignToVault(old, "vault1", "Personal");

        var reloaded = new AiChatService(_storageDirectory);
        var saved = Assert.Single(reloaded.Sessions);
        Assert.Equal("vault1", saved.VaultId);
        Assert.Equal("Personal", saved.VaultName);
        Assert.Empty(reloaded.UnassignedSessions);
        // Una conversación que ya tiene vault no se reasigna a otro.
        Assert.Throws<InvalidOperationException>(() => reloaded.AssignToVault(saved, "vault2", "Trabajo"));
    }

    // ===================== Borradores de chats nuevos =====================

    [Fact]
    public void NewChatDrafts_ArePerKey_Persist_AndEmptyOnesAreRemoved()
    {
        var service = new AiChatService(_storageDirectory);
        service.SaveNewChatDraft("v|vault", new AiChatDraft { Text = "Idea", Attachments = [new AiDraftAttachment { RelativePath = "Plan.md" }] });
        service.SaveNewChatDraft("v|s1", new AiChatDraft { Text = "Sesión" });

        var reloaded = new AiChatService(_storageDirectory);
        Assert.Equal("Idea", reloaded.FindNewChatDraft("v|vault")!.Text);
        Assert.Equal("Plan.md", reloaded.FindNewChatDraft("v|vault")!.Attachments.Single().RelativePath);
        Assert.Equal("Sesión", reloaded.FindNewChatDraft("v|s1")!.Text);

        reloaded.SaveNewChatDraft("v|vault", new AiChatDraft());
        Assert.Null(new AiChatService(_storageDirectory).FindNewChatDraft("v|vault"));
    }

    [Fact]
    public void DeleteAll_AlsoRemovesNewChatDrafts()
    {
        var service = new AiChatService(_storageDirectory);
        service.SaveNewChatDraft("v|vault", new AiChatDraft { Text = "Idea" });
        service.DeleteAll();
        Assert.Null(new AiChatService(_storageDirectory).FindNewChatDraft("v|vault"));
    }
}
