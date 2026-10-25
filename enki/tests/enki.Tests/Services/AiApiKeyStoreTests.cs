using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>La clave de API de Claude en el almacén seguro (aquí, uno en memoria): forma, máscara y fallos.</summary>
public sealed class AiApiKeyStoreTests
{
    [Theory]
    [InlineData("sk-ant-api03-0123456789abcdefghij", true)]
    [InlineData("  sk-ant-api03-0123456789abcdefghij  ", true)]
    [InlineData("sk-proj-0123456789abcdefghijklmn", false)]
    [InlineData("sk-ant-corta", false)]
    [InlineData("sk-ant-api03 con espacios 0123456789", false)]
    [InlineData("", false)]
    public void LooksLikeApiKey(string value, bool expected) => Assert.Equal(expected, AiApiKeyStore.LooksLikeApiKey(value));

    [Fact]
    public void Mask_ShowsOnlyTheLastCharacters() =>
        Assert.Equal("sk-ant-…wxyz", AiApiKeyStore.Mask("sk-ant-api03-0123456789abcdefwxyz"));

    [Fact]
    public void WriteReadDelete_RoundTrips_AndRejectsSomethingElse()
    {
        var store = new AiApiKeyStore(new TestPolarCredentialStore());
        Assert.Null(store.TryRead());

        store.Write(" sk-ant-api03-0123456789abcdefghij ");
        Assert.Equal("sk-ant-api03-0123456789abcdefghij", store.TryRead());
        Assert.Throws<ArgumentException>(() => store.Write("contraseña"));

        store.Delete();
        Assert.False(store.HasKey);
    }

    [Fact]
    public void TryRead_WithAFailingStore_ReturnsNull()
    {
        var backing = new TestPolarCredentialStore();
        backing.Write(new PolarCredentials("sk-ant-api03-0123456789abcdefghij", null));
        backing.FailReads = true;
        Assert.Null(new AiApiKeyStore(backing).TryRead());
        Assert.True(AiApiKeyStore.IsStoreFailure(new IOException()));
        Assert.True(AiApiKeyStore.IsStoreFailure(new PlatformNotSupportedException()));
    }
}
