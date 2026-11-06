using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Gemini por la API de Google con la clave del usuario: comprobación de la clave y del catálogo,
/// streaming SSE, clasificación de errores, capacidad y herramientas del vault por el MCP real.</summary>
public sealed class GeminiApiServiceTests : IDisposable
{
    private const string Key = "AIzaSyA-0123456789abcdefghijklmnopqrstu";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly TestPolarCredentialStore _keys = new();

    public GeminiApiServiceTests() => _settings = new SettingsService(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private GeminiApiService Create(HttpClient http, bool withKey = true)
    {
        var store = new AiApiKeyStore(_keys, AiApiKeyKind.Gemini);
        if (withKey) store.Write(Key);
        return new GeminiApiService(_settings, store, http);
    }

    private const string Catalog = """
        {"models":[
          {"name":"models/gemini-flash-latest","displayName":"Gemini Flash Latest","supportedGenerationMethods":["generateContent","countTokens"]},
          {"name":"models/gemini-2.5-pro","displayName":"Gemini 2.5 Pro","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-embedding-001","displayName":"Embedding","supportedGenerationMethods":["embedContent"]},
          {"name":"models/gemini-2.5-flash-preview-tts","displayName":"TTS","supportedGenerationMethods":["generateContent"]},
          {"name":"models/imagen-4.0-generate-001","displayName":"Imagen","supportedGenerationMethods":["predict"]}
        ]}
        """;

    [Fact]
    public async Task Detect_WithoutKey_AsksForIt_WithoutCallingGoogle()
    {
        var requests = 0;
        using var handler = new Handler(_ => { requests++; return Task.FromResult(Json("{}")); });
        using var http = new HttpClient(handler);
        var status = await Create(http, withKey: false).DetectAsync();
        Assert.Equal(ClaudeCliState.SignedOut, status.State);
        Assert.True(status.NeedsApiKey);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Detect_ListsOnlyChatModels_AndSendsKeyInHeaderNeverInUrl()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/v1beta/models", request.RequestUri!.AbsolutePath);
            Assert.DoesNotContain(Key, request.RequestUri.ToString(), StringComparison.Ordinal);
            Assert.Equal(Key, request.Headers.GetValues("x-goog-api-key").Single());
            return Task.FromResult(Json(Catalog));
        });
        using var http = new HttpClient(handler);
        var service = Create(http);
        var status = await service.DetectAsync();
        Assert.Equal(ClaudeCliState.Ready, status.State);
        Assert.True(status.SupportsVaultTools);
        Assert.True(status.SupportsEffort);
        Assert.Equal("AIza…" + Key[^4..], status.AccountEmail);
        Assert.Equal(["gemini-flash-latest", "gemini-2.5-pro"], service.Models.Select(m => m.Id));
        Assert.Equal("Gemini 2.5 Pro", service.Models[1].DisplayName);
    }

    [Fact]
    public async Task Detect_RejectedKey_IsFixedInSettings_AndNetworkFailureIsUnavailable()
    {
        using var rejected = new Handler(_ => Task.FromResult(Error(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}""")));
        using var rejectedHttp = new HttpClient(rejected);
        var status = await Create(rejectedHttp).DetectAsync();
        Assert.Equal(ClaudeCliState.SignedOut, status.State);
        Assert.True(status.NeedsApiKey);
        Assert.False(string.IsNullOrEmpty(status.Detail));

        using var offline = new Handler(_ => throw new HttpRequestException("sin red"));
        using var offlineHttp = new HttpClient(offline);
        var service = Create(offlineHttp);
        Assert.Equal(ClaudeCliState.Unsupported, (await service.DetectAsync()).State);
        Assert.Empty(service.Models);
    }

    [Fact]
    public async Task Turn_StreamsVisibleText_SkipsThoughts_AndUsesDefaultFlashWithEffort()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/v1beta/models/gemini-flash-latest:streamGenerateContent", request.RequestUri!.AbsolutePath);
            Assert.Equal("?alt=sse", request.RequestUri.Query);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("system", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("hola", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
            Assert.Equal("high", body["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
            Assert.Null(body["tools"]);
            return Sse(
                """{"candidates":[{"content":{"role":"model","parts":[{"text":"privado","thought":true},{"text":"Hola"}]}}]}""",
                """{"candidates":[{"content":{"role":"model","parts":[{"text":" mundo"}]},"finishReason":"STOP"}]}""");
        });
        using var http = new HttpClient(handler);
        var deltas = new List<string>();
        var result = await Create(http).RunTurnAsync(new("hola", "system", null, null, Effort: "high"),
            e => { if (e is ClaudeTextDelta delta) deltas.Add(delta.Text); }, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal("Hola mundo", result.Text);
        Assert.Equal(result.Text, string.Concat(deltas));
    }

    [Fact]
    public async Task Turn_StreamWithoutFinishReason_IsFailureWithPartialText()
    {
        using var handler = new Handler(_ => Task.FromResult(Sse(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Parcial"}]}}]}""")));
        using var http = new HttpClient(handler);
        var result = await Create(http).RunTurnAsync(new("hi", "system", null, "gemini-2.5-pro"), null, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("Parcial", result.Text);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED", ClaudeFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", ClaudeFailureKind.ModelUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", ClaudeFailureKind.Overloaded)]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", ClaudeFailureKind.SignedOut)]
    public async Task Turn_ApiErrors_AreClassified(HttpStatusCode code, string status, ClaudeFailureKind expected)
    {
        using var handler = new Handler(_ => Task.FromResult(Error(code,
            $$$"""{"error":{"code":{{{(int)code}}},"message":"boom","status":"{{{status}}}","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"30s"}]}}""")));
        using var http = new HttpClient(handler);
        var before = DateTimeOffset.UtcNow;
        var result = await Create(http).RunTurnAsync(new("hi", "system", null, "gemini-2.5-pro"), null, CancellationToken.None);
        Assert.Equal(expected, result.Failure);
        if (expected == ClaudeFailureKind.RateLimited)
            Assert.InRange(result.RateLimitResetsAt!.Value, before.AddSeconds(29), DateTimeOffset.UtcNow.AddSeconds(31));
        else Assert.Null(result.RateLimitResetsAt);
    }

    [Fact]
    public async Task Turn_ModelRejectingThinking_IsRetriedWithoutIt()
    {
        var bodies = new List<JsonNode>();
        using var handler = new Handler(async request =>
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            return bodies.Count == 1
                ? Error(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Thinking level is not supported for this model.","status":"INVALID_ARGUMENT"}}""")
                : Sse("""{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""");
        });
        using var http = new HttpClient(handler);
        var result = await Create(http).RunTurnAsync(new("hi", "system", null, "gemini-2.0-flash"), null, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(2, bodies.Count);
        Assert.NotNull(bodies[0]["generationConfig"]);
        Assert.Null(bodies[1]["generationConfig"]);
    }

    [Fact]
    public async Task Turn_BlockedBySafety_IsExplained()
    {
        using var handler = new Handler(_ => Task.FromResult(Sse("""{"promptFeedback":{"blockReason":"SAFETY"}}""")));
        using var http = new HttpClient(handler);
        var result = await Create(http).RunTurnAsync(new("hi", "system", null, "gemini-2.5-pro"), null, CancellationToken.None);
        Assert.Equal(ClaudeFailureKind.Failed, result.Failure);
        Assert.False(string.IsNullOrEmpty(result.FailureDetail));
    }

    [Fact]
    public async Task Turn_WithoutKey_FailsAsSignedOut_AndInvalidModelNeverReachesTheUrl()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No debería llamarse"));
        using var http = new HttpClient(handler);
        Assert.Equal(ClaudeFailureKind.SignedOut,
            (await Create(http, withKey: false).RunTurnAsync(new("hi", "s", null, null), null, CancellationToken.None)).Failure);
        Assert.Equal(ClaudeFailureKind.ModelUnavailable,
            (await Create(http).RunTurnAsync(new("hi", "s", null, "../models/x?key=1"), null, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData("gemini-2.5-flash", "low", "thinkingBudget", 1024)]
    [InlineData("gemini-2.5-pro", "medium", "thinkingBudget", -1)]
    public void ThinkingConfig_UsesBudgetsFor2x(string model, string effort, string field, int value) =>
        Assert.Equal(value, GeminiApiService.ThinkingConfig(model, effort)[field]!.GetValue<int>());

    [Theory]
    [InlineData("gemini-flash-latest", "low")]
    [InlineData("gemini-3-pro-preview", "high")]
    public void ThinkingConfig_UsesLevelsFromGemini3(string model, string effort) =>
        Assert.Equal(effort, GeminiApiService.ThinkingConfig(model, effort)["thinkingLevel"]!.GetValue<string>());

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Tools_UseRealMcp_EchoSignatures_AndRespectLiveWritePermissions(bool allowChanges, bool unknownTool)
    {
        var vault = new VaultService(_settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var toolbox = new AiVaultToolbox(vault, new SessionService(vault), new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings);
        using var server = new EnkiMcpServer(toolbox);
        var activities = new List<AiToolActivity>();
        var access = server.Register(new AiToolContext { Scope = AiAccessScope.Vault, ActivityReported = activities.Add });
        using var mcpHttp = new HttpClient();
        var modelRequests = new List<JsonNode>();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mcp")
            {
                using var forwarded = new HttpRequestMessage(HttpMethod.Post, request.RequestUri)
                    { Content = new StringContent(await request.Content!.ReadAsStringAsync(), Encoding.UTF8, "application/json") };
                forwarded.Headers.Authorization = request.Headers.Authorization;
                return await mcpHttp.SendAsync(forwarded);
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            modelRequests.Add(body);
            if (modelRequests.Count == 1)
            {
                var declarations = body["tools"]![0]!["functionDeclarations"]!.AsArray();
                var createNote = declarations.Single(d => d!["name"]!.GetValue<string>() == "create_note")!;
                Assert.Equal("object", createNote["parametersJsonSchema"]!["type"]!.GetValue<string>());
                _settings.Settings.Ai.AllowVaultChanges = allowChanges;
                var name = unknownTool ? "invented_tool" : "create_note";
                return Sse($$$"""{"candidates":[{"content":{"role":"model","parts":[{"text":"Voy."},{"functionCall":{"id":"call-1","name":"{{{name}}}","args":{"title":"Gemini","content":"# Gemini"}},"thoughtSignature":"firma"}]},"finishReason":"STOP"}]}""");
            }
            return Sse("""{"candidates":[{"content":{"role":"model","parts":[{"text":"Listo"}]},"finishReason":"STOP"}]}""");
        });
        using var http = new HttpClient(handler);
        var result = await Create(http).RunTurnAsync(new("Crea una nota", "system", null, "gemini-2.5-flash", access), null,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal("Voy.\n\nListo", result.Text);
        Assert.Equal(2, modelRequests.Count);
        var contents = modelRequests[1]["contents"]!.AsArray();
        Assert.Equal("model", contents[1]!["role"]!.GetValue<string>());
        Assert.Equal("firma", contents[1]!["parts"]![1]!["thoughtSignature"]!.GetValue<string>());
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("call-1", response["id"]!.GetValue<string>());
        var created = Directory.EnumerateFiles(vault.VaultPath, "*.md").Any();
        Assert.Equal(allowChanges && !unknownTool, created);
        Assert.Equal(created, response["response"]!["output"] is not null);
        if (unknownTool) Assert.Empty(activities);
        server.Unregister(access.BearerToken);
    }

    [Fact]
    public async Task Backend_ExposesGeminiCatalog_ForTheModelSelectors_WithProviderDefaultFirst()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(Catalog)));
        using var http = new HttpClient(handler);
        _settings.Settings.Ai.Provider = AiProvider.Gemini;
        var backend = new AiBackendService(new ClaudeCliService(_settings, _directory), _settings, _directory, gemini: Create(http));
        Assert.Equal(ClaudeCliState.Ready, (await backend.DetectAsync()).State);
        var options = AiModelOption.Build(AiProvider.Gemini, backend.Models, "gemini-retired");
        Assert.Equal(["", "gemini-flash-latest", "gemini-2.5-pro", "gemini-retired"], options.Select(o => o.Alias));
        Assert.Equal("Gemini 2.5 Pro", options[2].Label);
        backend.SelectProvider(AiProvider.Claude);
        Assert.Empty(backend.Models);
    }

    [Fact]
    public void Settings_KeepTheGeminiModel_AndOlderFilesStayValid()
    {
        var ai = _settings.Settings.Ai;
        ai.Provider = AiProvider.Gemini;
        ai.SetModel(AiProvider.Gemini, " gemini-2.5-pro ");
        ai.Normalize();
        _settings.Save();
        var reloaded = new SettingsService(_directory).Settings.Ai;
        Assert.Equal(AiProvider.Gemini, reloaded.Provider);
        Assert.Equal("gemini-2.5-pro", reloaded.ModelFor(AiProvider.Gemini));
        Assert.Equal("claude-sonnet-5-5", reloaded.ModelFor(AiProvider.Claude));
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(_directory, "settings.json")), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode code, string text) => new(code)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(params string[] events) => new(HttpStatusCode.OK)
        { Content = new StringContent(string.Concat(events.Select(e => "data: " + e + "\r\n\r\n")), Encoding.UTF8, "text/event-stream") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
