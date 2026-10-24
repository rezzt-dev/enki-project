using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;

namespace enki.Services;

/// <summary>Chat local por la API de Ollama, con descubrimiento de modelos y streaming NDJSON.
/// Las herramientas se ejecutan por el mismo MCP autenticado que usa Claude: no existe otra vía de escritura.</summary>
public sealed class OllamaService
{
    private static readonly HttpClient LocalHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    public IReadOnlyList<string> Models { get; private set; } = [];

    public OllamaService(SettingsService settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _http = httpClient ?? LocalHttp;
    }

    internal static Uri Endpoint(string endpoint, string path)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/"
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("Ai_LocalEndpointInvalid", nameof(endpoint));
        return new Uri(uri, path);
    }

    public async Task<ClaudeCliStatus> DetectAsync(string? selectedModel = null)
    {
        Models = [];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var endpoint = _settings.Settings.Ai.OllamaEndpoint;
            using var response = await _http.GetAsync(Endpoint(endpoint, "api/tags"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token).ConfigureAwait(false);
            Models = (data?["models"]?.AsArray() ?? []).Where(m => !IsRemote(m)).Select(m => m?["name"]?.GetValue<string>())
                .OfType<string>().Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (Models.Count == 0) return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_LocalEmpty"]);
            var model = selectedModel ?? _settings.Settings.Ai.LocalModel;
            if (string.IsNullOrEmpty(model)) model = Models[0];
            if (!Models.Contains(model, StringComparer.Ordinal))
                return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_LocalModelMissing"]);
            using var show = await _http.PostAsJsonAsync(Endpoint(endpoint, "api/show"), new { model }, timeout.Token).ConfigureAwait(false);
            show.EnsureSuccessStatusCode();
            var info = await show.Content.ReadFromJsonAsync<JsonObject>(timeout.Token).ConfigureAwait(false);
            if (IsRemote(info)) return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_LocalOnly"]);
            var capabilities = info?["capabilities"]?.AsArray();
            return new(ClaudeCliState.Ready, AuthMethod: "Ollama")
                { VaultToolsSupported = capabilities?.Any(c => c?.GetValue<string>() == "tools") == true, EffortSupported = false };
        }
        catch (ArgumentException)
        {
            return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_LocalEndpointInvalid"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_LocalUnavailable"]);
        }
    }

    private static bool IsRemote(JsonNode? model) =>
        !string.IsNullOrEmpty(model?["remote_host"]?.GetValue<string>())
        || !string.IsNullOrEmpty(model?["remote_model"]?.GetValue<string>())
        || (model?["name"]?.GetValue<string>()?.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) ?? false);

    public async Task<ClaudeTurnResult> RunTurnAsync(ClaudeTurnRequest request, Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellation)
    {
        var text = new StringBuilder();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        void Touch() => idle.CancelAfter(TimeSpan.FromSeconds(_settings.Settings.Ai.IdleTimeoutSeconds));
        try
        {
            var endpoint = Endpoint(_settings.Settings.Ai.OllamaEndpoint, "api/chat");
            var model = string.IsNullOrWhiteSpace(request.Model) ? (Models.Count > 0 ? Models[0] : null) : request.Model;
            if (model is null) return new(string.Empty, null, ClaudeFailureKind.ModelUnavailable);
            var messages = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
            // Se comprueba antes de enviar contenido: un alias de Ollama puede apuntar a la nube.
            Touch();
            using var show = await _http.PostAsJsonAsync(Endpoint(_settings.Settings.Ai.OllamaEndpoint, "api/show"),
                new { model }, idle.Token).ConfigureAwait(false);
            if (!show.IsSuccessStatusCode) return new(string.Empty, null, ClaudeFailureKind.ModelUnavailable);
            var info = await show.Content.ReadFromJsonAsync<JsonObject>(idle.Token).ConfigureAwait(false);
            if (IsRemote(info)) return new(string.Empty, null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_LocalOnly"]);
            var supportsTools = info?["capabilities"]?.AsArray().Any(c => c?.GetValue<string>() == "tools") == true;
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
            var tools = supportsTools && request.Tools is { } access
                ? await LoadToolsAsync(access, cancellation).ConfigureAwait(false) : null;
            for (var round = 0; round < 16; round++)
            {
                Touch();
                using var post = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new JsonObject
                    {
                        ["model"] = model, ["messages"] = messages.DeepClone(), ["stream"] = true,
                        ["tools"] = tools?.DeepClone()
                    })
                };
                using var response = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new(text.ToString(), null, response.StatusCode == HttpStatusCode.NotFound
                        ? ClaudeFailureKind.ModelUnavailable : ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_LocalRequestFailed"]);
                using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                var content = new StringBuilder();
                var calls = new JsonArray();
                var done = false;
                while (await reader.ReadLineAsync(idle.Token).ConfigureAwait(false) is { } line)
                {
                    Touch();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var chunk = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException();
                    if (chunk["error"] is not null) throw new JsonException();
                    if (chunk["message"] is JsonObject message)
                    {
                        var delta = message["content"]?.GetValue<string>() ?? string.Empty;
                        content.Append(delta);
                        text.Append(delta);
                        if (text.Length > 2_000_000) throw new JsonException();
                        if (delta.Length > 0) onEvent?.Invoke(new ClaudeTextDelta(delta));
                        if (message["tool_calls"] is JsonArray toolCalls)
                            foreach (var call in toolCalls) calls.Add(call?.DeepClone());
                        if (calls.Count > 32) throw new JsonException();
                    }
                    if (chunk["done"]?.GetValue<bool>() == true) { done = true; break; }
                }
                if (!done) throw new JsonException();
                if (calls.Count == 0) return new(text.ToString(), null, ClaudeFailureKind.None);
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.ToString(), ["tool_calls"] = calls.DeepClone() });
                // La espera de aprobación pertenece al MCP y tiene su propio límite; no es inactividad del modelo.
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                foreach (var call in calls)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var function = call?["function"]?.AsObject() ?? throw new JsonException();
                    var name = function["name"]?.GetValue<string>() ?? throw new JsonException();
                    if (request.Tools is null || tools is null || !tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == name))
                        throw new JsonException();
                    var result = await RpcAsync(request.Tools, "tools/call", new JsonObject
                        { ["name"] = name, ["arguments"] = function["arguments"]?.DeepClone() ?? new JsonObject() }, cancellation).ConfigureAwait(false);
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = name,
                        ["content"] = string.Join("\n", result["content"]!.AsArray().Select(c => c?["text"]?.GetValue<string>())) });
                }
            }
            return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_ToolLimit"]);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new(text.ToString(), null, ClaudeFailureKind.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_LocalRequestFailed"]);
        }
    }

    private async Task<JsonArray> LoadToolsAsync(ClaudeToolAccess access, CancellationToken cancellation)
    {
        var result = await RpcAsync(access, "tools/list", new JsonObject(), cancellation).ConfigureAwait(false);
        return new JsonArray(result["tools"]!.AsArray().Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function", ["function"] = new JsonObject
            {
                ["name"] = t!["name"]!.DeepClone(), ["description"] = t["description"]!.DeepClone(),
                ["parameters"] = t["inputSchema"]!.DeepClone()
            }
        }).ToArray());
    }

    private async Task<JsonObject> RpcAsync(ClaudeToolAccess access, string method, JsonObject parameters, CancellationToken cancellation)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters })
        };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.BearerToken);
        using var response = await _http.SendAsync(post, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellation).ConfigureAwait(false);
        return data?["result"]?.AsObject() ?? throw new JsonException();
    }
}
