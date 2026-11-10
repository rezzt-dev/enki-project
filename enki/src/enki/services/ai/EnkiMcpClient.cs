using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

/// <summary>Cliente mínimo del MCP del vault (<see cref="EnkiMcpServer"/>) para los proveedores que no son un
/// CLI y ejecutan ellos mismos el ciclo de herramientas (Ollama y Gemini). Habla con el mismo servidor y el
/// mismo token de turno que usan Claude y Codex: los permisos, confirmaciones y conflictos de disco se siguen
/// resolviendo en <see cref="AiVaultToolbox"/>, no aquí.</summary>
internal static class EnkiMcpClient
{
    /// <summary>El servidor solo escucha en <c>127.0.0.1</c>: sin proxies ni redirecciones que pudieran sacar el
    /// token del equipo.</summary>
    internal static readonly HttpClient LoopbackHttp = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Catálogo de herramientas del turno tal como lo anuncia el servidor (<c>name</c>,
    /// <c>description</c>, <c>inputSchema</c>).</summary>
    public static async Task<JsonArray> ListToolsAsync(HttpClient http, ClaudeToolAccess access, CancellationToken cancellation)
    {
        var result = await RpcAsync(http, access, "tools/list", new JsonObject(), cancellation).ConfigureAwait(false);
        return result["tools"]?.AsArray() ?? throw new JsonException();
    }

    /// <summary>Ejecuta una herramienta. Un fallo de la herramienta (ruta fuera del ámbito, permiso denegado…)
    /// no es una excepción: vuelve como texto con <c>IsError</c> para que el modelo pueda corregirse. Las imágenes
    /// del resultado (una foto o una página de PDF leída con <c>read_file</c>) vuelven aparte, en base64, para que
    /// cada proveedor las entregue al modelo en su propio formato.</summary>
    public static async Task<McpToolResult> CallToolAsync(HttpClient http, ClaudeToolAccess access, string name,
        JsonNode? arguments, CancellationToken cancellation)
    {
        var result = await RpcAsync(http, access, "tools/call", new JsonObject
            { ["name"] = name, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() }, cancellation).ConfigureAwait(false);
        var content = result["content"]?.AsArray() ?? [];
        var text = string.Join("\n", content.Where(c => c?["type"]?.GetValue<string>() is null or "text")
            .Select(c => c?["text"]?.GetValue<string>()));
        var images = content.Where(c => c?["type"]?.GetValue<string>() == "image")
            .Select(c => new McpImage(c!["mimeType"]?.GetValue<string>() ?? "image/jpeg", c["data"]?.GetValue<string>() ?? string.Empty))
            .Where(i => i.Base64.Length > 0)
            .ToList();
        return new McpToolResult(text, result["isError"]?.GetValue<bool>() == true, images);
    }

    private static async Task<JsonObject> RpcAsync(HttpClient http, ClaudeToolAccess access, string method, JsonObject parameters,
        CancellationToken cancellation)
    {
        using var post = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
        {
            Content = JsonContent.Create(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters })
        };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.BearerToken);
        using var response = await http.SendAsync(post, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellation).ConfigureAwait(false);
        return data?["result"]?.AsObject() ?? throw new JsonException();
    }
}

/// <summary>Imagen de un resultado MCP, en base64.</summary>
internal sealed record McpImage(string MimeType, string Base64);

/// <summary>Resultado de una herramienta tal como lo devuelve el servidor MCP del vault.</summary>
internal sealed record McpToolResult(string Text, bool IsError, IReadOnlyList<McpImage> Images);
