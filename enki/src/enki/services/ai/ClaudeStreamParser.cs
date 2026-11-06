using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace enki.Services;

/// <summary>Un evento relevante para Enki dentro de la salida <c>--output-format stream-json</c> del CLI de
/// Claude. El protocolo emite muchos más tipos (estado, estimaciones de tokens, firmas de razonamiento…);
/// <see cref="ClaudeStreamParser"/> solo traduce los que cambian lo que ve el usuario.</summary>
public abstract record ClaudeStreamEvent;

/// <summary>Primer evento de cada ejecución (<c>system/init</c>): identificador de la sesión del CLI, que es
/// el que luego se pasa con <c>--resume</c>, y el modelo que resolvió.</summary>
public sealed record ClaudeSessionStarted(string SessionId, string? Model) : ClaudeStreamEvent;

/// <summary>Fragmento de texto de la respuesta según se genera (<c>--include-partial-messages</c>).</summary>
public sealed record ClaudeTextDelta(string Text) : ClaudeStreamEvent;

/// <summary>El modelo ha empezado a razonar antes de escribir. Enki no muestra el razonamiento; solo lo usa
/// para indicar que la petición sigue viva.</summary>
public sealed record ClaudeThinkingStarted : ClaudeStreamEvent;

/// <summary>Empieza un bloque de texto nuevo. Con herramientas, una misma respuesta trae varios (lo que el
/// modelo escribe antes de una acción y lo que escribe después), y se separan como párrafos.</summary>
public sealed record ClaudeTextBlockStarted : ClaudeStreamEvent;

/// <summary>El modelo ha pedido usar una herramienta (<c>mcp__enki__create_note</c>…). Lo que hace de verdad
/// lo cuenta el servidor del vault, que es quien la ejecuta; esto solo indica que la respuesta sigue viva.</summary>
public sealed record ClaudeToolUseStarted(string Name) : ClaudeStreamEvent;

/// <summary>Mensaje del asistente completo. Con mensajes parciales llega además de los fragmentos, así que
/// solo se usa su texto si no llegó ningún <see cref="ClaudeTextDelta"/> (CLI sin mensajes parciales).</summary>
public sealed record ClaudeAssistantMessage(string Text, string? Error) : ClaudeStreamEvent;

/// <summary>Estado del límite de uso de la suscripción en la ventana actual.</summary>
public sealed record ClaudeRateLimit(string Status, DateTimeOffset? ResetsAt) : ClaudeStreamEvent
{
    public bool IsRejected => !string.Equals(Status, "allowed", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Status, "allowed_warning", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Último evento de la ejecución (<c>type: result</c>).</summary>
public sealed record ClaudeResult(
    bool IsError,
    string? Subtype,
    string? Text,
    IReadOnlyList<string> Errors,
    string? SessionId,
    int? ApiErrorStatus,
    string? TerminalReason) : ClaudeStreamEvent;

/// <summary>Traduce cada línea JSON de la salida del CLI a un <see cref="ClaudeStreamEvent"/>. Tolerante por
/// diseño: los campos cambian entre versiones del CLI, así que una línea que no se entiende —o un tipo
/// nuevo— se ignora en vez de romper la respuesta.</summary>
internal static class ClaudeStreamParser
{
    public static ClaudeStreamEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            // Los eventos de subagentes llevan el id de la herramienta que los lanzó: no son la respuesta
            // principal (Enki no ofrece herramientas que lancen subagentes, pero el protocolo los admite).
            if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
                return null;

            return GetString(root, "type") switch
            {
                "system" when GetString(root, "subtype") == "init" && GetString(root, "session_id") is { } id
                    => new ClaudeSessionStarted(id, GetString(root, "model")),
                "stream_event" => ParseStreamEvent(root),
                "assistant" => ParseAssistant(root),
                "rate_limit_event" => ParseRateLimit(root),
                "result" => ParseResult(root),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeStreamEvent? ParseStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var streamEvent)) return null;
        switch (GetString(streamEvent, "type"))
        {
            case "content_block_delta":
                if (streamEvent.TryGetProperty("delta", out var delta) && GetString(delta, "type") == "text_delta"
                    && GetString(delta, "text") is { Length: > 0 } text)
                    return new ClaudeTextDelta(text);
                return null;
            case "content_block_start":
                if (!streamEvent.TryGetProperty("content_block", out var block)) return null;
                return GetString(block, "type") switch
                {
                    "thinking" or "redacted_thinking" => new ClaudeThinkingStarted(),
                    "text" => new ClaudeTextBlockStarted(),
                    "tool_use" or "server_tool_use" => new ClaudeToolUseStarted(GetString(block, "name") ?? string.Empty),
                    _ => null
                };
            default:
                return null;
        }
    }

    private static ClaudeAssistantMessage? ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (GetString(block, "type") == "text" && GetString(block, "text") is { } piece) text.Append(piece);

        return text.Length == 0 ? null : new ClaudeAssistantMessage(text.ToString(), GetString(root, "error"));
    }

    private static ClaudeRateLimit? ParseRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || GetString(info, "status") is not { } status)
            return null;
        DateTimeOffset? resetsAt = info.TryGetProperty("resetsAt", out var resets) && resets.ValueKind == JsonValueKind.Number
            && resets.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return new ClaudeRateLimit(status, resetsAt);
    }

    private static ClaudeResult ParseResult(JsonElement root)
    {
        var errors = root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        int? apiStatus = root.TryGetProperty("api_error_status", out var status)
            && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code)
            ? code
            : null;
        return new ClaudeResult(
            IsError: root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
            Subtype: GetString(root, "subtype"),
            Text: GetString(root, "result"),
            Errors: errors,
            SessionId: GetString(root, "session_id"),
            ApiErrorStatus: apiStatus,
            TerminalReason: GetString(root, "terminal_reason"));
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
