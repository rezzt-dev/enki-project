using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

public enum ClaudeCliState
{
    /// <summary>Todavía no se ha comprobado, o se está comprobando.</summary>
    Unknown,
    /// <summary>No hay ningún ejecutable <c>claude</c> en la ruta configurada ni en las habituales.</summary>
    NotInstalled,
    /// <summary>Hay un ejecutable, pero es demasiado antiguo o no se comporta como el CLI de Claude.</summary>
    Unsupported,
    /// <summary>El CLI está instalado pero no tiene ninguna sesión iniciada.</summary>
    SignedOut,
    Ready
}

/// <summary>Resultado de <see cref="ClaudeCliService.DetectAsync"/>. Los datos de la cuenta salen de
/// <c>claude auth status</c>, que el propio CLI imprime: Enki no lee ni guarda ningún token.</summary>
public sealed record ClaudeCliStatus(
    ClaudeCliState State,
    string? ExecutablePath = null,
    string? Version = null,
    string? AccountEmail = null,
    string? AuthMethod = null,
    string? SubscriptionType = null,
    string? Detail = null)
{
    public static ClaudeCliStatus Unknown { get; } = new(ClaudeCliState.Unknown);

    internal IReadOnlySet<string> Capabilities { get; init; } = new HashSet<string>();
    internal bool? VaultToolsSupported { get; init; }
    internal bool? EffortSupported { get; init; }

    /// <summary>El CLI entiende todo lo necesario para trabajar sobre el vault con las herramientas de Enki
    /// sin ninguna otra: servidor MCP propio, solo esas herramientas permitidas y las integradas apagadas.
    /// Si falta algo, el asistente sigue funcionando, pero solo con texto.</summary>
    public bool SupportsVaultTools => VaultToolsSupported ?? ClaudeCliService.VaultToolFlags.All(Capabilities.Contains);

    public bool SupportsEffort => EffortSupported ?? Capabilities.Contains("--effort");
}

public enum ClaudeFailureKind
{
    None,
    NotInstalled,
    SignedOut,
    RateLimited,
    ModelUnavailable,
    /// <summary>El CLI ya no tiene la conversación que se le pidió retomar (se borró su historial, o el chat
    /// viene de otro equipo). <c>AssistantViewModel</c> lo resuelve solo abriendo una sesión nueva.</summary>
    SessionNotFound,
    Overloaded,
    Timeout,
    Failed
}

/// <summary>Un turno de conversación tal como se le pide al CLI.</summary>
/// <param name="Tools">Servidor MCP del vault con el que el modelo puede actuar en este turno, o
/// <see langword="null"/> para un turno solo de texto.</param>
/// <param name="Activity">Señal con la que quien atiende las herramientas avisa de que el turno sigue vivo.</param>
public sealed record ClaudeTurnRequest(string Prompt, string SystemPrompt, string? ResumeSessionId, string? Model,
    ClaudeToolAccess? Tools = null, CliActivitySignal? Activity = null, string Effort = "medium");

/// <summary>Dónde está el servidor MCP del vault (<see cref="EnkiMcpServer"/>) y el token, válido solo
/// durante un turno, con el que el CLI se identifica ante él.</summary>
public sealed record ClaudeToolAccess(string Endpoint, string BearerToken);

public sealed record ClaudeTurnResult(
    string Text,
    string? SessionId,
    ClaudeFailureKind Failure,
    string? FailureDetail = null,
    DateTimeOffset? RateLimitResetsAt = null)
{
    public bool Succeeded => Failure == ClaudeFailureKind.None;
}

/// <summary>Habla con el CLI oficial de Claude (Claude Code) que el usuario tiene instalado y con su sesión
/// iniciada, lanzándolo como subproceso en modo no interactivo (<c>claude -p</c>). Es la única vía que
/// mantiene a Enki fuera de las credenciales: no ofrece un inicio de sesión propio, no lee
/// <c>~/.claude/</c> ni extrae tokens, y el binario se ejecuta tal cual lo publicó Anthropic. El inicio de
/// sesión ocurre siempre en el flujo del propio CLI (<c>claude auth login</c> en una terminal).
///
/// <para>Cada turno se ejecuta sin las herramientas integradas del CLI (<c>--tools ""</c>: ni shell, ni
/// archivos, ni web), sin comandos ni personalizaciones del usuario y en una carpeta de trabajo privada y
/// vacía. Lo único con lo que el modelo puede actuar son las herramientas del vault que Enki le sirve por
/// MCP (<see cref="EnkiMcpServer"/>), con un token que solo vale para ese turno y limitadas al ámbito del
/// chat; sin ellas (CLI antiguo), el turno es solo texto. Ver <c>.docs/project-info/asistente-ia.md</c>.</para></summary>
public sealed class ClaudeCliService
{
    /// <summary>Nombre del servidor MCP del vault en la configuración del turno: las herramientas llegan al
    /// modelo como <c>mcp__enki__&lt;nombre&gt;</c>.</summary>
    public const string McpServerName = "enki";

    /// <summary>Flags sin los que no se le dan herramientas al modelo (ver
    /// <see cref="ClaudeCliStatus.SupportsVaultTools"/>).</summary>
    internal static readonly string[] VaultToolFlags =
        ["--mcp-config", "--strict-mcp-config", "--allowedTools", "--tools", "--setting-sources"];

    /// <summary>Tiempo máximo que el CLI espera a una herramienta del vault. Cubre de sobra la confirmación
    /// de un borrado definitivo (ver <see cref="AiVaultToolbox.ApprovalTimeout"/>), que es lo único que
    /// puede tardar.</summary>
    private const string McpToolTimeoutMilliseconds = "600000";

    /// <summary>Versión mínima probada. Las anteriores no tienen todos los flags de los que depende el modo
    /// no interactivo con streaming.</summary>
    internal static readonly SemanticVersion MinimumVersion = new(2, 0, 0, null);

    public const string LoginCommand = "claude auth login";
    public const string UnixInstallCommand = "curl -fsSL https://claude.ai/install.sh | bash";
    public const string WindowsInstallCommand = "irm https://claude.ai/install.ps1 | iex";
    public const string SetupDocumentationUrl = "https://code.claude.com/docs/en/setup";
    public const string LegalAndComplianceUrl = "https://code.claude.com/docs/en/legal-and-compliance";
    public const string ConsumerTermsUrl = "https://www.anthropic.com/legal/consumer-terms";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Herramientas integradas que se vetan cuando el CLI no entiende <c>--tools</c>.</summary>
    private const string FallbackDisallowedTools =
        "Bash,Edit,Write,MultiEdit,NotebookEdit,Read,Glob,Grep,WebFetch,WebSearch,Task,TodoWrite";

    /// <summary>Variables que el CLI de Claude usa para saber que lo lanzó otra sesión suya (por ejemplo,
    /// si Enki se abrió desde una terminal de Claude Code o desde VS Code). Heredarlas haría que el hijo
    /// se comportara como un subagente de esa sesión.</summary>
    private static readonly string[] ParentSessionVariables =
    [
        "CLAUDECODE", "CLAUDE_PID", "CLAUDE_EFFORT", "CLAUDE_AGENT_SDK_VERSION", "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_SSE_PORT", "CLAUDE_CODE_EMIT_STARTUP_TIMING", "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING",
        "CLAUDE_CODE_QUESTION_PREVIEW_FORMAT", "CLAUDE_CODE_ENABLE_TASKS", "MCP_CONNECTION_NONBLOCKING"
    ];

    /// <summary>Prefijos de credenciales de otros servicios que no tienen nada que hacer en el proceso del
    /// CLI. Las de Anthropic (<c>ANTHROPIC_*</c>) sí pasan: son configuración del propio usuario para su CLI.</summary>
    private static readonly string[] ForeignSecretPrefixes = ["OPENAI_", "SUPABASE_", "GITHUB_TOKEN", "GH_TOKEN"];

    private readonly SettingsService _settingsService;
    private readonly string _workingDirectory;
    private readonly object _detectLock = new();
    private Task<ClaudeCliStatus>? _pendingDetection;

    /// <param name="storageDirectory">Sustituye a <c>%AppData%/Enki</c> como raíz de la carpeta de trabajo
    /// del CLI — para que los tests no toquen la del desarrollador.</param>
    public ClaudeCliService(SettingsService settingsService, string? storageDirectory = null)
    {
        _settingsService = settingsService;
        StorageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        _workingDirectory = Path.Combine(StorageDirectory, "ai", "claude-workspace");
    }

    /// <summary>Raíz compartida por los transportes, también cuando la composición usa un disco de pruebas.</summary>
    internal string StorageDirectory { get; }

    /// <summary>Último estado conocido. <see cref="ClaudeCliState.Unknown"/> hasta la primera comprobación.</summary>
    public ClaudeCliStatus Status { get; private set; } = ClaudeCliStatus.Unknown;

    /// <summary>Se dispara (desde un hilo de fondo) cada vez que termina una comprobación, para que Ajustes
    /// y el Asistente muestren siempre el mismo estado sin preguntarse el uno al otro.</summary>
    public event Action<ClaudeCliStatus>? StatusChanged;

    /// <summary>Carpeta privada y vacía donde corre el CLI. Es fija —y no un temporal por petición— porque
    /// el CLI agrupa sus conversaciones por carpeta y <c>--resume</c> solo encuentra las de la misma.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>Comprueba si el CLI está instalado, es compatible y tiene sesión iniciada. Varias llamadas
    /// simultáneas comparten la misma comprobación.</summary>
    public Task<ClaudeCliStatus> DetectAsync()
    {
        lock (_detectLock)
        {
            if (_pendingDetection is { IsCompleted: false } pending) return pending;
            _pendingDetection = Task.Run(DetectCoreAsync);
            return _pendingDetection;
        }
    }

    private async Task<ClaudeCliStatus> DetectCoreAsync()
    {
        ClaudeCliStatus status;
        try
        {
            status = await ProbeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Warning("No se pudo ejecutar el CLI de Claude durante la comprobación", ex);
            status = new ClaudeCliStatus(ClaudeCliState.Unsupported, Detail: ex.Message);
        }

        Status = status;
        StatusChanged?.Invoke(status);
        return status;
    }

    private async Task<ClaudeCliStatus> ProbeAsync()
    {
        var executable = ClaudeCliLocator.Resolve(_settingsService.Settings.Ai.ClaudeCliPath);
        if (executable is null) return new ClaudeCliStatus(ClaudeCliState.NotInstalled);

        var (versionResult, versionOutput) = await RunProbeAsync(executable, ["--version"]).ConfigureAwait(false);
        var version = ParseVersion(versionOutput);
        if (versionResult.ExitCode != 0 || version is null)
            return new ClaudeCliStatus(ClaudeCliState.Unsupported, executable,
                Detail: FirstLine(versionResult.StandardError) ?? FirstLine(versionOutput));
        if (!SemanticVersion.TryParse(version, out var parsed) || parsed < MinimumVersion)
            return new ClaudeCliStatus(ClaudeCliState.Unsupported, executable, version);

        var (_, helpOutput) = await RunProbeAsync(executable, ["--help"]).ConfigureAwait(false);
        var capabilities = ParseCapabilities(helpOutput);
        if (!capabilities.Contains("--output-format") || !capabilities.Contains("--resume"))
            return new ClaudeCliStatus(ClaudeCliState.Unsupported, executable, version) { Capabilities = capabilities };

        var (authResult, authOutput) = await RunProbeAsync(executable, ["auth", "status", "--json"]).ConfigureAwait(false);
        var status = ParseAuthStatus(authOutput, executable, version) with { Capabilities = capabilities };
        // Un CLI sin el subcomando de estado responde con error: no se sabe si hay sesión, así que se da por
        // lista y, si no la hubiera, el primer mensaje lo explicará con el error real del CLI.
        if (authResult.ExitCode != 0 && status.State != ClaudeCliState.SignedOut)
            return new ClaudeCliStatus(ClaudeCliState.Ready, executable, version) { Capabilities = capabilities };
        return status;
    }

    private Task<(CliProcessResult Result, string StandardOutput)> RunProbeAsync(string executable, IReadOnlyList<string> arguments)
    {
        PrivateDataPermissions.EnsurePrivateDirectory(_workingDirectory);
        return CliProcessRunner.RunToEndAsync(new CliProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = _workingDirectory,
            IdleTimeout = ProbeTimeout,
            ExcludeEnvironmentVariable = IsExcludedEnvironmentVariable
        }, CancellationToken.None);
    }

    /// <summary>Envía un turno y entrega en <paramref name="onEvent"/> (desde un hilo de fondo) cada evento
    /// según llega. Nunca lanza por un fallo del CLI: lo devuelve clasificado en
    /// <see cref="ClaudeTurnResult.Failure"/>. Sí lanza <see cref="OperationCanceledException"/> si se
    /// cancela <paramref name="cancellationToken"/>, después de matar el proceso.</summary>
    public async Task<ClaudeTurnResult> RunTurnAsync(ClaudeTurnRequest request, Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellationToken)
    {
        var status = Status.State == ClaudeCliState.Unknown ? await DetectAsync().ConfigureAwait(false) : Status;
        if (status.ExecutablePath is not { } executable || status.State is ClaudeCliState.NotInstalled)
            return new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.NotInstalled);
        if (status.State == ClaudeCliState.SignedOut)
            return new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.SignedOut);

        PrivateDataPermissions.EnsurePrivateDirectory(_workingDirectory);
        var useSystemPromptFile = status.Capabilities.Contains("--system-prompt-file");
        // Sin archivo, el prompt de sistema iría como argumento; por un envoltorio .cmd eso pasa por cmd.exe,
        // que reinterpreta &, | o %, así que en ese caso viaja por stdin delante del mensaje.
        var systemPromptInStdin = !useSystemPromptFile && ClaudeCliLocator.IsBatchWrapper(executable);
        var tools = status.SupportsVaultTools ? request.Tools : null;
        string? systemPromptFile = null;
        string? mcpConfigFile = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (useSystemPromptFile)
                systemPromptFile = PrivateTempFile.Write("enki-ai-system-", ".md", Encoding.UTF8.GetBytes(request.SystemPrompt));
            // El token del turno no va nunca en la línea de órdenes (la lista de procesos es legible por otros
            // usuarios del equipo): viaja en un temporal privado que se borra al terminar.
            if (tools is not null)
                mcpConfigFile = PrivateTempFile.Write("enki-ai-mcp-", ".json", Encoding.UTF8.GetBytes(BuildMcpConfig(tools)));

            var arguments = BuildTurnArguments(request, status.Capabilities, systemPromptFile, systemPromptInStdin, mcpConfigFile);
            var stdin = systemPromptInStdin
                ? AiPromptBuilder.WrapSystemPromptForStdin(request.SystemPrompt, request.Prompt)
                : request.Prompt;

            var accumulator = new TurnAccumulator(onEvent);
            var processResult = await CliProcessRunner.RunAsync(new CliProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                StandardInput = stdin,
                WorkingDirectory = _workingDirectory,
                IdleTimeout = TimeSpan.FromSeconds(_settingsService.Settings.Ai.IdleTimeoutSeconds),
                ExcludeEnvironmentVariable = IsExcludedEnvironmentVariable,
                EnvironmentOverrides = tools is null ? null : new Dictionary<string, string> { ["MCP_TOOL_TIMEOUT"] = McpToolTimeoutMilliseconds },
                Activity = request.Activity
            }, accumulator.OnLine, cancellationToken).ConfigureAwait(false);

            var result = accumulator.Complete(processResult);
            // Solo metadatos: el contenido de los prompts y las respuestas nunca pasa por el log.
            LogService.Debug($"[ia] claude-cli: {stopwatch.ElapsedMilliseconds} ms, salida {processResult.ExitCode}, "
                + $"{request.Prompt.Length} car. enviados, {result.Text.Length} recibidos, herramientas {(tools is null ? "no" : "sí")}, "
                + $"resultado {result.Failure}");
            return result;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            LogService.Warning("No se pudo lanzar el CLI de Claude", ex);
            _ = DetectAsync();
            return new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.NotInstalled, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("Fallo de E/S al comunicarse con el CLI de Claude", ex);
            return new ClaudeTurnResult(string.Empty, null, ClaudeFailureKind.Failed, ex.Message);
        }
        finally
        {
            if (systemPromptFile is not null) TryDelete(systemPromptFile);
            if (mcpConfigFile is not null) TryDelete(mcpConfigFile);
        }
    }

    /// <summary>Configuración MCP del turno: un único servidor, el del vault, por HTTP local y con el token del
    /// turno. Es lo único que el CLI carga (<c>--strict-mcp-config</c>): los servidores MCP que el usuario
    /// tenga configurados para su propio uso no llegan a las conversaciones de Enki.</summary>
    internal static string BuildMcpConfig(ClaudeToolAccess tools) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
            {
                [McpServerName] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "http",
                    ["url"] = tools.Endpoint,
                    ["headers"] = new System.Text.Json.Nodes.JsonObject { ["Authorization"] = "Bearer " + tools.BearerToken }
                }
            }
        }.ToJsonString();

    internal static List<string> BuildTurnArguments(ClaudeTurnRequest request, IReadOnlySet<string> capabilities,
        string? systemPromptFile, bool systemPromptInStdin, string? mcpConfigFile = null)
    {
        var arguments = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (capabilities.Contains("--include-partial-messages")) arguments.Add("--include-partial-messages");

        if (capabilities.Contains("--tools")) arguments.AddRange(["--tools", ""]);
        else arguments.AddRange(["--disallowedTools", FallbackDisallowedTools]);
        if (capabilities.Contains("--strict-mcp-config")) arguments.Add("--strict-mcp-config");
        if (capabilities.Contains("--disable-slash-commands")) arguments.Add("--disable-slash-commands");

        if (mcpConfigFile is not null)
        {
            // Las herramientas del vault, y solo ellas, se aprueban de antemano: en modo -p nadie puede
            // contestar a un aviso de permisos, y cualquier otra herramienta queda denegada.
            arguments.AddRange(["--mcp-config", mcpConfigFile, "--allowedTools", "mcp__" + McpServerName]);
            if (capabilities.Contains("--permission-mode")) arguments.AddRange(["--permission-mode", "dontAsk"]);
            // --safe-mode apagaría también este servidor MCP. Sin ningún origen de ajustes no se cargan los
            // hooks, plugins, permisos ni CLAUDE.md del usuario, que es lo que importaba de él.
            if (capabilities.Contains("--setting-sources")) arguments.AddRange(["--setting-sources", ""]);
        }
        // Sin hooks, plugins, CLAUDE.md ni ajustes personalizados del usuario: un hook de su configuración
        // que se ejecutara en cada mensaje de Enki sería una sorpresa, y la sesión no necesita nada de eso.
        else if (capabilities.Contains("--safe-mode")) arguments.Add("--safe-mode");

        if (systemPromptFile is not null) arguments.AddRange(["--system-prompt-file", systemPromptFile]);
        else if (!systemPromptInStdin) arguments.AddRange(["--system-prompt", request.SystemPrompt]);

        if (!string.IsNullOrWhiteSpace(request.Model)) arguments.AddRange(["--model", request.Model.Trim()]);
        if (capabilities.Contains("--effort"))
            arguments.AddRange(["--effort", request.Effort is "low" or "high" ? request.Effort : "medium"]);
        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId)) arguments.AddRange(["--resume", request.ResumeSessionId]);
        return arguments;
    }

    internal static bool IsExcludedEnvironmentVariable(string name) =>
        ParentSessionVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ForeignSecretPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extrae <c>2.1.280</c> de <c>2.1.280 (Claude Code)</c>.</summary>
    internal static string? ParseVersion(string output)
    {
        var token = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null && SemanticVersion.TryParse(token.TrimStart('v'), out _) ? token.TrimStart('v') : null;
    }

    /// <summary>Flags que anuncia <c>claude --help</c>. Los flags cambian entre versiones, así que en vez de
    /// suponerlos por número de versión se pregunta al propio binario cuáles entiende.</summary>
    internal static HashSet<string> ParseCapabilities(string helpOutput)
    {
        var known = new[]
        {
            "--output-format", "--resume", "--include-partial-messages", "--tools", "--strict-mcp-config",
            "--disable-slash-commands", "--safe-mode", "--model", "--system-prompt", "--mcp-config",
            "--allowedTools", "--permission-mode", "--setting-sources", "--effort"
        };
        var capabilities = known.Where(flag => ContainsFlag(helpOutput, flag)).ToHashSet();
        // La ayuda no lista --system-prompt-file por separado: aparece como "--system-prompt[-file]".
        if (ContainsFlag(helpOutput, "--system-prompt-file") || helpOutput.Contains("--system-prompt[-file]", StringComparison.Ordinal))
            capabilities.Add("--system-prompt-file");
        return capabilities;
    }

    private static bool ContainsFlag(string text, string flag)
    {
        var index = 0;
        while ((index = text.IndexOf(flag, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index + flag.Length;
            if (end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '-')) return true;
            index = end;
        }
        return false;
    }

    internal static ClaudeCliStatus ParseAuthStatus(string json, string executable, string version)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var value) && value.ValueKind == JsonValueKind.True;
            string? Get(string name) => root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;
            return new ClaudeCliStatus(loggedIn ? ClaudeCliState.Ready : ClaudeCliState.SignedOut, executable, version,
                Get("email"), Get("authMethod"), Get("subscriptionType"));
        }
        catch (JsonException)
        {
            return new ClaudeCliStatus(ClaudeCliState.Ready, executable, version);
        }
    }

    /// <summary>Convierte el desenlace de un turno en un <see cref="ClaudeFailureKind"/>. El CLI no tiene
    /// códigos de error estables, así que se combinan el código HTTP que reporta, el evento de límite de uso
    /// y, como último recurso, el texto del error.</summary>
    internal static ClaudeFailureKind Classify(ClaudeResult? result, ClaudeRateLimit? rateLimit, string? assistantError,
        string standardError, int exitCode, bool timedOut)
    {
        if (timedOut) return ClaudeFailureKind.Timeout;
        if (result is { IsError: false } && exitCode == 0) return ClaudeFailureKind.None;

        var text = string.Join('\n', new[] { result?.Text, assistantError, standardError }
            .Concat(result?.Errors ?? []).Where(t => !string.IsNullOrWhiteSpace(t)));

        if (text.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
            return ClaudeFailureKind.SessionNotFound;
        if (rateLimit is { IsRejected: true } || result?.ApiErrorStatus == 429 || ContainsAny(text,
                "usage limit", "rate limit", "rate_limit", "limit reached", "limit will reset"))
            return ClaudeFailureKind.RateLimited;
        if (result?.ApiErrorStatus is 401 or 403 || ContainsAny(text, "not logged in", "please run /login",
                "invalid api key", "authentication_error", "oauth token", "/login", "log in again"))
            return ClaudeFailureKind.SignedOut;
        if (result?.ApiErrorStatus == 404 || ContainsAny(text, "model_not_found", "issue with the selected model",
                "unrecognized_model", "not_found_error"))
            return ClaudeFailureKind.ModelUnavailable;
        if (result?.ApiErrorStatus == 529 || ContainsAny(text, "overloaded"))
            return ClaudeFailureKind.Overloaded;
        return ClaudeFailureKind.Failed;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    internal static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo borrar el prompt de sistema temporal del asistente", ex);
        }
    }

    /// <summary>Junta los eventos de una ejecución en un único <see cref="ClaudeTurnResult"/>. Vive aparte
    /// para que el bucle de lectura —que corre en el hilo de fondo del proceso— solo tenga que llamar a
    /// <see cref="OnLine"/>.</summary>
    private sealed class TurnAccumulator(Action<ClaudeStreamEvent>? onEvent)
    {
        private const string ParagraphBreak = "\n\n";

        private readonly StringBuilder _deltas = new();
        private readonly StringBuilder _messages = new();
        private string? _sessionId;
        private ClaudeResult? _result;
        private ClaudeRateLimit? _rateLimit;
        private string? _assistantError;

        public void OnLine(string line)
        {
            if (ClaudeStreamParser.Parse(line) is not { } parsed) return;
            switch (parsed)
            {
                case ClaudeSessionStarted started: _sessionId = started.SessionId; break;
                case ClaudeTextBlockStarted:
                    // Un bloque de texto que sigue a otro (el modelo escribió, usó una herramienta y vuelve a
                    // escribir) empieza párrafo nuevo, en lo acumulado y en lo que ve el usuario.
                    if (_deltas.Length > 0 && !EndsWithBlankLine(_deltas))
                    {
                        _deltas.Append(ParagraphBreak);
                        onEvent?.Invoke(new ClaudeTextDelta(ParagraphBreak));
                    }
                    return;
                case ClaudeTextDelta delta: _deltas.Append(delta.Text); break;
                case ClaudeAssistantMessage message:
                    var separator = _messages.Length > 0 ? ParagraphBreak : string.Empty;
                    _messages.Append(separator).Append(message.Text);
                    _assistantError ??= message.Error;
                    // Sin mensajes parciales, el mensaje completo es lo único que llega: se reenvía como
                    // si fuera un fragmento para que la vista lo pinte igual.
                    if (_deltas.Length == 0 && message.Error is null) onEvent?.Invoke(new ClaudeTextDelta(separator + message.Text));
                    return;
                case ClaudeRateLimit rateLimit: _rateLimit = rateLimit; break;
                case ClaudeResult result:
                    _result = result;
                    _sessionId = result.SessionId ?? _sessionId;
                    break;
            }
            onEvent?.Invoke(parsed);
        }

        private static bool EndsWithBlankLine(StringBuilder text) =>
            text.Length >= 2 && text[^1] == '\n' && text[^2] == '\n';

        public ClaudeTurnResult Complete(CliProcessResult process)
        {
            var failure = Classify(_result, _rateLimit, _assistantError, process.StandardError, process.ExitCode, process.TimedOut);
            // Con herramientas, el "result" del CLI solo trae el último bloque de texto: se prefiere lo
            // acumulado, que tiene la respuesta entera.
            var text = _deltas.Length > 0 ? _deltas.ToString()
                : _messages.Length > 0 ? _messages.ToString()
                : _result is { IsError: false, Text: { } final } ? final : string.Empty;
            if (failure == ClaudeFailureKind.None) return new ClaudeTurnResult(text, _sessionId, failure);

            var detail = FirstLine(_result is { Errors.Count: > 0 } errored ? errored.Errors[0] : null) ?? FirstLine(_result?.Text)
                ?? FirstLine(process.StandardError) ?? (process.ExitCode != 0 ? $"exit {process.ExitCode}" : null);
            if (detail is { Length: > 300 }) detail = detail[..300] + "…";
            return new ClaudeTurnResult(_deltas.ToString(), _sessionId, failure, detail, _rateLimit?.ResetsAt);
        }
    }
}
