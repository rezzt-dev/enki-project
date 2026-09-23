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

        Assert.Equal(3, VaultService.CountFolderEntries(Path.Combine(_vaultPath, "carpeta")));
        Assert.Equal(0, VaultService.CountFolderEntries(Path.Combine(_vaultPath, "no-existe")));
    }
}
