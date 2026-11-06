using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace enki.Services;

/// <summary>Encuentra el ejecutable <c>claude</c> que el usuario instaló. Enki no lo instala, no lo
/// empaqueta y no lo modifica: solo lo busca. El orden es la ruta elegida en Ajustes, después el
/// <c>PATH</c>, y por último las carpetas donde lo dejan los instaladores oficiales — una app de
/// escritorio lanzada desde el Dock de macOS o desde el menú de un escritorio Linux no hereda el
/// <c>PATH</c> del shell del usuario, así que sin ellas no lo encontraría aunque <c>claude</c> funcione en
/// su terminal.</summary>
internal static class ClaudeCliLocator
{

    /// <summary>Ruta absoluta del ejecutable, o <see langword="null"/> si no está en ningún sitio conocido.
    /// Una ruta configurada que no existe no cae a la búsqueda automática: el usuario eligió esa, y
    /// encontrar otra distinta sin decírselo escondería el problema.</summary>
    public static string? Resolve(string? configuredPath) =>
        Resolve(configuredPath, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string? Resolve(string? configuredPath, string? pathVariable, bool isWindows,
        string userProfile, string appData, string localAppData, string executableName = "claude")
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim().Trim('"');
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
        }

        var names = isWindows ? new[] { executableName + ".exe", executableName + ".cmd", executableName + ".bat" } : new[] { executableName };
        var separator = isWindows ? ';' : ':';
        var pathDirectories = (pathVariable ?? string.Empty)
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return pathDirectories.Concat(WellKnownDirectories(isWindows, userProfile, appData, localAppData))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .SelectMany(directory => names.Select(name => SafeCombine(directory, name)))
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
    }

    /// <summary>Carpetas en las que dejan el binario el instalador nativo (<c>~/.local/bin</c>), la
    /// instalación local antigua (<c>~/.claude/local</c>), npm global y Homebrew.</summary>
    private static IEnumerable<string> WellKnownDirectories(bool isWindows, string userProfile, string appData,
        string localAppData)
    {
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "bin");
            yield return Path.Combine(userProfile, ".claude", "local");
        }

        if (isWindows)
        {
            if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "npm");
            if (!string.IsNullOrEmpty(localAppData)) yield return Path.Combine(localAppData, "Programs", "claude");
            yield break;
        }

        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, ".npm-global", "bin");
            yield return Path.Combine(userProfile, ".volta", "bin");
            yield return Path.Combine(userProfile, ".local", "share", "pnpm");
            yield return Path.Combine(userProfile, ".bun", "bin");
        }
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/usr/bin";
    }

    private static string? SafeCombine(string directory, string name)
    {
        try
        {
            return Path.Combine(directory, name);
        }
        catch (ArgumentException)
        {
            // Una entrada del PATH con caracteres inválidos no debe tumbar la búsqueda entera.
            return null;
        }
    }

    /// <summary>Los envoltorios <c>.cmd</c>/<c>.bat</c> de npm en Windows pasan por <c>cmd.exe</c>, que
    /// reinterpreta los argumentos: con ellos el texto largo nunca va como argumento.</summary>
    public static bool IsBatchWrapper(string executablePath) =>
        executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
