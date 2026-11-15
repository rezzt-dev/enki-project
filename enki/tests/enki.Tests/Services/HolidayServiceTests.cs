using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Localization;
using enki.Services.Holidays;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Reglas de fecha y catálogo de festivos (docs/calendar-feature/15-festivos-y-dias-no-laborables.md).
/// Las fechas esperadas son las publicadas por cada país; si una ley cambia, esta prueba es el sitio donde
/// dejarlo reflejado junto al cambio del catálogo.</summary>
[Collection("LocalizationMutatingTests")]
public sealed class HolidayServiceTests : IDisposable
{
    private readonly string _originalCulture = LocalizationService.Instance.CurrentCulture.Name;
    private readonly HolidayService _service = new();

    public void Dispose() => LocalizationService.Instance.SetLanguage(_originalCulture);

    private IReadOnlyList<HolidayOccurrence> Year(string country, int year, bool includeSecondary = true) =>
        _service.GetHolidays(new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1), [country], includeSecondary);

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void WesternEaster_MatchesPublishedDates(int year, int month, int day) =>
        Assert.Equal(D(year, month, day), HolidayDates.WesternEaster(year));

    [Theory]
    [InlineData(2024, 5, 5)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 12)]
    [InlineData(2027, 5, 2)]
    public void OrthodoxEaster_MatchesPublishedDates(int year, int month, int day) =>
        Assert.Equal(D(year, month, day), HolidayDates.OrthodoxEaster(year));

    [Fact]
    public void NthWeekday_CountsFromStartAndEnd()
    {
        Assert.Equal(D(2026, 11, 26), HolidayDates.NthWeekday(2026, 11, DayOfWeek.Thursday, 4));
        Assert.Equal(D(2026, 5, 25), HolidayDates.NthWeekday(2026, 5, DayOfWeek.Monday, -1));
        Assert.Equal(D(2026, 2, 2), HolidayDates.NthWeekday(2026, 2, DayOfWeek.Monday, 1));
    }

    [Fact]
    public void Spain2026_NationalHolidays_AreExactlyTheOfficialOnes()
    {
        var national = Year("ES", 2026, includeSecondary: false).Select(h => h.Date).ToList();

        Assert.Equal(
        [
            D(2026, 1, 1), D(2026, 1, 6), D(2026, 4, 3), D(2026, 5, 1), D(2026, 8, 15),
            D(2026, 10, 12), D(2026, 11, 1), D(2026, 12, 6), D(2026, 12, 8), D(2026, 12, 25)
        ], national);
        Assert.All(Year("ES", 2026, includeSecondary: false), h => Assert.True(h.IsNonWorking));
    }

    [Fact]
    public void SecondaryHolidays_AreReturnedOnlyWhenRequested()
    {
        Assert.Contains(Year("ES", 2026), h => h.Date == D(2026, 12, 24) && h.Kind == HolidayKind.Observance);
        Assert.Contains(Year("ES", 2026), h => h.Date == D(2026, 4, 2) && h.Kind == HolidayKind.Regional);
        Assert.DoesNotContain(Year("ES", 2026, includeSecondary: false), h => h.Date == D(2026, 12, 24));
    }

    [Fact]
    public void Colombia_EmilianiLaw_MovesHolidaysToNextMonday()
    {
        var dates = Year("CO", 2026).Select(h => h.Date).ToHashSet();

        Assert.Contains(D(2026, 1, 12), dates);   // Reyes: martes 6 → lunes 12.
        Assert.Contains(D(2026, 3, 23), dates);   // San José: jueves 19 → lunes 23.
        Assert.Contains(D(2026, 5, 18), dates);   // Ascensión: Pascua + 43.
        Assert.DoesNotContain(D(2026, 1, 6), dates);
    }

    [Fact]
    public void Argentina_MovableHolidays_FollowTheTransferRules()
    {
        var dates = Year("AR", 2026).Select(h => h.Date).ToHashSet();

        Assert.Contains(D(2026, 6, 15), dates);   // Güemes: miércoles 17 → lunes anterior.
        Assert.Contains(D(2026, 11, 23), dates);  // Soberanía: viernes 20 → lunes siguiente.
        Assert.Contains(D(2026, 2, 16), dates);   // Carnaval lunes.
        Assert.Contains(D(2026, 2, 17), dates);   // Carnaval martes.
    }

    [Fact]
    public void Chile_IndigenousPeoplesDay_FollowsTheSolstice_AndSaintPeterMovesToMonday()
    {
        Assert.Contains(Year("CL", 2025), h => h.Date == D(2025, 6, 20));
        Assert.Contains(Year("CL", 2026), h => h.Date == D(2026, 6, 21));
        Assert.Contains(Year("CL", 2027), h => h.Date == D(2027, 6, 28)); // Martes 29 → lunes 28.
    }

    [Fact]
    public void UnitedKingdom_ChristmasOnWeekend_GetsConsecutiveSubstituteDays()
    {
        var december = _service.GetHolidays(D(2027, 12, 1), D(2028, 1, 1), ["GB"], includeSecondary: false);

        var substitutes = december.Where(h => h.IsSubstitute).Select(h => h.Date).ToList();
        Assert.Equal([D(2027, 12, 27), D(2027, 12, 28)], substitutes);
        Assert.All(december.Where(h => h.IsSubstitute), h => Assert.Equal(HolidayKind.Public, h.Kind));
    }

    [Fact]
    public void UnitedStates_ObservedDays_CrossIntoThePreviousYear()
    {
        var holidays = _service.GetHolidays(D(2027, 12, 1), D(2028, 1, 1), ["US"], includeSecondary: false);

        Assert.Contains(holidays, h => h.Date == D(2027, 12, 24) && h.IsSubstitute);  // Navidad en sábado.
        Assert.Contains(holidays, h => h.Date == D(2027, 12, 31) && h.IsSubstitute);  // 1-ene-2028 en sábado.
        Assert.Contains(Year("US", 2027), h => h.Date == D(2027, 7, 5) && h.IsSubstitute);
    }

    [Fact]
    public void Netherlands_KingsDayOnSunday_IsCelebratedOnSaturday()
    {
        Assert.Contains(Year("NL", 2025), h => h.Date == D(2025, 4, 26) && h.Kind == HolidayKind.Public);
        Assert.DoesNotContain(Year("NL", 2025), h => h.Date == D(2025, 4, 27));
    }

    [Fact]
    public void YearConditions_ApplyLegalChanges()
    {
        Assert.DoesNotContain(Year("US", 2020), h => h.Date == D(2020, 6, 19));
        Assert.Contains(Year("US", 2026), h => h.Date == D(2026, 6, 19));
        Assert.DoesNotContain(Year("PT", 2014), h => h.Date == D(2014, 10, 5));
        Assert.Contains(Year("PT", 2026), h => h.Date == D(2026, 10, 5));
        Assert.Contains(Year("DE", 2017), h => h.Date == D(2017, 10, 31) && h.Kind == HolidayKind.Public);
        Assert.Contains(Year("DE", 2026), h => h.Date == D(2026, 10, 31) && h.Kind == HolidayKind.Regional);
    }

    [Fact]
    public void Names_UseOfficialNameInTheCountryLanguage_AndTranslationsOtherwise()
    {
        LocalizationService.Instance.SetLanguage("es-ES");
        var christmasEs = Assert.Single(Year("ES", 2026, false), h => h.Date == D(2026, 12, 25));
        Assert.Equal("Natividad del Señor", christmasEs.Name);

        LocalizationService.Instance.SetLanguage("en-US");
        var christmasEn = Assert.Single(Year("ES", 2026, false), h => h.Date == D(2026, 12, 25));
        Assert.Equal("Christmas Day", christmasEn.Name);
        Assert.Equal("Natividad del Señor", christmasEn.LocalName);
        var constitution = Assert.Single(Year("ES", 2026, false), h => h.Date == D(2026, 12, 6));
        Assert.Equal("Constitution Day", constitution.Name);

        LocalizationService.Instance.SetLanguage("de-DE");
        var christmasDeForSpain = Assert.Single(Year("ES", 2026, false), h => h.Date == D(2026, 12, 25));
        Assert.Equal("Weihnachten", christmasDeForSpain.Name);
    }

    [Fact]
    public void Substitutes_AreLabelledInTheActiveLanguage()
    {
        LocalizationService.Instance.SetLanguage("en-GB");
        var substitute = Assert.Single(_service.GetHolidays(D(2027, 12, 27), D(2027, 12, 28), ["GB"]));
        Assert.Equal("Christmas Day (substitute day)", substitute.Name);
    }

    [Fact]
    public void CountryDisplayName_IsTranslatedToTheInterfaceLanguage()
    {
        LocalizationService.Instance.SetLanguage("es-ES");
        Assert.Equal("Alemania", HolidayService.CountryDisplayName("DE"));
        LocalizationService.Instance.SetLanguage("fr-FR");
        Assert.Equal("Espagne", HolidayService.CountryDisplayName("ES"));
    }

    [Fact]
    public void UnknownCountries_AndEmptyRanges_AreIgnored()
    {
        Assert.Empty(_service.GetHolidays(D(2026, 1, 1), D(2027, 1, 1), ["XX"]));
        Assert.Empty(_service.GetHolidays(D(2026, 1, 2), D(2026, 1, 1), ["ES"]));
        Assert.False(HolidayService.IsSupported("XX"));
        Assert.True(HolidayService.IsSupported("es"));
    }

    // ===================== Integridad del catálogo =====================

    private static readonly string[] EuropeanUnion =
    [
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE",
        "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    ];

    [Fact]
    public void Catalog_CoversTheEuropeanUnion_LatinAmerica_AndInterfaceLanguageCountries()
    {
        var byRegion = _service.Countries.ToLookup(c => c.Region, c => c.Code);

        Assert.Equal(EuropeanUnion.OrderBy(c => c), byRegion[HolidayRegion.EuropeanUnion].OrderBy(c => c));
        Assert.Superset(new HashSet<string> { "AR", "BO", "BR", "CL", "CO", "CR", "CU", "DO", "EC", "SV", "GT", "HN", "MX", "NI", "PA", "PY", "PE", "UY", "VE" },
            byRegion[HolidayRegion.LatinAmerica].ToHashSet());
        Assert.Contains("US", byRegion[HolidayRegion.Other]);
        Assert.Contains("GB", byRegion[HolidayRegion.Other]);
    }

    [Fact]
    public void Catalog_EveryCountry_HasValidCulture_AndPlausibleHolidaysForManyYears()
    {
        foreach (var country in _service.Countries)
        {
            var culture = CultureInfo.GetCultureInfo(country.CultureCode);
            Assert.Equal(country.Code, new RegionInfo(culture.Name).TwoLetterISORegionName);

            for (var year = 2000; year <= 2060; year++)
            {
                var national = _service.GetHolidays(new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1), [country.Code], includeSecondary: false)
                    .Where(h => !h.IsSubstitute).ToList();
                Assert.InRange(national.Count, 6, 20);
                Assert.All(national, h => Assert.Equal(year, h.Date.Year));
            }
        }
    }

    [Fact]
    public void Catalog_CommonNames_ExistInEveryLanguage()
    {
        foreach (var language in SupportedLanguages.All)
        {
            var culture = CultureInfo.GetCultureInfo(language.CultureCode);
            foreach (var key in HolidayCatalog.CommonKeys)
                Assert.False(string.IsNullOrWhiteSpace(Strings.ResourceManager.GetString("Holiday_Common_" + key, culture)), $"{language.CultureCode}: {key}");
        }
    }
}
