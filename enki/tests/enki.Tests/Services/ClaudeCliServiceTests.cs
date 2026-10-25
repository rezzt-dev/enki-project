using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class ClaudeCliServiceTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClaudeCli _fake = new();

    public void Dispose()
    {
        _fake.Dispose();
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ClaudeCliService CreateService(string? cliPath = null, int idleTimeoutSeconds = 30)
    {
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.Ai.ClaudeCliPath = cliPath ?? _fake.ExecutablePath;
        settings.Settings.Ai.IdleTimeoutSeconds = idleTimeoutSeconds;
        return new ClaudeCliService(settings, _storageDirectory);
    }

    private static readonly ClaudeTurnRequest SimpleTurn = new("Hola", "Eres el asistente de Enki.", null, null);

    // ===================== Lógica pura =====================

    [Theory]
    [InlineData("2.1.280 (Claude Code)", "2.1.280")]
    [InlineData("v2.0.1", "2.0.1")]
    [InlineData("not a version", null)]
    [InlineData("", null)]
    public void ParseVersion(string output, string? expected) => Assert.Equal(expected, ClaudeCliService.ParseVersion(output));

    [Fact]
    public void ParseCapabilities_ReadsFlagsFromHelp_IncludingSystemPromptFile()
    {
        var capabilities = ClaudeCliService.ParseCapabilities(FakeClaudeCli.HelpText);

        Assert.Contains("--safe-mode", capabilities);
        Assert.Contains("--tools", capabilities);
        Assert.Contains("--system-prompt-file", capabilities);
        Assert.Contains("--model", capabilities);
    }

    [Fact]
    public void ParseCapabilities_DoesNotConfuseFlagPrefixes()
    {
        var capabilities = ClaudeCliService.ParseCapabilities("  --disallowed-tools <tools>\n  --fallback-model <m>\n  --resume-x\n");

        Assert.DoesNotContain("--tools", capabilities);
        Assert.DoesNotContain("--model", capabilities);
        Assert.DoesNotContain("--resume", capabilities);
    }

    [Fact]
    public void BuildTurnArguments_DisablesToolsAndCustomizations_AndResumes()
    {
        var capabilities = ClaudeCliService.ParseCapabilities(FakeClaudeCli.HelpText);
        var arguments = ClaudeCliService.BuildTurnArguments(
            new ClaudeTurnRequest("p", "sys", "sess-9", "opus"), capabilities, "/tmp/sys.md", systemPromptInStdin: false);

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages"], arguments.Take(5));
        var tools = arguments.IndexOf("--tools");
        Assert.Equal("", arguments[tools + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--disable-slash-commands", arguments);
        Assert.Contains("--safe-mode", arguments);
        Assert.Equal("/tmp/sys.md", arguments[arguments.IndexOf("--system-prompt-file") + 1]);
        Assert.DoesNotContain("--system-prompt", arguments);
        Assert.Equal("opus", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("sess-9", arguments[arguments.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("p", arguments);
    }

    [Fact]
    public void BuildTurnArguments_OnOldCli_FallsBackToDisallowingToolsAndInlinePrompt()
    {
        var arguments = ClaudeCliService.BuildTurnArguments(SimpleTurn,
            new HashSet<string> { "--output-format", "--resume" }, null, systemPromptInStdin: false);

        Assert.Contains("--disallowedTools", arguments);
        Assert.DoesNotContain("--include-partial-messages", arguments);
        Assert.DoesNotContain("--safe-mode", arguments);
        Assert.Equal(SimpleTurn.SystemPrompt, arguments[arguments.IndexOf("--system-prompt") + 1]);
        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("--effort", arguments);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("invalid", "medium")]
    public void BuildTurnArguments_PassesValidatedEffort_WhenCliSupportsIt(string effort, string expected)
    {
        var arguments = ClaudeCliService.BuildTurnArguments(SimpleTurn with { Effort = effort },
            ClaudeCliService.ParseCapabilities(FakeClaudeCli.HelpText), null, true);
        Assert.Equal(expected, arguments[arguments.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void BuildTurnArguments_ThroughBatchWrapper_KeepsSystemPromptOutOfArguments()
    {
        var arguments = ClaudeCliService.BuildTurnArguments(SimpleTurn,
            new HashSet<string> { "--output-format", "--resume" }, null, systemPromptInStdin: true);

        Assert.DoesNotContain("--system-prompt", arguments);
        Assert.DoesNotContain(SimpleTurn.SystemPrompt, arguments);
    }

    [Theory]
    [InlineData("CLAUDECODE", true)]
    [InlineData("CLAUDE_CODE_SESSION_ID", true)]
    [InlineData("CLAUDE_CODE_MESSAGING_TOKEN", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("SUPABASE_SERVICE_KEY", true)]
    [InlineData("ANTHROPIC_API_KEY", false)]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", false)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    public void ExcludedEnvironmentVariables(string name, bool excluded) =>
        Assert.Equal(excluded, ClaudeCliService.IsExcludedEnvironmentVariable(name));

    [Fact]
    public void ParseAuthStatus_ReadsAccountWithoutTokens()
    {
        var status = ClaudeCliService.ParseAuthStatus(
            """{"loggedIn":true,"authMethod":"claude.ai","email":"a@b.c","subscriptionType":"max"}""", "/bin/claude", "2.1.0");

        Assert.Equal(ClaudeCliState.Ready, status.State);
        Assert.Equal("a@b.c", status.AccountEmail);
        Assert.Equal("max", status.SubscriptionType);
        Assert.Equal(ClaudeCliState.SignedOut,
            ClaudeCliService.ParseAuthStatus("""{"loggedIn":false}""", "/bin/claude", "2.1.0").State);
    }

    public static IEnumerable<object?[]> ClassificationCases()
    {
        ClaudeResult Error(string? text = null, int? status = null, params string[] errors) =>
            new(true, "error", text, errors, "s", status, null);

        yield return [Error(errors: "No conversation found with session ID: x"), null, null, "", 1, false, ClaudeFailureKind.SessionNotFound];
        yield return [Error("Claude AI usage limit reached|1792888200"), null, null, "", 1, false, ClaudeFailureKind.RateLimited];
        yield return [null, new ClaudeRateLimit("rejected", null), null, "", 1, false, ClaudeFailureKind.RateLimited];
        yield return [Error(status: 401), null, null, "", 1, false, ClaudeFailureKind.SignedOut];
        yield return [null, null, null, "Invalid API key · Please run /login", 1, false, ClaudeFailureKind.SignedOut];
        yield return [Error("There's an issue with the selected model", 404), null, "model_not_found", "", 1, false, ClaudeFailureKind.ModelUnavailable];
        yield return [Error("Overloaded", 529), null, null, "", 1, false, ClaudeFailureKind.Overloaded];
        yield return [null, null, null, "", -1, true, ClaudeFailureKind.Timeout];
        yield return [null, null, null, "segfault", 139, false, ClaudeFailureKind.Failed];
        yield return [new ClaudeResult(false, "success", "ok", [], "s", null, "completed"), null, null, "", 0, false, ClaudeFailureKind.None];
    }

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Classify(ClaudeResult? result, ClaudeRateLimit? rateLimit, string? assistantError, string stderr, int exitCode,
        bool timedOut, ClaudeFailureKind expected) =>
        Assert.Equal(expected, ClaudeCliService.Classify(result, rateLimit, assistantError, stderr, exitCode, timedOut));

    // ===================== Contra un CLI falso =====================

    [Fact]
    public async Task Detect_ReportsReadyWithAccount()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService();
        ClaudeCliStatus? announced = null;
        service.StatusChanged += s => announced = s;

        var status = await service.DetectAsync();

        Assert.Equal(ClaudeCliState.Ready, status.State);
        Assert.Equal("2.1.280", status.Version);
        Assert.Equal("ana@example.com", status.AccountEmail);
        Assert.Equal(_fake.ExecutablePath, status.ExecutablePath);
        Assert.Same(status, announced);
        Assert.Same(status, service.Status);
    }

    [Fact]
    public async Task Detect_SignedOut()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.AuthJson = """{"loggedIn":false}""";
        File.WriteAllText(Path.Combine(_fake.Root, "auth-exit"), "1");

        Assert.Equal(ClaudeCliState.SignedOut, (await CreateService().DetectAsync()).State);
    }

    [Fact]
    public async Task Detect_TooOldVersion_IsUnsupported()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Version = "1.0.3 (Claude Code)";

        var status = await CreateService().DetectAsync();

        Assert.Equal(ClaudeCliState.Unsupported, status.State);
        Assert.Equal("1.0.3", status.Version);
    }

    [Fact]
    public async Task Detect_MissingConfiguredPath_IsNotInstalled()
    {
        var status = await CreateService(Path.Combine(_storageDirectory, "no-existe")).DetectAsync();

        Assert.Equal(ClaudeCliState.NotInstalled, status.State);
    }

    [Fact]
    public async Task RunTurn_StreamsFragments_SendsPromptByStdin_AndUsesPrivateEmptyFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = CreateService();
        await service.DetectAsync();
        var deltas = new List<string>();

        var result = await service.RunTurnAsync(new ClaudeTurnRequest("Contenido privado de una nota", "INSTRUCCIONES", null, "haiku"),
            e => { if (e is ClaudeTextDelta d) deltas.Add(d.Text); }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Hola mundo", result.Text);
        Assert.Equal("sess-1", result.SessionId);
        Assert.Equal(["Hola", " mundo"], deltas);
        Assert.Equal("Contenido privado de una nota", _fake.Stdin(0));
        Assert.Equal("INSTRUCCIONES", _fake.SystemPrompt(0));
        var arguments = _fake.Arguments(0);
        Assert.DoesNotContain(arguments, a => a.Contains("Contenido privado", StringComparison.Ordinal));
        Assert.Contains("haiku", arguments);
        Assert.True(Directory.Exists(service.WorkingDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(service.WorkingDirectory));
        // El prompt de sistema viajó en un temporal privado que ya no existe.
        var systemPromptFile = arguments[Array.IndexOf(arguments, "--system-prompt-file") + 1];
        Assert.False(File.Exists(systemPromptFile));
    }

    [Fact]
    public async Task RunTurn_DoesNotLeakParentClaudeSessionVariables()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", "padre");
        try
        {
            var service = CreateService();
            await service.DetectAsync();

            await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

            Assert.DoesNotContain("CLAUDE_CODE_SESSION_ID=", _fake.Environment(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_SESSION_ID", null);
        }
    }

    [Fact]
    public async Task RunTurn_ClassifiesFailures()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Output(FakeClaudeCli.SessionNotFoundLines("viejo"));
        _fake.ExitCode = 1;
        var service = CreateService();
        await service.DetectAsync();

        var result = await service.RunTurnAsync(SimpleTurn with { ResumeSessionId = "viejo" }, null, CancellationToken.None);

        Assert.Equal(ClaudeFailureKind.SessionNotFound, result.Failure);
        Assert.Contains("No conversation found", result.FailureDetail);
    }

    [Fact]
    public async Task RunTurn_RateLimitRejection_CarriesResetTime()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Output(
        [
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792888200}}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached","session_id":"s"}"""
        ]);
        _fake.ExitCode = 1;
        var service = CreateService();
        await service.DetectAsync();

        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

        Assert.Equal(ClaudeFailureKind.RateLimited, result.Failure);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792888200), result.RateLimitResetsAt);
    }

    [Fact]
    public async Task RunTurn_IdleCli_TimesOut()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Hangs = true;
        var service = CreateService(idleTimeoutSeconds: 1);
        await service.DetectAsync();

        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

        Assert.Equal(ClaudeFailureKind.Timeout, result.Failure);
    }

    [Fact]
    public async Task RunTurn_Cancellation_Throws()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Hangs = true;
        var service = CreateService();
        await service.DetectAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunTurnAsync(SimpleTurn, null, cancellation.Token));
    }

    [Fact]
    public async Task RunTurn_WhenNotInstalled_FailsWithoutLaunchingAnything()
    {
        var service = CreateService(Path.Combine(_storageDirectory, "no-existe"));

        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

        Assert.Equal(ClaudeFailureKind.NotInstalled, result.Failure);
        Assert.Equal(0, _fake.TurnCount);
    }
    [Fact]
    public void VaultTools_OnlyEnkiMcpAllowed_AndTokenStaysOutOfArguments()
    {
        var capabilities = ClaudeCliService.ParseCapabilities(FakeClaudeCli.HelpText
            + " --mcp-config --allowedTools --setting-sources --permission-mode");
        var access = new ClaudeToolAccess("http://127.0.0.1:12345/mcp", "private-token");
        var request = SimpleTurn with { Tools = access };
        var arguments = ClaudeCliService.BuildTurnArguments(request, capabilities, "/tmp/system.md", false, "/tmp/mcp.json");
        Assert.Equal("", arguments[arguments.IndexOf("--tools") + 1]);
        Assert.Equal("mcp__enki", arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.Equal("", arguments[arguments.IndexOf("--setting-sources") + 1]);
        Assert.Equal("dontAsk", arguments[arguments.IndexOf("--permission-mode") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--safe-mode", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains(access.BearerToken, StringComparison.Ordinal));
        using var config = System.Text.Json.JsonDocument.Parse(ClaudeCliService.BuildMcpConfig(access));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty("enki");
        Assert.Equal(access.Endpoint, server.GetProperty("url").GetString());
        Assert.Equal("Bearer " + access.BearerToken, server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.True(new ClaudeCliStatus(ClaudeCliState.Ready) { Capabilities = capabilities }.SupportsVaultTools);
        capabilities.Remove("--strict-mcp-config");
        Assert.False(new ClaudeCliStatus(ClaudeCliState.Ready) { Capabilities = capabilities }.SupportsVaultTools);
    }

    [Fact]
    public async Task ToolTurn_ConcatenatesTextBeforeAndAfterToolUse()
    {
        if (OperatingSystem.IsWindows()) return;
        _fake.Output(
        [
            """{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Voy a revisar."}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"mcp__enki__list_notes"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"text","text":""}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Listo."}}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Listo.","session_id":"s"}"""
        ]);
        var service = CreateService();
        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("Voy a revisar.\n\nListo.", result.Text);
    }

    // ===================== Clave de API propia =====================

    private (ClaudeCliService Service, TestPolarCredentialStore Store) CreateApiKeyService(string? key)
    {
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.Ai.ClaudeCliPath = _fake.ExecutablePath;
        settings.Settings.Ai.ClaudeUseApi = true;
        var store = new TestPolarCredentialStore();
        if (key is not null) store.Write(new PolarCredentials(key, null));
        return (new ClaudeCliService(settings, _storageDirectory, new AiApiKeyStore(store)), store);
    }

    [Fact]
    public async Task ApiKeyMode_WithoutAKey_AsksForIt_AndDoesNotRunATurn()
    {
        if (OperatingSystem.IsWindows()) return;
        var (service, _) = CreateApiKeyService(null);

        var status = await service.DetectAsync();
        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

        Assert.Equal(ClaudeCliState.SignedOut, status.State);
        Assert.True(status.NeedsApiKey);
        Assert.Equal(ClaudeFailureKind.SignedOut, result.Failure);
        Assert.Equal(0, _fake.TurnCount);
    }

    [Fact]
    public async Task ApiKeyMode_PassesTheKeyOnlyThroughTheChildEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        const string key = "sk-ant-api03-PRUEBA-0123456789abcdef";
        var (service, _) = CreateApiKeyService(key);

        var status = await service.DetectAsync();
        var result = await service.RunTurnAsync(SimpleTurn, null, CancellationToken.None);

        Assert.Equal(ClaudeCliState.Ready, status.State);
        Assert.Equal(ClaudeCliService.ApiKeyAuthMethod, status.AuthMethod);
        Assert.True(result.Succeeded);
        Assert.Contains("ANTHROPIC_API_KEY=" + key, _fake.Environment(0), StringComparison.Ordinal);
        Assert.DoesNotContain(key, string.Join(' ', _fake.Arguments(0)), StringComparison.Ordinal);
        Assert.DoesNotContain(key, _fake.Stdin(0), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEnvironmentOverrides_OnlyWhenThereIsSomethingToPass()
    {
        Assert.Null(ClaudeCliService.BuildEnvironmentOverrides(vaultTools: false, apiKey: null));
        Assert.Equal(["MCP_TOOL_TIMEOUT"], ClaudeCliService.BuildEnvironmentOverrides(true, null)!.Keys);
        Assert.Equal("k", ClaudeCliService.BuildEnvironmentOverrides(false, "k")!["ANTHROPIC_API_KEY"]);
    }
}
