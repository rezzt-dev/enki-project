using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;

namespace enki.Services;

/// <summary>Cuenta de ChatGPT mediante el CLI oficial. Enki nunca lee ni copia sus credenciales.
/// Cada turno es efímero; el historial se reconstruye desde Enki y no depende del almacén de Codex.</summary>
public sealed class CodexCliService
{
    public const string SetupDocumentationUrl = "https://developers.openai.com/codex/cli";
    public const string LoginCommand = "codex login";
    private static readonly string[] RequiredFlags = ["--ignore-user-config", "--ignore-rules", "--ephemeral", "--json", "--sandbox", "--strict-config"];
    private readonly SettingsService _settings;
    private readonly string _workspace;
    private ClaudeCliStatus _status = ClaudeCliStatus.Unknown;

    public CodexCliService(SettingsService settings, string? storageDirectory = null)
    {
        _settings = settings;
        _workspace = Path.Combine(storageDirectory ?? AppDataDirectory.Resolve(), "ai", "codex-workspace");
    }

    public async Task<ClaudeCliStatus> DetectAsync()
    {
        try
        {
            var executable = ClaudeCliLocator.Resolve(_settings.Settings.Ai.CodexCliPath,
                Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codex");
            if (executable is null) return _status = new(ClaudeCliState.NotInstalled);
            var (versionResult, version) = await ProbeAsync(executable, ["--version"]).ConfigureAwait(false);
            var (helpResult, help) = await ProbeAsync(executable, ["exec", "--help"]).ConfigureAwait(false);
            // No se admite degradar silenciosamente el aislamiento en versiones antiguas.
            if (versionResult.ExitCode != 0 || helpResult.ExitCode != 0
                || !RequiredFlags.All(help.Contains))
                return _status = new(ClaudeCliState.Unsupported, executable, version.Trim());
            var (auth, output) = await ProbeAsync(executable, ["login", "status"]).ConfigureAwait(false);
            var authText = output + auth.StandardError;
            if (auth.TimedOut) return _status = new(ClaudeCliState.Unsupported, executable, version.Trim());
            return _status = new(auth.ExitCode == 0 ? ClaudeCliState.Ready : ClaudeCliState.SignedOut,
                executable, version.Trim(), AuthMethod: authText.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
                    ? "ChatGPT" : "API") { VaultToolsSupported = true, EffortSupported = true };
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return _status = new(ClaudeCliState.Unsupported, Detail: ex.Message);
        }
    }

    private Task<(CliProcessResult Result, string StandardOutput)> ProbeAsync(string executable, string[] arguments)
    {
        PrivateDataPermissions.EnsurePrivateDirectory(_workspace);
        return CliProcessRunner.RunToEndAsync(new CliProcessStartInfo
        {
            FileName = executable, Arguments = arguments, WorkingDirectory = _workspace,
            IdleTimeout = TimeSpan.FromSeconds(20), ExcludeEnvironmentVariable = ExcludeVariable
        }, CancellationToken.None);
    }

    internal static List<string> BuildArguments(ClaudeTurnRequest request)
    {
        var args = new List<string>
        {
            "exec", "--strict-config", "--json", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
            "--sandbox", "read-only", "--color", "never"
        };
        void Config(string value) => args.AddRange(["-c", value]);
        Config("approval_policy=\"never\"");
        Config("web_search=\"disabled\"");
        Config("features.shell_tool=false");
        Config("features.apply_patch_freeform=false");
        Config("features.multi_agent=false");
        Config("features.apps=false");
        Config("features.skip_host_skill_discovery=true");
        Config("features.skill_search=false");
        Config("features.hooks=false");
        Config("features.remote_plugin=false");
        Config("features.goals=false");
        Config("features.image_generation=false");
        Config("features.sleep_tool=false");
        Config("features.shell_snapshot=false");
        Config("features.view_image=false");
        Config("project_doc_max_bytes=0");
        Config("model_reasoning_effort=" + JsonSerializer.Serialize(request.Effort));
        if (!string.IsNullOrWhiteSpace(request.Model)) args.AddRange(["--model", request.Model]);
        if (request.Tools is { } tools)
        {
            Config("mcp_servers.enki.url=" + JsonSerializer.Serialize(tools.Endpoint));
            Config("mcp_servers.enki.bearer_token_env_var=\"ENKI_MCP_TOKEN\"");
            Config("mcp_servers.enki.tool_timeout_sec=600");
            Config("mcp_servers.enki.required=true");
            // Solo el MCP de Enki se autoriza en el CLI; los permisos y las confirmaciones del vault
            // se siguen resolviendo en AiVaultToolbox. "auto" aún pediría permiso por cada escritura.
            Config("mcp_servers.enki.default_tools_approval_mode=\"approve\"");
        }
        args.Add("-");
        return args;
    }

    private static bool ExcludeVariable(string name) =>
        name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SUPABASE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GH_TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) && !name.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase);

    public async Task<ClaudeTurnResult> RunTurnAsync(ClaudeTurnRequest request, Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellation)
    {
        var status = _status.State == ClaudeCliState.Unknown ? await DetectAsync().ConfigureAwait(false) : _status;
        if (status.State != ClaudeCliState.Ready)
            return new(string.Empty, null, status.State == ClaudeCliState.SignedOut ? ClaudeFailureKind.SignedOut : ClaudeFailureKind.NotInstalled);
        var parser = new CodexTurnAccumulator(onEvent);
        try
        {
            var process = await CliProcessRunner.RunAsync(new CliProcessStartInfo
            {
                FileName = status.ExecutablePath!, Arguments = BuildArguments(request), WorkingDirectory = _workspace,
                StandardInput = AiPromptBuilder.WrapSystemPromptForStdin(request.SystemPrompt, request.Prompt),
                IdleTimeout = TimeSpan.FromSeconds(_settings.Settings.Ai.IdleTimeoutSeconds),
                Activity = request.Activity, ExcludeEnvironmentVariable = ExcludeVariable,
                EnvironmentOverrides = request.Tools is { } tools
                    ? new Dictionary<string, string> { ["ENKI_MCP_TOKEN"] = tools.BearerToken } : null
            }, parser.OnLine, cancellation).ConfigureAwait(false);
            return parser.Complete(process);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(parser.Text, null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_ConnectionFailed"]);
        }
    }
}

/// <summary>Acumula únicamente mensajes del agente: razonamiento y resultados de herramientas no son texto
/// de la respuesta. Exige el evento terminal además de una salida correcta del proceso.</summary>
internal sealed class CodexTurnAccumulator(Action<ClaudeStreamEvent>? onEvent)
{
    private readonly StringBuilder _text = new();
    private bool _completed;
    private string? _error;
    public string Text => _text.ToString();

    public void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "item.completed" when root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var type) && type.GetString() == "agent_message":
                    var text = item.GetProperty("text").GetString() ?? string.Empty;
                    var delta = (_text.Length > 0 ? "\n\n" : string.Empty) + text;
                    _text.Append(delta);
                    onEvent?.Invoke(new ClaudeTextDelta(delta));
                    break;
                case "turn.completed": _completed = true; break;
                case "turn.failed":
                    _error = root.TryGetProperty("error", out var error) ? error.ToString() : "failed";
                    break;
                case "error":
                    _error = root.TryGetProperty("message", out var message) ? message.GetString() : "failed";
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
    }

    public ClaudeTurnResult Complete(CliProcessResult result)
    {
        if (result.TimedOut) return new(Text, null, ClaudeFailureKind.Timeout);
        if (result.ExitCode == 0 && _completed) return new(Text, null, ClaudeFailureKind.None);
        var error = (_error ?? string.Empty) + result.StandardError;
        var failure = error.Contains("429", StringComparison.OrdinalIgnoreCase) || error.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ? ClaudeFailureKind.RateLimited
            : error.Contains("401", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase)
                || error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ? ClaudeFailureKind.SignedOut
            : error.Contains("model", StringComparison.OrdinalIgnoreCase) && (error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || error.Contains("not supported", StringComparison.OrdinalIgnoreCase)) ? ClaudeFailureKind.ModelUnavailable
            : ClaudeFailureKind.Failed;
        return new(Text, null, failure, LocalizationService.Instance["Ai_ConnectionFailed"]);
    }
}
