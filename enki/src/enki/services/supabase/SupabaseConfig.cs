using System;
using System.Collections.Generic;
using System.IO;

namespace enki.Services;

/// <summary>Lee la configuración pública de Polar generada al compilar y distribuida junto al
/// ejecutable en <c>polar.properties</c>. El empaquetado conserva este archivo sin distribuir
/// el resto de propiedades locales del desarrollador. Sin configuración, las funciones locales
/// siguen disponibles en builds de desarrollo.</summary>
public static class SupabaseConfig
{
    public static string? Url { get; }
    public static string? AnonKey { get; }
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AnonKey);

    static SupabaseConfig()
    {
        var values = Load();
        values.TryGetValue("SUPABASE_URL", out var url);
        values.TryGetValue("SUPABASE_ANON_KEY", out var anonKey);
        Url = NormalizeAndValidateHttpsUrl(url);
        AnonKey = string.IsNullOrWhiteSpace(anonKey) ? null : anonKey;
    }

    /// <summary>Rechaza protocolos distintos de HTTPS para que las llamadas REST y Realtime
    /// no transmitan datos en claro. Una URL inválida deja Polar sin configurar.</summary>
    internal static string? NormalizeAndValidateHttpsUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;

        var trimmed = rawUrl.TrimEnd('/');
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "polar.properties");
        if (!File.Exists(path)) return result;

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                result[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing/unreadable file just means every Polar-linking feature stays disabled.
            LogService.Warning($"No se pudo leer la configuración Supabase de '{path}'", ex);
        }

        return result;
    }
}
