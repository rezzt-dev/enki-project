using System;
using enki.Models;

namespace enki.Services;

/// <summary>Las opciones de "Aplazar" que ofrece la interfaz (T5 de
/// .docs/funcionalidades-pendientes-priorizadas.md). Los dos primeros valores son duraciones relativas
/// configurables en Ajustes › Notificaciones; los dos siguientes son horas de pared fijas del mismo día o
/// del siguiente.</summary>
public enum SnoozeOption
{
    /// <summary>Duración corta configurable — 10 minutos por defecto.</summary>
    QuickPrimary,

    /// <summary>Duración larga configurable — 1 hora por defecto.</summary>
    QuickSecondary,

    /// <summary>"Esta tarde": hoy a la hora configurada (18:00 por defecto); mañana si ya ha pasado.</summary>
    Evening,

    /// <summary>"Mañana": el día siguiente a la hora configurada (09:00 por defecto).</summary>
    Tomorrow,

    /// <summary>Duración elegida a mano en el momento.</summary>
    Custom
}

/// <summary>Valores rápidos de aplazamiento, tomados de <see cref="AppSettings"/> pero desacoplados de él
/// para que el cálculo sea comprobable sin construir un objeto de ajustes entero.</summary>
/// <param name="PrimaryMinutes">Minutos de <see cref="SnoozeOption.QuickPrimary"/>.</param>
/// <param name="SecondaryMinutes">Minutos de <see cref="SnoozeOption.QuickSecondary"/>.</param>
/// <param name="EveningHour">Hora local (0–23) de <see cref="SnoozeOption.Evening"/>.</param>
/// <param name="TomorrowHour">Hora local (0–23) de <see cref="SnoozeOption.Tomorrow"/>.</param>
public readonly record struct SnoozePreferences(int PrimaryMinutes, int SecondaryMinutes, int EveningHour, int TomorrowHour)
{
    public static SnoozePreferences Default => new(10, 60, 18, 9);

    public static SnoozePreferences From(AppSettings settings) => new(
        settings.SnoozePrimaryMinutes,
        settings.SnoozeSecondaryMinutes,
        settings.SnoozeEveningHour,
        settings.SnoozeTomorrowHour);
}

/// <summary>Convierte una opción de "Aplazar" en el instante UTC concreto al que hay que mover el aviso.
/// Lógica pura y sin estado —mismo criterio que <see cref="DoNotDisturbSchedule"/>— para poder probar los
/// casos peliagudos (cambio de horario de verano, franja "no molestar", cruce de medianoche) sin UI ni
/// disco de por medio.
///
/// <para>La aritmética de las opciones con hora de pared ("esta tarde", "mañana") se hace en hora local y
/// se convierte a UTC al final, por lo mismo que
/// <see cref="PolarTaskService.CalculateNextDueDate"/>: "mañana a las 09:00" tiene que seguir siendo las
/// 09:00 de reloj aunque entre medias haya una transición DST. Las duraciones relativas ("dentro de 10
/// minutos"), en cambio, sí son aritmética pura de UTC: 10 minutos son 10 minutos aunque el reloj de
/// pared salte una hora.</para></summary>
public static class SnoozeSchedule
{
    /// <summary>Límites del aplazamiento personalizado y de los valores rápidos de Ajustes: de un minuto a
    /// una semana. El mínimo evita un aplazamiento de cero que volvería a avisar en el mismo ciclo del
    /// temporizador; el máximo evita que un valor absurdo escrito a mano (o traído de un
    /// <c>settings.json</c> dañado) entierre un aviso durante años.</summary>
    public const int MinMinutes = 1;

    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>Instante UTC al que mover el aviso.</summary>
    /// <param name="option">Opción elegida por el usuario.</param>
    /// <param name="nowUtc">Instante de referencia, siempre en UTC.</param>
    /// <param name="preferences">Valores rápidos configurados.</param>
    /// <param name="customMinutes">Minutos de <see cref="SnoozeOption.Custom"/>; se ignora en el resto.</param>
    /// <param name="timeZone">Zona cuya hora de pared se preserva — la local del sistema por defecto;
    /// inyectable solo para que los tests de DST sean deterministas en cualquier máquina.</param>
    public static DateTime Resolve(SnoozeOption option, DateTime nowUtc, SnoozePreferences preferences,
        int customMinutes = 0, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var utcNow = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        switch (option)
        {
            case SnoozeOption.QuickPrimary:
                return utcNow.AddMinutes(ClampMinutes(preferences.PrimaryMinutes, SnoozePreferences.Default.PrimaryMinutes));
            case SnoozeOption.QuickSecondary:
                return utcNow.AddMinutes(ClampMinutes(preferences.SecondaryMinutes, SnoozePreferences.Default.SecondaryMinutes));
            case SnoozeOption.Custom:
                return utcNow.AddMinutes(ClampMinutes(customMinutes, SnoozePreferences.Default.PrimaryMinutes));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var targetLocal = option == SnoozeOption.Evening
            // "Esta tarde" pasada la hora de la tarde no tendría a dónde ir hoy, así que rueda al día
            // siguiente en vez de resolverse a un instante ya vencido.
            ? RollForward(local.Date.AddHours(ClampHour(preferences.EveningHour, SnoozePreferences.Default.EveningHour)), local)
            : local.Date.AddDays(1).AddHours(ClampHour(preferences.TomorrowHour, SnoozePreferences.Default.TomorrowHour));

        return ToUtc(targetLocal, zone);
    }

    /// <summary>Difiere un instante ya calculado hasta el final de la franja "no molestar" si cae dentro
    /// (criterio EARS de T5: "si el nuevo instante cae dentro de la franja «No molestar», el sistema
    /// deberá diferir el aviso hasta que termine"). Sin la franja activa devuelve el mismo instante.</summary>
    public static DateTime DeferPastDoNotDisturb(DateTime targetUtc, AppSettings settings, TimeZoneInfo? timeZone = null)
    {
        if (!settings.DoNotDisturbEnabled) return targetUtc;

        var zone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc), zone);
        var deferred = DoNotDisturbSchedule.NextInstantOutside(settings.DoNotDisturbStart, settings.DoNotDisturbEnd, local);
        return deferred == local ? targetUtc : ToUtc(deferred, zone);
    }

    /// <summary>Atajo de los dos pasos anteriores: resolver la opción y, acto seguido, esquivar la franja
    /// "no molestar". Es lo que llaman las vistas; <see cref="Resolve"/> queda expuesto aparte para los
    /// tests de la aritmética pura.</summary>
    public static DateTime ResolveRespectingQuietHours(SnoozeOption option, DateTime nowUtc, AppSettings settings,
        int customMinutes = 0, TimeZoneInfo? timeZone = null)
        => DeferPastDoNotDisturb(Resolve(option, nowUtc, SnoozePreferences.From(settings), customMinutes, timeZone), settings, timeZone);

    private static DateTime RollForward(DateTime candidateLocal, DateTime nowLocal) =>
        candidateLocal > nowLocal ? candidateLocal : candidateLocal.AddDays(1);

    /// <summary>Conversión local→UTC tolerante con las dos anomalías de DST, que aquí sí pueden darse con
    /// horas de pared fijas: la hora inexistente del salto de primavera (p. ej. 02:30) haría lanzar a
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>, y la hora repetida del salto de
    /// otoño resolvería a la primera de las dos. Se avanza en pasos de 15 minutos hasta salir del hueco —
    /// desvío acotado de menos de una hora, una vez al año, frente a una excepción que tumbaría el
    /// aplazamiento.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 8 && zone.IsInvalidTime(unspecified); i++)
            unspecified = unspecified.AddMinutes(15);

        return zone.IsInvalidTime(unspecified)
            ? DateTime.SpecifyKind(local, DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static int ClampMinutes(int minutes, int fallback) =>
        minutes is < MinMinutes or > MaxMinutes ? fallback : minutes;

    private static int ClampHour(int hour, int fallback) => hour is < 0 or > 23 ? fallback : hour;
}
