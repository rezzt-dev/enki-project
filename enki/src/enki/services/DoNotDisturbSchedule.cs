using System;

namespace enki.Services;

/// <summary>Lógica pura de la ventana horaria "no molestar" (docs/ajustes-docs/04-editor-y-notificaciones.md §2),
/// separada de <c>NotificationCenterViewModel</c> para poder testearla sin UI. Dentro de la ventana, un
/// evento nuevo se sigue registrando en la campana — esto solo decide si además dispara el toast del
/// sistema.</summary>
public static class DoNotDisturbSchedule
{
    /// <param name="start">Hora de inicio en formato "HH:mm".</param>
    /// <param name="end">Hora de fin en formato "HH:mm".</param>
    /// <param name="now">Instante a comprobar, en hora local.</param>
    /// <returns>False si <paramref name="start"/>/<paramref name="end"/> no son horas válidas o son
    /// iguales (ventana de duración cero, tratada como "nunca activa" en vez de "siempre activa" para
    /// evitar silenciar todo por un ajuste mal puesto).</returns>
    public static bool IsWithin(string? start, string? end, TimeOnly now)
    {
        if (!TimeOnly.TryParse(start, out var startTime) || !TimeOnly.TryParse(end, out var endTime)) return false;
        if (startTime == endTime) return false;

        // Same-day window (e.g. 13:00–18:00) vs. one that crosses midnight (e.g. 22:00–08:00).
        return startTime < endTime
            ? now >= startTime && now < endTime
            : now >= startTime || now < endTime;
    }
}
