using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Localization;

namespace enki.Services.Holidays;

/// <summary>Consulta del catálogo de festivos (<see cref="HolidayCatalog"/>) para el Calendario y Ajustes.
/// No guarda nada ni accede a la red: qué países están activos lo decide <c>AppSettings</c>, y aquí solo se
/// calculan las fechas de cada año (con caché, porque el calendario reconstruye la rejilla a menudo) y se
/// traduce cada nombre al idioma activo en el momento de la consulta, de modo que un cambio de idioma en
/// caliente no exige invalidar nada.</summary>
public sealed class HolidayService
{
    /// <summary>Instancia compartida: el catálogo es inmutable y la caché es segura entre hilos, así que no
    /// hace falta componerla a mano en <c>MainWindowViewModel</c>.</summary>
    public static HolidayService Shared { get; } = new();

    /// <summary>Años fuera de este intervalo no se calculan: las reglas de Pascua ortodoxa y del solsticio
    /// solo son exactas en él, y ninguna vista del calendario necesita ir más allá.</summary>
    private const int MinYear = 1950;
    private const int MaxYear = 2099;

    private readonly ConcurrentDictionary<(string Country, int Year), IReadOnlyList<RawOccurrence>> _cache = new();

    private sealed record RawOccurrence(DateOnly Date, HolidayDefinition Definition, bool IsSubstitute);

    /// <summary>Todos los países del catálogo, en el orden en que se declaran.</summary>
    public IReadOnlyList<HolidayCountryInfo> Countries { get; } =
        HolidayCatalog.Countries.Values.Select(c => c.Info).ToList();

    public static bool IsSupported(string? countryCode) =>
        countryCode is not null && HolidayCatalog.Countries.ContainsKey(countryCode);

    /// <summary>Nombre del país en el idioma de la interfaz. ICU devuelve "finés (Finlandia)" para la cultura
    /// fi-FI vista desde es-ES; el país es lo que va entre paréntesis. Si la plataforma no tiene datos de
    /// ICU (modo de globalización invariable) se usa el nombre en inglés.</summary>
    public static string CountryDisplayName(string countryCode)
    {
        if (!HolidayCatalog.Countries.TryGetValue(countryCode, out var country)) return countryCode;

        try
        {
            var displayName = CultureInfo.GetCultureInfo(country.Info.CultureCode).DisplayName;
            var open = displayName.LastIndexOf('(');
            var close = displayName.LastIndexOf(')');
            if (open >= 0 && close > open + 1)
            {
                var name = displayName[(open + 1)..close].Trim();
                if (name.Length > 0) return char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
            }
        }
        catch (CultureNotFoundException)
        {
            // Sin datos de cultura: se resuelve abajo con el nombre en inglés.
        }

        try { return new RegionInfo(country.Info.Code).EnglishName; }
        catch (ArgumentException) { return country.Info.Code; }
    }

    /// <summary>País del catálogo que corresponde a la región del sistema, para sugerirlo en Ajustes; nulo si
    /// la región no está en el catálogo.</summary>
    public static string? SuggestedCountryCode()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return IsSupported(code) ? code.ToUpperInvariant() : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Festivos de los <paramref name="countryCodes"/> con fecha en [<paramref name="start"/>,
    /// <paramref name="endExclusive"/>), ordenados por fecha y país. Con
    /// <paramref name="includeSecondary"/> a falso solo devuelve los festivos nacionales (los que aclaran la
    /// casilla); los regionales y los días señalados se omiten. Los códigos desconocidos se ignoran, así
    /// que un país retirado del catálogo en una versión futura no rompe unos ajustes antiguos.</summary>
    public IReadOnlyList<HolidayOccurrence> GetHolidays(
        DateOnly start, DateOnly endExclusive, IEnumerable<string> countryCodes, bool includeSecondary = true)
    {
        if (endExclusive <= start) return [];

        var result = new List<HolidayOccurrence>();
        var firstYear = Math.Max(start.Year, MinYear);
        var lastYear = Math.Min(endExclusive.AddDays(-1).Year, MaxYear);
        var uiLanguage = LocalizationService.Instance.CurrentCulture.TwoLetterISOLanguageName;

        foreach (var code in countryCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!HolidayCatalog.Countries.TryGetValue(code, out var country)) continue;
            var countryLanguage = CultureInfo.GetCultureInfo(country.Info.CultureCode).TwoLetterISOLanguageName;
            var preferLocal = string.Equals(uiLanguage, countryLanguage, StringComparison.OrdinalIgnoreCase);

            // Un año antes y uno después: el sustitutivo estadounidense del 1 de enero puede caer el 31 de
            // diciembre del año anterior.
            for (var year = firstYear - 1; year <= lastYear + 1; year++)
            {
                if (year is < MinYear or > MaxYear) continue;
                foreach (var raw in YearOccurrences(country, year))
                {
                    if (raw.Date < start || raw.Date >= endExclusive) continue;
                    if (!includeSecondary && raw.Definition.Kind != HolidayKind.Public) continue;
                    result.Add(Resolve(country, raw, preferLocal));
                }
            }
        }

        return result
            .OrderBy(h => h.Date)
            .ThenBy(h => h.Kind)
            .ThenBy(h => h.CountryCode, StringComparer.Ordinal)
            .ToList();
    }

    private IReadOnlyList<RawOccurrence> YearOccurrences(HolidayCatalog.Country country, int year) =>
        _cache.GetOrAdd((country.Info.Code, year), _ => ComputeYear(country, year));

    private static List<RawOccurrence> ComputeYear(HolidayCatalog.Country country, int year)
    {
        var occurrences = new List<RawOccurrence>();
        foreach (var definition in country.Definitions)
        {
            if (definition.DateIn(year) is { } date) occurrences.Add(new RawOccurrence(date, definition, false));
        }

        // Los sustitutivos se calculan después de tener todos los festivos del año para no caer en otro
        // festivo (Navidad en sábado y San Esteban en domingo dan lunes 27 y martes 28 en el Reino Unido).
        var taken = occurrences.Where(o => o.Definition.Kind == HolidayKind.Public).Select(o => o.Date).ToHashSet();
        foreach (var original in occurrences.Where(o => o.Definition.Kind == HolidayKind.Public).OrderBy(o => o.Date).ToList())
        {
            if (SubstituteDate(original.Date, original.Definition.Substitute, taken) is not { } substitute) continue;
            taken.Add(substitute);
            occurrences.Add(new RawOccurrence(substitute, original.Definition, true));
        }

        return occurrences.OrderBy(o => o.Date).ToList();
    }

    private static DateOnly? SubstituteDate(DateOnly date, HolidaySubstitute policy, HashSet<DateOnly> taken)
    {
        switch (policy)
        {
            case HolidaySubstitute.NextFreeWeekday when HolidayDates.IsWeekend(date):
            {
                var candidate = date.AddDays(1);
                while (HolidayDates.IsWeekend(candidate) || taken.Contains(candidate)) candidate = candidate.AddDays(1);
                return candidate;
            }
            case HolidaySubstitute.NearestWeekday when date.DayOfWeek == DayOfWeek.Saturday:
                return date.AddDays(-1);
            case HolidaySubstitute.NearestWeekday when date.DayOfWeek == DayOfWeek.Sunday:
                return date.AddDays(1);
            case HolidaySubstitute.SundayToMonday when date.DayOfWeek == DayOfWeek.Sunday:
                return date.AddDays(1);
            default:
                return null;
        }
    }

    /// <summary>Elige el nombre visible: el oficial si la interfaz está en el idioma del país; si no, la
    /// traducción común de los recursos y, para los festivos propios del país, el nombre en inglés.</summary>
    private static HolidayOccurrence Resolve(HolidayCatalog.Country country, RawOccurrence raw, bool preferLocal)
    {
        var definition = raw.Definition;
        var name = preferLocal
            ? definition.LocalName
            : definition.CommonKey is { } key
                ? LocalizationService.Instance["Holiday_Common_" + key]
                : definition.EnglishName;
        if (raw.IsSubstitute) name = string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Holiday_Substitute"], name);

        return new HolidayOccurrence(raw.Date, country.Info.Code, name, definition.LocalName, definition.Kind, raw.IsSubstitute);
    }
}
