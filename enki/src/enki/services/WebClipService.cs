using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>El resultado de guardar una página web (T3). <see cref="ExtractionComplete"/> en
/// <see langword="false"/> no es un fallo: es la alternativa prevista —nota solo con título y enlace—
/// cuando la página no se deja leer (error del servidor, muro de pago, se pinta entera con JavaScript), y
/// <see cref="PartialReason"/> dice cuál de esas fue para poder contárselo al usuario.</summary>
public sealed record WebClipResult(
    Uri Url,
    string Title,
    string Content,
    bool ExtractionComplete,
    LocalizedText? PartialReason);

/// <summary>La URL no se puede pedir siquiera: esquema no permitido, o apunta a una dirección que no sale
/// de la máquina o de la red del usuario. Se lanza <b>antes</b> de tocar la red.</summary>
public sealed class WebClipRejectedException(LocalizedText reason)
    : Exception(LocalizedTextFormatter.Resolve(reason))
{
    public LocalizedText Reason { get; } = reason;
}

/// <summary>
/// Web clipper: pegar una URL y que salga una nota (T3, .docs/project-info/web-clipper.md).
///
/// <para><b>Qué se permite pedir.</b> Solo <c>http</c>/<c>https</c>, y solo direcciones públicas. Lo
/// segundo se comprueba dos veces a propósito: aquí, cuando el host <em>ya es</em> una IP literal (y
/// entonces se rechaza sin llegar a abrir un socket, que es el criterio de aceptación), y dentro del
/// <see cref="RemoteImagePolicy.CreateHandler"/> que lleva puesto el <see cref="HttpClient"/>, que resuelve
/// el nombre, descarta lo que no sea público y conecta a esa lista ya validada. Esa segunda es la que
/// cierra de verdad el agujero: un dominio público puede resolver a <c>127.0.0.1</c>, y puede cambiar de
/// respuesta entre comprobarlo y conectarse. Reutiliza la misma política que el editor ya aplica a las
/// imágenes remotas en vez de estrenar una propia.</para>
///
/// <para><b>Todo acotado.</b> Un presupuesto de tiempo único para la descarga entera (no por salto),
/// <see cref="MaxRedirects"/> redirecciones seguidas a mano —para poder rechazar el paso de
/// <c>https</c> a <c>http</c>, que el runtime aceptaría—, <see cref="MaxContentBytes"/> de cuerpo cortando
/// al leer y no después, y solo tipos de contenido HTML.</para>
///
/// <para><b>No ejecuta ni descarga nada más.</b> La extracción (<see cref="HtmlReadability"/>) tira los
/// <c>script</c> antes de mirar el documento, y de las imágenes conserva la URL, no los bytes: la nota
/// resultante se pinta con la misma política de imágenes remotas que cualquier otra nota del vault.</para>
///
/// <para>La costura de pruebas es la misma que la de <c>PolarAuthService</c>/<c>SupabaseRestClient</c>: un
/// <see cref="HttpMessageHandler"/> opcional en el constructor, para probar el 403, la redirección, el
/// tiempo excedido, el cuerpo enorme y el tipo no HTML sin red — ver <c>WebClipServiceTests</c>.</para>
/// </summary>
public sealed class WebClipService : IDisposable
{
    /// <summary>Carpeta destino por defecto, en la raíz del vault. Nombre fijo y no traducido a propósito:
    /// es una carpeta real que el usuario ve, sincroniza y puede abrir desde otro editor, así que cambiar
    /// de idioma la aplicación no puede cambiarle el nombre a una carpeta que ya tiene notas dentro.</summary>
    public const string InboxFolderName = "Inbox";

    /// <summary>Tope del cuerpo descargado. Un artículo largo con su HTML entero rara vez pasa de unos
    /// cientos de kilobytes; 2 MB deja margen de sobra sin convertir la captura en una descarga.</summary>
    public const long MaxContentBytes = 2 * 1024 * 1024;

    public const int MaxRedirects = 5;

    /// <summary>Presupuesto total de la captura, redirecciones incluidas — no el <c>Timeout</c> por
    /// petición del <see cref="HttpClient"/>, que encadenando saltos se pagaría una vez por salto.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly string UserAgent =
        "Enki/" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "1.0")
        + " (web clipper)";

    private readonly VaultService _vaultService;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="httpHandler">Transporte alternativo para pruebas. Null —lo normal— usa el real, con el
    /// filtro de destino de <see cref="RemoteImagePolicy"/> puesto.</param>
    public WebClipService(VaultService vaultService, HttpMessageHandler? httpHandler = null)
    {
        _vaultService = vaultService;
        _ownsHttpClient = true;
        _http = new HttpClient(httpHandler ?? RemoteImagePolicy.CreateHandler())
        {
            Timeout = RequestTimeout
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // ===================== Validación de la URL =====================

    /// <summary>Convierte lo que el usuario pegó en una URL que se puede pedir, o explica por qué no.
    /// Acepta que falte el esquema (<c>ejemplo.org/x</c> → <c>https://ejemplo.org/x</c>), que es como se
    /// copia media web, pero <b>no</b> adivina nada más: un esquema distinto de <c>http</c>/<c>https</c>
    /// se rechaza, nunca se "corrige".</summary>
    public static bool TryParseUrl(string? text, out Uri url, out LocalizedText? rejection)
    {
        url = null!;
        rejection = LocalizedTextFormatter.Format("WebClip_Error_InvalidUrl");

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;

        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.Contains(':', StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)) return false;

        if (!RemoteImagePolicy.IsAllowedScheme(parsed))
        {
            rejection = LocalizedTextFormatter.Format("WebClip_Error_UnsupportedScheme", parsed.Scheme);
            return false;
        }
        if (IsNonPublicLiteralHost(parsed))
        {
            rejection = LocalizedTextFormatter.Format("WebClip_Error_PrivateAddress", parsed.Host);
            return false;
        }

        url = parsed;
        rejection = null;
        return true;
    }

    private static bool IsNonPublicLiteralHost(Uri url) => WebFetch.IsNonPublicLiteralHost(url);

    // ===================== Captura =====================

    /// <summary>Descarga <paramref name="url"/> y extrae lo que se pueda. Nunca lanza por un fallo de la
    /// página: un 403, un tiempo excedido, un cuerpo demasiado grande o un tipo que no es HTML devuelven un
    /// <see cref="WebClipResult"/> con <see cref="WebClipResult.ExtractionComplete"/> en
    /// <see langword="false"/>, porque la nota se crea igual con el título y el enlace. Sí lanza
    /// <see cref="WebClipRejectedException"/> (la URL no se debía pedir) y
    /// <see cref="OperationCanceledException"/> (el usuario canceló), que son las dos cosas que no son "la
    /// página no colaboró".</summary>
    public async Task<WebClipResult> ClipAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!RemoteImagePolicy.IsAllowedScheme(url))
            throw new WebClipRejectedException(LocalizedTextFormatter.Format("WebClip_Error_UnsupportedScheme", url.Scheme));
        if (IsNonPublicLiteralHost(url))
            throw new WebClipRejectedException(LocalizedTextFormatter.Format("WebClip_Error_PrivateAddress", url.Host));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestTimeout);

        try
        {
            var (html, finalUrl) = await FetchAsync(url, budget.Token).ConfigureAwait(false);
            var article = HtmlReadability.Extract(html);
            var title = ResolveTitle(article.Title, finalUrl);

            return article.Markdown.Length == 0
                ? Partial(finalUrl, title, LocalizedTextFormatter.Format("WebClip_Partial_NoReadableText"))
                : new WebClipResult(finalUrl, title, BuildNote(finalUrl, title, article), ExtractionComplete: true, null);
        }
        // La cancelación del usuario se distingue del tiempo excedido por cuál de los dos tokens disparó:
        // el suyo cancela de verdad, el presupuesto agotado es un fallo más de la página.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Partial(url, ResolveTitle(null, url), LocalizedTextFormatter.Format("WebClip_Partial_Timeout"));
        }
        catch (WebClipContentException ex)
        {
            return Partial(url, ResolveTitle(null, url), ex.Reason);
        }
        catch (HttpRequestException ex)
        {
            LogService.Warning($"El web clipper no pudo descargar '{url}'", ex);
            var reason = ex.StatusCode is { } status
                ? LocalizedTextFormatter.Format("WebClip_Partial_HttpStatus", (int)status)
                : LocalizedTextFormatter.Format("WebClip_Partial_Unreachable");
            return Partial(url, ResolveTitle(null, url), reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException)
        {
            LogService.Warning($"El web clipper no pudo procesar '{url}'", ex);
            return Partial(url, ResolveTitle(null, url), LocalizedTextFormatter.Format("WebClip_Partial_Unreachable"));
        }
    }

    private static WebClipResult Partial(Uri url, string title, LocalizedText reason) =>
        new(url, title, BuildNote(url, title, article: null), ExtractionComplete: false, reason);

    /// <summary>Un fallo de la página que ya trae su propio motivo traducido (tipo de contenido, tamaño).
    /// Interna y sin salir de este archivo: fuera de aquí solo se ve como un resultado parcial.</summary>
    private sealed class WebClipContentException(LocalizedText reason) : Exception(LocalizedTextFormatter.Resolve(reason))
    {
        public LocalizedText Reason { get; } = reason;
    }

    /// <summary>Descarga con las reglas comunes de <see cref="WebFetch"/> y traduce sus rechazos a los de la
    /// captura: una IP privada tras una redirección es una URL que no se debía pedir; el resto, una página que
    /// no colaboró.</summary>
    private async Task<(string Html, Uri FinalUrl)> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            var response = await WebFetch.GetAsync(_http, url, MaxContentBytes, MaxRedirects,
                mediaType => mediaType is "text/html" or "application/xhtml+xml" or "text/plain" or null, ct).ConfigureAwait(false);
            return (response.DecodeText(), response.FinalUrl);
        }
        catch (WebFetchException ex)
        {
            throw ex.Failure switch
            {
                WebFetchFailure.PrivateAddress => new WebClipRejectedException(
                    LocalizedTextFormatter.Format("WebClip_Error_PrivateAddress", ex.Detail ?? url.Host)),
                WebFetchFailure.TooManyRedirects => new WebClipContentException(LocalizedTextFormatter.Format("WebClip_Partial_TooManyRedirects")),
                WebFetchFailure.UnsupportedType => new WebClipContentException(LocalizedTextFormatter.Format("WebClip_Partial_NotHtml", ex.Detail)),
                _ => new WebClipContentException(LocalizedTextFormatter.Format("WebClip_Partial_TooLarge"))
            };
        }
    }

    /// <summary>Título de la nota. Sin título en la página se usa el dominio, que sigue siendo mejor
    /// identificador que "Nota nueva" cuando el clip acaba en la bandeja de entrada.</summary>
    private static string ResolveTitle(string? extracted, Uri url)
    {
        var title = extracted?.Trim() ?? string.Empty;
        return title.Length > 0 ? title : url.Host;
    }

    // ===================== Nota resultante =====================

    /// <summary>Compone el Markdown de la nota: frontmatter con el origen y la fecha de captura, un
    /// encabezado con el título, una línea de procedencia y —si la hubo— la extracción. Se escribe a mano
    /// y no con YamlDotNet porque el resto del frontmatter que Enki escribe (<see cref="NoteTagHelper"/>)
    /// también se escribe a mano, y mezclar dos generadores sobre el mismo bloque es la forma segura de
    /// que un día dejen de entenderse.</summary>
    private static string BuildNote(Uri url, string title, HtmlArticle? article)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("title: ").Append(YamlScalar(title)).Append('\n');
        builder.Append("source: ").Append(YamlScalar(url.ToString())).Append('\n');
        builder.Append("clipped: ").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(article?.Author))
            builder.Append("author: ").Append(YamlScalar(article.Author!)).Append('\n');
        if (!string.IsNullOrWhiteSpace(article?.Published))
            builder.Append("published: ").Append(YamlScalar(article.Published!)).Append('\n');
        builder.Append("tags: [clip]\n");
        builder.Append("---\n\n");

        builder.Append("# ").Append(title).Append("\n\n");
        builder.Append("> ")
            .Append(LocalizationService.Instance["WebClip_Note_SourceLabel"])
            .Append(": <").Append(url).Append(">\n\n");

        if (!string.IsNullOrWhiteSpace(article?.Description))
            builder.Append('*').Append(article.Description!.Trim()).Append("*\n\n");

        if (!string.IsNullOrWhiteSpace(article?.Markdown)) builder.Append(article.Markdown).Append('\n');
        else builder.Append(LocalizationService.Instance["WebClip_Note_NoContent"]).Append('\n');

        return builder.ToString();
    }

    /// <summary>Escalar YAML seguro: entre comillas dobles siempre, escapando lo único que puede romper
    /// esa forma. Un título con dos puntos, una almohadilla o un salto de línea es lo normal en una página
    /// web, y sin comillas cualquiera de los tres deja el frontmatter ilegible.</summary>
    private static string YamlScalar(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .ReplaceLineEndings(" ") + "\"";

    /// <summary>Escribe la nota en el vault. Separado de <see cref="ClipAsync"/> a propósito: así cancelar
    /// durante la descarga no puede dejar media nota creada, y así se puede probar la extracción sin tocar
    /// el disco.</summary>
    public NoteFile SaveNote(WebClipResult result, string? folderPath = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _vaultService.CreateNote(result.Title, folderPath ?? EnsureInboxFolder(), result.Content);
    }

    /// <summary>Crea <c>&lt;vault&gt;/Inbox</c> si hace falta y devuelve su ruta — el destino por defecto
    /// tanto del clipper como de la captura rápida (T2), que es lo que hace que las dos acaben en el mismo
    /// sitio sin que ninguna tenga que saber de la otra.</summary>
    public string EnsureInboxFolder()
    {
        var inbox = Path.Combine(_vaultService.VaultPath, InboxFolderName);
        Directory.CreateDirectory(inbox);
        return inbox;
    }
}
