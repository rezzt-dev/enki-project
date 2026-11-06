using System;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="SnoozeSchedule"/> — la aritmética de "Aplazar" (T5 de
/// .docs/funcionalidades-pendientes-priorizadas.md). Los casos con hora de pared usan una zona inyectada
/// (Europe/Madrid) en vez de la del sistema, mismo criterio que los tests de DST de
/// <c>PolarTaskService.CalculateNextDueDate</c>: si no, la prueba diría cosas distintas según la máquina
/// que la ejecute.</summary>
public class SnoozeScheduleTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static SnoozePreferences Preferences => SnoozePreferences.Default;

    [Fact]
    public void QuickPrimary_AddsConfiguredMinutes()
    {
        var now = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.QuickPrimary, now, Preferences, timeZone: Madrid);

        Assert.Equal(now.AddMinutes(10), result);
    }

    [Fact]
    public void QuickSecondary_AddsConfiguredMinutes()
    {
        var now = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.QuickSecondary, now, Preferences, timeZone: Madrid);

        Assert.Equal(now.AddHours(1), result);
    }

    [Fact]
    public void Custom_AddsRequestedMinutes()
    {
        var now = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Custom, now, Preferences, customMinutes: 90, timeZone: Madrid);

        Assert.Equal(now.AddMinutes(90), result);
    }

    /// <summary>Un valor imposible (0, negativo o de años) degrada al de fábrica en vez de producir un
    /// aplazamiento que vuelve a avisar en el mismo ciclo o que entierra el aviso para siempre.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(999_999)]
    public void Custom_OutOfRangeMinutes_FallBackToDefault(int minutes)
    {
        var now = new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Custom, now, Preferences, minutes, Madrid);

        Assert.Equal(now.AddMinutes(SnoozePreferences.Default.PrimaryMinutes), result);
    }

    /// <summary>10:00 en Madrid en enero son las 09:00 UTC; "esta tarde" son las 18:00 locales del mismo
    /// día, es decir las 17:00 UTC.</summary>
    [Fact]
    public void Evening_BeforeTheEveningHour_ResolvesToTodayAtThatWallClockTime()
    {
        var now = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Evening, now, Preferences, timeZone: Madrid);

        Assert.Equal(new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Evening_AfterTheEveningHour_RollsToTheNextDay()
    {
        // 21:00 locales del 15 de enero = 20:00 UTC; las 18:00 de hoy ya han pasado.
        var now = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Evening, now, Preferences, timeZone: Madrid);

        Assert.Equal(new DateTime(2026, 1, 16, 17, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Tomorrow_ResolvesToTheNextDayAtTheConfiguredWallClockTime()
    {
        var now = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Tomorrow, now, Preferences, timeZone: Madrid);

        // 09:00 locales del 16 de enero (CET, UTC+1) = 08:00 UTC.
        Assert.Equal(new DateTime(2026, 1, 16, 8, 0, 0, DateTimeKind.Utc), result);
    }

    /// <summary>La noche del 28 al 29 de marzo de 2026 España pasa de CET (UTC+1) a CEST (UTC+2). "Mañana
    /// a las 09:00" tiene que seguir siendo las 09:00 de reloj, no un desplazamiento de una hora: el
    /// instante UTC cambia (07:00 en vez de 08:00) precisamente porque la hora de pared se conserva.</summary>
    [Fact]
    public void Tomorrow_AcrossASpringDstTransition_KeepsTheWallClockTime()
    {
        var now = new DateTime(2026, 3, 28, 20, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Tomorrow, now, Preferences, timeZone: Madrid);

        Assert.Equal(new DateTime(2026, 3, 29, 7, 0, 0, DateTimeKind.Utc), result);
    }

    /// <summary>Con la hora de "mañana" puesta en las 02:00, la madrugada del cambio de primavera esa
    /// hora local no existe. El aplazamiento tiene que resolverse igualmente —desplazado dentro de la
    /// misma hora— en vez de lanzar.</summary>
    [Fact]
    public void Tomorrow_AtANonExistentLocalTime_StillResolves()
    {
        var preferences = SnoozePreferences.Default with { TomorrowHour = 2 };
        var now = new DateTime(2026, 3, 28, 20, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.Resolve(SnoozeOption.Tomorrow, now, preferences, timeZone: Madrid);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.True(result > now);
        Assert.True(result < now.AddDays(1));
    }

    [Fact]
    public void DeferPastDoNotDisturb_TargetInsideTheWindow_MovesItToTheEndOfTheWindow()
    {
        var settings = new AppSettings { DoNotDisturbEnabled = true, DoNotDisturbStart = "22:00", DoNotDisturbEnd = "08:00" };
        // 02:00 locales del 16 de enero = 01:00 UTC, de lleno en la franja.
        var target = new DateTime(2026, 1, 16, 1, 0, 0, DateTimeKind.Utc);

        var result = SnoozeSchedule.DeferPastDoNotDisturb(target, settings, Madrid);

        // 08:00 locales del 16 de enero = 07:00 UTC.
        Assert.Equal(new DateTime(2026, 1, 16, 7, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void DeferPastDoNotDisturb_TargetOutsideTheWindow_IsLeftAlone()
    {
        var settings = new AppSettings { DoNotDisturbEnabled = true, DoNotDisturbStart = "22:00", DoNotDisturbEnd = "08:00" };
        var target = new DateTime(2026, 1, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(target, SnoozeSchedule.DeferPastDoNotDisturb(target, settings, Madrid));
    }

    [Fact]
    public void DeferPastDoNotDisturb_WithTheWindowDisabled_IsLeftAlone()
    {
        var settings = new AppSettings { DoNotDisturbEnabled = false, DoNotDisturbStart = "22:00", DoNotDisturbEnd = "08:00" };
        var target = new DateTime(2026, 1, 16, 1, 0, 0, DateTimeKind.Utc);

        Assert.Equal(target, SnoozeSchedule.DeferPastDoNotDisturb(target, settings, Madrid));
    }

    [Fact]
    public void Preferences_FromSettings_ReadsTheFourConfigurableValues()
    {
        var settings = new AppSettings
        {
            SnoozePrimaryMinutes = 15,
            SnoozeSecondaryMinutes = 120,
            SnoozeEveningHour = 19,
            SnoozeTomorrowHour = 7
        };

        Assert.Equal(new SnoozePreferences(15, 120, 19, 7), SnoozePreferences.From(settings));
    }
}
