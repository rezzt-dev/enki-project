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

    /// <summary>Primer instante local en el que la ventana "no molestar" ya no está activa, partiendo de
    /// <paramref name="localInstant"/>. Devuelve el propio <paramref name="localInstant"/> cuando la
    /// ventana no está configurada, no es válida o simplemente no lo cubre.
    ///
    /// <para>Existe para el aplazamiento de recordatorios (T5): <see cref="IsWithin"/> solo decide si un
    /// evento que <em>acaba</em> de ocurrir puede sonar, pero al aplazar se elige un instante futuro, y el
    /// criterio de producto es diferir el aviso hasta que termine la franja en vez de tragárselo — un
    /// recordatorio aplazado a las 02:00 dentro de "no molestar" no debe perderse, debe esperar a las
    /// 08:00. La ventana que cruza medianoche se resuelve igual que en <see cref="IsWithin"/>: el final se
    /// busca en el mismo día y, si ya ha pasado, en el siguiente.</para></summary>
    /// <param name="start">Hora de inicio en formato "HH:mm".</param>
    /// <param name="end">Hora de fin en formato "HH:mm".</param>
    /// <param name="localInstant">Instante a diferir, en hora local.</param>
    public static DateTime NextInstantOutside(string? start, string? end, DateTime localInstant)
    {
        if (!IsWithin(start, end, TimeOnly.FromDateTime(localInstant))) return localInstant;

        // IsWithin ya garantiza que ambas horas son válidas y distintas, así que esta rama del TryParse no
        // puede darse; se comprueba igualmente en vez de usar Parse para no depender del formato de hora
        // de la cultura activa ni arriesgar una excepción en un cálculo que solo debe diferir un aviso.
        if (!TimeOnly.TryParse(end, out var endTime)) return localInstant;

        var candidate = localInstant.Date.Add(endTime.ToTimeSpan());
        return candidate > localInstant ? candidate : candidate.AddDays(1);
    }
}
