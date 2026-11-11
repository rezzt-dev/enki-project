using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Textos de la exportación en el idioma de la interfaz, resueltos por quien exporta: así el exportador no
/// depende de la localización y se puede probar solo.</summary>
/// <param name="UserLabel">Encabezado de los mensajes del usuario ("Tú").</param>
/// <param name="AssistantLabel">Encabezado de las respuestas: el nombre de la personalidad del asistente.</param>
/// <param name="AttachmentsLabel">"Adjuntos".</param>
/// <param name="ActionsLabel">"Acciones en el vault".</param>
/// <param name="InterruptedLabel">"Respuesta interrumpida".</param>
/// <param name="ToolLabel">Nombre visible de una herramienta (<c>create_note</c> → "Crear nota").</param>
/// <param name="StateLabel">Cómo terminó una acción, o <see langword="null"/> si terminó bien y sigue hecha.</param>
public sealed record AiChatExportLabels(
    string UserLabel,
    string AssistantLabel,
    string AttachmentsLabel,
    string ActionsLabel,
    string InterruptedLabel,
    Func<string, string> ToolLabel,
    Func<AiToolActivity, string?> StateLabel);

/// <summary>"Exportar conversación" (AIA-09): la conversación entera como una nota Markdown, con propiedades de
/// origen en el frontmatter (de dónde sale, proveedor, modelo y fechas) y un apartado por mensaje con su autor, la
/// hora, los adjuntos y lo que el asistente hizo en el vault. Solo usa lo que ya está en el historial del chat: no
/// vuelve a leer notas ni adjuntos.</summary>
public static class AiChatExporter
{
    public const string SourceValue = "enki-assistant";

    public static string ToMarkdown(AiChatSession session, string title, AiChatExportLabels labels, DateTime exportedUtc,
        CultureInfo culture)
    {
        var body = new StringBuilder("# ").Append(title.Trim()).Append("\n\n");
        foreach (var message in session.Messages)
        {
            var author = message.Role == AiChatRole.User ? labels.UserLabel : labels.AssistantLabel;
            body.Append("## ").Append(author).Append(" · ").Append(FormatLocal(message.CreatedUtc, culture)).Append("\n\n");
            if (message.Text.Trim() is { Length: > 0 } text) body.Append(text).Append("\n\n");

            if (message.Attachments.Count > 0)
                body.Append('*').Append(labels.AttachmentsLabel).Append(":* ")
                    .Append(string.Join(", ", message.Attachments.Select(a => a.RelativePath.Length > 0 ? a.RelativePath : a.Title)))
                    .Append("\n\n");

            if (message.Activities.Count > 0)
            {
                body.Append('*').Append(labels.ActionsLabel).Append(":*\n\n");
                foreach (var activity in message.Activities)
                {
                    body.Append("- ").Append(labels.ToolLabel(activity.Tool));
                    if (activity.Target.Length > 0) body.Append(": ").Append(activity.Target);
                    if (labels.StateLabel(activity) is { Length: > 0 } state) body.Append(" (").Append(state).Append(')');
                    body.Append('\n');
                }
                body.Append('\n');
            }

            if (message.IsInterrupted) body.Append('*').Append(labels.InterruptedLabel).Append("*\n\n");
        }

        var content = body.ToString().TrimEnd() + "\n";
        foreach (var (key, value) in Properties(session, exportedUtc))
            content = NotePropertyHelper.Set(content, key, value);
        return content;
    }

    /// <summary>Propiedades de origen. Claves fijas en inglés, como el resto de propiedades que Enki escribe, para
    /// poder filtrarlas igual en cualquier idioma.</summary>
    private static IEnumerable<(string Key, string? Value)> Properties(AiChatSession session, DateTime exportedUtc)
    {
        yield return ("source", SourceValue);
        yield return ("chat", session.Title.Length > 0 ? session.Title : null);
        yield return ("provider", session.Provider.ToString().ToLowerInvariant());
        yield return ("model", string.IsNullOrWhiteSpace(session.Model) ? null : session.Model);
        yield return ("created", FormatIso(session.CreatedUtc));
        yield return ("exported", FormatIso(exportedUtc));
    }

    private static string FormatIso(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);

    private static string FormatLocal(DateTime utc, CultureInfo culture) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", culture);
}
