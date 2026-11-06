using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

/// <summary>Por qué una descarga de <see cref="WebFetch"/> no llegó a dar un cuerpo utilizable, sin contar los
/// fallos de red y los códigos HTTP de error, que siguen siendo <see cref="HttpRequestException"/>.</summary>
public enum WebFetchFailure
{
    /// <summary>El destino (o una redirección) es una IP literal que no sale de la máquina o de la red local.</summary>
    PrivateAddress,
    TooManyRedirects,
    /// <summary>El tipo de contenido no es de los que quien llama sabe leer.</summary>
    UnsupportedType,
    TooLarge
}

public sealed class WebFetchException(WebFetchFailure failure, string? detail = null)
    : Exception($"{failure}: {detail}")
{
    public WebFetchFailure Failure { get; } = failure;

    /// <summary>El host rechazado o el tipo de contenido recibido, según <see cref="Failure"/>.</summary>
    public string? Detail { get; } = detail;
}

/// <summary>Lo descargado: el cuerpo ya acotado, la dirección final tras las redirecciones y el tipo declarado.</summary>
public sealed record WebFetchResponse(byte[] Body, Uri FinalUrl, string? MediaType, string? Charset)
{
    public string DecodeText() => WebFetch.Decode(Body, Charset);
}

/// <summary>Descarga acotada de una página pública: la comparten el web clipper (<see cref="WebClipService"/>)
/// y el acceso a internet del asistente (<see cref="AiWebService"/>), para que ambos apliquen exactamente las
/// mismas reglas.
///
/// <para>El <see cref="HttpClient"/> que se le pasa tiene que llevar el filtro de destino de
/// <see cref="RemoteImagePolicy.CreateHandler"/>, que resuelve el nombre y conecta solo a direcciones
/// públicas (lo que cierra el DNS rebinding). Aquí se añade el rechazo previo de las IP literales privadas,
/// sin abrir siquiera un socket, y las redirecciones seguidas a mano, para rechazar el paso de <c>https</c> a
/// <c>http</c> (<see cref="RemoteImagePolicy.ResolveRedirect"/>). El cuerpo se corta al leerlo, no después.</para></summary>
public static class WebFetch
{
    /// <summary>Cierto cuando el host ya <em>es</em> una dirección y no sale de la máquina o de la red del
    /// usuario (<c>127.0.0.1</c>, <c>192.168.1.1</c>, <c>[::1]</c>, <c>169.254.169.254</c>). Los nombres de
    /// dominio los filtra el handler al resolverlos.</summary>
    public static bool IsNonPublicLiteralHost(Uri url) =>
        IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && !RemoteImagePolicy.IsPubliclyRoutable(address);

    /// <param name="acceptsMediaType">Decide, con el tipo ya en minúsculas (o <see langword="null"/> si el
    /// servidor no lo declara), si vale la pena descargar el cuerpo.</param>
    /// <param name="configure">Cabeceras propias de esta petición (idioma, tipo aceptado).</param>
    public static async Task<WebFetchResponse> GetAsync(HttpClient http, Uri url, long maxBytes, int maxRedirects,
        Func<string?, bool> acceptsMediaType, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        if (IsNonPublicLiteralHost(url)) throw new WebFetchException(WebFetchFailure.PrivateAddress, url.Host);

        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            configure?.Invoke(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (hop == maxRedirects) throw new WebFetchException(WebFetchFailure.TooManyRedirects);
                current = RemoteImagePolicy.ResolveRedirect(current, response.Headers.Location);
                if (IsNonPublicLiteralHost(current)) throw new WebFetchException(WebFetchFailure.PrivateAddress, current.Host);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (!acceptsMediaType(mediaType)) throw new WebFetchException(WebFetchFailure.UnsupportedType, mediaType);

            // Content-Length es una pista del servidor, no una garantía: sirve para no empezar siquiera una
            // descarga que ya se declara demasiado grande; el corte de verdad es el de ReadCappedAsync.
            if (response.Content.Headers.ContentLength > maxBytes) throw new WebFetchException(WebFetchFailure.TooLarge);

            var body = await ReadCappedAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
            return new WebFetchResponse(body, current, mediaType, response.Content.Headers.ContentType?.CharSet);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Lee el cuerpo cortando de verdad en <paramref name="maxBytes"/>: un servidor que no envíe
    /// <c>Content-Length</c> —o que mienta— conseguiría si no que se descargara entero antes de mirar cuánto ocupa.</summary>
    public static async Task<byte[]> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new WebFetchException(WebFetchFailure.TooLarge);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Decodifica el cuerpo con la codificación que declare la cabecera y, si no la declara, la
    /// que declare el propio documento en su <c>&lt;meta charset&gt;</c>. UTF-8 es el último recurso.
    /// Deliberadamente acotado a las codificaciones que trae .NET de serie: registrar el proveedor de
    /// páginas de código completo significaría otro paquete por un puñado de sitios antiguos, y esos se
    /// leen mal pero se leen (UTF-8 conserva el ASCII intacto).</summary>
    public static string Decode(byte[] bytes, string? declaredCharset)
    {
        var charset = declaredCharset;
        if (string.IsNullOrWhiteSpace(charset))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var at = head.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var value = head[(at + "charset".Length)..].TrimStart('=', ' ', '"', '\'');
                var end = value.IndexOfAny(['"', '\'', ' ', ';', '>', '/', '\r', '\n']);
                charset = end < 0 ? value : value[..end];
            }
        }

        var encoding = charset?.Trim().Trim('"').ToLowerInvariant() switch
        {
            "utf-8" or "utf8" or null or "" => Encoding.UTF8,
            "iso-8859-1" or "latin1" or "windows-1252" or "cp1252" => Encoding.Latin1,
            "us-ascii" or "ascii" => Encoding.ASCII,
            "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8
        };
        return encoding.GetString(bytes);
    }
}
