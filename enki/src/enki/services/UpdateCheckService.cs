using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Resultado de una comprobación de versión. Distingue los tres desenlaces que la interfaz trata
/// distinto: hay novedad, no la hay, o no se pudo saber.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>Hay una release estable más reciente que la instalada y el usuario no la ha omitido.</summary>
    UpdateAvailable,

    /// <summary>La instalada está al día (o por delante, si es una compilación de desarrollo).</summary>
    UpToDate,

    /// <summary>La comprobación automática se saltó por el límite de una cada 24 h.</summary>
    Throttled,

    /// <summary>No hubo respuesta utilizable: sin red, límite de la API de GitHub, JSON inesperado…</summary>
    Failed
}

/// <param name="Outcome">Qué pasó.</param>
/// <param name="Update">La release nueva, solo con <see cref="UpdateCheckOutcome.UpdateAvailable"/>.</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Update = null);

/// <summary>Comprueba si hay una release más reciente en el repositorio oficial (T9). Solo avisa: nunca
/// descarga ni ejecuta un binario, y lo único que llega a abrirse es la página de la release en el
/// navegador del sistema.
///
/// <para><b>Privacidad.</b> Es una única petición GET anónima a la API pública de GitHub, sin
/// identificadores, sin telemetría y sin cookies: la misma que haría abrir la página de releases a mano.
/// Está desactivada de fábrica (<see cref="AppSettings.CheckUpdatesOnStartup"/>) — la §9 del documento de
/// funcionalidades dejaba abierto el valor por defecto y se elige el que encaja con el resto del producto:
/// con el ajuste apagado, Enki no toca la red por su cuenta jamás, y "Buscar ahora" sigue disponible
/// siempre para quien quiera comprobarlo a mano.</para>
///
/// <para><b>Robustez.</b> Timeout corto y ni un solo camino que propague excepción: un fallo de red en el
/// arranque devuelve <see cref="UpdateCheckOutcome.Failed"/> y el arranque sigue como si nada. Solo la
/// comprobación manual convierte ese <c>Failed</c> en un mensaje visible.</para></summary>
public sealed class UpdateCheckService : IDisposable
{
    /// <summary>Repositorio oficial. Es la única fuente aceptada, y el host del enlace devuelto se
    /// contrasta contra <see cref="ReleasePageHost"/> antes de dárselo a nadie: si un día la API
    /// respondiera con una URL de otro sitio —proxy, respuesta manipulada, error del servidor— el aviso se
    /// descarta en vez de ofrecer un enlace que el usuario abriría confiando en Enki.</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/rezzt-dev/enki-project/releases/latest";

    public const string ReleasesPageUrl = "https://github.com/rezzt-dev/enki-project/releases";

    private const string ReleasePageHost = "github.com";

    /// <summary>Corto a propósito: esto corre en el arranque y no puede retrasarlo. Si GitHub tarda más,
    /// el usuario se enterará en el próximo arranque o al pulsar "Buscar ahora".</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Una comprobación automática cada 24 h como mucho (criterio EARS de T9). La manual no
    /// pasa por aquí: si el usuario pulsa el botón, se comprueba.</summary>
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Tope de las notas de versión que se guardan para enseñar. Una release con un changelog
    /// larguísimo no debe inflar <c>notifications.json</c>.</summary>
    private const int MaxNotesLength = 2000;

    private const string UserAgent = "Enki";

    private readonly SettingsService _settingsService;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTime> _clock;
    private readonly string _apiUrl;

    /// <param name="httpHandler">Transporte alternativo para pruebas; null usa el real.</param>
    /// <param name="clock">Reloj inyectable —mismo patrón de costura de test que el
    /// <c>PopDueReminders(DateTime)</c> de <see cref="ReminderService"/>— para poder comprobar el límite
    /// de una cada 24 h sin esperar un día.</param>
    /// <param name="apiUrl">Endpoint alternativo, solo para pruebas.</param>
    public UpdateCheckService(SettingsService settingsService, HttpMessageHandler? httpHandler = null,
        Func<DateTime>? clock = null, string? apiUrl = null)
    {
        _settingsService = settingsService;
        _clock = clock ?? (() => DateTime.UtcNow);
        _apiUrl = apiUrl ?? LatestReleaseApiUrl;
        _ownsHttpClient = true;
        _http = new HttpClient(httpHandler ?? new HttpClientHandler()) { Timeout = RequestTimeout };
        // GitHub rechaza con 403 cualquier petición sin User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent + "/" + InstalledVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    /// <summary>Versión instalada, leída del ensamblado. <c>Assembly.GetName().Version</c> devuelve cuatro
    /// componentes ("0.1.0.0"); <see cref="SemanticVersion"/> ignora el cuarto.</summary>
    public static string InstalledVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Comprobación del arranque: respeta el ajuste y el límite de una cada 24 h, y nunca habla
    /// si no hay nada que decir.</summary>
    public Task<UpdateCheckResult> CheckOnStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_settingsService.Settings.CheckUpdatesOnStartup)
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Throttled));

        if (_settingsService.Settings.LastUpdateCheckUtc is { } last && _clock() - last < AutomaticCheckInterval)
            return Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Throttled));

        return CheckAsync(cancellationToken);
    }

    /// <summary>Comprobación a petición del usuario ("Buscar ahora" en Ajustes): ni ajuste ni límite de
    /// frecuencia, pero sí se anota la fecha para que la automática del próximo arranque no repita la
    /// petición.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        _settingsService.Settings.LastUpdateCheckUtc = _clock();
        _settingsService.Save();

        var release = await FetchLatestReleaseAsync(cancellationToken);
        if (release is null) return new UpdateCheckResult(UpdateCheckOutcome.Failed);

        return Evaluate(release, InstalledVersion, _settingsService.Settings.SkippedUpdateVersion);
    }

    /// <summary>Decide qué hacer con una release ya descargada. <c>internal static</c> y sin nada de red ni
    /// de ajustes para que los tests puedan recorrer las reglas (prerelease, borrador, versión omitida,
    /// versión igual o anterior) sin montar un servidor.</summary>
    internal static UpdateCheckResult Evaluate(GitHubRelease release, string installedVersion, string? skippedVersion)
    {
        // Un borrador no es una release publicada, y una prerelease solo interesa a quien la busca a
        // propósito — Enki no tiene canal alfa/beta, así que se ignoran las dos.
        if (release.Draft || release.PreRelease) return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);

        if (!SemanticVersion.TryParse(release.TagName, out var latest)) return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        if (latest.IsPreRelease) return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        if (!SemanticVersion.TryParse(installedVersion, out var installed)) return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        if (latest <= installed) return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);

        // "Omitir esta versión" calla solo esa: una posterior vuelve a avisar.
        if (SemanticVersion.TryParse(skippedVersion, out var skipped) && latest <= skipped)
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);

        var url = SafeReleaseUrl(release.HtmlUrl);
        if (url is null)
        {
            LogService.Warning($"Release descartada: su enlace no apunta al repositorio oficial ({ExternalLinkPolicy.Describe(release.HtmlUrl ?? string.Empty)}).");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        var version = latest.ToString();
        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, new UpdateInfo(
            version,
            string.IsNullOrWhiteSpace(release.Name) ? version : release.Name!.Trim(),
            url,
            Trim(release.Body),
            release.PublishedAt?.UtcDateTime));
    }

    /// <summary>La URL de la release solo se acepta si es <c>https</c> y del host del repositorio oficial.
    /// Si no lo es, se devuelve <see langword="null"/> y el aviso entero se descarta — no se "arregla"
    /// cayendo a la página genérica de releases, porque eso enmascararía una respuesta manipulada.</summary>
    private static string? SafeReleaseUrl(string? candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals(ReleasePageHost, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith("." + ReleasePageHost, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;

    private static string Trim(string? notes)
    {
        var text = (notes ?? string.Empty).Trim();
        return text.Length <= MaxNotesLength ? text : text[..MaxNotesLength].TrimEnd() + "…";
    }

    /// <summary>Ni una excepción sale de aquí: cualquier fallo es "no se pudo saber". El motivo técnico
    /// queda en el log, igual que en <c>WebClipService</c>.</summary>
    private async Task<GitHubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(_apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogService.Warning($"Comprobación de actualizaciones: GitHub respondió {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<GitHubRelease>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or UriFormatException)
        {
            LogService.Warning("No se pudo comprobar si hay una versión nueva", ex);
            return null;
        }
    }

    /// <summary>Los campos de <c>releases/latest</c> que se usan. <c>internal</c> para que
    /// <see cref="Evaluate"/> se pueda probar directamente.</summary>
    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool PreRelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
