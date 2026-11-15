using System;

namespace enki.Services.Holidays;

/// <summary>Regla de un festivo del catálogo: cómo se calcula su fecha en un año dado, cómo se llama y en
/// qué años está vigente. Inmutable; los modificadores (<see cref="Regional"/>, <see cref="Since"/>…)
/// devuelven una copia para que <see cref="HolidayCatalog"/> se lea como una tabla.</summary>
internal sealed record HolidayDefinition(Func<int, DateOnly?> Rule, string? CommonKey, string LocalName, string EnglishName)
{
    public HolidayKind Kind { get; init; } = HolidayKind.Public;

    /// <summary>Años en los que la regla existe (leyes que crean, suprimen o restablecen festivos).</summary>
    public Func<int, bool>? AppliesIn { get; init; }

    public HolidaySubstitute Substitute { get; init; } = HolidaySubstitute.None;

    public HolidayDefinition Regional() => this with { Kind = HolidayKind.Regional };

    public HolidayDefinition Observance() => this with { Kind = HolidayKind.Observance };

    public HolidayDefinition Since(int year) => When(y => y >= year);

    public HolidayDefinition Until(int year) => When(y => y <= year);

    /// <summary>Combina la condición con las anteriores (<c>Since(2016).Until(2020)</c> es un intervalo).</summary>
    public HolidayDefinition When(Func<int, bool> condition)
    {
        var previous = AppliesIn;
        return this with { AppliesIn = previous is null ? condition : y => previous(y) && condition(y) };
    }

    /// <summary>Traslada la fecha calculada según una ley de "puentes" (ver <see cref="HolidayDates"/>).</summary>
    public HolidayDefinition Shift(Func<DateOnly, DateOnly> shift)
    {
        var rule = Rule;
        return this with { Rule = year => rule(year) is { } date ? shift(date) : null };
    }

    public HolidayDefinition WithSubstitute(HolidaySubstitute substitute) => this with { Substitute = substitute };

    public DateOnly? DateIn(int year) => AppliesIn is { } applies && !applies(year) ? null : Rule(year);
}
