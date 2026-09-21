using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="SemanticVersion"/> — la comparación de versiones del aviso de versión nueva
/// (T9). El caso que motiva no usar <c>System.Version</c> es el de las <em>prereleases</em>: para .NET
/// <c>1.0.0-beta</c> no existe, y aquí tiene que quedar por debajo de <c>1.0.0</c>.</summary>
public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v0.10.0", 0, 10, 0, null)]
    [InlineData("V2.0.0", 2, 0, 0, null)]
    [InlineData("1.0.0-beta.2", 1, 0, 0, "beta.2")]
    [InlineData("1.0.0+build.5", 1, 0, 0, null)]
    [InlineData("1.0.0-rc.1+build.5", 1, 0, 0, "rc.1")]
    [InlineData("0.1", 0, 1, 0, null)]
    [InlineData("3", 3, 0, 0, null)]
    // Cuatro componentes es lo que devuelve Assembly.GetName().Version; el cuarto se ignora.
    [InlineData("0.1.0.0", 0, 1, 0, null)]
    public void TryParse_AcceptsTheFormsThatReachIt(string text, int major, int minor, int patch, string? preRelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch, preRelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-soy-una-version")]
    [InlineData("1.x.0")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0.0.0")]
    public void TryParse_RejectsWhatIsNotAVersion(string? text) =>
        Assert.False(SemanticVersion.TryParse(text, out _));

    /// <summary>El caso que el documento de la funcionalidad pone de ejemplo: la comparación es numérica
    /// por componente, no textual (si no, "0.9.0" saldría después de "0.10.0").</summary>
    [Fact]
    public void Compare_IsNumericPerComponent_NotTextual()
    {
        Assert.True(Parse("0.9.0") < Parse("0.10.0"));
        Assert.True(Parse("1.0.0") > Parse("0.99.99"));
        Assert.True(Parse("1.2.10") > Parse("1.2.9"));
    }

    [Fact]
    public void Compare_APreReleaseComesBeforeItsOwnRelease()
    {
        Assert.True(Parse("1.0.0-beta") < Parse("1.0.0"));
        Assert.True(Parse("1.0.0") > Parse("1.0.0-rc.1"));
    }

    /// <summary>SemVer §11.4: numéricos entre sí como números, alfanuméricos en orden ASCII, un numérico
    /// antes que un alfanumérico, y a igualdad de prefijo gana el que tenga más identificadores.</summary>
    [Fact]
    public void Compare_OrdersPreReleaseIdentifiers_PerTheSpec()
    {
        Assert.True(Parse("1.0.0-alpha") < Parse("1.0.0-alpha.1"));
        Assert.True(Parse("1.0.0-alpha.1") < Parse("1.0.0-alpha.beta"));
        Assert.True(Parse("1.0.0-alpha.beta") < Parse("1.0.0-beta"));
        Assert.True(Parse("1.0.0-beta.2") < Parse("1.0.0-beta.11"));
        Assert.True(Parse("1.0.0-rc.1") < Parse("1.0.0"));
    }

    [Fact]
    public void Compare_EqualVersions_AreEqual()
    {
        Assert.Equal(0, Parse("1.2.3").CompareTo(Parse("1.2.3")));
        Assert.Equal(0, Parse("v1.2.3").CompareTo(Parse("1.2.3+build")));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.2")]
    [InlineData("0.1.0.0", "0.1.0")]
    public void ToString_NormalizesBackToCanonicalSemVer(string text, string expected) =>
        Assert.Equal(expected, Parse(text).ToString());

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }
}
