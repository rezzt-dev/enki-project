using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Services.Holidays;

/// <summary>Catálogo integrado de festivos nacionales, regionales y días señalados: los 27 países de la
/// Unión Europea, Latinoamérica y los países de los idiomas de la interfaz que quedan fuera de esos dos
/// grupos (Estados Unidos y Reino Unido). Las reglas viven en el código y no en un servicio web para que el
/// calendario funcione sin conexión, sin enviar a nadie el país del usuario y con resultados
/// reproducibles en las pruebas. Los festivos que dependen de un decreto anual (puentes turísticos,
/// festivos locales) no se pueden calcular y quedan fuera a propósito.
///
/// Cada país se declara como una tabla en <c>HolidayCatalog.Europe.cs</c> y
/// <c>HolidayCatalog.Americas.cs</c> con las fábricas de abajo: <see cref="C"/> para un festivo con nombre
/// común traducido en los recursos (<c>Holiday_Common_*</c>) y <see cref="H"/> para uno propio del país,
/// con su nombre oficial y el nombre en inglés de respaldo.</summary>
internal static partial class HolidayCatalog
{
    internal sealed record Country(HolidayCountryInfo Info, IReadOnlyList<HolidayDefinition> Definitions);

    private static readonly Lazy<IReadOnlyDictionary<string, Country>> LazyCountries = new(() =>
        Europe().Concat(Americas()).Concat(Others())
            .ToDictionary(c => c.Info.Code, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, Country> Countries => LazyCountries.Value;

    // ===================== Claves de los nombres comunes (Holiday_Common_<clave> en Strings.resx) =====================

    internal const string NewYear = "NewYear";
    internal const string Epiphany = "Epiphany";
    internal const string CarnivalMonday = "CarnivalMonday";
    internal const string CarnivalTuesday = "CarnivalTuesday";
    internal const string MaundyThursday = "MaundyThursday";
    internal const string GoodFriday = "GoodFriday";
    internal const string HolySaturday = "HolySaturday";
    internal const string EasterSunday = "EasterSunday";
    internal const string EasterMonday = "EasterMonday";
    internal const string LabourDay = "LabourDay";
    internal const string Ascension = "Ascension";
    internal const string Pentecost = "Pentecost";
    internal const string WhitMonday = "WhitMonday";
    internal const string CorpusChristi = "CorpusChristi";
    internal const string Assumption = "Assumption";
    internal const string AllSaints = "AllSaints";
    internal const string AllSouls = "AllSouls";
    internal const string ImmaculateConception = "ImmaculateConception";
    internal const string ChristmasEve = "ChristmasEve";
    internal const string Christmas = "Christmas";
    internal const string StStephen = "StStephen";
    internal const string NewYearsEve = "NewYearsEve";
    internal const string MothersDay = "MothersDay";
    internal const string FathersDay = "FathersDay";
    internal const string ValentinesDay = "ValentinesDay";
    internal const string Halloween = "Halloween";

    /// <summary>Todas las claves comunes, para comprobar en las pruebas que existen en los recursos.</summary>
    internal static readonly IReadOnlyList<string> CommonKeys =
    [
        NewYear, Epiphany, CarnivalMonday, CarnivalTuesday, MaundyThursday, GoodFriday, HolySaturday,
        EasterSunday, EasterMonday, LabourDay, Ascension, Pentecost, WhitMonday, CorpusChristi, Assumption,
        AllSaints, AllSouls, ImmaculateConception, ChristmasEve, Christmas, StStephen, NewYearsEve,
        MothersDay, FathersDay, ValentinesDay, Halloween
    ];

    // ===================== Fábricas de la tabla =====================

    private static Country Define(string code, string cultureCode, HolidayRegion region, params HolidayDefinition[] definitions) =>
        new(new HolidayCountryInfo(code, cultureCode, region), definitions);

    /// <summary>Festivo con nombre común traducido; <paramref name="localName"/> es el nombre oficial que se
    /// muestra cuando la interfaz está en el idioma del país.</summary>
    private static HolidayDefinition C(Func<int, DateOnly?> rule, string commonKey, string localName) =>
        new(rule, commonKey, localName, localName);

    /// <summary>Festivo propio del país: nombre oficial y nombre en inglés para el resto de idiomas.</summary>
    private static HolidayDefinition H(Func<int, DateOnly?> rule, string localName, string englishName) =>
        new(rule, null, localName, englishName);

    // ===================== Reglas de fecha =====================

    private static Func<int, DateOnly?> F(int month, int day) => year => new DateOnly(year, month, day);

    /// <summary>Días desde el domingo de Pascua occidental (-2 es Viernes Santo, 1 Lunes de Pascua…).</summary>
    private static Func<int, DateOnly?> E(int offset) => year => HolidayDates.WesternEaster(year).AddDays(offset);

    /// <summary>Días desde el domingo de Pascua ortodoxa.</summary>
    private static Func<int, DateOnly?> O(int offset) => year => HolidayDates.OrthodoxEaster(year).AddDays(offset);

    /// <summary>El enésimo día de la semana del mes (negativo: contando desde el final).</summary>
    private static Func<int, DateOnly?> Nth(int month, DayOfWeek dayOfWeek, int occurrence) =>
        year => HolidayDates.NthWeekday(year, month, dayOfWeek, occurrence);

    private static Func<int, DateOnly?> OnOrAfter(int month, int day, DayOfWeek dayOfWeek) =>
        year => HolidayDates.WeekdayOnOrAfter(year, month, day, dayOfWeek);

    // ===================== Días señalados compartidos =====================

    private static HolidayDefinition ChristmasEveDay(string localName) => C(F(12, 24), ChristmasEve, localName).Observance();

    private static HolidayDefinition NewYearsEveDay(string localName) => C(F(12, 31), NewYearsEve, localName).Observance();

    private static HolidayDefinition Mothers(Func<int, DateOnly?> rule, string localName) => C(rule, MothersDay, localName).Observance();

    private static HolidayDefinition Fathers(Func<int, DateOnly?> rule, string localName) => C(rule, FathersDay, localName).Observance();

    private static IEnumerable<Country> Others() =>
    [
        Define("US", "en-US", HolidayRegion.Other,
            C(F(1, 1), NewYear, "New Year's Day").WithSubstitute(HolidaySubstitute.NearestWeekday),
            H(Nth(1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day", "Martin Luther King Jr. Day"),
            H(Nth(2, DayOfWeek.Monday, 3), "Washington's Birthday", "Washington's Birthday"),
            C(F(2, 14), ValentinesDay, "Valentine's Day").Observance(),
            H(F(3, 17), "St. Patrick's Day", "St. Patrick's Day").Observance(),
            C(E(0), EasterSunday, "Easter Sunday").Observance(),
            Mothers(Nth(5, DayOfWeek.Sunday, 2), "Mother's Day"),
            H(Nth(5, DayOfWeek.Monday, -1), "Memorial Day", "Memorial Day"),
            Fathers(Nth(6, DayOfWeek.Sunday, 3), "Father's Day"),
            H(F(6, 19), "Juneteenth National Independence Day", "Juneteenth National Independence Day").Since(2021)
                .WithSubstitute(HolidaySubstitute.NearestWeekday),
            H(F(7, 4), "Independence Day", "Independence Day").WithSubstitute(HolidaySubstitute.NearestWeekday),
            C(Nth(9, DayOfWeek.Monday, 1), LabourDay, "Labor Day"),
            H(Nth(10, DayOfWeek.Monday, 2), "Columbus Day", "Columbus Day"),
            C(F(10, 31), Halloween, "Halloween").Observance(),
            H(F(11, 11), "Veterans Day", "Veterans Day").WithSubstitute(HolidaySubstitute.NearestWeekday),
            H(Nth(11, DayOfWeek.Thursday, 4), "Thanksgiving Day", "Thanksgiving Day"),
            ChristmasEveDay("Christmas Eve"),
            C(F(12, 25), Christmas, "Christmas Day").WithSubstitute(HolidaySubstitute.NearestWeekday),
            NewYearsEveDay("New Year's Eve")),

        // Festivos bancarios de Inglaterra y Gales; los de Escocia e Irlanda del Norte, como regionales.
        Define("GB", "en-GB", HolidayRegion.Other,
            C(F(1, 1), NewYear, "New Year's Day").WithSubstitute(HolidaySubstitute.NextFreeWeekday),
            H(F(1, 2), "2nd January (Scotland)", "2nd January (Scotland)").Regional(),
            C(F(2, 14), ValentinesDay, "Valentine's Day").Observance(),
            H(F(3, 17), "St Patrick's Day (Northern Ireland)", "St Patrick's Day (Northern Ireland)").Regional(),
            Mothers(E(-21), "Mothering Sunday"),
            C(E(-2), GoodFriday, "Good Friday"),
            C(E(0), EasterSunday, "Easter Sunday").Observance(),
            C(E(1), EasterMonday, "Easter Monday"),
            H(year => year == 2020 ? new DateOnly(2020, 5, 8) : HolidayDates.NthWeekday(year, 5, DayOfWeek.Monday, 1),
                "Early May bank holiday", "Early May bank holiday"),
            H(F(5, 8), "Coronation of King Charles III", "Coronation of King Charles III").When(y => y == 2023),
            H(year => year == 2022 ? new DateOnly(2022, 6, 2) : HolidayDates.NthWeekday(year, 5, DayOfWeek.Monday, -1),
                "Spring bank holiday", "Spring bank holiday"),
            H(F(6, 3), "Platinum Jubilee bank holiday", "Platinum Jubilee bank holiday").When(y => y == 2022),
            Fathers(Nth(6, DayOfWeek.Sunday, 3), "Father's Day"),
            H(F(7, 12), "Battle of the Boyne (Northern Ireland)", "Battle of the Boyne (Northern Ireland)").Regional(),
            H(Nth(8, DayOfWeek.Monday, 1), "Summer bank holiday (Scotland)", "Summer bank holiday (Scotland)").Regional(),
            H(Nth(8, DayOfWeek.Monday, -1), "Summer bank holiday", "Summer bank holiday"),
            H(F(9, 19), "State Funeral of Queen Elizabeth II", "State Funeral of Queen Elizabeth II").When(y => y == 2022),
            C(F(10, 31), Halloween, "Halloween").Observance(),
            H(F(11, 30), "St Andrew's Day (Scotland)", "St Andrew's Day (Scotland)").Regional(),
            ChristmasEveDay("Christmas Eve"),
            C(F(12, 25), Christmas, "Christmas Day").WithSubstitute(HolidaySubstitute.NextFreeWeekday),
            H(F(12, 26), "Boxing Day", "Boxing Day").WithSubstitute(HolidaySubstitute.NextFreeWeekday),
            NewYearsEveDay("New Year's Eve"))
    ];
}
