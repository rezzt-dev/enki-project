using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>De qué depende que una herramienta se ofrezca en un chat (ver <see cref="AiAccessScope"/>).</summary>
public enum AiToolScope
{
    /// <summary>Notas, carpetas y archivos de los espacios del ámbito (el vault y/o sesiones).</summary>
    Content,
    /// <summary>Kanban, recordatorios, agenda y papelera: cuelgan del vault, no de una sesión.</summary>
    Vault,
    /// <summary>Como <see cref="Vault"/>, y además con una cuenta de Polar vinculada.</summary>
    Polar,
    /// <summary>Crear, renombrar o borrar sesiones: solo con acceso al vault entero.</summary>
    Sessions
}

public enum AiToolEffect
{
    Read,
    /// <summary>Cambia algo, de forma recuperable (historial de versiones, papelera).</summary>
    Change,
    /// <summary>No tiene vuelta atrás: pide confirmación al usuario en cada uso.</summary>
    Irreversible
}

public enum AiToolErrorKind
{
    InvalidArguments,
    NotFound,
    AccessDenied,
    Locked,
    /// <summary>La nota cambió desde que se leyó, o su pestaña tiene cambios sin resolver.</summary>
    Conflict,
    /// <summary>El usuario no confirmó la acción.</summary>
    Denied,
    /// <summary>El vault o el módulo no está disponible (unidad desconectada, Polar sin vincular…).</summary>
    Unavailable,
    Failed
}

/// <summary>Fallo de una herramienta que se devuelve al modelo como resultado con error (en inglés, que es
/// para él) en vez de cortar el turno. El usuario ve su propia versión traducida según <see cref="Kind"/>.</summary>
public sealed class AiToolException(AiToolErrorKind kind, string message) : Exception(message)
{
    public AiToolErrorKind Kind { get; } = kind;
}

/// <summary>Una herramienta del vault: nombre MCP, descripción y esquema de entrada para el modelo, y la
/// función que la ejecuta sobre los servicios reales.</summary>
public sealed record AiToolDefinition(
    string Name,
    AiToolScope Scope,
    AiToolEffect Effect,
    string Description,
    JsonObject InputSchema,
    Func<AiToolCall, Task<object?>> Handler);

/// <summary>Una invocación concreta: argumentos ya parseados, el turno al que pertenece y la actividad que la
/// representa en el chat.</summary>
public sealed class AiToolCall(AiToolContext context, JsonElement arguments, AiToolActivity activity)
{
    public AiToolContext Context { get; } = context;
    public AiToolActivity Activity { get; } = activity;

    /// <summary>Lo que se muestra en la fila de actividad del chat. Se llama en cuanto se sabe qué elemento se
    /// toca, también antes de fallar, para que el usuario vea sobre qué se intentó.</summary>
    public void Target(AiToolTargetKind kind, string label, string? id = null)
    {
        Activity.TargetKind = kind;
        Activity.Target = label;
        Activity.TargetId = id;
    }

    public bool Has(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out _);

    /// <summary>El argumento existe y vale <c>null</c>: en las ediciones, "borrar este campo".</summary>
    public bool IsNull(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Null;

    public JsonElement? Get(string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public string? GetText(string name)
    {
        if (Get(name) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => throw Invalid($"'{name}' must be a string.")
        };
    }

    public string RequireText(string name)
    {
        var value = GetText(name);
        return string.IsNullOrWhiteSpace(value) ? throw Invalid($"'{name}' is required.") : value;
    }

    public int? GetInteger(string name, int min = int.MinValue, int max = int.MaxValue)
    {
        if (Get(name) is not { } value) return null;
        int parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) parsed = number;
        else if (value.ValueKind == JsonValueKind.String
                 && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text)) parsed = text;
        else throw Invalid($"'{name}' must be an integer.");
        return parsed < min || parsed > max ? throw Invalid($"'{name}' must be between {min} and {max}.") : parsed;
    }

    public bool? GetFlag(string name)
    {
        if (Get(name) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw Invalid($"'{name}' must be true or false.")
        };
    }

    public List<string>? GetTextList(string name)
    {
        if (Get(name) is not { } value) return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (value.ValueKind != JsonValueKind.Array) throw Invalid($"'{name}' must be a list of strings.");
        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : throw Invalid($"'{name}' must be a list of strings."))
            .Where(item => item.Length > 0)
            .ToList();
    }

    public List<JsonElement>? GetObjects(string name)
    {
        if (Get(name) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object))
            throw Invalid($"'{name}' must be a list of objects.");
        return value.EnumerateArray().ToList();
    }

    /// <summary>Argumento anidado (un objeto dentro de los argumentos) leído con las mismas reglas.</summary>
    public AiToolCall Nested(JsonElement element) => new(Context, element, Activity);

    public static AiToolException Invalid(string message) => new(AiToolErrorKind.InvalidArguments, message);
}
