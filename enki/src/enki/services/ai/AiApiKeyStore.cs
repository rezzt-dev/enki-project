using System;
using System.IO;

namespace enki.Services;

/// <summary>Clave de API de Anthropic del usuario, para usar Claude con facturación por API en vez de con su
/// suscripción (<see cref="Models.AiSettings.ClaudeUseApi"/>). Vive en el almacén seguro del sistema —DPAPI en
/// Windows, Keychain en macOS, Secret Service en Linux—, el mismo que guarda la sesión de Polar
/// (<see cref="PolarCredentialStore"/>) con su propia entrada: nunca en <c>settings.json</c>, en el vault ni en
/// el log. Solo se lee para pasársela al CLI oficial como <c>ANTHROPIC_API_KEY</c> en su propio proceso.</summary>
public sealed class AiApiKeyStore
{
    private const string Purpose = "anthropic";

    /// <summary>Página de la consola de Anthropic donde el usuario crea y gestiona sus claves.</summary>
    public const string ConsoleUrl = "https://console.anthropic.com/settings/keys";

    private readonly Lazy<IPolarCredentialStore> _store;

    /// <param name="storageDirectory">Raíz de los datos de Enki: distingue la entrada de cada instalación (y la
    /// de desarrollo de la real).</param>
    public AiApiKeyStore(string storageDirectory)
    {
        _store = new Lazy<IPolarCredentialStore>(() => PolarCredentialStore.Create(storageDirectory, Purpose));
    }

    /// <summary>Para las pruebas: un almacén en memoria.</summary>
    internal AiApiKeyStore(IPolarCredentialStore store)
    {
        _store = new Lazy<IPolarCredentialStore>(() => store);
    }

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
        if (!LooksLikeApiKey(apiKey)) throw new ArgumentException("Ai_ApiKey_Invalid", nameof(apiKey));
        _store.Value.Write(new PolarCredentials(apiKey.Trim(), null));
    }

    public void Delete() => _store.Value.Delete();

    /// <summary>Comprobación de forma, no de validez: la clave de verdad la valida Anthropic en el primer turno.
    /// Evita guardar por error otra cosa pegada del portapapeles.</summary>
    public static bool LooksLikeApiKey(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        return key.StartsWith("sk-ant-", StringComparison.Ordinal) && key.Length is >= 24 and <= 512
            && !key.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>Los cuatro últimos caracteres, para que el usuario reconozca qué clave tiene guardada.</summary>
    public static string Mask(string apiKey) => "sk-ant-…" + apiKey[^4..];

    /// <summary>Los fallos esperables del almacén seguro: los mismos que con la sesión de Polar, más una
    /// plataforma sin almacén.</summary>
    internal static bool IsStoreFailure(Exception ex) =>
        ex is PlatformNotSupportedException or InvalidDataException || SettingsService.IsCredentialStoreFailure(ex);
}
