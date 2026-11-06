using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class NoteAttachmentServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-attachments-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;

    public NoteAttachmentServiceTests()
    {
        _vault = new VaultService(new SettingsService(_directory));
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Source(string name, string content = "contenido")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Import_UsesRelativeEscapedReference_AndDoesNotOverwrite()
    {
        var note = _vault.CreateNote("Proyecto");
        var service = new NoteAttachmentService(_vault);
        var source = Source("Informe (final).pdf");
        var first = service.SaveFromFile(note, source);
        var second = service.SaveFromFile(note, source);
        Assert.Equal("Proyecto.attachments/Informe-%28final%29.pdf", first);
        Assert.NotEqual(first, second);
        Assert.Equal("contenido", File.ReadAllText(Path.Combine(_vault.VaultPath, Uri.UnescapeDataString(first))));
    }

    [Fact]
    public void Import_InternalFile_ReusesOriginal()
    {
        var note = _vault.CreateNote("Proyecto");
        var path = Path.Combine(_vault.VaultPath, "audio.mp3");
        File.WriteAllText(path, "audio");
        Assert.Equal("audio.mp3", new NoteAttachmentService(_vault).SaveFromFile(note, path));
        Assert.False(Directory.Exists(NoteImageAttachments.FolderFor(note.FullPath)));
    }

    [Theory]
    [InlineData("script.sh")]
    [InlineData("programa.exe")]
    [InlineData("atajo.desktop")]
    public void Import_Executable_IsRejectedBeforeWriting(string fileName)
    {
        var note = _vault.CreateNote("Proyecto");
        Assert.Throws<InvalidDataException>(() => new NoteAttachmentService(_vault).SaveFromFile(note, Source(fileName)));
        Assert.False(Directory.Exists(NoteImageAttachments.FolderFor(note.FullPath)));
    }

    [Fact]
    public void Import_EmptyAndOversized_AreRejectedBeforeWriting()
    {
        var note = _vault.CreateNote("Proyecto");
        var source = Source("vacio.pdf", "");
        var service = new NoteAttachmentService(_vault);
        Assert.Throws<InvalidDataException>(() => service.SaveFromFile(note, source));
        using (var stream = File.Create(source)) stream.SetLength(NoteAttachmentService.MaxSourceBytes + 1);
        Assert.Throws<InvalidDataException>(() => service.SaveFromFile(note, source));
        Assert.False(Directory.Exists(NoteImageAttachments.FolderFor(note.FullPath)));
    }

    [Fact]
    public void Import_EncryptedSession_EncryptsAttachmentAndRejectsLockedSource()
    {
        var sessions = new SessionService(_vault);
        var (session, _) = sessions.CreateEncrypted("Privado", "clave-segura");
        var note = _vault.CreateNote("Informe", session.RootPath);
        var service = new NoteAttachmentService(_vault);
        var reference = service.SaveFromFile(note, Source("datos.pdf"));
        var path = Path.Combine(session.RootPath, Uri.UnescapeDataString(reference));
        Assert.NotEqual(File.ReadAllBytes(Source("datos.pdf")), File.ReadAllBytes(path));
        Assert.Equal("contenido", System.Text.Encoding.UTF8.GetString(_vault.ReadFileBytes(path)));
        sessions.Lock(session);
        var publicNote = _vault.CreateNote("Publica");
        Assert.Throws<SessionLockedException>(() => service.SaveFromFile(publicNote, path));
        Assert.Throws<SessionLockedException>(() => service.SaveFromFile(note, Source("otro.pdf")));
    }

    [Fact]
    public void Import_ReservedCharactersInName_RoundTripsWithoutChangingTheTarget()
    {
        var note = _vault.CreateNote("Proyecto");
        var source = Source("Informe#1[final].pdf");
        var reference = new NoteAttachmentService(_vault).SaveFromFile(note, source);
        Assert.Contains("%23", reference);
        Assert.Contains("%5B", reference);
        var resolution = ExternalLinkPolicy.Resolve(reference, Path.GetDirectoryName(note.FullPath), _vault.VaultPath);
        Assert.NotNull(resolution.Target);
        Assert.Equal("contenido", File.ReadAllText(resolution.Target!));
    }

    [Fact]
    public void Import_WithoutExtension_PreservesFileName()
    {
        var note = _vault.CreateNote("Proyecto");
        var reference = new NoteAttachmentService(_vault).SaveFromFile(note, Source("LICENSE"));
        Assert.Equal("Proyecto.attachments/LICENSE", reference);
    }

    [Theory]
    [InlineData("file.pdf", true)]
    [InlineData("file.mp4", true)]
    [InlineData("file.mp3", true)]
    [InlineData("file.zip", true)]
    [InlineData("photo.png", false)]
    [InlineData("Note.md", false)]
    [InlineData("Note", false)]
    public void Classification_DistinguishesNotesImagesAndAttachments(string path, bool expected) =>
        Assert.Equal(expected, NoteAttachmentService.IsAttachmentTarget(path));
}
