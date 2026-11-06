using System;
using enki.Services;
using ImageMagick;
using Xunit;

namespace enki.Tests.Services;

public class ImagePreviewServiceTests
{
    [Theory]
    [InlineData(MagickFormat.Tiff, "tiff")]
    [InlineData(MagickFormat.Tga, "tga")]
    [InlineData(MagickFormat.Avif, "avif")]
    [InlineData(MagickFormat.WebP, "webp")]
    [InlineData(MagickFormat.Png, "png")]
    [InlineData(MagickFormat.Qoi, "qoi")]
    [InlineData(MagickFormat.Jxl, "jxl")]
    public void ImageDecoder_ActuallyDecodesTheFormatAndBoundsBothDimensions(MagickFormat format, string extension)
    {
        using var original = new MagickImage(MagickColors.Red, 40, 200);
        var bytes = original.ToByteArray(format);
        using var bitmap = new MagickImage(ImagePreviewService.DecodePng(bytes, 80, extension, out var size));
        Assert.Equal(40, size.Width);
        Assert.Equal(200, size.Height);
        Assert.Equal(16, (int)bitmap.Width);
        Assert.Equal(80, (int)bitmap.Height);
    }

    [Fact]
    public void Heic_DecodesAnHevcFixtureWithoutRequiringAnEncoder()
    {
        // Rectángulo rojo 40 × 200 generado con heif-enc; Magick.NET decodifica HEVC pero no lo codifica.
        var bytes = Convert.FromBase64String("AAAAHGZ0eXBoZWljAAAAAG1pZjFoZWljbWlhZgAAAX5tZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAAACJpbG9jAAAAAERAAAEAAQAAAAABogABAAAAAAAAAC4AAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABodmMxAAAAAA5waXRtAAAAAAABAAAA/mlwcnAAAADeaXBjbwAAAHdodmNDAQNwAAAAAAAAAAAAHvAA/P34+AAADwNgAAEAGEABDAH//wNwAAADAJAAAAMAAAMAHroCQGEAAQArQgEBA3AAAAMAkAAAAwAAAwAeoCCAyWW6uSmubgIaDAgAAAMAyAAAAwAIQGIAAQAGRAHBc8CJAAAAE2NvbHJuY2x4AAEADQAGgAAAABRpc3BlAAAAAAAAAEAAAADIAAAAKGNsYXAAAAAoAAAAAQAAAMgAAAAB////6AAAAAIAAAAAAAAAAgAAABBwaXhpAAAAAAMICAgAAAAYaXBtYQAAAAAAAAABAAEFgQIDBYQAAAA2bWRhdAAAACooAa8TgPgQ4af//E0o//p1zw/TzHvzopCAT867ZT6kqMZVYEwAAAMAA+I=");
        using var image = new MagickImage(ImagePreviewService.DecodePng(bytes, 80, "heic", out var size));
        Assert.Equal(40, size.Width);
        Assert.Equal(200, size.Height);
        Assert.Equal(16u, image.Width);
        Assert.Equal(80u, image.Height);
    }
}
