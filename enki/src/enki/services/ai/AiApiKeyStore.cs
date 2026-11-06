using System;
using System.IO;
using System.Linq;

namespace enki.Services;

/// <summary>De qué servicio es la clave: cada uno tiene su propia entrada en el almacén seguro y su forma.</summary>
public enum AiApiKeyKind
{
    /// <summary>Clave de Anthropic para Claude con facturación por API (<see cref="Models.AiSettings.ClaudeUseApi"/>).</summary>
    Anthropic,
    /// <summary>Clave de Google AI Studio con la que Enki llama a la API de Gemini (<see cref="GeminiApiService"/>).</summary>
    Gemini,
    /// <summary>Clave de la API de Brave Search, para el acceso a internet del asistente (<see cref="AiWebService"/>).</summary>
    BraveSearch
}

/// <summary>Clave de API del propio usuario para un proveedor del asistente. Vive en el almacén seguro del
/// sistema —DPAPI en Windows, Keychain en macOS, Secret Service en Linux—, el mismo que guarda la sesión de
/// Polar (<see cref="PolarCredentialStore"/>) con una entrada por proveedor: nunca en <c>settings.json</c>, en
/// el vault ni en el log. La de Anthropic solo se le pasa al CLI oficial como <c>ANTHROPIC_API_KEY</c> en su
/// propio proceso; la de Gemini, solo en la cabecera <c>x-goog-api-key</c> de las peticiones a Google; la de
/// Brave Search, solo en la cabecera <c>X-Subscription-Token</c> de las búsquedas.</summary>
public sealed class AiApiKeyStore
{
    /// <summary>Página de la consola de Anthropic donde el usuario crea y gestiona sus claves.</summary>
    public const string AnthropicKeysUrl = "https://console.anthropic.com/settings/keys";

    /// <summary>Página de Google AI Studio donde se crea una clave, también gratuita, con una cuenta de Google.</summary>
    public const string GeminiKeysUrl = "https://aistudio.google.com/apikey";

    /// <summary>Panel de la API de Brave Search, donde se crea la clave (con nivel gratuito).</summary>
    public const string BraveSearchKeysUrl = "https://api-dashboard.search.brave.com/app/keys";

    private readonly Lazy<IPolarCredentialStore> _store;

    /// <param name="storageDirectory">Raíz de los datos de Enki: distingue la entrada de cada instalación (y la
    /// de desarrollo de la real).</param>
    public AiApiKeyStore(string storageDirectory, AiApiKeyKind kind = AiApiKeyKind.Anthropic)
    {
        Kind = kind;
        _store = new Lazy<IPolarCredentialStore>(() => PolarCredentialStore.Create(storageDirectory, Purpose(kind)));
    }

    /// <summary>Para las pruebas: un almacén en memoria.</summary>
    internal AiApiKeyStore(IPolarCredentialStore store, AiApiKeyKind kind = AiApiKeyKind.Anthropic)
    {
        Kind = kind;
        _store = new Lazy<IPolarCredentialStore>(() => store);
    }

    public AiApiKeyKind Kind { get; }

    /// <summary>Dónde crea y gestiona el usuario las claves de este proveedor.</summary>
    public string KeysUrl => Kind switch
    {
        AiApiKeyKind.Gemini => GeminiKeysUrl,
        AiApiKeyKind.BraveSearch => BraveSearchKeysUrl,
        _ => AnthropicKeysUrl
    };

    // "anthropic" es la entrada que ya existía: cambiarla perdería las claves guardadas.
    private static string Purpose(AiApiKeyKind kind) => kind switch
    {
        AiApiKeyKind.Gemini => "gemini",
        AiApiKeyKind.BraveSearch => "brave-search",
        _ => "anthropic"
    };

    /// <summary>La clave guardada, o <see langword="null"/> si no hay ninguna o el almacén no responde (en ese
    /// caso se registra el motivo, sin la clave).</summary>
    public string? TryRead()
    {
        try
        {
            return _store.Value.Read()?.AccessToken is { Length: > 0 } key ? key : null;
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            LogService.Warning("No se pudo leer la clave de API del asistente del almacén seguro", ex);
            return null;
        }
    }

    public bool HasKey => TryRead() is not null;

    /// <summary>Guarda la clave. Lanza si el almacén falla: quien la guarda avisa al usuario.</summary>
    public void Write(string apiKey)
    {
        if (!LooksLikeApiKey(apiKey, Kind)) throw new ArgumentException("Ai_ApiKey_Invalid", nameof(apiKey));
        _store.Value.Write(new PolarCredentials(apiKey.Trim(), null));
    }

    public void Delete() => _store.Value.Delete();

    public bool IsWellFormed(string? value) => LooksLikeApiKey(value, Kind);

    public string MaskKey(string apiKey) => Mask(apiKey, Kind);

    /// <summary>Comprobación de forma, no de validez: la clave de verdad la valida el proveedor en el primer
    /// turno. Evita guardar por error otra cosa pegada del portapapeles.</summary>
    public static bool LooksLikeApiKey(string? value, AiApiKeyKind kind = AiApiKeyKind.Anthropic)
    {
        var key = value?.Trim() ?? string.Empty;
        if (key.Length is < 24 or > 512 || key.Any(char.IsWhiteSpace)) return false;
        // Las claves de AI Studio y de Brave no tienen un prefijo documentado como estable: basta con descartar
        // lo que claramente es otra cosa (una clave de Anthropic u OpenAI, una URL, texto con signos).
        return kind is AiApiKeyKind.Gemini or AiApiKeyKind.BraveSearch
            ? !key.StartsWith("sk-", StringComparison.Ordinal) && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            : key.StartsWith("sk-ant-", StringComparison.Ordinal);
    }

    /// <summary>Los cuatro últimos caracteres, para que el usuario reconozca qué clave tiene guardada.</summary>
    public static string Mask(string apiKey, AiApiKeyKind kind = AiApiKeyKind.Anthropic) =>
        (kind is AiApiKeyKind.Gemini or AiApiKeyKind.BraveSearch ? apiKey[..4] : "sk-ant-") + "…" + apiKey[^4..];

    /// <summary>Los fallos esperables del almacén seguro: los mismos que con la sesión de Polar, más una
    /// plataforma sin almacén.</summary>
    internal static bool IsStoreFailure(Exception ex) =>
        ex is PlatformNotSupportedException or InvalidDataException || SettingsService.IsCredentialStoreFailure(ex);
}
