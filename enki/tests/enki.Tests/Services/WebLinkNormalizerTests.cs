using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="WebLinkNormalizer"/>, la puerta por la que pasa todo enlace web que se añade a
/// una tarjeta Kanban antes de guardarse y, más tarde, entregarse al navegador del sistema.</summary>
public class WebLinkNormalizerTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("  http://example.com/ruta?q=1#ancla  ", "http://example.com/ruta?q=1#ancla")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("www.ejemplo.es/año", "https://www.ejemplo.es/a%C3%B1o")]
    [InlineData("github.com:443/enki", "https://github.com/enki")]
    [InlineData("HTTP://localhost:8080/panel", "http://localhost:8080/panel")]
    public void TryNormalize_AcceptsWebAddresses(string input, string expected)
    {
        Assert.Equal(WebLinkError.None, WebLinkNormalizer.TryNormalize(input, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:alguien@example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("hola mundo.com")]
    [InlineData("notas")]
    [InlineData("localhost:3000")]
    [InlineData("https://")]
    [InlineData(@"\\servidor\recurso")]
    public void TryNormalize_RejectsWhatIsNotAWebPage(string input)
    {
        Assert.Equal(WebLinkError.Invalid, WebLinkNormalizer.TryNormalize(input, out var url));
        Assert.Equal(string.Empty, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_ReportsEmptyInput(string? input) =>
        Assert.Equal(WebLinkError.Empty, WebLinkNormalizer.TryNormalize(input, out _));

    [Fact]
    public void TryNormalize_RejectsOverlongInput() =>
        Assert.Equal(WebLinkError.Invalid,
            WebLinkNormalizer.TryNormalize("https://example.com/" + new string('a', WebLinkNormalizer.MaxLength), out _));

    [Fact]
    public void EveryNormalizedLink_IsAcceptedByTheOpenerPolicy()
    {
        foreach (var input in new[] { "example.com", "http://a.b/c", "www.ejemplo.es" })
        {
            WebLinkNormalizer.TryNormalize(input, out var url);
            Assert.True(ExternalLinkPolicy.IsSafeUrl(url), url);
        }
    }

    [Theory]
    [InlineData("https://www.example.com/", "example.com")]
    [InlineData("https://example.com/docs/guia/", "example.com/docs/guia")]
    [InlineData("http://localhost:8080/panel?x=1", "localhost:8080/panel?x=1")]
    [InlineData("https://www.ejemplo.es/a%C3%B1o", "ejemplo.es/año")]
    [InlineData("no es una url", "no es una url")]
    public void Describe_ShowsHostAndPathWithoutNoise(string url, string expected) =>
        Assert.Equal(expected, WebLinkNormalizer.Describe(url));
}
