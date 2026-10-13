using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class VaultServiceFoldersTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "enki-folders-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;
    public VaultServiceFoldersTests()
    {
        _vault = new VaultService(new SettingsService(_storage));
        _vault.InitializeVault(Path.Combine(_storage, "vault"), seedWelcomeNote: false);
    }
    public void Dispose() => Directory.Delete(_storage, true);

    [Theory]
    [InlineData("../escape")]
    [InlineData(".hidden")]
    [InlineData("note.attachments")]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("folder.")]
    [InlineData(" ")]
    [InlineData("a\\b")]
    public void Create_RejectsInvalidNames(string name) =>
        Assert.Throws<IOException>(() => _vault.CreateManagedFolder(_vault.VaultPath, name));

    [Fact]
    public void Copy_PreservesMixedContentsEmptyFoldersAndAttachmentsWithoutOverwrite()
    {
        var folder = _vault.CreateManagedFolder(_vault.VaultPath, "Project");
        Directory.CreateDirectory(Path.Combine(folder.FullPath, "empty"));
        Directory.CreateDirectory(Path.Combine(folder.FullPath, "note.attachments"));
        File.WriteAllText(Path.Combine(folder.FullPath, "note.md"), "![](note.attachments/picture.png)");
        File.WriteAllBytes(Path.Combine(folder.FullPath, "note.attachments", "picture.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_vault.VaultPath, "Project 2"), "collision");
        var copy = _vault.TransferFolder(folder.FullPath, _vault.VaultPath, false);
        Assert.Equal("Project 3", copy.Name);
        Assert.True(Directory.Exists(Path.Combine(copy.FullPath, "empty")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(copy.FullPath, "note.attachments", "picture.png")));
        Assert.Equal("![](note.attachments/picture.png)", File.ReadAllText(Path.Combine(copy.FullPath, "note.md")));
        Assert.True(Directory.Exists(folder.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_RejectsDescendantAndOutsideVault(bool cut)
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        var child = _vault.CreateManagedFolder(source.FullPath, "child");
        Assert.Throws<IOException>(() => _vault.TransferFolder(source.FullPath, child.FullPath, cut));
        Assert.Throws<IOException>(() => _vault.TransferFolder(source.FullPath, _storage, cut));
        Assert.True(Directory.Exists(source.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_BetweenEncryptedSessions_ReencryptsAllFiles(bool cut)
    {
        var sessions = new SessionService(_vault);
        var (origin, _) = sessions.CreateEncrypted("Origin", "password-origin");
        var (target, _) = sessions.CreateEncrypted("Target", "password-target");
        var source = _vault.CreateManagedFolder(origin.RootPath, "Project");
        var note = _vault.CreateNote("Note", source.FullPath);
        _vault.WriteContent(note, "secret");
        var attachments = Directory.CreateDirectory(Path.Combine(source.FullPath, "Note.attachments"));
        _vault.WriteBinaryFile(Path.Combine(attachments.FullName, "a.png"), [7, 8, 9]);
        var result = _vault.TransferFolder(source.FullPath, target.RootPath, cut);
        Assert.Equal("secret", _vault.ReadContent(_vault.GetNote(Path.Combine(result.FullPath, "Note.md"))!));
        Assert.Equal([7, 8, 9], _vault.ReadFileBytes(Path.Combine(result.FullPath, "Note.attachments", "a.png")));
        Assert.Equal(!cut, Directory.Exists(source.FullPath));
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(result.FullPath, "Note.md")));
    }

    [Fact]
    public void DeleteAndRestore_EncryptedFolder_PreservesOriginAndRequiresUnlock()
    {
        var sessions = new SessionService(_vault);
        var (session, _) = sessions.CreateEncrypted("Private", "password-private");
        var source = _vault.CreateManagedFolder(session.RootPath, "Project");
        var note = _vault.CreateNote("Note", source.FullPath);
        _vault.WriteContent(note, "secret");
        _vault.DeleteFolder(source);
        var trashed = Assert.Single(_vault.GetTrashedFolders());
        sessions.Lock(session);
        Assert.Empty(_vault.GetTrashedFolders());
        Assert.Throws<SessionLockedException>(() => _vault.RestoreFolder(trashed));
    }

    [Fact]
    public void Restore_WithCollision_UsesOriginalParentAndUniqueName()
    {
        var parent = _vault.CreateManagedFolder(_vault.VaultPath, "parent");
        var source = _vault.CreateManagedFolder(parent.FullPath, "child");
        File.WriteAllText(Path.Combine(source.FullPath, "a.md"), "content");
        _vault.DeleteFolder(source);
        _vault.CreateManagedFolder(parent.FullPath, "child");
        var result = _vault.RestoreFolder(Assert.Single(_vault.GetTrashedFolders()));
        Assert.Equal(Path.Combine(parent.FullPath, "child 2"), result.FullPath);
        Assert.Equal("content", File.ReadAllText(Path.Combine(result.FullPath, "a.md")));
        Assert.Empty(_vault.GetTrashedFolders());
    }

    [Fact]
    public void Mutation_CanBeCancelledBeforeAnyChange()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        _vault.FolderChanging += change => change.Cancel = true;
        Assert.Throws<IOException>(() => _vault.RenameManagedFolder(source.FullPath, "renamed"));
        Assert.Throws<IOException>(() => _vault.DeleteFolder(source));
        Assert.True(Directory.Exists(source.FullPath));
        Assert.Empty(_vault.GetTrashedFolders());
    }

    [Fact]
    public void Copy_WithSymbolicLink_RollsBackPartialDestination()
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        File.WriteAllText(Path.Combine(source.FullPath, "a.txt"), "content");
        Directory.CreateSymbolicLink(Path.Combine(source.FullPath, "link"), _storage);
        Assert.Throws<IOException>(() => _vault.TransferFolder(source.FullPath, _vault.VaultPath, false));
        Assert.False(Directory.Exists(Path.Combine(_vault.VaultPath, "source 2")));
        Assert.Empty(Directory.GetDirectories(_vault.VaultPath, ".folder-*"));
        Directory.Delete(Path.Combine(source.FullPath, "link"));
    }
    [Theory]
    [InlineData("sessions")]
    [InlineData("Sessions")]
    public void FolderNamedSessions_DoesNotCollideWithTrashedSessionsContainer(string name)
    {
        var source = _vault.CreateManagedFolder(_vault.VaultPath, name);
        _vault.DeleteFolder(source);
        var folder = Assert.Single(_vault.GetTrashedFolders());
        Assert.NotEqual("sessions", folder.Name);
        Assert.Equal(name, _vault.RestoreFolder(folder).Name);
    }

    [Fact]
    public void TransferToLockedSession_LeavesSourceAndDestinationIntact()
    {
        var sessions = new SessionService(_vault);
        var (target, _) = sessions.CreateEncrypted("Private", "password-private");
        var source = _vault.CreateManagedFolder(_vault.VaultPath, "source");
        _vault.CreateNote("note", source.FullPath);
        sessions.Lock(target);
        Assert.Throws<SessionLockedException>(() => _vault.TransferFolder(source.FullPath, target.RootPath, true));
        Assert.True(Directory.Exists(source.FullPath));
        Assert.False(Directory.Exists(Path.Combine(target.RootPath, "source")));
    }

}
