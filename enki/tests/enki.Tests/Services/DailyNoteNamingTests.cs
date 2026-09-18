using System;
using System.Globalization;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="DailyNoteNaming"/>: el nombre que recibe la nota diaria de cada día y —lo que
/// hace posible la navegación día anterior / día siguiente— la lectura inversa de ese nombre. Sin tocar
/// disco: la clase es estática y pura, y una <see cref="NoteTemplate"/> se construye aquí a mano.</summary>
public class DailyNoteNamingTests
{
    private static NoteTemplate Template(string? titlePattern) => new()
    {
        Id = "daily",
        Name = "Diario",
        TitlePattern = titlePattern,
        FilePath = "/tmp/daily.md",
        RelativePath = "daily.md",
        Body = "# {{date}}\n"
    };

    private static readonly DateOnly SampleDate = new(2026, 9, 18);

    [Fact]
    public void TitleFor_WithoutTemplate_IsTheInvariantIsoDate() =>
        Assert.Equal("2026-09-18", DailyNoteNaming.TitleFor(SampleDate, template: null));

    [Fact]
    public void TitleFor_TemplateWithoutTitlePattern_FallsBackToTheIsoDate() =>
        Assert.Equal("2026-09-18", DailyNoteNaming.TitleFor(SampleDate, Template(null)));

    [Fact]
    public void TitleFor_AppliesTheTemplateTitlePattern() =>
        Assert.Equal("Diario 2026-09-18", DailyNoteNaming.TitleFor(SampleDate, Template("Diario {{date}}")));

    [Fact]
    public void TitleFor_AppliesAnExplicitDateFormat() =>
        Assert.Equal("18-09-2026", DailyNoteNaming.TitleFor(SampleDate, Template("{{date:dd-MM-yyyy}}")));

    [Fact]
    public void TryParseDate_ReadsTheIsoDateWithoutTemplate()
    {
        Assert.True(DailyNoteNaming.TryParseDate("2026-09-18", template: null, out var date));
        Assert.Equal(SampleDate, date);
    }

    [Fact]
    public void TryParseDate_ReadsTheDateBackFromTheTemplatePattern()
    {
        Assert.True(DailyNoteNaming.TryParseDate("Diario 2026-09-18", Template("Diario {{date}}"), out var date));
        Assert.Equal(SampleDate, date);
    }

    [Fact]
    public void TryParseDate_ReadsTheDateBackFromACustomFormat()
    {
        Assert.True(DailyNoteNaming.TryParseDate("18-09-2026", Template("{{date:dd-MM-yyyy}}"), out var date));
        Assert.Equal(SampleDate, date);
    }

    /// <summary>Configurar una plantilla hoy no puede dejar huérfanas las notas diarias que el vault ya
    /// tenía con el nombre <c>yyyy-MM-dd</c>: el calendario de constancia se vaciaría de golpe.</summary>
    [Fact]
    public void TryParseDate_StillReadsThePlainIsoNameWhenATemplateIsConfigured()
    {
        Assert.True(DailyNoteNaming.TryParseDate("2026-09-18", Template("Diario {{date}}"), out var date));
        Assert.Equal(SampleDate, date);
    }

    [Theory]
    [InlineData("Ideas sueltas")]
    [InlineData("Diario")]
    [InlineData("2026-13-40")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseDate_RejectsWhatIsNotADailyNoteName(string? title) =>
        Assert.False(DailyNoteNaming.TryParseDate(title, Template("Diario {{date}}"), out _));

    /// <summary>Un desplazamiento en el <c>title</c> nombraría la nota con un día distinto del que
    /// representa y haría imposible encontrarla: se ignora solo ahí, de modo que el nombre sigue
    /// identificando su propio día en las dos direcciones.</summary>
    [Fact]
    public void TitleFor_IgnoresAnOffsetInTheTitlePattern()
    {
        var template = Template("Diario {{date+1d}}");

        var title = DailyNoteNaming.TitleFor(SampleDate, template);

        Assert.Equal("Diario 2026-09-18", title);
        Assert.True(DailyNoteNaming.TryParseDate(title, template, out var parsed));
        Assert.Equal(SampleDate, parsed);
    }

    /// <summary>Un patrón que no fija el día (solo mes y año) no identifica una nota diaria: dos días
    /// distintos producirían el mismo nombre.</summary>
    [Fact]
    public void TryParseDate_RejectsATitlePatternWithoutADay() =>
        Assert.False(DailyNoteNaming.TryParseDate("septiembre 2026", Template("{{month}} {{year}}"), out _));

    /// <summary>El caso que justifica la validación de ida y vuelta: el nombre encaja con el formato
    /// derivado del patrón, pero no es el que la plantilla habría generado para esa fecha.</summary>
    [Fact]
    public void TryParseDate_RejectsANameThatDoesNotRoundTrip() =>
        Assert.False(DailyNoteNaming.TryParseDate("Notas 2026-09-18", Template("Diario {{date}}"), out _));

    /// <summary>Ida y vuelta completa sobre un rango largo: todo nombre generado por el patrón se vuelve a
    /// leer como el día que lo generó.</summary>
    [Theory]
    [InlineData("Diario {{date}}")]
    [InlineData("{{date}}")]
    [InlineData("{{date:yyyy.MM.dd}} · diario")]
    public void TitleFor_AndTryParseDate_RoundTripEveryDayOfAYear(string pattern)
    {
        var template = Template(pattern);
        for (var date = new DateOnly(2026, 1, 1); date.Year == 2026; date = date.AddDays(1))
        {
            var title = DailyNoteNaming.TitleFor(date, template);
            Assert.True(DailyNoteNaming.TryParseDate(title, template, out var parsed), title);
            Assert.Equal(date, parsed);
        }
    }

    /// <summary>El nombre sin plantilla no depende del idioma activo: es un nombre de archivo, no texto de
    /// interfaz.</summary>
    [Fact]
    public void TitleFor_IsInvariantAcrossCultures()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("2026-09-18", DailyNoteNaming.TitleFor(SampleDate, template: null));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
