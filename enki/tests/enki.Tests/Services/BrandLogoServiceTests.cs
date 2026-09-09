using System;
using System.IO;
using System.Linq;
using System.Text;
using enki.Services;
using SkiaSharp;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="BrandLogoService"/> — el logo que el usuario elige en Ajustes › Apariencia:
/// que se optimice de verdad (se reduzca a <see cref="BrandLogoService.MaxDimensionPixels"/> px y ocupe
/// menos), que no pierda la transparencia por el camino, que solo quede un archivo por logo y que un
/// archivo que no es una imagen no rompa nada. Mismo patrón que el resto de pruebas de servicios: cada
/// una escribe en su propio directorio temporal desechable, nunca en <c>%AppData%/Enki</c>.</summary>
public class BrandLogoServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private BrandLogoService CreateService() => new(_storageDirectory);

    private string SourcePath(string fileName) => Path.Combine(_storageDirectory, fileName);

    /// <summary>Imagen con detalle de alta frecuencia en todos los píxeles: el caso "foto", donde PNG no
    /// tiene nada que comprimir y JPEG sí.</summary>
    private string WritePhoto(string fileName, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            var wave = (byte)(128 + 127 * Math.Sin(x / 3.0) * Math.Cos(y / 5.0));
            bitmap.SetPixel(x, y, new SKColor(wave, (byte)(255 - wave), (byte)(x % 256), byte.MaxValue));
        }
        return Write(fileName, bitmap, SKEncodedImageFormat.Png, 100);
    }

    /// <summary>Logo plano de cuatro colores, con o sin un cuadrante transparente.</summary>
    private string WriteFlatLogo(string fileName, int width, int height, bool transparentCorner)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            var left = x < width / 2;
            var top = y < height / 2;
            var color = (left, top) switch
            {
                (true, true) when transparentCorner => SKColors.Transparent,
                (true, true) => SKColors.Red,
                (false, true) => SKColors.Green,
                (true, false) => SKColors.Blue,
                _ => SKColors.Black
            };
            bitmap.SetPixel(x, y, color);
        }
        return Write(fileName, bitmap, SKEncodedImageFormat.Png, 100);
    }

    private string Write(string fileName, SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        var path = SourcePath(fileName);
        Directory.CreateDirectory(_storageDirectory);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static SKImageInfo Describe(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        return bitmap.Info;
    }

    [Fact]
    public void Import_ScalesAnOversizedImageDownToTheMaximumSide()
    {
        var service = CreateService();

        var result = service.Import(WritePhoto("grande.png", 1200, 800));

        Assert.True(result.Success);
        var info = Describe(service.LogoPath!);
        Assert.Equal(BrandLogoService.MaxDimensionPixels, info.Width);
        // 800 × (256 / 1200), redondeado: la proporción original se mantiene.
        Assert.Equal(171, info.Height);
    }

    [Fact]
    public void Import_LeavesAnImageThatAlreadyFitsAtItsOwnSize()
    {
        var service = CreateService();

        Assert.True(service.Import(WriteFlatLogo("pequeno.png", 64, 64, transparentCorner: false)).Success);

        var info = Describe(service.LogoPath!);
        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);
    }

    [Fact]
    public void Import_StoresFewerBytesThanTheOriginal()
    {
        var service = CreateService();

        var result = service.Import(WritePhoto("grande.png", 1200, 800));

        Assert.True(result.StoredBytes < result.OriginalBytes);
        Assert.Equal(result.StoredBytes, new FileInfo(service.LogoPath!).Length);
    }

    [Fact]
    public void Import_KeepsTransparencyByStoringAPng()
    {
        var service = CreateService();

        Assert.True(service.Import(WriteFlatLogo("con-alfa.png", 300, 300, transparentCorner: true)).Success);

        Assert.Equal(".png", Path.GetExtension(service.LogoPath));
        using var stored = SKBitmap.Decode(service.LogoPath);
        Assert.Equal(0, stored.GetPixel(10, 10).Alpha);
    }

    /// <summary>Una foto opaca solo se guarda como JPEG porque ahí el ahorro es de otro orden de magnitud;
    /// un logo plano, donde los dos candidatos rondan el mismo tamaño, se queda en el PNG sin pérdida.</summary>
    [Fact]
    public void Import_OnlyGivesUpLosslessPngWhenJpegSavesRealSpace()
    {
        var photoService = CreateService();
        Assert.True(photoService.Import(WritePhoto("foto.png", 600, 600)).Success);
        Assert.Equal(".jpg", Path.GetExtension(photoService.LogoPath));

        var logoService = CreateService();
        Assert.True(logoService.Import(WriteFlatLogo("plano.png", 600, 600, transparentCorner: false)).Success);
        Assert.Equal(".png", Path.GetExtension(logoService.LogoPath));
    }

    [Fact]
    public void Import_ReplacesThePreviousLogoInsteadOfLeavingTwoFiles()
    {
        var service = CreateService();

        Assert.True(service.Import(WritePhoto("foto.png", 600, 600)).Success);
        Assert.Equal(".jpg", Path.GetExtension(service.LogoPath));

        Assert.True(service.Import(WriteFlatLogo("plano.png", 600, 600, transparentCorner: true)).Success);
        Assert.Equal(".png", Path.GetExtension(service.LogoPath));

        var stored = Directory.EnumerateFiles(_storageDirectory, "brand-logo.*").ToList();
        Assert.Single(stored);
    }

    [Fact]
    public void Import_RejectsAFileThatIsNotAnImageWithoutTouchingTheCurrentLogo()
    {
        var service = CreateService();
        Assert.True(service.Import(WriteFlatLogo("plano.png", 64, 64, transparentCorner: false)).Success);
        var previous = File.ReadAllBytes(service.LogoPath!);

        var notAnImage = SourcePath("apuntes.txt");
        File.WriteAllText(notAnImage, "esto no es una imagen", Encoding.UTF8);

        var result = service.Import(notAnImage);

        Assert.False(result.Success);
        Assert.Equal(BrandLogoImportFailure.UnsupportedFormat, result.Failure);
        Assert.Equal(previous, File.ReadAllBytes(service.LogoPath!));
    }

    [Fact]
    public void Import_RejectsAFileThatIsNoLongerThere()
    {
        var service = CreateService();

        var result = service.Import(SourcePath("se-fue.png"));

        Assert.False(result.Success);
        Assert.Equal(BrandLogoImportFailure.FileNotFound, result.Failure);
        Assert.False(service.HasLogo);
    }

    [Fact]
    public void Import_RejectsASourceOverTheSizeCeiling()
    {
        var service = CreateService();
        Directory.CreateDirectory(_storageDirectory);
        var huge = SourcePath("enorme.png");
        // Archivo disperso: interesa su tamaño declarado, no escribir de verdad 64 MB en el disco del
        // desarrollador para comprobar un techo.
        using (var stream = File.Create(huge)) stream.SetLength(BrandLogoService.MaxSourceBytes + 1);

        var result = service.Import(huge);

        Assert.False(result.Success);
        Assert.Equal(BrandLogoImportFailure.TooLarge, result.Failure);
    }

    [Fact]
    public void ReadLogoBytes_ReturnsWhatIsOnDiskAndNullWhenThereIsNoLogo()
    {
        var service = CreateService();
        Assert.Null(service.ReadLogoBytes());

        Assert.True(service.Import(WriteFlatLogo("plano.png", 64, 64, transparentCorner: false)).Success);

        Assert.Equal(File.ReadAllBytes(service.LogoPath!), service.ReadLogoBytes());
    }

    [Fact]
    public void Remove_DeletesTheLogoAndOnlyReportsAChangeWhenThereWasOne()
    {
        var service = CreateService();
        Assert.False(service.Remove());

        Assert.True(service.Import(WriteFlatLogo("plano.png", 64, 64, transparentCorner: false)).Success);

        Assert.True(service.Remove());
        Assert.False(service.HasLogo);
        Assert.Null(service.LogoPath);
        Assert.False(service.Remove());
    }

    [Fact]
    public void LogoChanged_FiresOnceForEachRealChange()
    {
        var service = CreateService();
        var changes = 0;
        service.LogoChanged += () => changes++;

        service.Import(WriteFlatLogo("plano.png", 64, 64, transparentCorner: false));
        service.Import(SourcePath("no-existe.png"));
        service.Remove();
        service.Remove();

        Assert.Equal(2, changes);
    }
}
