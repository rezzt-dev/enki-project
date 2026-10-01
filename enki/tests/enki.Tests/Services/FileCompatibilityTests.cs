using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

public class FileCompatibilityTests
{
    [Theory]
    [InlineData("Dockerfile", VaultFileKind.Code, ".dockerfile")]
    [InlineData("Dockerfile.production", VaultFileKind.Code, ".dockerfile")]
    [InlineData("Makefile", VaultFileKind.Code, ".makefile")]
    [InlineData("CMakeLists.txt", VaultFileKind.Code, ".cmake")]
    [InlineData("component.axaml", VaultFileKind.Code, ".xml")]
    [InlineData("app.mts", VaultFileKind.Code, ".ts")]
    [InlineData("module.cjs", VaultFileKind.Code, ".js")]
    [InlineData("README", VaultFileKind.Code, ".txt")]
    public void CodeNames_SelectTheRightKindAndGrammar(string name, VaultFileKind kind, string extension)
    {
        Assert.Equal(kind, VaultFile.KindFromPath(name));
        Assert.Equal(extension, CodePreviewService.LanguageExtension(name));
    }

    [Theory]
    [InlineData("photo.HEIC", VaultFileKind.Image)]
    [InlineData("photo.avif", VaultFileKind.Image)]
    [InlineData("scan.tiff", VaultFileKind.Image)]
    [InlineData("movie.m2ts", VaultFileKind.Video)]
    [InlineData("movie.mpg", VaultFileKind.Video)]
    [InlineData("sound.aiff", VaultFileKind.Audio)]
    public void AdditionalFormats_AreRecognized(string path, VaultFileKind expected) =>
        Assert.Equal(expected, VaultFile.KindFromPath(path));

    [Theory]
    [InlineData(-1, "00:00")]
    [InlineData(65_000, "01:05")]
    [InlineData(3_665_000, "1:01:05")]
    public void PlaybackTime_HandlesUnknownAndLongDurations(long milliseconds, string expected) =>
        Assert.Equal(expected, MediaPreviewViewModel.FormatTime(milliseconds));
}
