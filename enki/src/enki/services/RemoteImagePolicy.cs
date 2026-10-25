using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

/// <summary>
/// A qué direcciones puede conectarse el editor cuando una nota trae una imagen con URL, y con qué
/// límites.
///
/// <para><b>Por qué existe.</b> Un <c>![](http://…)</c> dentro de una nota es texto que puede no haber
/// escrito quien la lee (un vault sincronizado, una plantilla descargada, una nota recibida). Hasta este
/// punto el editor descargaba cualquier destino sin mirarlo: bastaba con
/// <c>![](http://192.168.1.1/admin)</c> o <c>![](http://169.254.169.254/…)</c> para que abrir la nota
/// sondease la red local del usuario o el servicio de metadatos de su nube, sin que él viera nada más que
/// un hueco de imagen rota (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-07).</para>
///
/// <para><b>Dónde se comprueba, y por qué ahí.</b> No sobre la cadena de la URL, que no es una defensa:
/// un nombre de dominio público puede resolver a <c>127.0.0.1</c>, y puede cambiar de respuesta entre la
/// comprobación y la conexión (DNS rebinding). La comprobación vive en
/// <see cref="SocketsHttpHandler.ConnectCallback"/>: ahí se resuelve el nombre, se descartan las
/// direcciones no públicas y se conecta <em>a esas mismas direcciones ya validadas</em>, de modo que no hay
/// hueco entre lo que se valida y lo que se usa. Y como cada salto de una redirección abre su propia
/// conexión, el filtro se aplica igual a todos sin código adicional.</para>
///
/// <para>Esto no protege contra un píxel de seguimiento —un servidor público que solo quiere ver la IP del
/// lector y el momento en que abre la nota sigue funcionando—. Eso depende de la preferencia
/// <c>EditorPreferences.RemoteImages</c>, que es una decisión de producto y aquí no se toca.</para>
/// </summary>
internal static class RemoteImagePolicy
{
    /// <summary>Tope total para descargar una imagen, redirecciones incluidas. No es el timeout por
    /// petición del <see cref="HttpClient"/>: con varios saltos ese se pagaría una vez por salto.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Un CDN de imágenes encadena como mucho un par de saltos (dominio corto → dominio real →
    /// variante); más que eso es un bucle o alguien paseando la petición, y cada salto es otra resolución
    /// DNS y otra conexión que pagar.</summary>
    public const int MaxRedirects = 3;

    /// <summary>Cliente HTTP con el filtro de destino puesto. Las redirecciones se siguen a mano
    /// (<c>MarkdownImageCache.Download</c>) en vez de dejarlas al runtime, para poder rechazar
    /// explícitamente —y poder probar— el paso de <c>https</c> a <c>http</c>.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = (context, ct) =>
            ConnectToPublicEndpointAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)
    };

    /// <summary>Resuelve <paramref name="host"/>, se queda solo con las direcciones públicas y conecta a
    /// una de ellas. Si no queda ninguna, no se conecta a nada: lanza, y quien llama ya pinta la imagen
    /// como rota. Conectar a la lista ya filtrada —y no al nombre— es lo que cierra el DNS rebinding, y
    /// también lo que hace que <c>localhost</c> o un dominio que apunte a una IP interna no sean casos
    /// especiales, sino el mismo caso.</summary>
    internal static async ValueTask<Stream> ConnectToPublicEndpointAsync(string host, int port, CancellationToken ct)
    {
        var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = Array.FindAll(resolved, IsPubliclyRoutable);
        if (allowed.Length == 0)
            throw new HttpRequestException($"El destino remoto '{host}' no resuelve a ninguna dirección pública.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Destino de una redirección, ya resuelto contra <paramref name="from"/> (la cabecera
    /// <c>Location</c> puede ser relativa) y validado. Se rechaza un esquema que no sea
    /// <c>http</c>/<c>https</c> y se rechaza bajar de <c>https</c> a <c>http</c>: una petición que empezó
    /// cifrada no debe terminar en claro porque lo pida el servidor del otro lado, que es exactamente quien
    /// no decide eso. Al revés (de <c>http</c> a <c>https</c>) sí se acepta, porque mejora.</summary>
    public static Uri ResolveRedirect(Uri from, Uri? location)
    {
        if (location is null)
            throw new HttpRequestException("La redirección de una imagen remota no trae cabecera Location.");

        var target = location.IsAbsoluteUri ? location : new Uri(from, location);
        if (!IsAllowedScheme(target))
            throw new HttpRequestException($"La redirección de una imagen remota apunta a un esquema no permitido: '{target.Scheme}'.");

        if (string.Equals(from.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("La redirección de una imagen remota degrada de https a http.");
        }

        return target;
    }

    /// <summary>Los dos únicos esquemas que <c>MarkdownImageResolver</c> clasifica como imagen remota;
    /// se vuelve a comprobar aquí porque una redirección puede llevar a cualquier otro.</summary>
    public static bool IsAllowedScheme(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Si <paramref name="address"/> es una dirección de internet a la que tiene sentido que el editor
    /// pida una imagen. Falso para todo lo que no sale de la máquina o de la red del usuario: loopback,
    /// rangos privados, link-local (que incluye el <c>169.254.169.254</c> de los metadatos de las nubes),
    /// CGNAT, multicast, documentación y reservados, más sus equivalentes en IPv6 y las formas en que una
    /// dirección IPv4 puede venir disfrazada dentro de una IPv6 (<c>::ffff:127.0.0.1</c>, 6to4, NAT64),
    /// que es el hueco clásico por el que se cuela un filtro escrito solo para IPv4.
    ///
    /// <para>Es una función pura sobre la dirección ya resuelta: no toca la red, no mira el nombre del
    /// host y no depende de nada externo, así que se prueba con una tabla de casos — ver
    /// <c>RemoteImagePolicyTests</c>. La lista blanca es implícita ("lo que no esté en ningún rango
    /// reservado"), que es lo correcto aquí: un rango reservado nuevo que no conozcamos se acepta, pero
    /// ninguno de los conocidos se cuela por olvidar una regla positiva.</para>
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6) return IsPubliclyRoutable(address.MapToIPv4());
            return IsPubliclyRoutableV6(address.GetAddressBytes());
        }

        return address.AddressFamily == AddressFamily.InterNetwork &&
               IsPubliclyRoutableV4(address.GetAddressBytes());
    }

    private static bool IsPubliclyRoutableV4(byte[] b) => !(
        b[0] == 0 ||                                       // 0.0.0.0/8      "esta red"
        b[0] == 10 ||                                      // 10/8           privada
        b[0] == 127 ||                                     // 127/8          loopback
        (b[0] == 100 && (b[1] & 0xC0) == 64) ||            // 100.64/10      CGNAT
        (b[0] == 169 && b[1] == 254) ||                    // 169.254/16     link-local (metadatos de nube)
        (b[0] == 172 && (b[1] & 0xF0) == 16) ||            // 172.16/12      privada
        (b[0] == 192 && b[1] == 0 && b[2] == 0) ||         // 192.0.0/24     asignaciones IETF
        (b[0] == 192 && b[1] == 0 && b[2] == 2) ||         // 192.0.2/24     documentación
        (b[0] == 192 && b[1] == 168) ||                    // 192.168/16     privada
        (b[0] == 198 && (b[1] & 0xFE) == 18) ||            // 198.18/15      benchmarking
        (b[0] == 198 && b[1] == 51 && b[2] == 100) ||      // 198.51.100/24  documentación
        (b[0] == 203 && b[1] == 0 && b[2] == 113) ||       // 203.0.113/24   documentación
        b[0] >= 224);                                      // 224/4 y 240/4  multicast, reservado, broadcast

    private static bool IsPubliclyRoutableV6(byte[] b)
    {
        // 6to4 (2002::/16) y NAT64 (64:ff9b::/96) llevan una IPv4 incrustada: lo que decide es esa IPv4, no
        // el prefijo. Sin esto, "2002:7f00:0001::" sería una forma perfectamente válida de escribir 127.0.0.1.
        if (b[0] == 0x20 && b[1] == 0x02) return IsPubliclyRoutableV4([b[2], b[3], b[4], b[5]]);
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return IsPubliclyRoutableV4([b[12], b[13], b[14], b[15]]);

        if ((b[0] & 0xFE) == 0xFC) return false;                       // fc00::/7   unique local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;       // fe80::/10  link-local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return false;       // fec0::/10  site-local (obsoleto)
        if (b[0] == 0xFF) return false;                                // ff00::/8   multicast
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 documentación
        if (b[0] == 0x01 && b[1] == 0x00 && IsZero(b, 2, 6)) return false;              // 100::/64 descarte

        // ::, ::1 y el resto del bloque ::/8, que no es direccionable.
        return !IsZero(b, 0, 8);
    }

    private static bool IsZero(byte[] b, int start, int count)
    {
        for (var i = start; i < start + count; i++)
            if (b[i] != 0) return false;
        return true;
    }
}
