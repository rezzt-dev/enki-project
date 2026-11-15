using System;

namespace enki.Services.Holidays;

/// <summary>Qué significa un día del catálogo para la planificación.</summary>
public enum HolidayKind
{
    /// <summary>Festivo nacional no laborable: el calendario aclara su casilla igual que un fin de semana.</summary>
    Public,

    /// <summary>Festivo solo en parte del país (comunidades, estados, regiones). Se muestra, pero no aclara la
    /// casilla porque no es inhábil para todo el mundo.</summary>
    Regional,

    /// <summary>Día especial o señalado sin descanso obligatorio (Nochebuena, Día de la Madre…).</summary>
    Observance
}

/// <summary>Agrupación de los países en Ajustes › Calendario.</summary>
public enum HolidayRegion
{
    EuropeanUnion,
    LatinAmerica,
    Other
}

/// <summary>Día adicional que algunos países conceden cuando un festivo cae en fin de semana. El festivo
/// original se conserva y se añade el sustitutivo.</summary>
internal enum HolidaySubstitute
{
    None,

    /// <summary>Reino Unido, Irlanda, Bulgaria…: el siguiente día laborable que no sea ya festivo.</summary>
    NextFreeWeekday,

    /// <summary>Estados Unidos: el sábado se observa el viernes anterior y el domingo el lunes siguiente.</summary>
    NearestWeekday,

    /// <summary>Panamá, Bolivia…: solo el domingo se traslada al lunes.</summary>
    SundayToMonday
}

/// <summary>Un festivo concreto del catálogo resuelto para un año y ya con el nombre en el idioma de la
/// interfaz (<see cref="Name"/>) — <see cref="LocalName"/> conserva el nombre oficial en el idioma del país.</summary>
public sealed record HolidayOccurrence(
    DateOnly Date,
    string CountryCode,
    string Name,
    string LocalName,
    HolidayKind Kind,
    bool IsSubstitute)
{
    /// <summary>Solo los festivos nacionales cuentan como día inhábil para aclarar la casilla.</summary>
    public bool IsNonWorking => Kind == HolidayKind.Public;
}

/// <summary>País disponible en el catálogo. <see cref="CultureCode"/> sirve tanto para elegir el idioma de
/// los nombres oficiales como para obtener el nombre del país traducido mediante ICU.</summary>
public sealed record HolidayCountryInfo(string Code, string CultureCode, HolidayRegion Region);
