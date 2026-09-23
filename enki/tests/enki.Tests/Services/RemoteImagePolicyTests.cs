using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="RemoteImagePolicy"/>, el filtro que decide a qué direcciones puede conectarse
/// el editor cuando una nota trae un <c>![](http://…)</c>. Antes no había ninguno:
/// <c>![](http://192.168.1.1/admin)</c> o <c>![](http://169.254.169.254/…)</c> escritos en una nota ajena
/// convertían abrirla en un sondeo de la red local del usuario, sin que él viera más que un hueco de imagen
/// rota (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-07).
///
/// <para>Ningún test aquí toca la red: <see cref="RemoteImagePolicy.IsPubliclyRoutable"/> es una función
/// pura sobre una dirección ya resuelta, y la comprobación de conexión se prueba contra nombres que
/// resuelven localmente.</para></summary>
public class RemoteImagePolicyTests
{
    // ===================== IPv4 =====================

    [Theory]
    [InlineData("0.0.0.0")]            // "esta red"
    [InlineData("10.0.0.1")]           // 10/8
    [InlineData("10.255.255.254")]
    [InlineData("127.0.0.1")]          // 127/8 loopback
    [InlineData("127.1.2.3")]
    [InlineData("100.64.0.1")]         // 100.64/10 CGNAT
    [InlineData("100.127.255.254")]
    [InlineData("169.254.169.254")]    // metadatos de nube — el destino clásico de un SSRF
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]         // 172.16/12
    [InlineData("172.31.255.254")]
    [InlineData("192.0.0.1")]          // asignaciones IETF
    [InlineData("192.0.2.5")]          // documentación
    [InlineData("192.168.1.1")]        // 192.168/16
    [InlineData("198.18.0.1")]         // benchmarking
    [InlineData("198.51.100.7")]       // documentación
    [InlineData("203.0.113.7")]        // documentación
    [InlineData("224.0.0.1")]          // multicast
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]          // reservado
    [InlineData("255.255.255.255")]    // broadcast
    public void PrivateAndReservedIPv4_IsBlocked(string address) =>
        Assert.False(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")]     // justo por debajo del bloque CGNAT
    [InlineData("100.128.0.0")]        // justo por encima
    [InlineData("11.0.0.1")]           // justo por encima de 10/8
    [InlineData("9.255.255.255")]      // justo por debajo
    [InlineData("172.15.255.255")]     // justo por debajo de 172.16/12
    [InlineData("172.32.0.1")]         // justo por encima
    [InlineData("169.253.0.1")]        // justo por debajo de link-local
    [InlineData("169.255.0.1")]        // justo por encima
    [InlineData("192.167.255.255")]    // justo por debajo de 192.168/16
    [InlineData("192.169.0.1")]        // justo por encima
    [InlineData("198.20.0.1")]         // justo por encima de 198.18/15
    [InlineData("223.255.255.255")]    // justo por debajo de multicast
    public void PublicIPv4_IsAllowed(string address) =>
        Assert.True(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    // ===================== IPv6 =====================

    [Theory]
    [InlineData("::")]                 // sin especificar
    [InlineData("::1")]                // loopback
    [InlineData("fc00::1")]            // unique local
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]            // link-local
    [InlineData("febf::1")]
    [InlineData("fec0::1")]            // site-local (obsoleto)
    [InlineData("ff02::1")]            // multicast
    [InlineData("2001:db8::1")]        // documentación
    [InlineData("100::1")]             // descarte
    public void PrivateAndReservedIPv6_IsBlocked(string address) =>
        Assert.False(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:db9::1")]        // justo fuera del bloque de documentación
    public void PublicIPv6_IsAllowed(string address) =>
        Assert.True(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::ffff:127.0.0.1")]   // IPv4 mapeada a IPv6 — el hueco clásico de un filtro solo-IPv4
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("2002:7f00:0001::")]   // 6to4 con 127.0.0.1 incrustada
    [InlineData("2002:c0a8:0101::")]   // 6to4 con 192.168.1.1 incrustada
    [InlineData("64:ff9b::7f00:1")]    // NAT64 con 127.0.0.1 incrustada
    public void IPv4DisguisedAsIPv6_IsBlockedToo(string address) =>
        Assert.False(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:0808:0808::")]   // 6to4 con una IPv4 pública incrustada
    [InlineData("64:ff9b::808:808")]   // NAT64 con una IPv4 pública incrustada
    public void PublicIPv4DisguisedAsIPv6_IsStillAllowed(string address) =>
        Assert.True(RemoteImagePolicy.IsPubliclyRoutable(IPAddress.Parse(address)));

    // ===================== Conexión =====================

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    public async Task ConnectToPublicEndpoint_RefusesAHostThatResolvesToAPrivateAddress(string host)
    {
        // Es la comprobación de verdad: ocurre en la capa de conexión, sobre la dirección resuelta, así que
        // también cubre un nombre público que resuelva a una IP interna (DNS rebinding) — validar la cadena
        // de la URL no sirve para eso. Y no se conecta a nada: lanza antes del socket.
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await RemoteImagePolicy.ConnectToPublicEndpointAsync(host, 80, CancellationToken.None));
    }

    // ===================== Redirecciones =====================

    [Fact]
    public void Redirect_FromHttpsToHttp_IsRefused()
    {
        var from = new Uri("https://ejemplo.test/a.png");

        Assert.Throws<HttpRequestException>(
            () => RemoteImagePolicy.ResolveRedirect(from, new Uri("http://ejemplo.test/b.png")));
    }

    [Fact]
    public void Redirect_FromHttpToHttps_IsAccepted()
    {
        var from = new Uri("http://ejemplo.test/a.png");

        var target = RemoteImagePolicy.ResolveRedirect(from, new Uri("https://ejemplo.test/b.png"));

        Assert.Equal("https://ejemplo.test/b.png", target.ToString());
    }

    [Fact]
    public void Redirect_ToAnotherScheme_IsRefused()
    {
        var from = new Uri("https://ejemplo.test/a.png");

        Assert.Throws<HttpRequestException>(
            () => RemoteImagePolicy.ResolveRedirect(from, new Uri("file:///etc/passwd")));
    }

    [Fact]
    public void Redirect_WithoutALocationHeader_IsRefused()
    {
        Assert.Throws<HttpRequestException>(
            () => RemoteImagePolicy.ResolveRedirect(new Uri("https://ejemplo.test/a.png"), null));
    }

    [Fact]
    public void Redirect_WithARelativeLocation_IsResolvedAgainstTheOrigin()
    {
        var from = new Uri("https://ejemplo.test/fotos/a.png");

        var target = RemoteImagePolicy.ResolveRedirect(from, new Uri("/otras/b.png", UriKind.Relative));

        Assert.Equal("https://ejemplo.test/otras/b.png", target.ToString());
    }
}
