namespace enki.Models;

/// <summary>Consumo de una respuesta tal como lo informa el proveedor: solo cifras, nunca contenido.
/// <see cref="InputTokens"/> incluye los que se leyeron de la caché del proveedor.</summary>
public sealed class AiTokenUsage
{
    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Parte de <see cref="InputTokens"/> servida desde la caché del proveedor (más barata).</summary>
    public long CachedInputTokens { get; set; }

    /// <summary>Coste en dólares cuando el proveedor lo calcula y el turno se facturó a una clave de API propia.
    /// Con una suscripción no hay coste por respuesta, así que no se guarda.</summary>
    public decimal? CostUsd { get; set; }

    public bool IsEmpty => InputTokens <= 0 && OutputTokens <= 0;

    /// <summary>Suma dos consumos (las rondas de herramientas de un turno, o las respuestas de un chat).</summary>
    public static AiTokenUsage? Add(AiTokenUsage? a, AiTokenUsage? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new AiTokenUsage
        {
            InputTokens = a.InputTokens + b.InputTokens,
            OutputTokens = a.OutputTokens + b.OutputTokens,
            CachedInputTokens = a.CachedInputTokens + b.CachedInputTokens,
            CostUsd = a.CostUsd is null && b.CostUsd is null ? null : (a.CostUsd ?? 0) + (b.CostUsd ?? 0)
        };
    }
}
