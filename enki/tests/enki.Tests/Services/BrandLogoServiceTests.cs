using System;
using System.IO;
using System.Linq;
using System.Text;
using enki.Services;
using SkiaSharp;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="BrandLogoService"/> — el logo que el usuario elige en Ajustes › Apariencia:
/// que se guarde tal cual, sin reescalar ni recodificar (Enki es local, degradar la imagen no compra
/// nada), que la extensión salga del formato real y no del nombre, que solo quede un archivo por logo y
/// que un archivo que no es una imagen no rompa nada. Cómo se reduce para pintarla es cosa de
/// <c>BrandLogoImage</c>, que se prueba en <c>enki.UITests</c> porque necesita plataforma gráfica. Mismo
/// patrón que el resto de pruebas de servicios: cada una escribe en su propio directorio temporal
/// desechable, nunca en <c>%AppData%/Enki</c>.</summary>
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

    /// <summary>Imagen con detalle de alta frecuencia en todos los píxeles: el caso "foto", el que más
    /// perdería si se recodificara.</summary>
    private string WritePhoto(string fileName, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            var wave = (byte)(128 + 127 * Math.Sin(x / 3.0) * Math.Cos(y / 5.0));
            bitmap.SetPixel(x, y, new SKColor(wave, (byte)(255 - wave), (byte)(x % 256), byte.MaxValue));
        }
        return Write(fileName, bitmap, format, 92);
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

    /// <summary>El contrato central: lo que se guarda es el archivo elegido, byte a byte. Ni un píxel
    /// menos ni una recodificación por el camino.</summary>
    [Fact]
    public void Import_StoresTheChosenFileUntouched()
    {
        var service = CreateService();
        var source = WritePhoto("grande.png", 1200, 800);

        var result = service.Import(source);

        Assert.True(result.Success);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(service.LogoPath!));
        var info = Describe(service.LogoPath!);
        Assert.Equal(1200, info.Width);
        Assert.Equal(800, info.Height);
    }

    [Fact]
    public void Import_ReportsWhatWasStoredAndItsRealPixelSize()
    {
        var service = CreateService();
        var source = WritePhoto("grande.png", 1200, 800);

        var stored = service.Import(source).Logo;

        Assert.NotNull(stored);
        Assert.Equal(new FileInfo(source).Length, stored.Bytes);
        Assert.Equal(1200, stored.PixelWidth);
        Assert.Equal(800, stored.PixelHeight);
        Assert.Equal(stored, service.Describe());
    }

    [Fact]
    public void Describe_ReturnsNullWhenThereIsNoLogo()
    {
        Assert.Null(CreateService().Describe());
    }

    [Fact]
    public void Import_KeepsTransparencyBecauseNothingIsReencoded()
    {
        var service = CreateService();

        Assert.True(service.Import(WriteFlatLogo("con-alfa.png", 300, 300, transparentCorner: true)).Success);

        Assert.Equal(".png", Path.GetExtension(service.LogoPath));
        using var stored = SKBitmap.Decode(service.LogoPath);
        Assert.Equal(0, stored.GetPixel(10, 10).Alpha);
    }

    /// <summary>La extensión con la que se guarda sale de la cabecera, no del nombre: un JPEG llamado
    /// ".png" no puede acabar en <c>%AppData%</c> mintiendo sobre lo que lleva dentro.</summary>
    [Fact]
    public void Import_NamesTheFileAfterItsRealFormat()
    {
        var service = CreateService();

        Assert.True(service.Import(WritePhoto("disfrazada.png", 300, 300, SKEncodedImageFormat.Jpeg)).Success);

        Assert.Equal(".jpg", Path.GetExtension(service.LogoPath));
    }

    [Fact]
    public void Import_ReplacesThePreviousLogoInsteadOfLeavingTwoFiles()
    {
        var service = CreateService();

        Assert.True(service.Import(WritePhoto("foto.jpg", 600, 600, SKEncodedImageFormat.Jpeg)).Success);
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

    /// <summary>El diálogo puede navegar hasta la propia carpeta de Enki y volver a elegir el logo que ya
    /// está puesto: eso no puede acabar en un error ni, mucho menos, borrando el archivo.</summary>
    [Fact]
    public void Import_AcceptsTheStoredLogoItselfAsSource()
    {
        var service = CreateService();
        Assert.True(service.Import(WriteFlatLogo("plano.png", 64, 64, transparentCorner: false)).Success);
        var stored = service.LogoPath!;

        var result = service.Import(stored);

        Assert.True(result.Success);
        Assert.Equal(stored, service.LogoPath);
        Assert.True(File.Exists(stored));
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
