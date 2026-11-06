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

public sealed class AiBackendServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public AiBackendServiceTests() => _settings = new SettingsService(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SettingsAndHistory_KeepIndependentModels_AndMigrateClaudeDefaults()
    {
        var ai = _settings.Settings.Ai;
        ai.SetModel(AiProvider.OpenAi, "my-openai-model");
        ai.SetModel(AiProvider.Ollama, "qwen3:8b");
        ai.Provider = AiProvider.OpenAi;
        ai.ConsentAcceptedUtc = DateTime.UtcNow;
        ai.Enabled = true;
        _settings.Save();
        var reloaded = new SettingsService(_directory).Settings.Ai;
        Assert.Equal(AiProvider.OpenAi, reloaded.Provider);
        Assert.Equal("my-openai-model", reloaded.OpenAiModel);
        Assert.Equal("qwen3:8b", reloaded.LocalModel);
        Assert.Equal("claude-sonnet-5-5", reloaded.DefaultModel);
        Assert.Equal(AiProvider.Claude, new AiChatSession().Provider);
        var chats = new AiChatService(_directory);
        var session = new AiChatSession { Provider = AiProvider.Ollama, Model = "qwen3:8b" };
        chats.Save(session);
        Assert.Equal(AiProvider.Ollama, Assert.Single(new AiChatService(_directory).Sessions).Provider);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1:11434/api")]
    [InlineData("http://user:password@localhost:11434")]
    [InlineData("http://localhost:11434?x=1")]
    [InlineData("file:///tmp")]
    public void Ollama_RejectsNonlocalOrAmbiguousAddresses(string endpoint) =>
        Assert.Throws<ArgumentException>(() => OllamaService.Endpoint(endpoint, "api/chat"));

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void Ollama_AcceptsLoopback(string endpoint) =>
        Assert.Equal("/api/chat", OllamaService.Endpoint(endpoint, "api/chat").AbsolutePath);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ollama_DiscoversModels_AndToolsFromSelectedModel(bool hasTools)
    {
        using var handler = new Handler(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/api/tags"
            ? "{\"models\":[{\"name\":\"qwen3:8b\"}]}"
            : hasTools ? "{\"capabilities\":[\"tools\",\"completion\"]}" : "{\"capabilities\":[\"completion\"]}")));
        using var http = new HttpClient(handler);
        var service = new OllamaService(_settings, http);
        var status = await service.DetectAsync();
        Assert.Equal(ClaudeCliState.Ready, status.State);
        Assert.Equal(hasTools, status.SupportsVaultTools);
        Assert.False(status.SupportsEffort);
        Assert.Equal("qwen3:8b", Assert.Single(service.Models));
    }

    [Fact]
    public async Task Ollama_EmptyServer_DoesNotPretendReady()
    {
        using var handler = new Handler(_ => Task.FromResult(Json("{\"models\":[]}")));
        using var http = new HttpClient(handler);
        var service = new OllamaService(_settings, http);
        Assert.Equal(ClaudeCliState.Unsupported, (await service.DetectAsync()).State);
        Assert.Empty(service.Models);
    }

    [Fact]
    public async Task Ollama_StreamsOnlyContent_AndRequiresTerminalEvent()
    {
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/show") return Json("{\"capabilities\":[\"completion\"]}");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("qwen3:8b", body["model"]!.GetValue<string>());
            Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
            return Json("{\"message\":{\"thinking\":\"private\",\"content\":\"Hola\"},\"done\":false}\n"
                + "{\"message\":{\"content\":\" mundo\"},\"done\":true}\n");
        });
        using var http = new HttpClient(handler);
        var service = new OllamaService(_settings, http);
        var deltas = new List<string>();
        var result = await service.RunTurnAsync(new("hola", "system", null, "qwen3:8b"),
            e => { if (e is ClaudeTextDelta delta) deltas.Add(delta.Text); }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("Hola mundo", result.Text);
        Assert.Equal(result.Text, string.Concat(deltas));
    }

    [Fact]
    public async Task Ollama_TruncatedStream_IsFailureWithPartialText()
    {
        using var handler = new Handler(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/api/show"
            ? "{\"capabilities\":[\"completion\"]}" : "{\"message\":{\"content\":\"Partial\"},\"done\":false}\n")));
        using var http = new HttpClient(handler);
        var result = await new OllamaService(_settings, http).RunTurnAsync(new("hi", "system", null, "qwen3:8b"), null, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("Partial", result.Text);
    }

    [Fact]
    public async Task Ollama_CancellationPropagates_AndProviderCannotChangeDuringTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        _settings.Settings.Ai.Provider = AiProvider.Ollama;
        var backend = new AiBackendService(new ClaudeCliService(_settings, _directory), _settings, _directory,
            new OllamaService(_settings, http));
        using var cancellation = new CancellationTokenSource();
        var turn = backend.RunTurnAsync(new("hi", "system", null, "qwen3:8b"), null, cancellation.Token);
        await started.Task;
        backend.SelectProvider(AiProvider.OpenAi);
        Assert.Equal(AiProvider.Ollama, backend.Provider);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        Assert.False(backend.IsBusy);
    }

    [Fact]
    public async Task Assistant_LocalChat_RestoresProviderAndHistory_WithoutMixingClaudeSessionIds()
    {
        var prompts = new List<string>();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Json("{\"models\":[{\"name\":\"qwen3:8b\"}]}");
            if (request.RequestUri.AbsolutePath == "/api/show") return Json("{\"capabilities\":[\"completion\"]}");
            prompts.Add((JsonNode.Parse(await request.Content!.ReadAsStringAsync())!)["messages"]![1]!["content"]!.GetValue<string>());
            return Json("{\"message\":{\"content\":\"Respuesta local\"},\"done\":true}\n");
        });
        using var http = new HttpClient(handler);
        _settings.Settings.Ai.Enabled = true;
        _settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        _settings.Settings.Ai.ClaudeCliPath = Path.Combine(_directory, "missing-claude");
        var claude = new ClaudeCliService(_settings, _directory);
        var backend = new AiBackendService(claude, _settings, _directory, new OllamaService(_settings, http));
        var status = new ClaudeCliStatusViewModel(claude, a => a(), backend);
        var vault = new VaultService(_settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var chats = new AiChatService(_directory);
        var vm = new AssistantViewModel(claude, chats, _settings, vault, new NotificationService(_settings, _directory),
            status, dispatch: a => a());
        vm.SelectedProvider = vm.ProviderOptions.Single(p => p.Provider == AiProvider.Ollama);
        await status.RefreshAsync();
        vm.DraftText = "Mi color es verde";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.False(vm.HasError, vm.ErrorMessage);
        vm.DraftText = "¿Cuál es mi color?";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("Mi color es verde", prompts[1]);
        Assert.Contains("Respuesta local", prompts[1]);
        var saved = Assert.Single(chats.Sessions);
        Assert.Equal(AiProvider.Ollama, saved.Provider);
        Assert.Null(saved.CliSessionId);
        vm.SelectedProvider = vm.ProviderOptions.Single(p => p.Provider == AiProvider.Claude);
        Assert.Empty(vm.Messages);
        vm.SelectSessionCommand.Execute(vm.Sessions.Single());
        Assert.Equal(AiProvider.Ollama, backend.Provider);
        Assert.Equal(4, vm.Messages.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ollama_ToolsUseRealMcp_AndRespectLiveWritePermissions(bool allowChanges)
    {
        var vault = new VaultService(_settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var toolbox = new AiVaultToolbox(vault, sessions, new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"),
            new ReminderService(_directory), _settings);
        using var server = new EnkiMcpServer(toolbox);
        var activities = new List<AiToolActivity>();
        var access = server.Register(new AiToolContext { Scope = AiAccessScope.Vault, ActivityReported = activities.Add });
        using var mcpHttp = new HttpClient();
        var chatRequests = 0;
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/mcp")
            {
                Assert.Equal(access.BearerToken, request.Headers.Authorization!.Parameter);
                using var forwarded = new HttpRequestMessage(HttpMethod.Post, request.RequestUri)
                    { Content = new StringContent(await request.Content!.ReadAsStringAsync(), Encoding.UTF8, "application/json") };
                forwarded.Headers.Authorization = request.Headers.Authorization;
                return await mcpHttp.SendAsync(forwarded);
            }
            if (request.RequestUri.AbsolutePath == "/api/show") return Json("{\"capabilities\":[\"completion\",\"tools\"]}");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            chatRequests++;
            if (chatRequests == 1)
            {
                Assert.Contains(body["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() == "create_note");
                _settings.Settings.Ai.AllowVaultChanges = allowChanges;
                return Json("{\"message\":{\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"create_note\",\"arguments\":{\"title\":\"Local\",\"content\":\"# Local\"}}}]},\"done\":true}\n");
            }
            Assert.Equal("tool", body["messages"]!.AsArray().Last()!["role"]!.GetValue<string>());
            return Json("{\"message\":{\"content\":\"Listo\"},\"done\":true}\n");
        });
        using var http = new HttpClient(handler);
        var result = await new OllamaService(_settings, http).RunTurnAsync(new("Crea una nota", "system", null,
            "qwen3:8b", access), null, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(2, chatRequests);
        Assert.Equal(allowChanges, Directory.EnumerateFiles(vault.VaultPath, "*.md").Any());
        if (allowChanges) Assert.NotEmpty(activities);
        server.Unregister(access.BearerToken);
    }

    [Fact]
    public async Task Ollama_RemoteAliases_AreRejectedBeforeSendingConversation()
    {
        var requestedPaths = new List<string>();
        using var handler = new Handler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"remote_host\":\"https://ollama.com\",\"capabilities\":[\"completion\"]}"));
        });
        using var http = new HttpClient(handler);
        var result = await new OllamaService(_settings, http).RunTurnAsync(new("private", "private", null,
            "cloud-alias"), null, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("/api/show", Assert.Single(requestedPaths));
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => _send = (request, _) => send(request);
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
}
