using System.Text;
using enki.ViewModels;
using SkiaSharp;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre la parte de <see cref="BrandLogoImage"/> que se puede comprobar sin plataforma gráfica:
/// a qué tamaño se le pide la imagen a Skia antes de pintarla. El bitmap en sí no se decodifica aquí —el
/// host headless de <c>enki.UITests</c> no decodifica de verdad, así que probarlo allí tampoco diría
/// nada—, pero la aritmética es justo lo que arregla el logo dentado: reducir de una vez hasta el tamaño
/// de pintado en vez de dejar que el <c>Image</c> se quede con uno de cada N píxeles.</summary>
public class BrandLogoImageTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>El lado que manda es el <em>menor</em>, no el mayor: con la forma circular la imagen se
    /// recorta al llenar el cuadro, así que es el lado corto el que tiene que dar la talla.</summary>
    [Fact]
    public void ScaledDecodeWidth_LeavesTheShortestSideAtTheDisplaySize()
    {
        // 1000 × (128 / 600) = 213,33 → la proporción original se mantiene.
        Assert.Equal(213, BrandLogoImage.ScaledDecodeWidth(1000, 600, 128));
        Assert.Equal(128, BrandLogoImage.ScaledDecodeWidth(600, 1000, 128));
        Assert.Equal(128, BrandLogoImage.ScaledDecodeWidth(4000, 4000, 128));
    }

    /// <summary>Una imagen que ya cabe se decodifica tal cual (<c>0</c>): ampliarla solo la emborronaría y
    /// ocuparía memoria de más.</summary>
    [Fact]
    public void ScaledDecodeWidth_DoesNotUpscaleAnImageThatAlreadyFits()
    {
        Assert.Equal(0, BrandLogoImage.ScaledDecodeWidth(64, 48, 128));
        Assert.Equal(0, BrandLogoImage.ScaledDecodeWidth(4000, 128, 128));
    }

    [Fact]
    public void ReadPixelSize_ReadsTheHeaderAndGivesUpOnWhatIsNotAnImage()
    {
        Assert.Equal((320, 240), BrandLogoImage.ReadPixelSize(Png(320, 240)));
        Assert.Equal((0, 0), BrandLogoImage.ReadPixelSize(Encoding.UTF8.GetBytes("esto no es una imagen")));
        Assert.Equal((0, 0), BrandLogoImage.ReadPixelSize([]));
    }
}
