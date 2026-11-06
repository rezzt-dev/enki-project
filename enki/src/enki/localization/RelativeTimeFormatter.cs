using System;

namespace enki.Localization;

/// <summary>Formato común de tiempos relativos y fechas, según el idioma activo de la aplicación.</summary>
public static class RelativeTimeFormatter
{
    public static string Format(DateTime utc) => Format(utc, DateTime.UtcNow);

    public static string Format(DateTime utc, DateTime nowUtc)
    {
        var delta = nowUtc - utc;
        if (delta.TotalMinutes < 1) return LocalizationService.Instance["Common_RelativeTime_JustNow"];
        if (delta.TotalMinutes < 60)
        {
            var minutes = (int)delta.TotalMinutes;
            return Plural.Get("Common_RelativeTime_MinutesAgo", minutes, minutes);
        }
        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            return Plural.Get("Common_RelativeTime_HoursAgo", hours, hours);
        }
        if (delta.TotalDays < 7)
        {
            var days = (int)delta.TotalDays;
            return Plural.Get("Common_RelativeTime_DaysAgo", days, days);
        }
        return utc.ToLocalTime().ToString("d", LocalizationService.Instance.CurrentCulture);
    }

    /// <summary>The forward-looking counterpart of <see cref="Format"/> — "in 2 minutes" instead of "2
    /// minutes ago" — used by the Polar sync backoff countdown (<c>PolarSyncService.NextSyncAttemptUtc</c>).
    /// Minute-granularity only since that's as precise as the backoff interval itself ever gets.</summary>
    public static string FormatUntil(DateTime utc, DateTime nowUtc)
    {
        var delta = utc - nowUtc;
        if (delta.TotalMinutes < 1) return LocalizationService.Instance["Common_RelativeTime_Soon"];

        var minutes = (int)Math.Ceiling(delta.TotalMinutes);
        return Plural.Get("Common_RelativeTime_InMinutes", minutes, minutes);
    }

    /// <summary>Fecha y hora locales con el orden regional y el reloj de 12/24 horas del idioma activo.</summary>
    public static string FormatAbsolute(DateTime utc) =>
        utc.ToLocalTime().ToString("g", LocalizationService.Instance.CurrentCulture);

    /// <summary>Fecha abreviada sin año y hora local, respetando el orden de día y mes del idioma.</summary>
    public static string FormatAbsoluteShort(DateTime utc)
    {
        var culture = LocalizationService.Instance.CurrentCulture;
        var pattern = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        var local = utc.ToLocalTime();
        return local.ToString(pattern, culture) + ", " + local.ToString("t", culture);
    }

    /// <summary>Hora local a secas cuando el instante cae hoy, y fecha corta + hora cuando no — el
    /// formato de "Aplazado hasta 15:40" / "Aplazado hasta 3 mar, 09:00" (T5). Decidir entre las dos
    /// formas aquí y no en cada vista evita que dos pantallas muestren el mismo dato de forma distinta,
    /// mismo motivo por el que <see cref="Format(DateTime)"/> vive en esta clase y no en cada item.</summary>
    public static string FormatClockOrShortDate(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("t", LocalizationService.Instance.CurrentCulture)
            : FormatAbsoluteShort(utc);
    }
}
