using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Acceso a internet del asistente sin red: búsqueda (DuckDuckGo y Brave), lectura de páginas,
/// rechazos y caché, con un transporte que contesta lo que cada prueba necesita.</summary>
public sealed class AiWebServiceTests : IDisposable
{
    private const string BraveKey = "BSAtestkey0123456789abcdefghijk";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly TestPolarCredentialStore _keys = new();

    public AiWebServiceTests() => _settings = new SettingsService(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private AiWebService Service(Handler handler) =>
        new(_settings, new AiApiKeyStore(_keys, AiApiKeyKind.BraveSearch), handler);

    private static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    internal const string DuckDuckGoPage = """
        <div class="result results_links results_links_deep result--ad">
          <h2 class="result__title"><a rel="nofollow" class="result__a" href="https://duckduckgo.com/y.js?ad_domain=ads.example">Anuncio</a></h2>
          <a class="result__snippet" href="https://duckduckgo.com/y.js?x=1">Compra ya</a>
        </div>
        <div class="result results_links results_links_deep web-result">
          <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fdocs%3Fq%3D1&amp;rut=abc">Example <b>Docs</b></a></h2>
          <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fdocs">La documentación &amp; <b>guías</b> de Example.</a>
        </div>
        <div class="result results_links web-result">
          <h2 class="result__title"><a class="result__a" rel="nofollow" href="https://news.example/item">Noticia</a></h2>
        </div>
        """;

    [Fact]
    public void ParseDuckDuckGo_SkipsAdsAndUnwrapsRedirectLinks()
    {
        var results = AiWebService.ParseDuckDuckGo(DuckDuckGoPage);

        Assert.Equal(2, results.Count);
        Assert.Equal("Example Docs", results[0].Title);
        Assert.Equal("https://example.org/docs?q=1", results[0].Url);
        Assert.Equal("La documentación & guías de Example.", results[0].Snippet);
        Assert.Equal("https://news.example/item", results[1].Url);
        Assert.Equal(string.Empty, results[1].Snippet);
    }

    [Fact]
    public async Task Search_DuckDuckGo_PostsTheQueryAndLimitsResults()
    {
        var handler = new Handler(_ => Html(DuckDuckGoPage));
        using var web = Service(handler);

        var results = await web.SearchAsync("  enki   notas ", 1, CancellationToken.None);

        Assert.Single(results);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(AiWebService.DuckDuckGoUrl, request.RequestUri!.AbsoluteUri);
        Assert.Contains("q=enki+notas", handler.Bodies.Single());
    }

    [Fact]
    public async Task Search_DuckDuckGo_ChallengeIsReportedAsUnavailable()
    {
        using var web = Service(new Handler(_ => Html("<html>anomaly</html>", HttpStatusCode.Accepted)));
        var error = await Assert.ThrowsAsync<AiWebException>(() => web.SearchAsync("x", 5, CancellationToken.None));
        Assert.Equal(AiWebFailure.SearchUnavailable, error.Failure);
    }

    [Fact]
    public async Task Search_Brave_NeedsAKeyAndSendsItOnlyInTheHeader()
    {
        _settings.Settings.Ai.WebSearchEngine = AiWebSearchEngine.Brave;
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"web":{"results":[
                  {"title":"Enki <strong>app</strong>","url":"https://enki.example/","description":"Notas <strong>locales</strong>","age":"2 days ago"},
                  {"title":"Mal","url":"ftp://files.example/"}
                ]}}
                """, Encoding.UTF8, "application/json")
        });
        using var web = Service(handler);

        var missing = await Assert.ThrowsAsync<AiWebException>(() => web.SearchAsync("enki", 5, CancellationToken.None));
        Assert.Equal(AiWebFailure.SearchKeyMissing, missing.Failure);
        Assert.Empty(handler.Requests);

        new AiApiKeyStore(_keys, AiApiKeyKind.BraveSearch).Write(BraveKey);
        var result = Assert.Single(await web.SearchAsync("enki", 5, CancellationToken.None));

        Assert.Equal("Enki app", result.Title);
        Assert.Equal("2 days ago — Notas locales", result.Snippet);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(BraveKey, request.Headers.GetValues("X-Subscription-Token").Single());
        Assert.DoesNotContain(BraveKey, request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("count=5", request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiWebFailure.SearchKeyRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, AiWebFailure.SearchRateLimited)]
    [InlineData(HttpStatusCode.BadGateway, AiWebFailure.SearchUnavailable)]
    public async Task Search_Brave_ClassifiesErrors(HttpStatusCode status, AiWebFailure expected)
    {
        _settings.Settings.Ai.WebSearchEngine = AiWebSearchEngine.Brave;
        new AiApiKeyStore(_keys, AiApiKeyKind.BraveSearch).Write(BraveKey);
        using var web = Service(new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent("{}") }));

        var error = await Assert.ThrowsAsync<AiWebException>(() => web.SearchAsync("enki", 5, CancellationToken.None));
        Assert.Equal(expected, error.Failure);
    }

    [Fact]
    public async Task Fetch_ReadsHtmlWithAbsoluteLinks_AndRemembersThePage()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/old"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/new", UriKind.Relative) } }
            : Html("""
                <html><head><title>Guía de Enki</title><meta name="description" content="Resumen"></head>
                <body><main><h1>Guía</h1><p>Lee <a href="capitulo-2">el capítulo 2</a> o <a href="https://other.example/x">otra web</a>.</p></main></body></html>
                """));
        using var web = Service(handler);

        var page = await web.FetchAsync(new Uri("https://docs.example/old"), CancellationToken.None);

        Assert.Equal(new Uri("https://docs.example/new"), page.Url);
        Assert.Equal("Guía de Enki", page.Title);
        Assert.Equal("Resumen", page.Description);
        Assert.Contains("[el capítulo 2](https://docs.example/capitulo-2)", page.Text);
        Assert.Equal(["https://docs.example/capitulo-2", "https://other.example/x"], page.Links);

        await web.FetchAsync(new Uri("https://docs.example/old"), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        web.ClearCache();
        await web.FetchAsync(new Uri("https://docs.example/new"), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_ReturnsPlainTextAndJsonAsIs()
    {
        using var web = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") }));
        var page = await web.FetchAsync(new Uri("https://api.example/status"), CancellationToken.None);
        Assert.Equal("{\"ok\":true}", page.Text);
        Assert.Equal("application/json", page.ContentType);
    }

    [Fact]
    public async Task Fetch_RejectsBinaryContent_ErrorStatuses_AndRedirectsToPrivateAddresses()
    {
        using var pdf = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/pdf") } } }));
        var unsupported = await Assert.ThrowsAsync<AiWebException>(() => pdf.FetchAsync(new Uri("https://a.example/f.pdf"), CancellationToken.None));
        Assert.Equal(AiWebFailure.UnsupportedContent, unsupported.Failure);
        Assert.Equal("application/pdf", unsupported.Detail);

        using var missing = Service(new Handler(_ => Html("no", HttpStatusCode.NotFound)));
        var notFound = await Assert.ThrowsAsync<AiWebException>(() => missing.FetchAsync(new Uri("https://a.example/x"), CancellationToken.None));
        Assert.Equal(AiWebFailure.HttpStatus, notFound.Failure);
        Assert.Equal(404, notFound.Status);

        using var redirect = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Found)
            { Headers = { Location = new Uri("http://192.168.1.1/admin") } }));
        var blocked = await Assert.ThrowsAsync<AiWebException>(() => redirect.FetchAsync(new Uri("http://a.example/"), CancellationToken.None));
        Assert.Equal(AiWebFailure.PrivateAddress, blocked.Failure);
    }

    [Fact]
    public async Task Fetch_CutsBodiesThatAreTooLarge()
    {
        using var web = Service(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('a', (int)AiWebService.MaxPageBytes + 10), Encoding.UTF8, "text/plain") }));
        var error = await Assert.ThrowsAsync<AiWebException>(() => web.FetchAsync(new Uri("https://a.example/big"), CancellationToken.None));
        Assert.Equal(AiWebFailure.TooLarge, error.Failure);
    }

    [Theory]
    [InlineData("example.org/docs", "https://example.org/docs")]
    [InlineData("http://example.org", "http://example.org/")]
    public void ParseUrl_AcceptsPublicHttpAddresses(string text, string expected) =>
        Assert.Equal(expected, AiWebService.ParseUrl(text).AbsoluteUri);

    [Theory]
    [InlineData("ftp://example.org/file", AiWebFailure.InvalidUrl)]
    [InlineData("file:///etc/passwd", AiWebFailure.InvalidUrl)]
    [InlineData("https://user:secret@example.org/", AiWebFailure.InvalidUrl)]
    [InlineData("", AiWebFailure.InvalidUrl)]
    [InlineData("http://127.0.0.1:11434/api/tags", AiWebFailure.PrivateAddress)]
    [InlineData("http://localhost/", AiWebFailure.PrivateAddress)]
    [InlineData("http://[::1]/", AiWebFailure.PrivateAddress)]
    [InlineData("http://169.254.169.254/latest/meta-data", AiWebFailure.PrivateAddress)]
    public void ParseUrl_RejectsOtherSchemesCredentialsAndLocalAddresses(string text, AiWebFailure expected) =>
        Assert.Equal(expected, Assert.Throws<AiWebException>(() => AiWebService.ParseUrl(text)).Failure);

    [Fact]
    public void UrlSet_ComparesWithoutFragmentOrTrailingSlash_AndReadsUrlsFromText()
    {
        var urls = new AiWebUrlSet();
        urls.AddFrom("Mira https://Example.org/Docs/, y también (https://b.example/x?y=1).");

        Assert.True(urls.Contains(new Uri("https://example.org/Docs#intro")));
        Assert.True(urls.Contains(new Uri("https://b.example/x?y=1")));
        Assert.False(urls.Contains(new Uri("https://b.example/x?y=2")));
        Assert.False(urls.Contains(new Uri("https://example.org/docs")));
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = respond(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
