using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;

namespace enki.Services;

/// <summary>Modelo de Gemini que la clave del usuario puede usar, tal como lo lista la API.</summary>
public sealed record GeminiModel(string Id, string DisplayName);

/// <summary>Gemini por la API oficial de Google (<c>generativelanguage.googleapis.com</c>) con la clave de Google
/// AI Studio del propio usuario, guardada en el almacén seguro (<see cref="AiApiKeyStore"/>).
/// <para>Es una clave y no la cuenta de Google del usuario porque Google no permite que una aplicación de
/// terceros use los planes de consumo (gratuito, Google AI Pro —también el de estudiantes— o Ultra) a través de
/// Gemini CLI ni de Antigravity CLI, y recomienda para ese caso una clave de AI Studio, que también tiene nivel
/// gratuito. Ver <c>.docs/project-info/asistente-ia.md</c>, "Configurar Gemini".</para>
/// <para>Como Ollama, reconstruye el contexto desde el historial de Enki en cada turno y ejecuta las
/// herramientas por el mismo MCP autenticado que el resto de proveedores (<see cref="EnkiMcpClient"/>): no hay
/// otra vía de escritura sobre el vault.</para></summary>
public sealed class GeminiApiService
{
    public const string DocumentationUrl = "https://ai.google.dev/gemini-api/docs/api-key";
    public const string TermsUrl = "https://ai.google.dev/gemini-api/terms";

    /// <summary>Alias que Google mantiene apuntando al Flash más reciente: tiene nivel gratuito y no hay que
    /// actualizar Enki cada vez que sale una versión.</summary>
    internal const string PreferredDefaultModel = "gemini-flash-latest";

    private const int MaxRounds = 16;
    private const int MaxCallsPerRound = 32;
    private const int MaxResponseChars = 2_000_000;

    private static readonly Uri ApiBase = new("https://generativelanguage.googleapis.com/v1beta/");

    /// <summary>Respeta el proxy del sistema (la API es remota), pero no sigue redirecciones: la clave no debe
    /// viajar a otro host.</summary>
    private static readonly HttpClient ApiHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    // Variantes que no sirven para un chat de texto con herramientas.
    private static readonly string[] NonChatMarkers =
        ["embedding", "tts", "image", "live", "audio", "robotics", "computer-use", "aqa", "learnlm"];

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly HttpClient _mcpHttp;

    /// <param name="httpClient">Para las pruebas: atiende tanto a la API como al MCP del vault.</param>
    public GeminiApiService(SettingsService settings, AiApiKeyStore apiKeys, HttpClient? httpClient = null)
    {
        _settings = settings;
        ApiKeys = apiKeys;
        _http = httpClient ?? ApiHttp;
        _mcpHttp = httpClient ?? EnkiMcpClient.LoopbackHttp;
    }

    public AiApiKeyStore ApiKeys { get; }

    /// <summary>Modelos de chat disponibles para la clave guardada, de la última comprobación.</summary>
    public IReadOnlyList<GeminiModel> Models { get; private set; } = [];

    public async Task<ClaudeCliStatus> DetectAsync()
    {
        Models = [];
        if (ApiKeys.TryRead() is not { } key) return KeyMissing();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = new List<GeminiModel>();
            string? pageToken = null;
            for (var page = 0; page < 10; page++)
            {
                var path = "models?pageSize=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, path));
                request.Headers.Add("x-goog-api-key", key);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return StatusFor(await GeminiApiError.ReadAsync(response, timeout.Token).ConfigureAwait(false), key);
                var data = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token).ConfigureAwait(false);
                models.AddRange((data?["models"]?.AsArray() ?? []).Select(ToChatModel).OfType<GeminiModel>());
                pageToken = data?["nextPageToken"]?.GetValue<string>();
                if (string.IsNullOrEmpty(pageToken)) break;
            }
            Models = models.DistinctBy(m => m.Id, StringComparer.Ordinal).ToArray();
            if (Models.Count == 0) return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_Gemini_NoModels"]);
            return Ready(ApiKeys.MaskKey(key));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException
            or InvalidOperationException)
        {
            return new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_Gemini_Unavailable"]);
        }
    }

    /// <param name="maskedKey">Solo los últimos caracteres, para que el usuario reconozca qué clave está en uso.</param>
    private static ClaudeCliStatus Ready(string maskedKey) =>
        new(ClaudeCliState.Ready, AccountEmail: maskedKey, AuthMethod: "API") { VaultToolsSupported = true, EffortSupported = true };

    private static ClaudeCliStatus KeyMissing(string? detail = null) =>
        new(ClaudeCliState.SignedOut, AuthMethod: "API", Detail: detail)
            { NeedsApiKey = true, VaultToolsSupported = true, EffortSupported = true };

    /// <summary>Una comprobación que falla no deja el chat "listo": una clave rechazada se arregla en Ajustes y
    /// una región sin servicio o una caída de Google se explican tal cual.</summary>
    private ClaudeCliStatus StatusFor(GeminiApiError error, string key) => error.Classify() switch
    {
        ClaudeFailureKind.SignedOut => KeyMissing(LocalizationService.Instance["Ai_Gemini_KeyRejected"]),
        // Listar modelos no consume cuota: si aun así se agotó, la clave es válida y el chat lo explicará.
        ClaudeFailureKind.RateLimited => Ready(ApiKeys.MaskKey(key)),
        _ when error.IsRegionUnsupported => new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_Gemini_Region"]),
        _ => new(ClaudeCliState.Unsupported, Detail: LocalizationService.Instance["Ai_Gemini_Unavailable"])
    };

    internal static GeminiModel? ToChatModel(JsonNode? model)
    {
        var name = model?["name"]?.GetValue<string>();
        if (name is null || !name.StartsWith("models/gemini-", StringComparison.Ordinal)) return null;
        var id = name["models/".Length..];
        if (!IsValidModelId(id) || NonChatMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase))) return null;
        var methods = model?["supportedGenerationMethods"]?.AsArray();
        if (methods?.Any(m => m?.GetValue<string>() == "generateContent") != true) return null;
        var label = model?["displayName"]?.GetValue<string>();
        return new(id, string.IsNullOrWhiteSpace(label) ? id : label.Trim());
    }

    /// <summary>El identificador acaba en la ruta de la petición: solo se admiten los caracteres que usa Google.</summary>
    internal static bool IsValidModelId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_');

    /// <summary>Modelo del turno: el del chat o, vacío, el Flash más reciente que la clave pueda usar.</summary>
    internal string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        if (Models.Count == 0 || Models.Any(m => m.Id == PreferredDefaultModel)) return PreferredDefaultModel;
        return Models.FirstOrDefault(m => m.Id.Contains("flash", StringComparison.Ordinal) && !m.Id.Contains("lite", StringComparison.Ordinal))?.Id
            ?? Models[0].Id;
    }

    /// <summary>La capacidad del chat (ligera, media, alta) en el formato de cada generación: Gemini 3 y
    /// posteriores usan niveles; 2.x, un presupuesto de tokens (-1 = el que decida el modelo).</summary>
    internal static JsonObject ThinkingConfig(string model, string effort)
    {
        var level = effort is "low" or "high" ? effort : "medium";
        return model.StartsWith("gemini-2", StringComparison.Ordinal) || model.StartsWith("gemini-1", StringComparison.Ordinal)
            ? new JsonObject { ["thinkingBudget"] = level switch { "low" => 1024, "high" => 16384, _ => -1 } }
            : new JsonObject { ["thinkingLevel"] = level };
    }

    public async Task<ClaudeTurnResult> RunTurnAsync(ClaudeTurnRequest request, Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellation)
    {
        if (ApiKeys.TryRead() is not { } key) return new(string.Empty, null, ClaudeFailureKind.SignedOut);
        var model = ResolveModel(request.Model);
        if (!IsValidModelId(model)) return new(string.Empty, null, ClaudeFailureKind.ModelUnavailable);
        var text = new StringBuilder();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        void Touch() => idle.CancelAfter(TimeSpan.FromSeconds(_settings.Settings.Ai.IdleTimeoutSeconds));
        try
        {
            var tools = request.Tools is { } access
                ? await EnkiMcpClient.ListToolsAsync(_mcpHttp, access, cancellation).ConfigureAwait(false) : null;
            var toolNames = tools?.Select(t => t?["name"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            var declarations = tools is null ? null : new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t!["name"]!.DeepClone(),
                ["description"] = t["description"]?.DeepClone(),
                ["parametersJsonSchema"] = t["inputSchema"]?.DeepClone()
            }).ToArray());
            var contents = new JsonArray(UserContent(new JsonArray(new JsonObject { ["text"] = request.Prompt })));
            JsonObject? thinking = ThinkingConfig(model, request.Effort);
            var endpoint = new Uri(ApiBase, "models/" + model + ":streamGenerateContent?alt=sse");
            for (var round = 0; round < MaxRounds; round++)
            {
                Touch();
                var body = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }) },
                    ["contents"] = contents.DeepClone()
                };
                if (declarations is { Count: > 0 })
                    body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations.DeepClone() });
                if (thinking is not null) body["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinking.DeepClone() };
                using var post = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
                post.Headers.Add("x-goog-api-key", key);
                using var response = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await GeminiApiError.ReadAsync(response, idle.Token).ConfigureAwait(false);
                    // Un modelo antiguo o un alias pueden no aceptar el nivel de capacidad: se repite sin él en vez
                    // de dejar el chat inservible con ese modelo.
                    if (round == 0 && thinking is not null && error.IsThinkingRejected)
                    {
                        thinking = null;
                        round--;
                        continue;
                    }
                    return error.ToResult(text.ToString());
                }
                var turn = await ReadStreamAsync(response, text, onEvent, Touch, idle.Token).ConfigureAwait(false);
                if (turn.Error is { } streamError) return streamError.ToResult(text.ToString());
                if (turn.Blocked) return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_Gemini_Blocked"]);
                if (turn.FinishReason is null) throw new JsonException("La respuesta terminó sin finishReason.");
                if (turn.FinishReason == "MALFORMED_FUNCTION_CALL")
                    return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_Gemini_RequestFailed"]);
                if (turn.Calls.Count == 0) return new(text.ToString(), null, ClaudeFailureKind.None);
                if (request.Tools is null || turn.Calls.Count > MaxCallsPerRound)
                    return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_ToolLimit"]);

                // Las partes del modelo vuelven tal cual: Gemini exige recibir sus firmas de razonamiento
                // (thoughtSignature) junto a cada llamada a herramienta.
                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = turn.Parts });
                // La espera de aprobación pertenece al MCP y tiene su propio límite; no es inactividad del modelo.
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                var results = new JsonArray();
                var images = new List<McpImage>();
                foreach (var call in turn.Calls)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var name = call["name"]?.GetValue<string>() ?? string.Empty;
                    // Un nombre inventado se le devuelve como error para que se corrija, sin llegar al servidor.
                    var outcome = toolNames.Contains(name)
                        ? await EnkiMcpClient.CallToolAsync(_mcpHttp, request.Tools, name, call["args"], cancellation).ConfigureAwait(false)
                        : new McpToolResult("Unknown tool: " + name, true, []);
                    var functionResponse = new JsonObject
                    {
                        ["name"] = name,
                        ["response"] = new JsonObject { [outcome.IsError ? "error" : "output"] = outcome.Text }
                    };
                    if (call["id"] is { } id) functionResponse["id"] = id.DeepClone();
                    results.Add(new JsonObject { ["functionResponse"] = functionResponse });
                    images.AddRange(outcome.Images);
                }
                // Las imágenes de una lectura (una foto, páginas de un PDF) van como partes inlineData del mismo turno,
                // detrás de las respuestas de función: así las ve cualquier modelo Gemini con visión.
                foreach (var image in images)
                    results.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = image.Base64 } });
                contents.Add(UserContent(results));
            }
            return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_ToolLimit"]);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new(text.ToString(), null, ClaudeFailureKind.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException)
        {
            return new(text.ToString(), null, ClaudeFailureKind.Failed, LocalizationService.Instance["Ai_Gemini_RequestFailed"]);
        }
    }

    private static JsonObject UserContent(JsonArray parts) =>
        new() { ["role"] = "user", ["parts"] = parts };

    private sealed record StreamedTurn(JsonArray Parts, List<JsonObject> Calls, string? FinishReason, bool Blocked, GeminiApiError? Error);

    /// <summary>Lee una respuesta SSE (<c>data: {GenerateContentResponse}</c> por evento). Solo el texto que no es
    /// razonamiento se enseña; todas las partes se conservan para devolverlas al modelo.</summary>
    private static async Task<StreamedTurn> ReadStreamAsync(HttpResponseMessage response, StringBuilder text,
        Action<ClaudeStreamEvent>? onEvent, Action touch, CancellationToken cancellation)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var parts = new JsonArray();
        var calls = new List<JsonObject>();
        string? finishReason = null;
        var blocked = false;
        // Entre rondas de herramientas el texto se separa en párrafos, como hace Codex entre mensajes.
        var needsSeparator = text.Length > 0;
        while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
        {
            touch();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            var chunk = JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException();
            if (chunk["error"] is JsonObject error) return new(parts, calls, finishReason, blocked, GeminiApiError.From(error, null));
            if (chunk["promptFeedback"]?["blockReason"] is not null) blocked = true;
            if (chunk["candidates"]?.AsArray().FirstOrDefault() is not JsonObject candidate) continue;
            foreach (var part in candidate["content"]?["parts"]?.AsArray() ?? [])
            {
                if (part is not JsonObject value) continue;
                parts.Add(value.DeepClone());
                if (value["functionCall"] is JsonObject call) calls.Add(call);
                if (value["thought"]?.GetValue<bool>() == true || value["text"]?.GetValue<string>() is not { Length: > 0 } delta) continue;
                if (needsSeparator)
                {
                    delta = (text.ToString().EndsWith('\n') ? "\n" : "\n\n") + delta;
                    needsSeparator = false;
                }
                text.Append(delta);
                if (text.Length > MaxResponseChars) throw new JsonException("Respuesta demasiado larga.");
                onEvent?.Invoke(new ClaudeTextDelta(delta));
            }
            if (candidate["finishReason"]?.GetValue<string>() is { Length: > 0 } reason)
            {
                finishReason = reason;
                blocked |= reason is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION" or "IMAGE_SAFETY";
            }
        }
        if (blocked && finishReason is null) finishReason = "SAFETY";
        return new(parts, calls, finishReason, blocked, null);
    }
}

/// <summary>Error de la API de Google (<c>{"error": {code, message, status, details}}</c>) y cómo lo ve el
/// asistente. El mensaje de Google se conserva como detalle, nunca la clave.</summary>
internal sealed record GeminiApiError(int Code, string Status, string Message, string? Reason, TimeSpan? RetryDelay)
{
    public static async Task<GeminiApiError> ReadAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        JsonObject? error = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            error = JsonNode.Parse(body)?["error"] as JsonObject;
        }
        catch (JsonException) { }
        return From(error, (int)response.StatusCode);
    }

    public static GeminiApiError From(JsonObject? error, int? httpStatus)
    {
        var details = error?["details"]?.AsArray() ?? [];
        string? reason = null;
        TimeSpan? retry = null;
        foreach (var detail in details)
        {
            reason ??= detail?["reason"]?.GetValue<string>();
            if (detail?["retryDelay"]?.GetValue<string>() is { } delay
                && delay.EndsWith('s')
                && double.TryParse(delay[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds is > 0 and < 86_400)
                retry = TimeSpan.FromSeconds(seconds);
        }
        var code = error?["code"]?.GetValue<int>() ?? httpStatus ?? 0;
        return new(code, error?["status"]?.GetValue<string>() ?? string.Empty,
            error?["message"]?.GetValue<string>() ?? string.Empty, reason, retry);
    }

    private bool Mentions(string value) => Message.Contains(value, StringComparison.OrdinalIgnoreCase);

    public bool IsRegionUnsupported => Status == "FAILED_PRECONDITION" && Mentions("location");

    public bool IsThinkingRejected => Code == 400 && (Mentions("thinking") || Mentions("thought"));

    public ClaudeFailureKind Classify() => this switch
    {
        { Reason: "API_KEY_INVALID" or "API_KEY_EXPIRED" or "API_KEY_SERVICE_BLOCKED" } => ClaudeFailureKind.SignedOut,
        { Code: 401 } or { Status: "UNAUTHENTICATED" } => ClaudeFailureKind.SignedOut,
        { Code: 403 } or { Status: "PERMISSION_DENIED" } => ClaudeFailureKind.SignedOut,
        { Code: 429 } or { Status: "RESOURCE_EXHAUSTED" } => ClaudeFailureKind.RateLimited,
        { Code: 404 } or { Status: "NOT_FOUND" } => ClaudeFailureKind.ModelUnavailable,
        { Code: 500 or 502 or 503 or 504 } or { Status: "UNAVAILABLE" or "INTERNAL" or "DEADLINE_EXCEEDED" } => ClaudeFailureKind.Overloaded,
        _ => ClaudeFailureKind.Failed
    };

    public ClaudeTurnResult ToResult(string partialText)
    {
        var failure = Classify();
        var detail = IsRegionUnsupported ? LocalizationService.Instance["Ai_Gemini_Region"]
            : Message.Length > 0 ? Message : LocalizationService.Instance["Ai_Gemini_RequestFailed"];
        return new(partialText, null, failure, detail,
            failure == ClaudeFailureKind.RateLimited && RetryDelay is { } delay ? DateTimeOffset.UtcNow + delay : null);
    }
}
