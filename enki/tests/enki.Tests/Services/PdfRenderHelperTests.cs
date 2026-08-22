using System;
using System.IO;
using enki.ViewModels;
using SkiaSharp;
using Xunit;

namespace enki.Tests.Services;

public class PdfRenderHelperTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public PdfRenderHelperTests()
    {
        Directory.CreateDirectory(_storageDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private string CreateTwoPagePdf()
    {
        var path = Path.Combine(_storageDirectory, "input.pdf");
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);
        for (var i = 0; i < 2; i++)
        {
            using var canvas = document.BeginPage(400, 300);
            using var font = new SKFont(SKTypeface.Default, 20);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawText($"Page {i + 1}", 20, 40, SKTextAlign.Left, font, paint);
            document.EndPage();
        }
        document.Close();
        return path;
    }

    [Fact]
    public void GetPageCount_TwoPagePdf_ReturnsTwo()
    {
        var path = CreateTwoPagePdf();
        Assert.Equal(2, PdfRenderHelper.GetPageCount(path));
    }

}
