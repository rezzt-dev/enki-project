using System;
using System.Collections.Generic;
using System.IO;

namespace enki.Services;

/// <summary>Reads <c>SUPABASE_URL</c>/<c>SUPABASE_ANON_KEY</c> from a <c>local.properties</c> file shipped
/// next to the executable (see the <c>None Include</c> in enki.csproj, which copies it there from the repo
/// root at build time). Same file name/format Polar's Android build reads via Gradle
/// (docs/supabase-sync/08-configuracion-y-credenciales.md) — adapted to a plain runtime read here since a
/// desktop app has no BuildConfig codegen step. Never committed to git (see root .gitignore); if the file
/// is missing (fresh checkout with no credentials yet), <see cref="IsConfigured"/> is false and every
/// Polar-linking feature stays disabled rather than throwing.</summary>
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

    /// <summary>Rejects anything that isn't <c>https://</c> instead of trimming and returning it as-is —
    /// a scheme other than https would silently degrade every Polar-linking feature to plain text, both the
    /// REST calls here and the Realtime websocket (<see cref="PolarRealtimeService"/>, which builds its
    /// <c>wss://</c> URL by replacing <c>https://</c> in this same value). Treating a non-https URL as
    /// unconfigured (<see cref="IsConfigured"/> becomes <see langword="false"/>) is safer than trying to
    /// "fix" it, since a misconfigured <c>local.properties</c> is a build/ops mistake worth surfacing loudly
    /// rather than silently working over an insecure channel (docs/analisis-proyecto/roadmap.md, Fase 4).
    /// <c>internal</c> purely so this pure function can be unit-tested directly — the static constructor
    /// itself can't be re-run per test case.</summary>
    internal static string? NormalizeAndValidateHttpsUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;

        var trimmed = rawUrl.TrimEnd('/');
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
    }

    private static Dictionary<string, string> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "local.properties");
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
        catch
        {
            // Missing/unreadable file just means every Polar-linking feature stays disabled.
        }

        return result;
    }
}
