using System;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Las dos enumeraciones de <see cref="VaultService"/> que añade la búsqueda y la rejilla de Archivos:
/// la recursiva y el contador de una carpeta. Las dos tienen que ver exactamente lo mismo que la navegación —
/// nada de notas, ni de archivos ocultos, ni de lo que hay en <c>.trash</c> o <c>.sessions</c>.</summary>
public class VaultServiceFilesTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly VaultService _vault;
    private readonly string _vaultPath;

    public VaultServiceFilesTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
        _vault = new VaultService(new SettingsService(_storageDirectory));
        _vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_vaultPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
    }

    [Fact]
    public void GetFilesRecursive_FindsFilesAtAnyDepth_ButNotNotesHiddenFilesOrHiddenFolders()
    {
        Touch("raiz.png");
        Touch("a/b/c/profundo.pdf");
        Touch("a/nota.md");
        Touch("a/.oculto.txt");
        Touch(".trash/borrado.png");
        Touch("Nota.attachments/imagen.png");
        Touch("a/Nota.ATTACHMENTS/sub/imagen.png");

        var names = _vault.GetFilesRecursive(_vaultPath).Select(f => f.FileName).OrderBy(n => n).ToList();

        Assert.Equal(["profundo.pdf", "raiz.png"], names);
    }

    [Fact]
    public void CountFolderEntries_CountsVisibleSubfoldersAndNonNoteFiles()
    {
        Touch("carpeta/uno.png");
        Touch("carpeta/dos.txt");
        Touch("carpeta/nota.md");
        Touch("carpeta/.oculto");
        Touch("carpeta/sub/tres.pdf");
        Directory.CreateDirectory(Path.Combine(_vaultPath, "carpeta", ".privada"));
        Touch("carpeta/Nota.attachments/imagen.png");
        Touch("carpeta/Otra.ATTACHMENTS/sub/imagen.png");

        Assert.Equal(3, VaultService.CountFolderEntries(Path.Combine(_vaultPath, "carpeta")));
        Assert.Equal(0, VaultService.CountFolderEntries(Path.Combine(_vaultPath, "no-existe")));
    }

    [Fact]
    public void CountFilesRecursive_MatchesFileBrowser_AndHandlesMissingRoot()
    {
        Touch("raiz.pdf");
        Touch("a/b/imagen.png");
        Touch("a/nota.md");
        Touch("a/otra.MD");
        Touch("a/.oculto.json");
        Touch(".tasks/reminders.json");
        Touch(".sessions/sesion/archivo.pdf");
        Touch(".trash/borrado.pdf");
        Touch("Nota.attachments/imagen.png");
        Touch("a/Nota.ATTACHMENTS/sub/imagen.png");

        Assert.Equal(2, VaultService.CountFilesRecursive(_vaultPath));
        Assert.Equal(_vault.GetFilesRecursive(_vaultPath).Count, VaultService.CountFilesRecursive(_vaultPath));
        Assert.Equal(0, VaultService.CountFilesRecursive(Path.Combine(_vaultPath, "no-existe")));
        Assert.Equal(0, VaultService.CountFilesRecursive(string.Empty));
    }

    [Fact]
    public void GetFolderTree_HidesAttachmentFoldersAtAnyDepth_WithoutDeletingTheirContents()
    {
        Touch("Nota.attachments/sub/imagen.png");
        Touch("visible/Nota.ATTACHMENTS/sub/imagen.png");
        Touch("visible/normal/archivo.pdf");
        Touch("visible/Nota.attachments.backup/archivo.pdf");

        var folder = Assert.Single(_vault.GetFolderTree());

        Assert.Equal(Path.Combine(_vaultPath, "visible"), folder.FullPath);
        Assert.Equal(new[] { "normal", "Nota.attachments.backup" },
            folder.Children.Select(child => Path.GetFileName(child.FullPath)));
        Assert.True(File.Exists(Path.Combine(_vaultPath, "Nota.attachments/sub/imagen.png")));
        Assert.True(File.Exists(Path.Combine(_vaultPath, "visible/Nota.ATTACHMENTS/sub/imagen.png")));
    }

    [Fact]
    public void RecursiveNotes_ExcludeAttachmentContentsFromListingsAndCounts()
    {
        Touch("visible/nota.md");
        Touch("Nota.attachments/oculta.md");
        Touch("visible/Nota.ATTACHMENTS/sub/oculta.md");

        var note = Assert.Single(_vault.GetAllNotesRecursive(_vaultPath));

        Assert.Equal(Path.Combine(_vaultPath, "visible/nota.md"), note.FullPath);
        Assert.Equal(note.FullPath, Assert.Single(_vault.GetAllNotesRecursiveWithContent(_vaultPath)).Note.FullPath);
        Assert.Equal(1, VaultService.CountNotesRecursive(_vaultPath));
    }
}
