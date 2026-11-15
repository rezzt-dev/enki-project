using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Localization;
using enki.Services;
using enki.Services.Holidays;

namespace enki.ViewModels;

/// <summary>Un festivo tal y como lo dibuja el Calendario: etiqueta en la casilla (con el código del país
/// cuando hay varios activos), color de la capa del país y texto completo para la descripción emergente.
/// No es un <see cref="CalendarItemViewModel"/> a propósito: no se puede editar, mover, completar ni borrar,
/// no ocupa hueco de chip y no debe aparecer en la exportación .ics ni en el resumen de "Hoy".</summary>
public sealed class CalendarHolidayViewModel
{
    public required DateTime Date { get; init; }
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public required string Name { get; init; }
    public required string LocalName { get; init; }
    public required HolidayKind Kind { get; init; }
    public required string ColorHex { get; init; }

    /// <summary>Prefija el código del país ("ES · Nochebuena") solo cuando hay más de un país activo.</summary>
    public required bool ShowCountryCode { get; init; }

    public bool IsNonWorking => Kind == HolidayKind.Public;

    /// <summary>Los regionales y señalados se leen como información secundaria.</summary>
    public double LabelOpacity => IsNonWorking ? 1.0 : 0.7;

    public string Label => ShowCountryCode ? $"{CountryCode} · {Name}" : Name;

    public string KindLabel => LocalizationService.Instance[Kind switch
    {
        HolidayKind.Public => "Calendar_Holiday_KindPublic",
        HolidayKind.Regional => "Calendar_Holiday_KindRegional",
        _ => "Calendar_Holiday_KindObservance"
    }];

    /// <summary>"Nochebuena — España · Día señalado" (y el nombre oficial cuando difiere del visible).</summary>
    public string Tooltip
    {
        get
        {
            var title = string.Equals(Name, LocalName, StringComparison.Ordinal) ? Name : $"{Name} ({LocalName})";
            return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Holiday_Tooltip"],
                title, CountryName, KindLabel);
        }
    }

    /// <summary>Prefijo de las claves de capa y de color de cada país ("holidays:ES").</summary>
    public const string LayerPrefix = "holidays:";

    public static string LayerKeyFor(string countryCode) => LayerPrefix + countryCode.ToUpperInvariant();

    /// <summary>Color por defecto de la capa de un país: recorre la paleta de acentos desde el final para no
    /// coincidir con las primeras columnas y listas, que la recorren desde el principio.</summary>
    public static string DefaultColorFor(int countryIndex)
    {
        var pool = CalendarLayerPalette.AccentPool;
        return pool[pool.Count - 1 - countryIndex % pool.Count];
    }

    /// <summary>Agrupa por día local los festivos de los países <paramref name="countryCodes"/> en el rango
    /// [<paramref name="startLocal"/>, <paramref name="endLocalExclusive"/>).</summary>
    public static IReadOnlyDictionary<DateTime, IReadOnlyList<CalendarHolidayViewModel>> BuildLookup(
        HolidayService holidayService,
        IReadOnlyList<string> countryCodes,
        IReadOnlyDictionary<string, string> colorByCountry,
        bool includeSecondary,
        DateTime startLocal,
        DateTime endLocalExclusive)
    {
        if (countryCodes.Count == 0) return new Dictionary<DateTime, IReadOnlyList<CalendarHolidayViewModel>>();

        var names = countryCodes.ToDictionary(c => c, HolidayService.CountryDisplayName, StringComparer.OrdinalIgnoreCase);
        var showCode = countryCodes.Count > 1;
        return holidayService
            .GetHolidays(DateOnly.FromDateTime(startLocal), DateOnly.FromDateTime(endLocalExclusive), countryCodes, includeSecondary)
            .Select(h => new CalendarHolidayViewModel
            {
                Date = h.Date.ToDateTime(TimeOnly.MinValue),
                CountryCode = h.CountryCode,
                CountryName = names.GetValueOrDefault(h.CountryCode, h.CountryCode),
                Name = h.Name,
                LocalName = h.LocalName,
                Kind = h.Kind,
                ColorHex = colorByCountry.GetValueOrDefault(h.CountryCode, DefaultColorFor(0)),
                ShowCountryCode = showCode
            })
            .GroupBy(h => h.Date)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CalendarHolidayViewModel>)g.ToList());
    }
}

/// <summary>Resumen de los festivos de un día para las casillas de Mes, mini-calendario, Semana/Día y
/// Agenda: si el día es inhábil (fin de semana o festivo nacional) y la etiqueta principal. Calculado una
/// vez por casilla para que las cuatro vistas apliquen exactamente el mismo criterio.</summary>
public sealed class CalendarDayHolidayInfo
{
    public static readonly CalendarDayHolidayInfo None = new([], isWeekend: false, shade: false);

    public IReadOnlyList<CalendarHolidayViewModel> Holidays { get; }
    public bool IsWeekend { get; }
    public bool IsHoliday => Holidays.Any(h => h.IsNonWorking);

    /// <summary>Aclarar la casilla: fin de semana o festivo nacional, si el ajuste lo permite.</summary>
    public bool IsNonWorkingDay { get; }

    public bool HasHolidays => Holidays.Count > 0;

    /// <summary>Primero los festivos nacionales: son los que explican por qué la casilla está aclarada.</summary>
    public CalendarHolidayViewModel? Primary => Holidays.OrderBy(h => h.Kind).FirstOrDefault();

    public string PrimaryLabel => Primary?.Label ?? string.Empty;

    public string PrimaryColorHex => Primary?.ColorHex ?? CalendarItemViewModel.KanbanLayerColorHex;

    public double PrimaryLabelOpacity => Primary?.LabelOpacity ?? 1.0;

    /// <summary>"+1" cuando el mismo día tiene más festivos de los que caben en la etiqueta.</summary>
    public string ExtraCountLabel => Holidays.Count > 1
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Calendar_Holiday_MoreCount"], Holidays.Count - 1)
        : string.Empty;

    public bool HasExtraHolidays => Holidays.Count > 1;

    public string Tooltip => string.Join(Environment.NewLine, Holidays.OrderBy(h => h.Kind).Select(h => h.Tooltip));

    /// <summary>Nulo sin festivos, para que la casilla no muestre una descripción emergente vacía.</summary>
    public string? TooltipOrNull => HasHolidays ? Tooltip : null;

    public CalendarDayHolidayInfo(IReadOnlyList<CalendarHolidayViewModel> holidays, bool isWeekend, bool shade)
    {
        Holidays = holidays;
        IsWeekend = isWeekend;
        IsNonWorkingDay = shade && (isWeekend || holidays.Any(h => h.IsNonWorking));
    }

    public static CalendarDayHolidayInfo For(
        DateTime date,
        IReadOnlyDictionary<DateTime, IReadOnlyList<CalendarHolidayViewModel>> lookup,
        bool shade) =>
        new(lookup.TryGetValue(date.Date, out var holidays) ? holidays : [],
            date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, shade);
}
