using enki.Models;
using Xunit;

namespace enki.Tests.Models;

public class VaultFileTests
{
    [Theory]
    [InlineData(".png", VaultFileKind.Image)]
    [InlineData(".PNG", VaultFileKind.Image)]
    [InlineData(".jpg", VaultFileKind.Image)]
    [InlineData(".jpeg", VaultFileKind.Image)]
    [InlineData(".gif", VaultFileKind.Image)]
    [InlineData(".bmp", VaultFileKind.Image)]
    [InlineData(".webp", VaultFileKind.Image)]
    [InlineData(".pdf", VaultFileKind.Pdf)]
    [InlineData(".PDF", VaultFileKind.Pdf)]
    [InlineData(".svg", VaultFileKind.Other)]
    [InlineData(".docx", VaultFileKind.Other)]
    [InlineData(".zip", VaultFileKind.Other)]
    [InlineData("", VaultFileKind.Other)]
    public void KindFromExtension_MapsByExtensionCaseInsensitively(string extension, VaultFileKind expected)
    {
        Assert.Equal(expected, VaultFile.KindFromExtension(extension));
    }

    [Fact]
    public void KindFromExtension_AcceptsExtensionWithOrWithoutLeadingDot()
    {
        Assert.Equal(VaultFileKind.Image, VaultFile.KindFromExtension("png"));
        Assert.Equal(VaultFileKind.Image, VaultFile.KindFromExtension(".png"));
    }
}
