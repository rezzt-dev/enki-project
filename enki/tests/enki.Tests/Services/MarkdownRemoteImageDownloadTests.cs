using System;
using System.Net;
using System.Net.Http;
using enki.Views.Editor.Markdown;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre la descarga de una imagen remota del editor
/// (<c>MarkdownImageCache.Download</c>) con un transporte falso, igual que <c>PolarAuthServiceTests</c>
/// hace con Supabase. Lo que se fija aquí es el tope de tamaño —que antes se comprobaba <em>después</em> de
/// haberlo descargado todo— y el control de las redirecciones, que antes las seguía el
/// <see cref="HttpClient"/> sin límite propio ni restricción de esquema
/// (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-07).
///
/// <para>El filtro de destino por IP no pasa por esta costura: vive en el handler real y se prueba en
/// <see cref="RemoteImagePolicyTests"/>. Los tests son secuenciales porque comparten el cliente estático
/// de la caché.</para></summary>
[Collection("MarkdownRemoteImageDownload")]
public class MarkdownRemoteImageDownloadTests : IDisposable
{
    private readonly FakeHttpMessageHandler _handler = new();

    public MarkdownRemoteImageDownloadTests() => MarkdownImageCache.UseHttpHandlerForTests(_handler);

    public void Dispose() => MarkdownImageCache.UseHttpHandlerForTests(null);

    private static HttpResponseMessage Image(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        return response;
    }

    private static HttpResponseMessage RedirectTo(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public void ASmallImage_IsDownloadedWhole()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        _handler.Enqueue(Image(body));

        Assert.Equal(body, MarkdownImageCache.Download("https://ejemplo.test/a.png"));
    }

    [Fact]
    public void AnOversizedBodyWithoutContentLength_IsCutWhileReading()
    {
        // Este es el caso que el tope no cubría: sin Content-Length, ReadAsByteArrayAsync se traía los 17 MB
        // enteros a memoria y solo después se miraba el tamaño, que es justo lo que el tope existía para
        // evitar. Un cuerpo no buscable no declara longitud, así que reproduce a ese servidor.
        var body = new UnknownLengthStream(new byte[17 * 1024 * 1024]);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        _handler.Enqueue(response);

        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Throws<InvalidOperationException>(() => MarkdownImageCache.Download("https://ejemplo.test/grande.png"));

        // Lo que distingue el corte real del "descárgalo todo y luego mídelo": el cuerpo deja de leerse en
        // cuanto pasa del tope, no cuando el servidor termina de mandarlo. Sin esto, el test también pasaba
        // con la versión anterior, porque esa también acababa lanzando — después de haberse traído los
        // 17 MB a memoria.
        Assert.True(body.BytesRead <= MaxRemoteBytes + ChunkSize,
            $"Se leyeron {body.BytesRead} bytes: el cuerpo se está descargando entero antes de cortar.");
    }

    private const long MaxRemoteBytes = 16L * 1024 * 1024;
    private const int ChunkSize = 81920;

    [Fact]
    public void AnOversizedContentLength_IsRefusedBeforeReadingTheBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1]) };
        response.Content.Headers.ContentLength = 17L * 1024 * 1024;
        _handler.Enqueue(response);

        Assert.Throws<InvalidOperationException>(() => MarkdownImageCache.Download("https://ejemplo.test/grande.png"));
    }

    [Fact]
    public void AFewRedirects_AreFollowed()
    {
        var body = new byte[] { 9 };
        _handler.Enqueue(RedirectTo("https://cdn.ejemplo.test/a.png"));
        _handler.Enqueue(RedirectTo("/variantes/a.png"));
        _handler.Enqueue(Image(body));

        Assert.Equal(body, MarkdownImageCache.Download("https://ejemplo.test/a.png"));
        Assert.Equal("https://cdn.ejemplo.test/variantes/a.png", _handler.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public void AChainOfRedirects_StopsAtTheLimit()
    {
        for (var i = 0; i < 10; i++) _handler.Enqueue(RedirectTo($"https://ejemplo.test/{i}.png"));

        Assert.Throws<InvalidOperationException>(() => MarkdownImageCache.Download("https://ejemplo.test/a.png"));
        // 1 petición inicial + MaxRedirects saltos, y ni una más.
        Assert.Equal(1 + 3, _handler.Requests.Count);
    }

    [Fact]
    public void ARedirectFromHttpsToHttp_IsRefused()
    {
        _handler.Enqueue(RedirectTo("http://ejemplo.test/a.png"));

        // Una petición que empezó cifrada no termina en claro porque lo pida el servidor del otro lado.
        Assert.Throws<HttpRequestException>(() => MarkdownImageCache.Download("https://ejemplo.test/a.png"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public void ANonImageScheme_IsRefusedBeforeAnyRequest()
    {
        Assert.Throws<InvalidOperationException>(() => MarkdownImageCache.Download("file:///etc/passwd"));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void AnErrorResponse_Propagates()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Throws<HttpRequestException>(() => MarkdownImageCache.Download("https://ejemplo.test/a.png"));
    }

    /// <summary>Un cuerpo que no declara su longitud, como el de un servidor que responde con
    /// <c>Transfer-Encoding: chunked</c>. <see cref="System.IO.MemoryStream"/> no sirve para esto:
    /// <see cref="StreamContent"/> le saca el <c>Content-Length</c> por ser buscable.</summary>
    private sealed class UnknownLengthStream(byte[] data) : System.IO.Stream
    {
        private int _position;

        public long BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
