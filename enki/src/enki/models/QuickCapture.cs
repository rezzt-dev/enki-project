using System.Text.Json.Serialization;

namespace enki.Models;

/// <summary>Destino real de una captura rápida. Se persiste por vault para que el selector recuerde la
/// última elección sin mantener una copia paralela de la nota o tarjeta creada.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuickCaptureDestination
{
    InboxNote,
    KanbanCard
}

/// <summary>Resultado de una captura ya escrita en su almacén definitivo.</summary>
public sealed record QuickCaptureResult(QuickCaptureDestination Destination, string Title, string? Path, string? CardId);
