using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using enki.Models;

namespace enki.Services;

/// <summary>Un archivo del paquete de diagnóstico: texto ya depurado, tal y como se guardará.</summary>
public sealed record DiagnosticsFile(string Name, string Content)
{
    public int SizeBytes => Encoding.UTF8.GetByteCount(Content);
}

/// <summary>Contenido de un paquete de diagnóstico, revisable antes de guardarlo.</summary>
public sealed record DiagnosticsPackage(IReadOnlyList<DiagnosticsFile> Files)
{
    public int TotalBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>Escribe el paquete como <c>.zip</c> en <paramref name="destination"/>. Primero a un
    /// archivo temporal junto al destino y después se mueve: un fallo a medias no deja un zip corrupto
    /// con el nombre definitivo.</summary>
    public void WriteZip(string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Files)
                {
                    var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(file.Content);
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

/// <summary>Reúne lo mínimo para reportar un fallo sin enseñar las notas (MED-08): versión, sistema, ajustes
/// sin credenciales y los últimos registros recortados. No hay telemetría: el paquete lo genera el usuario a
/// mano, lo ve entero antes de guardarlo y lo envía él, si quiere.
///
/// <para><b>Qué nunca entra.</b> Contenido de notas, tarjetas o recordatorios; rutas del vault, de la
/// carpeta personal o de cualquier otro lugar; nombre de usuario y de equipo; correos, claves, tokens y
/// cadenas largas de aspecto secreto. Se garantiza por dos vías: los ajustes se filtran por lista blanca de
/// forma de valor (solo booleanos, números y textos cortos sin separadores) además de por nombre, y todo el
/// texto —ajustes y registros— pasa por <see cref="Scrub"/>, que sustituye lo conocido (la ruta del vault, la
/// carpeta personal…) y, después, cualquier ruta absoluta o patrón de secreto que quede.</para></summary>
public sealed class DiagnosticsPackageService
{
    /// <summary>Tope de registros incluidos, en bytes de texto ya depurado.</summary>
    public const int MaxLogBytes = 256 * 1024;

    /// <summary>Tope de líneas de registro (las últimas).</summary>
    public const int MaxLogLines = 2000;

    private static readonly string[] SensitiveNameFragments =
    [
        "path", "vault", "token", "key", "secret", "password", "credential", "email", "user", "url", "endpoint",
        "folder", "dir", "session", "note", "file", "account", "workspace", "shortcut", "family", "name"
    ];

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AppSettings _settings;
    private readonly string? _logDirectory;
    private readonly List<(string Value, string Replacement)> _knownLiterals;

    /// <param name="settings">Ajustes actuales; no se modifican.</param>
    /// <param name="logDirectory">Carpeta de los <c>enki-*.log</c>; <see langword="null"/> si no hay registro.</param>
    /// <param name="extraSensitiveValues">Valores adicionales a ocultar allí donde aparezcan.</param>
    public DiagnosticsPackageService(AppSettings settings, string? logDirectory, IEnumerable<string>? extraSensitiveValues = null)
    {
        _settings = settings;
        _logDirectory = logDirectory;
        _knownLiterals = BuildLiterals(settings, extraSensitiveValues ?? []);
    }

    public DiagnosticsPackage Build(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var files = new List<DiagnosticsFile>
        {
            new("resumen.txt", Scrub(BuildSummary(now))),
            new("ajustes.json", Scrub(BuildSettingsJson())),
            new("registros.txt", Scrub(BuildLogs())),
        };
        files.Insert(0, new DiagnosticsFile("LEEME.txt", BuildReadme(files)));
        return new DiagnosticsPackage(files);
    }

    // ───────────────────────────── contenido ─────────────────────────────

    private static string BuildReadme(IReadOnlyList<DiagnosticsFile> files)
    {
        var text = new StringBuilder();
        text.AppendLine("PAQUETE DE DIAGNOSTICO DE ENKI");
        text.AppendLine();
        text.AppendLine("Lo ha generado el usuario a mano y no se ha enviado a ningun sitio. Contiene:");
        foreach (var file in files) text.AppendLine(CultureInfo.InvariantCulture, $"- {file.Name}: {Describe(file.Name)}");
        text.AppendLine();
        text.AppendLine("No contiene notas, tarjetas ni recordatorios; tampoco rutas del vault ni de la carpeta personal,");
        text.AppendLine("nombre de usuario, correos, claves o tokens. Revisalo antes de compartirlo.");
        return text.ToString();
    }

    private static string Describe(string name) => name switch
    {
        "resumen.txt" => "version de Enki, sistema operativo y entorno",
        "ajustes.json" => "ajustes de la aplicacion (solo interruptores, numeros y opciones cortas)",
        "registros.txt" => "ultimas lineas del registro, sin rutas ni credenciales",
        _ => name
    };

    private string BuildSummary(DateTime nowUtc)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Enki: {AppVersion()}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Generado: {nowUtc:yyyy-MM-dd HH:mm} UTC");
        text.AppendLine(CultureInfo.InvariantCulture, $"Sistema: {RuntimeInformation.OSDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Arquitectura: {RuntimeInformation.OSArchitecture} (proceso {RuntimeInformation.ProcessArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $".NET: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Procesadores: {Environment.ProcessorCount}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Memoria disponible: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");
        text.AppendLine(CultureInfo.InvariantCulture, $"Idioma de la aplicacion: {_settings.Language ?? "(sin elegir)"}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Cultura del sistema: {CultureInfo.InstalledUICulture.Name}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Desfase horario: UTC{TimeZoneInfo.Local.GetUtcOffset(nowUtc):hh\\:mm}");
        if (OperatingSystem.IsLinux())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Sesion grafica: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(desconocida)"}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Escritorio: {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "(desconocido)"}");
        }
        text.AppendLine(CultureInfo.InvariantCulture, $"Vault configurado: {(string.IsNullOrWhiteSpace(_settings.VaultPath) ? "no" : "si")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Registro de depuracion: {(_settings.DebugLoggingEnabled ? "activo" : "inactivo")}");
        return text.ToString();
    }

    private string BuildSettingsJson()
    {
        var node = JsonSerializer.SerializeToNode(_settings) as JsonObject;
        var safe = node is null ? new JsonObject() : FilterSettings(node);
        return safe.ToJsonString(Indented);
    }

    /// <summary>Deja solo lo que es seguro por su forma y por su nombre: booleanos, números y textos cortos
    /// sin separadores de ruta ni arrobas, en propiedades cuyo nombre no sugiera datos personales.
    /// Los objetos se recorren un nivel (los ajustes de IA); listas y diccionarios se resumen con su
    /// tamaño, porque suelen contener rutas, nombres o identificadores.</summary>
    internal static JsonObject FilterSettings(JsonObject source, int depth = 0)
    {
        var result = new JsonObject();
        foreach (var (name, value) in source)
        {
            if (IsSensitiveName(name)) continue;
            switch (value)
            {
                case null:
                    break;
                case JsonArray array:
                    result[name] = $"<lista de {array.Count}>";
                    break;
                case JsonObject child when depth == 0:
                    var filtered = FilterSettings(child, depth + 1);
                    if (filtered.Count > 0) result[name] = filtered;
                    break;
                case JsonObject child:
                    result[name] = $"<objeto de {child.Count}>";
                    break;
                case JsonValue scalar:
                    if (IsSafeScalar(scalar)) result[name] = JsonNode.Parse(scalar.ToJsonString());
                    break;
            }
        }
        return result;
    }

    private static bool IsSensitiveName(string name) =>
        SensitiveNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))
        || name.EndsWith("Id", StringComparison.Ordinal);

    private static bool IsSafeScalar(JsonValue value)
    {
        if (value.TryGetValue<bool>(out _) || value.TryGetValue<double>(out _) || value.TryGetValue<long>(out _)) return true;
        if (!value.TryGetValue<string>(out var text)) return false;
        return text.Length <= 40 && !text.Contains('/') && !text.Contains('\\') && !text.Contains('@') && !text.Contains(':', StringComparison.Ordinal)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }

    private string BuildLogs()
    {
        if (_logDirectory is null || !Directory.Exists(_logDirectory)) return "(sin registros)" + Environment.NewLine;

        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "enki-*.log").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            var tail = ReadTail(file, MaxLogBytes * 2);
            lines.InsertRange(0, SplitLines(tail));
            if (lines.Count >= MaxLogLines * 2) break;
        }

        var kept = lines.Skip(Math.Max(0, lines.Count - MaxLogLines)).ToList();
        var scrubbed = Scrub(string.Join(Environment.NewLine, kept));
        // El tope en bytes se aplica sobre el texto ya depurado y por el final: lo más reciente es lo útil.
        while (Encoding.UTF8.GetByteCount(scrubbed) > MaxLogBytes)
        {
            var cut = scrubbed.IndexOf('\n', Math.Max(1, scrubbed.Length - MaxLogBytes));
            scrubbed = cut < 0 ? string.Empty : scrubbed[(cut + 1)..];
            if (scrubbed.Length == 0) break;
        }
        return scrubbed.Length == 0 ? "(sin registros)" + Environment.NewLine : scrubbed + Environment.NewLine;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Length > 0);

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            // Si se empezó a mitad de archivo, la primera línea está cortada: se descarta.
            if (start > 0 && text.IndexOf('\n') is var newline and >= 0) text = text[(newline + 1)..];
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida";

    // ───────────────────────────── depuración ─────────────────────────────

    private static List<(string, string)> BuildLiterals(AppSettings settings, IEnumerable<string> extra)
    {
        var list = new List<(string, string)>();
        void Add(string? value, string replacement)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return;
            list.Add((value, replacement));
            var trimmed = value.TrimEnd('/', '\\');
            if (trimmed != value && trimmed.Length >= 3) list.Add((trimmed, replacement));
            // La misma ruta con el otro separador, por si el registro mezcla las dos formas.
            var swapped = trimmed.Contains('/', StringComparison.Ordinal) ? trimmed.Replace('/', '\\') : trimmed.Replace('\\', '/');
            if (swapped != trimmed) list.Add((swapped, replacement));
        }

        Add(settings.VaultPath, "<vault>");
        foreach (var recent in settings.RecentVaults) Add(recent.Path, "<vault>");
        Add(settings.PolarAccessToken, "<oculto>");
        Add(settings.PolarRefreshToken, "<oculto>");
        Add(settings.PolarEmail, "<correo>");
        Add(settings.PolarUserId, "<oculto>");
        foreach (var value in extra) Add(value, "<oculto>");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
        Add(AppDataDirectory.Resolve(), "<datos>");
        Add(Environment.UserName, "<usuario>");
        Add(Environment.MachineName, "<equipo>");
        return list.OrderByDescending(t => t.Item1.Length).ToList();
    }

    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex Bearer = new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex Assignment = new(@"(?i)\b(api[_-]?key|apikey|token|secret|password|passwd|authorization|credential)s?(""?\s*[:=]\s*""?)[^\s""',;&]+", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex KnownKey = new(@"\b(?:sk|pk|sb|ghp|gho|xox[abp]|AKIA)[-_A-Za-z0-9]{16,}", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex UrlQuery = new(@"(https?://[^\s?#'""<>]*)(?:\?[^\s'""<>]*)?", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex UrlUserInfo = new(@"(https?://)[^/\s@]+@", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex LongSecret = new(@"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_=-]{40,}(?![A-Za-z0-9+/_=-])", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex Guid32 = new(@"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex SourcePath = new(@"\bin (?:[A-Za-z]:)?[^\s'""<>|]*[/\\]([\w.]+\.cs):line (\d+)", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex PlaceholderTail = new(@"(<vault>|<home>|<datos>)(?:[/\\][^'""\r\n<>]*)?", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex WindowsPath = new(@"(?<![\w<])[A-Za-z]:[/\\][^'""<>|?*:\r\n]*|\\\\[\w.$-]+\\[^'""<>|?*:\r\n]*", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex UnixPath = new(@"(?<![\w<>:.\-/])/(?:[^\s'""<>/|]+/)+[^\s'""<>|]*", RegexOptions.Compiled, RegexSafety.MatchTimeout);

    /// <summary>Sustituye lo conocido y, después, lo que se parezca a un secreto o a una ruta absoluta. Se
    /// pasa por aquí todo el texto del paquete.</summary>
    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Los nombres de archivo de código de Enki en las trazas de pila (Foo.cs:line 12) no son datos del
        // usuario y son lo más útil de un informe: se protegen antes de quitar rutas.
        var protectedFrames = new List<string>();
        text = SourcePath.Replace(text, m =>
        {
            protectedFrames.Add($"in {m.Groups[1].Value}:line {m.Groups[2].Value}");
            return $"\u0001{protectedFrames.Count - 1}\u0002";
        });

        foreach (var (value, replacement) in _knownLiterals)
            text = text.Replace(value, replacement, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        // Tras la ruta del vault viene el nombre de una nota o de una carpeta: se borra el resto de la ruta.
        text = PlaceholderTail.Replace(text, "$1");

        text = Jwt.Replace(text, "<token>");
        text = Bearer.Replace(text, "$1 <token>");
        text = Assignment.Replace(text, "$1$2<oculto>");
        text = KnownKey.Replace(text, "<oculto>");
        text = UrlUserInfo.Replace(text, "$1");
        text = UrlQuery.Replace(text, "$1");
        text = Email.Replace(text, "<correo>");
        text = WindowsPath.Replace(text, "<ruta>");
        text = UnixPath.Replace(text, "<ruta>");
        text = Guid32.Replace(text, "<id>");
        text = LongSecret.Replace(text, "<oculto>");

        for (var i = 0; i < protectedFrames.Count; i++)
            text = text.Replace($"\u0001{i}\u0002", protectedFrames[i], StringComparison.Ordinal);
        return text;
    }
}
