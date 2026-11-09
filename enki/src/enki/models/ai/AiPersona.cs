using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Models;

/// <summary>Personalidad del asistente que define el usuario: un nombre, un contexto general (quién es, cómo
/// trabaja, cómo quiere que le ayude) y un contexto propio para cada sesión del vault. El contexto de cada chat
/// vive en el propio chat (<see cref="AiChatSession.Context"/>). Se guarda en
/// <c>%AppData%/Enki/ai/persona.json</c> (ver <see cref="Services.AiPersonaService"/>) y no en
/// <c>settings.json</c>: es texto personal que no debe viajar en el paquete de diagnóstico ni en el perfil de
/// personalización portable. Ver <c>.docs/project-info/asistente-ia.md</c>, sección «personalidad».</summary>
public sealed class AiPersona
{
    public const int MaxNameLength = 40;
    public const int MaxContextLength = 4_000;

    /// <summary>Límite del contexto de una sesión y del de un chat: son matices sobre el general, no un
    /// segundo perfil completo, y viajan en cada turno.</summary>
    public const int MaxScopedContextLength = 2_000;

    /// <summary>Nombre con el que el asistente se presenta y con el que lo muestra la interfaz. Vacío para el
    /// nombre genérico de la interfaz («Asistente»).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contexto general que se aplica a todos los chats.</summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>Contexto de cada sesión, por <see cref="SessionKey"/>. Vive en la instalación y no en el
    /// manifiesto de la sesión porque ese manifiesto se lee sin desbloquear las sesiones protegidas.</summary>
    public Dictionary<string, string> SessionContexts { get; set; } = [];

    public bool IsEmpty => Name.Length == 0 && Context.Length == 0 && SessionContexts.Count == 0;

    /// <summary>Clave de <see cref="SessionContexts"/>: la identidad del vault más la de la sesión, para que dos
    /// vaults con sesiones del mismo id no compartan contexto.</summary>
    public static string SessionKey(string vaultId, string sessionId) => vaultId + "/" + sessionId;

    /// <summary>Sanea valores imposibles de un archivo editado a mano o de otra versión.</summary>
    public void Normalize()
    {
        Name = CleanName(Name);
        Context = CleanText(Context, MaxContextLength);
        SessionContexts = (SessionContexts ?? [])
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => (pair.Key, Value: CleanText(pair.Value, MaxScopedContextLength)))
            .Where(pair => pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Nombre en una sola línea, sin espacios sobrantes ni caracteres de control, recortado al
    /// límite.</summary>
    public static string CleanName(string? name)
    {
        var singleLine = string.Join(' ', (name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var clean = new string(singleLine.Where(c => !char.IsControl(c)).ToArray());
        return Truncate(clean, MaxNameLength).Trim();
    }

    /// <summary>Texto libre sin espacios en los extremos, con saltos de línea normalizados y recortado al
    /// límite sin partir un emoji.</summary>
    public static string CleanText(string? text, int maxLength)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Trim();
        return Truncate(normalized, maxLength).TrimEnd();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length];
    }
}
