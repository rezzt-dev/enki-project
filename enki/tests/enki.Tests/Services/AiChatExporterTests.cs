using System;
using System.Globalization;
using System.Linq;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>"Exportar conversación" (AIA-09): la conversación entera como Markdown, con propiedades de origen.</summary>
public sealed class AiChatExporterTests
{
    private static readonly AiChatExportLabels Labels = new("Tú", "Enki", "Adjuntos", "Acciones en el vault", "Respuesta interrumpida",
        tool => tool == "create_note" ? "Crear nota" : tool,
        activity => activity.State == AiToolActivityState.Failed ? "falló" : null);

    private static AiChatSession Chat()
    {
        var session = new AiChatSession { Title = "Plan del viaje", Provider = AiProvider.Claude, Model = "claude-sonnet-5-5" };
        session.Messages.Add(new AiChatMessage
        {
            Role = AiChatRole.User,
            Text = "Organiza el viaje",
            Attachments = [new AiContextAttachment { Title = "Ideas", RelativePath = "Viajes/Ideas.md" }]
        });
        session.Messages.Add(new AiChatMessage
        {
            Role = AiChatRole.Assistant,
            Text = "He creado la nota.",
            Activities =
            [
                new AiToolActivity { Tool = "create_note", Target = "Itinerario", State = AiToolActivityState.Succeeded },
                new AiToolActivity { Tool = "move_note", Target = "Otra", State = AiToolActivityState.Failed }
            ]
        });
        session.Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Text = "Y además…", IsInterrupted = true });
        return session;
    }

    [Fact]
    public void ToMarkdown_IncludesEveryMessage_WithAuthorsAttachmentsAndActions()
    {
        var markdown = AiChatExporter.ToMarkdown(Chat(), "Conversación · Plan del viaje", Labels, DateTime.UtcNow, CultureInfo.GetCultureInfo("es-ES"));

        Assert.Contains("# Conversación · Plan del viaje", markdown);
        Assert.Equal(3, markdown.Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal)));
        Assert.Contains("## Tú · ", markdown);
        Assert.Contains("## Enki · ", markdown);
        Assert.Contains("Organiza el viaje", markdown);
        Assert.Contains("*Adjuntos:* Viajes/Ideas.md", markdown);
        Assert.Contains("- Crear nota: Itinerario\n", markdown);
        Assert.Contains("- move_note: Otra (falló)", markdown);
        Assert.Contains("*Respuesta interrumpida*", markdown);
    }

    [Fact]
    public void ToMarkdown_AddsSourceProperties()
    {
        var markdown = AiChatExporter.ToMarkdown(Chat(), "Conversación", Labels, DateTime.UtcNow, CultureInfo.InvariantCulture);

        var properties = NotePropertyHelper.Extract(markdown);
        Assert.Equal(AiChatExporter.SourceValue, properties["source"]);
        Assert.Equal("Plan del viaje", properties["chat"]);
        Assert.Equal("claude", properties["provider"]);
        Assert.Equal("claude-sonnet-5-5", properties["model"]);
        Assert.True(properties.ContainsKey("exported"));
        Assert.StartsWith("---", markdown);
    }
}
