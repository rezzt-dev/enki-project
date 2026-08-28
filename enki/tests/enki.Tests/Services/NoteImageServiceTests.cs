using System;
using System.IO;
using System.Linq;
using System.Text;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="NoteImageService"/> — dónde acaba en disco una imagen pegada o arrastrada, y
/// con qué ruta se la referencia desde la nota. Mismo patrón que el resto de pruebas de servicios: cada
/// una monta su propio vault en un directorio temporal desechable.</summary>
public class NoteImageServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public NoteImageServiceTests() => _vaultPath = Path.Combine(_storageDirectory, "vault");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    private static readonly byte[] SampleImage = Encoding.UTF8.GetBytes("no es un PNG de verdad, pero son bytes");

    [Fact]
    public void SaveBytes_WritesIntoTheNotesOwnAttachmentsFolder()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);

        var reference = service.SaveBytes(note, SampleImage, ".png");

        Assert.NotNull(reference);
        Assert.StartsWith("Proyecto.attachments/", reference, StringComparison.Ordinal);

        var folder = Path.Combine(_vaultPath, "Proyecto.attachments");
        var written = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal(SampleImage, File.ReadAllBytes(written));
    }

    [Fact]
    public void SaveBytes_TwiceInTheSameSecond_DoesNotOverwriteTheFirstImage()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);

        var first = service.SaveBytes(note, SampleImage, ".png");
        var second = service.SaveBytes(note, SampleImage, ".png");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_vaultPath, "Proyecto.attachments")).Length);
    }

    [Fact]
    public void SaveFromFile_OutsideTheVault_CopiesItInWithASanitizedName()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);

        var source = Path.Combine(_storageDirectory, "Captura de pantalla.png");
        File.WriteAllBytes(source, SampleImage);

        var reference = service.SaveFromFile(note, source);

        Assert.Equal("Proyecto.attachments/Captura-de-pantalla.png", reference);
        Assert.True(File.Exists(Path.Combine(_vaultPath, "Proyecto.attachments", "Captura-de-pantalla.png")));
    }

    [Fact]
    public void SaveFromFile_AlreadyInsideTheVault_LinksItWhereItIsInsteadOfDuplicatingIt()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);

        var existing = Path.Combine(_vaultPath, "adjuntos");
        Directory.CreateDirectory(existing);
        var source = Path.Combine(existing, "diagrama.png");
        File.WriteAllBytes(source, SampleImage);

        var reference = service.SaveFromFile(note, source);

        Assert.Equal("adjuntos/diagrama.png", reference);
        Assert.False(Directory.Exists(Path.Combine(_vaultPath, "Proyecto.attachments")));
    }

    [Fact]
    public void SaveFromFile_MissingSource_ReturnsNull()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);

        Assert.Null(service.SaveFromFile(note, Path.Combine(_storageDirectory, "no-existe.png")));
    }

    [Fact]
    public void RenameNote_TakesTheAttachmentsFolderWithIt()
    {
        // Sin esto, cada ![](Proyecto.attachments/…) de la nota apuntaría tras el renombrado a una carpeta
        // que ya no se llama así, y las imágenes desaparecerían del editor sin explicación.
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);
        var reference = service.SaveBytes(note, SampleImage, ".png");
        var fileName = reference!.Split('/').Last();

        vault.RenameNote(note, "Proyecto renombrado");

        Assert.False(Directory.Exists(Path.Combine(_vaultPath, "Proyecto.attachments")));
        Assert.True(File.Exists(Path.Combine(_vaultPath, "Proyecto renombrado.attachments", fileName)));
    }

    [Fact]
    public void MoveNote_TakesTheAttachmentsFolderWithIt()
    {
        var vault = CreateVault();
        var note = vault.CreateNote("Proyecto");
        var service = new NoteImageService(vault);
        var reference = service.SaveBytes(note, SampleImage, ".png");
        var fileName = reference!.Split('/').Last();

        var destination = Path.Combine(_vaultPath, "archivo");
        Directory.CreateDirectory(destination);
        vault.MoveNote(note, destination);

        Assert.False(Directory.Exists(Path.Combine(_vaultPath, "Proyecto.attachments")));
        Assert.True(File.Exists(Path.Combine(destination, "Proyecto.attachments", fileName)));
    }
}
