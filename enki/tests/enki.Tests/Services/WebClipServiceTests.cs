using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Web clipper (T3): la parte de red y la nota resultante. Todo con transporte falso —la misma
/// costura que <c>PolarAuthService</c>/<c>SupabaseRestClient</c>— para poder recorrer los casos que en la
/// web real no se pueden provocar a voluntad: el 403, la redirección, el cuerpo enorme, el tipo que no es
/// HTML y la cancelación. Lo que <b>no</b> pasa por el transporte falso son los rechazos: esos se deciden
/// antes de que exista una petición, y eso es justamente lo que se comprueba.</summary>
public class WebClipServiceTests : IDisposable
{
    private readonly TestPolarCredentialStore _credentialStore = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();

    private const string Article = """
        <html><head><title>Un artículo — Diario</title>
        <meta property="og:title" content="Un artículo"></head>
        <body><article>
          <p>Primer párrafo del artículo, suficientemente largo como para que el extractor lo tome en serio
             y no lo confunda con un aviso de cookies o con el pie de página del sitio.</p>
          <p>Segundo párrafo, igual de largo, para que la puntuación del bloque deje claro que aquí está el
             contenido y no en la barra lateral que este documento ni siquiera tiene.</p>
        </article></body></html>
        """;

    private WebClipService CreateService(out VaultService vault)
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vaultPath);
        var settings = new SettingsService(Path.Combine(_root, "app-data"), _credentialStore);
        vault = new VaultService(settings);
        vault.SetVaultPath(vaultPath);
        return new WebClipService(vault, _handler);
    }

    private static HttpResponseMessage Html(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html")
        {
            CharSet = "utf-8"
        };
        return response;
    }

    public void Dispose()
    {
        _handler.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ===================== Rechazos, antes de cualquier petición =====================

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://ejemplo.org/x")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("")]
    public void TryParseUrl_RejectsWhatMustNeverBeRequested(string input)
    {
        Assert.False(WebClipService.TryParseUrl(input, out _, out var rejection));
        Assert.NotNull(rejection);
    }

    [Fact]
    public void TryParseUrl_AssumesHttpsWhenTheSchemeIsMissing()
    {
        Assert.True(WebClipService.TryParseUrl("ejemplo.org/articulo", out var url, out _));
        Assert.Equal("https://ejemplo.org/articulo", url.ToString());
    }

    [Fact]
    public async Task ClipAsync_RejectsAPrivateAddressWithoutSendingARequest()
    {
        using var service = CreateService(out _);

        await Assert.ThrowsAsync<WebClipRejectedException>(
            () => service.ClipAsync(new Uri("http://10.0.0.5/panel")));

        Assert.Empty(_handler.Requests);
    }

    // ===================== Captura completa =====================

    [Fact]
    public async Task ClipAsync_ExtractsTitleAndBody()
    {
        using var service = CreateService(out _);
        _handler.Enqueue(Html(Article));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/articulo"));

        Assert.True(result.ExtractionComplete);
        Assert.Equal("Un artículo", result.Title);
        Assert.Contains("Primer párrafo del artículo", result.Content, StringComparison.Ordinal);
        Assert.Contains("source: \"https://ejemplo.org/articulo\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("tags: [clip]", result.Content, StringComparison.Ordinal);
        Assert.StartsWith("---\n", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClipAsync_FollowsRedirectsAndRecordsTheFinalUrl()
    {
        using var service = CreateService(out _);
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://ejemplo.org/final");
        _handler.Enqueue(redirect);
        _handler.Enqueue(Html(Article));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/corta"));

        Assert.True(result.ExtractionComplete);
        Assert.Equal("https://ejemplo.org/final", result.Url.ToString());
        Assert.Contains("source: \"https://ejemplo.org/final\"", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Una petición que empezó cifrada no puede terminar en claro porque lo pida el servidor del
    /// otro lado, que es exactamente quien no decide eso. Se trata como un fallo de la página, así que cae
    /// en la alternativa de título + enlace.</summary>
    [Fact]
    public async Task ClipAsync_RefusesARedirectThatDowngradesFromHttpsToHttp()
    {
        using var service = CreateService(out _);
        var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        redirect.Headers.Location = new Uri("http://ejemplo.org/final");
        _handler.Enqueue(redirect);

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/corta"));

        Assert.False(result.ExtractionComplete);
        Assert.Single(_handler.Requests);
    }

    // ===================== Alternativa: título y enlace =====================

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ClipAsync_StillProducesANoteWhenTheSiteRefuses(HttpStatusCode status)
    {
        using var service = CreateService(out _);
        _handler.Enqueue(Html("<html><body>nope</body></html>", status));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/paywall"));

        Assert.False(result.ExtractionComplete);
        Assert.NotNull(result.PartialReason);
        Assert.Equal("ejemplo.org", result.Title);
        Assert.Contains("source: \"https://ejemplo.org/paywall\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClipAsync_StillProducesANoteWhenThePageHasNoReadableText()
    {
        using var service = CreateService(out _);
        _handler.Enqueue(Html("<html><head><title>Solo JS</title></head><body><div id=\"app\"></div></body></html>"));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/spa"));

        Assert.False(result.ExtractionComplete);
        Assert.Equal("Solo JS", result.Title);
    }

    [Fact]
    public async Task ClipAsync_StopsAtTheSizeLimitEvenWithoutContentLength()
    {
        using var service = CreateService(out _);
        var oversized = new HttpResponseMessage(HttpStatusCode.OK)
        {
            // StreamContent no anuncia Content-Length, así que el corte tiene que venir de la lectura.
            Content = new StreamContent(new MemoryStream(new byte[WebClipService.MaxContentBytes + 1024]))
        };
        oversized.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        _handler.Enqueue(oversized);

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/enorme"));

        Assert.False(result.ExtractionComplete);
    }

    [Fact]
    public async Task ClipAsync_RefusesContentThatIsNotAWebPage()
    {
        using var service = CreateService(out _);
        var pdf = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        pdf.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        _handler.Enqueue(pdf);

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/doc.pdf"));

        Assert.False(result.ExtractionComplete);
    }

    /// <summary>Cancelar es lo único que no produce nota: el criterio de aceptación dice que tras cancelar
    /// no debe crearse ninguna.</summary>
    [Fact]
    public async Task ClipAsync_PropagatesTheUsersCancellation()
    {
        using var service = CreateService(out _);
        using var cancellation = new CancellationTokenSource();
        _handler.Enqueue(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Html(Article);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ClipAsync(new Uri("https://ejemplo.org/lenta"), cancellation.Token));
    }

    // ===================== Nota en el vault =====================

    [Fact]
    public async Task SaveNote_CreatesTheInboxFolderAndWritesTheNoteThere()
    {
        using var service = CreateService(out var vault);
        _handler.Enqueue(Html(Article));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/articulo"));
        var note = service.SaveNote(result);

        var inbox = Path.Combine(vault.VaultPath, WebClipService.InboxFolderName);
        Assert.True(Directory.Exists(inbox));
        Assert.Equal(inbox, Path.GetDirectoryName(note.FullPath));
        Assert.Contains("Primer párrafo del artículo", File.ReadAllText(note.FullPath), StringComparison.Ordinal);
    }

    /// <summary>Un título con caracteres que no valen en un nombre de archivo —y dos capturas de la misma
    /// página— no pueden acabar en una excepción ni pisando la nota anterior.</summary>
    [Fact]
    public async Task SaveNote_SanitizesTheFileNameAndNeverOverwrites()
    {
        using var service = CreateService(out _);
        var page = Article.Replace("content=\"Un artículo\"", "content=\"¿Qué? / cómo: \\\"esto\\\"\"", StringComparison.Ordinal);
        _handler.Enqueue(Html(page));
        _handler.Enqueue(Html(page));

        var first = service.SaveNote(await service.ClipAsync(new Uri("https://ejemplo.org/a")));
        var second = service.SaveNote(await service.ClipAsync(new Uri("https://ejemplo.org/a")));

        Assert.NotEqual(first.FullPath, second.FullPath);
        Assert.True(File.Exists(first.FullPath));
        Assert.True(File.Exists(second.FullPath));
    }

    /// <summary>El frontmatter tiene que seguir siendo legible aunque el título traiga dos puntos, una
    /// almohadilla o un salto de línea, que es lo normal en un título de página web.</summary>
    [Fact]
    public async Task ClipAsync_QuotesFrontmatterValuesThatWouldBreakYaml()
    {
        using var service = CreateService(out _);
        _handler.Enqueue(Html(Article.Replace(
            "content=\"Un artículo\"", "content=\"Noticia: #última hora\"", StringComparison.Ordinal)));

        var result = await service.ClipAsync(new Uri("https://ejemplo.org/a"));

        var frontmatter = result.Content.Split("---", StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("title: \"Noticia: #última hora\"", frontmatter, StringComparison.Ordinal);
    }
}
