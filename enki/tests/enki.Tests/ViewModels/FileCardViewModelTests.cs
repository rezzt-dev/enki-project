using System;
using System.IO;
using System.Linq;
using System.Text;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Lo que la previsualización de Archivos carga de una tarjeta y cómo decide qué visor enseñar
/// (docs/agent-docs/seccion-archivos.md §6): el visor de código no debe volcar binarios, tiene que respetar la
/// codificación del archivo y truncar sin inventarse texto, y lo que no tiene visor va al panel de información.</summary>
public class FileCardViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly VaultService _vault;
    private readonly string _vaultPath;

    public FileCardViewModelTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
        _vault = new VaultService(new SettingsService(_storageDirectory));
        _vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private FileCardViewModel CardFor(string fileName, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_vaultPath, fileName), bytes);
        return new FileCardViewModel(_vault, _vault.GetFiles(_vaultPath).Single(f => f.FileName == fileName));
    }

    [Fact]
    public void LooksBinary_DetectsNullBytes_ButNotUnicodeTextWithAByteOrderMark()
    {
        Assert.True(FileCardViewModel.LooksBinary([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]));
        Assert.False(FileCardViewModel.LooksBinary(Encoding.UTF8.GetBytes("a,b,c\n1,2,3")));
        Assert.False(FileCardViewModel.LooksBinary(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hola")).ToArray()));
    }

    [Fact]
    public void ABinaryFileWithATextExtension_GoesToTheInfoPanel_InsteadOfTheCodeViewer()
    {
        var card = CardFor("datos.csv", [0x01, 0x00, 0x02, 0x00, 0xFF]);

        card.EnsureCodeContentLoaded();

        Assert.False(card.IsCodeViewer);
        Assert.True(card.ShowsInfoPanel);
        Assert.Equal(LocalizationService.Instance["Files_BinaryPreviewHint"], card.PreviewHint);
    }

    [Fact]
    public void AUtf16TextFile_IsDecodedWithItsOwnEncoding()
    {
        var card = CardFor("utf16.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("¡hola, año!")).ToArray());

        card.EnsureCodeContentLoaded();

        Assert.True(card.IsCodeViewer);
        Assert.Equal("¡hola, año!", card.CodeContent);
        Assert.Equal(".txt", card.CodeLanguage);
    }

    /// <summary>Truncar ya no añade una línea inventada al final del texto (se copiaba con el contenido): el
    /// aviso lo da la previsualización por su cuenta a partir de <see cref="FileCardViewModel.IsCodeTruncated"/>.</summary>
    [Fact]
    public void AVeryLongTextFile_IsTruncatedWithoutAddingTextOfItsOwn()
    {
        var card = CardFor("registro.log", Encoding.UTF8.GetBytes(new string('x', 600_000)));

        card.EnsureCodeContentLoaded();

        Assert.True(card.IsCodeTruncated);
        Assert.Equal(512_000, card.CodeContent?.Length);
        Assert.True(card.CodeContent!.All(c => c == 'x'));
    }

    [Fact]
    public void AudioAndUnknownFiles_UseTheInfoPanel_WithTheirOwnPrimaryAction()
    {
        var audio = CardFor("cancion.mp3", [1, 2, 3]);
        var unknown = CardFor("modelo.blend", [1, 2, 3]);

        Assert.True(audio.ShowsInfoPanel);
        Assert.True(audio.IsPlayable);
        Assert.Equal(LocalizationService.Instance["Files_AudioPreviewHint"], audio.PreviewHint);

        Assert.True(unknown.ShowsInfoPanel);
        Assert.False(unknown.IsPlayable);
        Assert.Equal("BLEND", unknown.ExtensionLabel);
    }

    [Fact]
    public void UpdateFile_ForgetsTheLoadedText_SoItIsReadAgainFromTheNewFile()
    {
        var card = CardFor("uno.txt", Encoding.UTF8.GetBytes("uno"));
        card.EnsureCodeContentLoaded();
        File.WriteAllText(Path.Combine(_vaultPath, "dos.txt"), "dos");

        card.UpdateFile(_vault.GetFiles(_vaultPath).Single(f => f.FileName == "dos.txt"));
        Assert.Null(card.CodeContent);

        card.EnsureCodeContentLoaded();
        Assert.Equal("dos", card.CodeContent);
    }
}
