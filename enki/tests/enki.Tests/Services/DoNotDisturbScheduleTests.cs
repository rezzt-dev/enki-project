using System;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="DoNotDisturbSchedule.IsWithin"/> — el horario "no molestar"
/// (docs/ajustes-docs/04-editor-y-notificaciones.md §2), incluyendo el caso que motiva tenerlo como método
/// puro aparte: una ventana que cruza medianoche (p. ej. 22:00–08:00, el valor por defecto).</summary>
public class DoNotDisturbScheduleTests
{
    [Theory]
    [InlineData("23:30")]
    [InlineData("00:00")]
    [InlineData("07:59")]
    public void CrossingMidnightWindow_TimesInsideIt_ReturnTrue(string time)
    {
        Assert.True(DoNotDisturbSchedule.IsWithin("22:00", "08:00", TimeOnly.Parse(time)));
    }

    [Theory]
    [InlineData("08:00")]
    [InlineData("12:00")]
    [InlineData("21:59")]
    public void CrossingMidnightWindow_TimesOutsideIt_ReturnFalse(string time)
    {
        Assert.False(DoNotDisturbSchedule.IsWithin("22:00", "08:00", TimeOnly.Parse(time)));
    }

    [Theory]
    [InlineData("13:00")]
    [InlineData("17:59")]
    public void SameDayWindow_TimesInsideIt_ReturnTrue(string time)
    {
        Assert.True(DoNotDisturbSchedule.IsWithin("13:00", "18:00", TimeOnly.Parse(time)));
    }

    [Theory]
    [InlineData("12:59")]
    [InlineData("18:00")]
    [InlineData("23:00")]
    public void SameDayWindow_TimesOutsideIt_ReturnFalse(string time)
    {
        Assert.False(DoNotDisturbSchedule.IsWithin("13:00", "18:00", TimeOnly.Parse(time)));
    }

    [Fact]
    public void EqualStartAndEnd_NeverActive()
    {
        Assert.False(DoNotDisturbSchedule.IsWithin("10:00", "10:00", TimeOnly.Parse("10:00")));
    }

    [Theory]
    [InlineData(null, "08:00")]
    [InlineData("22:00", null)]
    [InlineData("not-a-time", "08:00")]
    public void InvalidTimes_ReturnFalseInsteadOfThrowing(string? start, string? end)
    {
        Assert.False(DoNotDisturbSchedule.IsWithin(start, end, TimeOnly.Parse("23:00")));
    }
}
