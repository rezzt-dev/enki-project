using System;

namespace enki.Services.Holidays;

/// <summary>Cálculo de fechas de los festivos del catálogo (<see cref="HolidayCatalog"/>): Pascua
/// occidental y ortodoxa, n-ésimo día de la semana de un mes, primer día de la semana a partir de una
/// fecha, solsticio de junio y los traslados legales a lunes/viernes de cada país. Todo es aritmética
/// pura sobre <see cref="DateOnly"/>, sin red ni zona horaria del equipo, para que el resultado sea el
/// mismo en cualquier máquina y se pueda probar año a año.</summary>
internal static class HolidayDates
{
    /// <summary>Domingo de Pascua gregoriano (algoritmo anónimo de Meeus/Jones/Butcher).</summary>
    public static DateOnly WesternEaster(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Domingo de Pascua ortodoxo expresado en el calendario gregoriano: algoritmo juliano de
    /// Meeus más la diferencia entre calendarios (13 días entre 1900 y 2099).</summary>
    public static DateOnly OrthodoxEaster(int year)
    {
        var a = year % 4;
        var b = year % 7;
        var c = year % 19;
        var d = (19 * c + 15) % 30;
        var e = (2 * a + 4 * b - d + 34) % 7;
        var month = (d + e + 114) / 31;
        var day = (d + e + 114) % 31 + 1;
        var julianToGregorian = year / 100 - year / 400 - 2;
        return new DateOnly(year, month, day).AddDays(julianToGregorian);
    }

    /// <summary>El <paramref name="occurrence"/>-ésimo <paramref name="dayOfWeek"/> del mes; con un valor
    /// negativo cuenta desde el final (-1 es el último).</summary>
    public static DateOnly NthWeekday(int year, int month, DayOfWeek dayOfWeek, int occurrence)
    {
        ArgumentOutOfRangeException.ThrowIfZero(occurrence);

        if (occurrence > 0)
        {
            var first = new DateOnly(year, month, 1);
            var offset = ((int)dayOfWeek - (int)first.DayOfWeek + 7) % 7;
            return first.AddDays(offset + (occurrence - 1) * 7);
        }

        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var back = ((int)last.DayOfWeek - (int)dayOfWeek + 7) % 7;
        return last.AddDays(-back + (occurrence + 1) * 7);
    }

    /// <summary>Primer <paramref name="dayOfWeek"/> igual o posterior al día indicado — p. ej. el sábado de
    /// San Juan sueco (entre el 20 y el 26 de junio).</summary>
    public static DateOnly WeekdayOnOrAfter(int year, int month, int day, DayOfWeek dayOfWeek)
    {
        var start = new DateOnly(year, month, day);
        return start.AddDays(((int)dayOfWeek - (int)start.DayOfWeek + 7) % 7);
    }

    /// <summary>Fecha local del solsticio de junio con el desfase UTC fijo indicado (Meeus, cap. 27,
    /// término medio). El error del término medio es de minutos, suficiente para decidir el día salvo que
    /// el solsticio caiga a escasos minutos de la medianoche local.</summary>
    public static DateOnly JuneSolstice(int year, int utcOffsetHours)
    {
        var y = (year - 2000) / 1000.0;
        var jde = 2451716.56767 + 365241.62603 * y + 0.00325 * y * y + 0.00888 * y * y * y - 0.00030 * y * y * y * y;
        var utc = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jde - 2451545.0);
        return DateOnly.FromDateTime(utc.AddHours(utcOffsetHours));
    }

    public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    // ===================== Traslados legales (el festivo se mueve, no se duplica) =====================

    /// <summary>Colombia (Ley Emiliani): si no cae en lunes, se celebra el lunes siguiente.</summary>
    public static DateOnly NextMonday(DateOnly date) =>
        date.AddDays(((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7);

    /// <summary>Argentina (feriados trasladables) y República Dominicana: martes y miércoles pasan al
    /// lunes anterior; jueves y viernes, al lunes siguiente.</summary>
    public static DateOnly NearestMonday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Tuesday => date.AddDays(-1),
        DayOfWeek.Wednesday => date.AddDays(-2),
        DayOfWeek.Thursday => date.AddDays(4),
        DayOfWeek.Friday => date.AddDays(3),
        _ => date
    };

    /// <summary>Argentina, paso a la inmortalidad de Güemes: miércoles al lunes anterior; jueves y viernes
    /// al lunes siguiente; el martes se mantiene.</summary>
    public static DateOnly GuemesMonday(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Tuesday ? date : NearestMonday(date);

    /// <summary>Chile (Ley 19.668): martes, miércoles o jueves pasan al lunes de esa semana; el viernes,
    /// al lunes siguiente.</summary>
    public static DateOnly ChileMonday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Tuesday => date.AddDays(-1),
        DayOfWeek.Wednesday => date.AddDays(-2),
        DayOfWeek.Thursday => date.AddDays(-3),
        DayOfWeek.Friday => date.AddDays(3),
        _ => date
    };

    /// <summary>Chile, Día de las Iglesias Evangélicas: si cae en martes pasa al viernes anterior; si cae en
    /// miércoles, al viernes siguiente.</summary>
    public static DateOnly ChileFriday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Tuesday => date.AddDays(-4),
        DayOfWeek.Wednesday => date.AddDays(2),
        _ => date
    };

    /// <summary>Ecuador (festivos cívicos): martes al lunes anterior; miércoles y jueves al viernes
    /// siguiente; sábado al viernes anterior; domingo al lunes siguiente.</summary>
    public static DateOnly EcuadorBridge(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Tuesday => date.AddDays(-1),
        DayOfWeek.Wednesday => date.AddDays(2),
        DayOfWeek.Thursday => date.AddDays(1),
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date
    };

    /// <summary>Países Bajos, Día del Rey: si cae en domingo se celebra el sábado anterior.</summary>
    public static DateOnly SundayToSaturday(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? date.AddDays(-1) : date;
}
