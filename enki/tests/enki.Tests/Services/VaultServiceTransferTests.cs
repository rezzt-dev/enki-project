using System;
using System.IO;
using System.Text;
using enki.Services;
using enki.Models;
using Xunit;

namespace enki.Tests.Services;

public sealed class VaultServiceTransferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;
    private readonly SessionService _sessions;

    public VaultServiceTransferTests()
    {
        _vault = new VaultService(new SettingsService(_directory));
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
    }

    [Fact]
    public void CopyNoteRepeatedly_PreservesOriginalAndUsesUniqueNames()
    {
        var note = _vault.CreateNote("Nota");
        _vault.WriteContent(note, "contenido");
        var first = _vault.TransferNote(note, _vault.VaultPath, false);
        var second = _vault.TransferNote(note, _vault.VaultPath, false);
        Assert.Equal("Nota 2.md", Path.GetFileName(first.FullPath));
        Assert.Equal("Nota 3.md", Path.GetFileName(second.FullPath));
        Assert.Equal("contenido", _vault.ReadContent(first));
        Assert.Equal("contenido", _vault.ReadContent(note));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferNote_CopiesNestedAttachmentsAndRewritesCollisionReference(bool cut)
    {
        var note = _vault.CreateNote("Mi nota");
        var attachments = NoteImageAttachments.FolderFor(note.FullPath)!;
        Directory.CreateDirectory(Path.Combine(attachments, "nested"));
        File.WriteAllBytes(Path.Combine(attachments, "nested", "image.png"), [1, 2, 3]);
        _vault.WriteContent(note, "![](Mi%20nota.attachments/nested/image.png)");
        var folder = Directory.CreateDirectory(Path.Combine(_vault.VaultPath, "Destino")).FullName;
        File.WriteAllText(Path.Combine(folder, "Mi nota.md"), "existente");
        var result = _vault.TransferNote(note, folder, cut);
        Assert.Contains("Mi%20nota%202.attachments/nested/image.png", _vault.ReadContent(result));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(NoteImageAttachments.FolderFor(result.FullPath)!, "nested", "image.png")));
        Assert.Equal(!cut, File.Exists(note.FullPath));
        Assert.Equal("existente", File.ReadAllText(Path.Combine(folder, "Mi nota.md")));
    }

    [Fact]
    public void CutToSameFolder_DoesNotCreateDuplicate()
    {
        var note = _vault.CreateNote("Nota");
        Assert.Same(note, _vault.TransferNote(note, _vault.VaultPath, true));
        Assert.Single(_vault.GetNotes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferAcrossEncryptionBoundary_ReencryptsNoteAndAttachments(bool cut)
    {
        var (origin, _) = _sessions.CreateEncrypted("Origen", "password-origin");
        var (destination, _) = _sessions.CreateEncrypted("Destino", "password-destination");
        var note = _vault.CreateNote("Nota", origin.RootPath);
        _vault.WriteContent(note, "secreto");
        var sourceAttachments = NoteImageAttachments.FolderFor(note.FullPath)!;
        Directory.CreateDirectory(Path.Combine(sourceAttachments, "nested"));
        _vault.WriteBinaryFile(Path.Combine(sourceAttachments, "nested", "image.png"), [4, 5, 6]);
        var result = _vault.TransferNote(note, destination.RootPath, cut);
        Assert.Equal("secreto", _vault.ReadContent(result));
        var image = Path.Combine(NoteImageAttachments.FolderFor(result.FullPath)!, "nested", "image.png");
        Assert.Equal(new byte[] { 4, 5, 6 }, _vault.ReadFileBytes(image));
        Assert.NotEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(image));
        Assert.DoesNotContain("secreto", Encoding.UTF8.GetString(File.ReadAllBytes(result.FullPath)));
        Assert.Equal(!cut, File.Exists(note.FullPath));
    }

    [Fact]
    public void LockedDestination_LeavesSourceAndNoPartialCopy()
    {
        var (session, _) = _sessions.CreateEncrypted("Privada", "password-private");
        var note = _vault.CreateNote("Nota");
        var attachments = NoteImageAttachments.FolderFor(note.FullPath)!;
        Directory.CreateDirectory(attachments);
        File.WriteAllText(Path.Combine(attachments, "a.png"), "image");
        _sessions.Lock(session);
        Assert.Throws<SessionLockedException>(() => _vault.TransferNote(note, session.RootPath, true));
        Assert.True(File.Exists(note.FullPath));
        Assert.False(File.Exists(Path.Combine(session.RootPath, "Nota.md")));
        Assert.False(Directory.Exists(Path.Combine(session.RootPath, "Nota.attachments")));
    }

    [Fact]
    public void CopyBinary_ReencryptsAndPreservesSource()
    {
        var (session, _) = _sessions.CreateEncrypted("Privada", "password-private");
        var source = Path.Combine(_vault.VaultPath, "file.bin");
        File.WriteAllBytes(source, [0, 1, 2, 255]);
        var encrypted = _vault.CopyFileToFolder(source, session.RootPath);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, _vault.ReadFileBytes(encrypted));
        var copy = _vault.CopyFileToFolder(encrypted, _vault.VaultPath);
        Assert.Equal("file 2.bin", Path.GetFileName(copy));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
