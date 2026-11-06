using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Un resultado de búsqueda tal como se le devuelve al modelo.</summary>
public sealed record AiWebSearchResult(string Title, string Url, string Snippet);

/// <summary>Una página web ya leída y convertida a texto (Markdown para el HTML).</summary>
/// <param name="Url">Dirección final, tras las redirecciones.</param>
/// <param name="Links">Destinos absolutos que aparecen en el texto, para poder seguirlos.</param>
public sealed record AiWebPage(Uri Url, string Title, string? Description, string? Published, string ContentType,
    string Text, IReadOnlyList<string> Links);

/// <summary>Por qué falló una búsqueda o la lectura de una página. El asistente lo traduce a un mensaje para el
/// modelo (qué puede hacer) y otro para el usuario (en su idioma).</summary>
public enum AiWebFailure
{
    InvalidUrl,
    /// <summary>La dirección apunta a este equipo o a la red local: nunca se pide.</summary>
    PrivateAddress,
    Unreachable,
    Timeout,
    /// <summary>El servidor contestó con un error HTTP (<see cref="AiWebException.Status"/>).</summary>
    HttpStatus,
    UnsupportedContent,
    TooLarge,
    TooManyRedirects,
    /// <summary>El buscador no dejó buscar (página de verificación anti-robots) o devolvió algo ilegible.</summary>
    SearchUnavailable,
    /// <summary>Brave Search elegido sin clave guardada.</summary>
    SearchKeyMissing,
    SearchKeyRejected,
    SearchRateLimited
}

public sealed class AiWebException(AiWebFailure failure, string? detail = null, int? status = null)
    : Exception($"{failure}: {detail ?? status?.ToString(CultureInfo.InvariantCulture)}")
{
    public AiWebFailure Failure { get; } = failure;
    public string? Detail { get; } = detail;
    public int? Status { get; } = status;
}

/// <summary>Acceso a internet del asistente: buscar (DuckDuckGo sin clave, o Brave Search con la clave del
/// usuario) y leer páginas públicas. Lo usan las herramientas <c>web_search</c> y <c>web_fetch</c> de
/// <see cref="AiVaultToolbox"/>, igual para todos los proveedores: el modelo nunca navega por su cuenta, lo hace
/// Enki desde este equipo y con estas reglas.
///
/// <para><b>Qué se puede pedir.</b> Lo mismo que el web clipper (<see cref="WebFetch"/>): solo
/// <c>http</c>/<c>https</c> y solo direcciones públicas, comprobadas al resolver y al conectar, así que el
/// modelo no puede llegar al router, a un servicio local ni al propio servidor MCP de Enki. Sin proxy del
/// sistema por el mismo motivo (el filtro tiene que ver la dirección real a la que se conecta). Cuerpos de
/// hasta <see cref="MaxPageBytes"/>, solo tipos de texto, y un tiempo máximo por petición.</para>
///
/// <para><b>Qué no se guarda.</b> Nada en disco: las páginas leídas se recuerdan unos minutos en memoria
/// (<see cref="CacheLifetime"/>) para poder pasar de página sin volver a descargarlas. Las consultas y las
/// direcciones no pasan por el log.</para></summary>
public sealed partial class AiWebService : IDisposable
{
    public const string DuckDuckGoUrl = "https://html.duckduckgo.com/html/";
    public const string BraveSearchUrl = "https://api.search.brave.com/res/v1/web/search";

    public const int MaxResults = 10;
    public const long MaxPageBytes = 3 * 1024 * 1024;
    private const long MaxSearchBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int MaxQueryLength = 400;
    private const int CacheSize = 8;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private static readonly string UserAgent = "Mozilla/5.0 (compatible; Enki/"
        + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0") + "; +assistant)";

    /// <summary>Lo que se sabe leer. Lo demás (PDF, imágenes, binarios) se rechaza antes de descargarlo.</summary>
    private static readonly HashSet<string> TextMediaTypes = new(StringComparer.Ordinal)
    {
        "text/html", "application/xhtml+xml", "text/plain", "text/markdown", "text/x-markdown", "text/csv",
        "application/json", "application/ld+json", "text/xml", "application/xml", "application/rss+xml",
        "application/atom+xml"
    };

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _clock;
    private readonly LinkedList<(Uri Key, AiWebPage Page, DateTime ReadUtc)> _cache = new();
    private readonly object _cacheGate = new();

    /// <param name="httpHandler">Transporte para las pruebas. Null —lo normal— usa el real, con el filtro de
    /// destino de <see cref="RemoteImagePolicy"/> puesto.</param>
    public AiWebService(SettingsService settings, AiApiKeyStore braveKeys, HttpMessageHandler? httpHandler = null,
        Func<DateTime>? clock = null)
    {
        _settings = settings;
        BraveKeys = braveKeys;
        _clock = clock ?? (() => DateTime.UtcNow);
        var handler = httpHandler;
        if (handler is null)
        {
            var sockets = RemoteImagePolicy.CreateHandler();
            sockets.UseProxy = false;
            handler = sockets;
        }
        _http = new HttpClient(handler) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>Clave de Brave Search (solo se lee con ese buscador elegido).</summary>
    public AiApiKeyStore BraveKeys { get; }

    public AiWebSearchEngine Engine => _settings.Settings.Ai.WebSearchEngine;

    public static string EngineLabel(AiWebSearchEngine engine) => engine == AiWebSearchEngine.Brave ? "Brave Search" : "DuckDuckGo";

    // ===================== Búsqueda =====================

    public async Task<IReadOnlyList<AiWebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        query = Collapse(query);
        if (query.Length == 0) throw new ArgumentException("The search query is empty.", nameof(query));
        if (query.Length > MaxQueryLength) query = query[..MaxQueryLength];
        maxResults = Math.Clamp(maxResults, 1, MaxResults);

        return await RunAsync(async token =>
        {
            var results = Engine == AiWebSearchEngine.Brave
                ? await SearchBraveAsync(query, maxResults, token).ConfigureAwait(false)
                : await SearchDuckDuckGoAsync(query, token).ConfigureAwait(false);
            return (IReadOnlyList<AiWebSearchResult>)results
                .Where(r => r.Url.Length > 0)
                .DistinctBy(r => r.Url, StringComparer.Ordinal)
                .Take(maxResults)
                .ToList();
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<AiWebSearchResult>> SearchDuckDuckGoAsync(string query, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DuckDuckGoUrl)
        {
            Content = new FormUrlEncodedContent([new("q", query), new("kl", DuckDuckGoRegion()), new("b", string.Empty)])
        };
        request.Headers.Accept.ParseAdd("text/html");
        AddLanguage(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // DuckDuckGo contesta 202 con una página de verificación cuando decide que la petición es automática.
        if (response.StatusCode == HttpStatusCode.Accepted || (int)response.StatusCode == 429)
            throw new AiWebException(AiWebFailure.SearchUnavailable, "challenge", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new AiWebException(AiWebFailure.SearchUnavailable, null, (int)response.StatusCode);
        var html = await ReadLimitedStringAsync(response.Content, ct).ConfigureAwait(false);
        var results = ParseDuckDuckGo(html);
        if (results.Count == 0 && html.Contains("anomaly", StringComparison.OrdinalIgnoreCase))
            throw new AiWebException(AiWebFailure.SearchUnavailable, "challenge");
        return results;
    }

    private async Task<List<AiWebSearchResult>> SearchBraveAsync(string query, int maxResults, CancellationToken ct)
    {
        if (BraveKeys.TryRead() is not { } key) throw new AiWebException(AiWebFailure.SearchKeyMissing);
        var uri = $"{BraveSearchUrl}?q={Uri.EscapeDataString(query)}&count={maxResults.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        AddLanguage(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        switch ((int)response.StatusCode)
        {
            case 401 or 403: throw new AiWebException(AiWebFailure.SearchKeyRejected, null, (int)response.StatusCode);
            case 429: throw new AiWebException(AiWebFailure.SearchRateLimited, null, 429);
            case < 200 or > 299: throw new AiWebException(AiWebFailure.SearchUnavailable, null, (int)response.StatusCode);
        }
        return ParseBrave(await ReadLimitedStringAsync(response.Content, ct).ConfigureAwait(false));
    }

    /// <summary>Resultados de la versión HTML de DuckDuckGo: cada uno es un enlace <c>result__a</c> seguido de su
    /// <c>result__snippet</c>. Los anuncios (que pasan por <c>y.js</c>) se descartan, y los enlaces de
    /// redirección (<c>/l/?uddg=…</c>) se cambian por su destino real.</summary>
    internal static List<AiWebSearchResult> ParseDuckDuckGo(string html)
    {
        var results = new List<AiWebSearchResult>();
        string? title = null, url = null;
        foreach (Match anchor in AnchorRegex().Matches(html))
        {
            var attributes = anchor.Groups["attrs"].Value;
            var classes = AttributeValue(attributes, "class");
            if (Regex.IsMatch(classes, @"\bresult__a\b"))
            {
                if (title is not null && url is not null) results.Add(new AiWebSearchResult(title, url, string.Empty));
                title = TextOfHtml(anchor.Groups["body"].Value);
                url = DuckDuckGoTarget(WebUtility.HtmlDecode(AttributeValue(attributes, "href")));
            }
            else if (Regex.IsMatch(classes, @"\bresult__snippet\b") && title is not null && url is not null)
            {
                results.Add(new AiWebSearchResult(title, url, TextOfHtml(anchor.Groups["body"].Value)));
                title = url = null;
            }
        }
        if (title is not null && url is not null) results.Add(new AiWebSearchResult(title, url, string.Empty));
        return results.Where(r => r.Title.Length > 0).ToList();
    }

    private static string? DuckDuckGoTarget(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        else if (href.StartsWith('/')) href = "https://duckduckgo.com" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.StartsWith("/y.js", StringComparison.Ordinal)) return null;
            var target = QueryValue(uri.Query, "uddg");
            if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out uri)) return null;
        }
        return RemoteImagePolicy.IsAllowedScheme(uri) ? uri.AbsoluteUri : null;
    }

    internal static List<AiWebSearchResult> ParseBrave(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return [];
            var results = new List<AiWebSearchResult>();
            foreach (var item in items.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !RemoteImagePolicy.IsAllowedScheme(uri)) continue;
                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? TextOfHtml(t.GetString()!) : uri.Host;
                var snippet = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? TextOfHtml(d.GetString()!) : string.Empty;
                if (item.TryGetProperty("age", out var age) && age.ValueKind == JsonValueKind.String && age.GetString() is { Length: > 0 } ageText)
                    snippet = snippet.Length > 0 ? $"{ageText} — {snippet}" : ageText;
                results.Add(new AiWebSearchResult(title, uri.AbsoluteUri, snippet));
            }
            return results;
        }
        catch (JsonException)
        {
            throw new AiWebException(AiWebFailure.SearchUnavailable, "malformed response");
        }
    }

    // ===================== Páginas =====================

    /// <summary>Convierte lo que escribió el modelo en una dirección que se puede pedir: acepta que falte el
    /// esquema (<c>ejemplo.org/x</c>), nunca "corrige" otro distinto, y rechaza credenciales en la URL.</summary>
    public static Uri ParseUrl(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 4096) throw new AiWebException(AiWebFailure.InvalidUrl, trimmed);
        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.Contains(':', StringComparison.Ordinal))
            trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var url) || !RemoteImagePolicy.IsAllowedScheme(url)
            || url.Host.Length == 0 || url.UserInfo.Length > 0)
            throw new AiWebException(AiWebFailure.InvalidUrl, trimmed);
        if (WebFetch.IsNonPublicLiteralHost(url) || url.IsLoopback)
            throw new AiWebException(AiWebFailure.PrivateAddress, url.Host);
        return url;
    }

    /// <summary>Descarga y lee <paramref name="url"/>. Una página ya leída hace poco sale de memoria, para que
    /// pedir el siguiente tramo de un texto largo no vuelva a descargarlo.</summary>
    public async Task<AiWebPage> FetchAsync(Uri url, CancellationToken ct)
    {
        if (FromCache(url) is { } cached) return cached;
        var page = await RunAsync(async token =>
        {
            WebFetchResponse response;
            try
            {
                response = await WebFetch.GetAsync(_http, url, MaxPageBytes, MaxRedirects,
                    mediaType => mediaType is null || TextMediaTypes.Contains(mediaType), token, request =>
                    {
                        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
                        AddLanguage(request);
                    }).ConfigureAwait(false);
            }
            catch (WebFetchException ex)
            {
                throw ex.Failure switch
                {
                    WebFetchFailure.PrivateAddress => new AiWebException(AiWebFailure.PrivateAddress, ex.Detail),
                    WebFetchFailure.TooManyRedirects => new AiWebException(AiWebFailure.TooManyRedirects),
                    WebFetchFailure.UnsupportedType => new AiWebException(AiWebFailure.UnsupportedContent, ex.Detail),
                    _ => new AiWebException(AiWebFailure.TooLarge)
                };
            }
            // Analizar varios megas de HTML no debe parar la interfaz: las herramientas se ejecutan en su hilo.
            return await Task.Run(() => Read(response), token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        Remember(url, page);
        return page;
    }

    internal static AiWebPage Read(WebFetchResponse response)
    {
        var text = response.DecodeText();
        var mediaType = response.MediaType ?? (LooksLikeHtml(text) ? "text/html" : "text/plain");
        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            var article = HtmlReadability.ExtractPage(text, response.FinalUrl);
            var title = article.Title is { Length: > 0 } extracted ? extracted.Trim() : response.FinalUrl.Host;
            return new AiWebPage(response.FinalUrl, title, article.Description, article.Published, mediaType,
                article.Markdown, MarkdownLinks(article.Markdown));
        }
        var plain = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return new AiWebPage(response.FinalUrl, response.FinalUrl.Host + response.FinalUrl.AbsolutePath, null, null, mediaType,
            plain, []);
    }

    private static bool LooksLikeHtml(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 1024)).TrimStart();
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Los destinos <c>http</c>/<c>https</c> de los enlaces Markdown de un texto, sin repetir.</summary>
    internal static IReadOnlyList<string> MarkdownLinks(string markdown) =>
        MarkdownLinkRegex().Matches(markdown).Select(m => m.Groups["url"].Value).Distinct(StringComparer.Ordinal).ToList();

    private AiWebPage? FromCache(Uri url)
    {
        lock (_cacheGate)
        {
            var now = _clock();
            for (var node = _cache.First; node is not null;)
            {
                var next = node.Next;
                if (now - node.Value.ReadUtc > CacheLifetime) _cache.Remove(node);
                else if (node.Value.Key == url || node.Value.Page.Url == url) return node.Value.Page;
                node = next;
            }
            return null;
        }
    }

    private void Remember(Uri url, AiWebPage page)
    {
        lock (_cacheGate)
        {
            _cache.AddFirst((url, page, _clock()));
            while (_cache.Count > CacheSize) _cache.RemoveLast();
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Vacía las páginas recordadas (al apagar la IA o cambiar de vault no deben quedar a mano).</summary>
    public void ClearCache()
    {
        lock (_cacheGate) _cache.Clear();
    }

    // ===================== Utilidades =====================

    /// <summary>Un presupuesto de tiempo para toda la operación y una traducción común de los fallos de red. La
    /// cancelación del turno se propaga tal cual; agotar el presupuesto es un fallo más.</summary>
    private static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestTimeout);
        try
        {
            return await work(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AiWebException(AiWebFailure.Timeout);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            throw new AiWebException(AiWebFailure.HttpStatus, null, (int)status);
        }
        catch (HttpRequestException ex)
        {
            // El handler envuelve lo que lanza el filtro de destino al resolver en su propia excepción.
            throw (ex as NonPublicDestinationException ?? ex.InnerException as NonPublicDestinationException) is { } blocked
                ? new AiWebException(AiWebFailure.PrivateAddress, blocked.Host)
                : new AiWebException(AiWebFailure.Unreachable, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UriFormatException or InvalidOperationException)
        {
            throw new AiWebException(AiWebFailure.Unreachable, ex.Message);
        }
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            if (content.Headers.ContentLength > MaxSearchBytes) throw new WebFetchException(WebFetchFailure.TooLarge);
            var bytes = await WebFetch.ReadCappedAsync(content, MaxSearchBytes, ct).ConfigureAwait(false);
            return WebFetch.Decode(bytes, content.Headers.ContentType?.CharSet);
        }
        catch (WebFetchException)
        {
            throw new AiWebException(AiWebFailure.SearchUnavailable, "too large");
        }
    }

    private static void AddLanguage(HttpRequestMessage request)
    {
        var culture = LocalizationService.Instance.CurrentCulture;
        var language = culture.Name.Length > 0 ? culture.Name : "en-US";
        request.Headers.AcceptLanguage.ParseAdd($"{language},{culture.TwoLetterISOLanguageName};q=0.9,en;q=0.7");
    }

    /// <summary>Región de DuckDuckGo (<c>es-es</c>, <c>us-en</c>…) a partir del idioma de la interfaz.</summary>
    private static string DuckDuckGoRegion()
    {
        var culture = LocalizationService.Instance.CurrentCulture;
        var parts = culture.Name.Split('-');
        return parts.Length == 2 && parts[1].All(char.IsAsciiLetter)
            ? $"{parts[1].ToLowerInvariant()}-{parts[0].ToLowerInvariant()}"
            : "wt-wt";
    }

    private static string AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"\b{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : string.Empty;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && pair[..separator] == name) return Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static string TextOfHtml(string html) => Collapse(WebUtility.HtmlDecode(TagRegex().Replace(html, string.Empty)));

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex MarkdownLinkRegex();
}
