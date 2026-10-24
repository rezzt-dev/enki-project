using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>Una conversación del asistente, tal como se guarda en
/// <c>%AppData%/Enki/ai/chats/&lt;id&gt;.json</c> (ver <see cref="Services.AiChatService"/>). El texto de los
/// mensajes se guarda aquí para poder mostrar el historial sin leer nunca los archivos internos del CLI
/// (<c>~/.claude/</c>); el CLI conserva su propia copia, que se retoma con <see cref="CliSessionId"/>.</summary>
public sealed class AiChatSession
{
    public AiProvider Provider { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Título visible en el historial: el principio del primer mensaje, o el que el usuario le
    /// ponga al renombrar.</summary>
    public string Title { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Identificador de sesión que devuelve el CLI de Claude en su primer evento, y que se le pasa
    /// con <c>--resume</c> en los turnos siguientes. <see langword="null"/> hasta el primer turno completado,
    /// o tras perderse la sesión del CLI (ver <c>AssistantViewModel</c>, que entonces abre una nueva y le
    /// pasa el historial guardado aquí).</summary>
    public string? CliSessionId { get; set; }

    /// <summary>Alias de modelo con el que se envían los turnos de este chat; vacío para el del CLI.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Esfuerzo de razonamiento para los turnos siguientes: low, medium o high.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Nombre visible de la skill con la que se inició el chat, si la hay.</summary>
    public string? SkillName { get; set; }

    /// <summary>Cuerpo de la skill copiado al crear el chat. Se copia —en vez de releer la skill— porque el
    /// prompt de sistema de una conversación tiene que ser el mismo en todos sus turnos aunque la skill se
    /// edite o se borre después.</summary>
    public string? SkillInstructions { get; set; }

    /// <summary>Identificador (<see cref="Session.Id"/>) de la sesión del vault en la que se abrió el chat, o
    /// <see langword="null"/> para un chat del vault —el caso por defecto—. Fija el ámbito por defecto de
    /// las herramientas del asistente (ver <see cref="Services.AiAccessScope"/>) y no cambia durante la
    /// conversación: se elige antes del primer mensaje.</summary>
    public string? SessionId { get; set; }

    /// <summary>Nombre de la sesión al abrir el chat. Solo para mostrarlo si la sesión ya no está en el vault
    /// abierto (se borró, o el chat viene de otro vault); el nombre vigente se lee siempre de la sesión.</summary>
    public string? SessionName { get; set; }

    /// <summary>Acceso ampliado del asistente. En un chat del vault es "acceso a las sesiones"; en un chat de
    /// sesión este valor heredado se ignora: el ámbito queda limitado a esa sesión. Apagado por defecto;
    /// en chats del vault se puede modificar entre turnos.</summary>
    public bool FullVaultAccess { get; set; }

    public List<AiChatMessage> Messages { get; set; } = [];
}

public enum AiChatRole
{
    User,
    Assistant
}

public sealed class AiChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AiChatRole Role { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Notas que viajaron como contexto con este mensaje (solo mensajes del usuario). Se guardan
    /// título, ruta y tamaño, no el contenido: la nota sigue viviendo en el vault.</summary>
    public List<AiContextAttachment> Attachments { get; set; } = [];

    /// <summary>Lo que el asistente hizo en el vault durante esta respuesta (solo mensajes del asistente),
    /// en el orden en que lo hizo. Se guarda para que el historial siga explicando qué cambió.</summary>
    public List<AiToolActivity> Activities { get; set; } = [];

    /// <summary>La respuesta se cortó antes de terminar (el usuario la detuvo, o falló a mitad).</summary>
    public bool IsInterrupted { get; set; }
}

public sealed class AiContextAttachment
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Ruta relativa al vault, con <c>/</c> como separador — nunca la absoluta del equipo.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Caracteres que se enviaron realmente (tras recortar).</summary>
    public int SentChars { get; set; }

    public bool WasTruncated { get; set; }
}
