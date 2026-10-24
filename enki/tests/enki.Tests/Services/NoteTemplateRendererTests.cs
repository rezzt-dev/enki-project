using System;
using System.Globalization;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre el motor de variables de las plantillas de notas
/// (.docs/project-info/plantillas-de-notas.md §2): qué se sustituye, qué se deja intacto y con qué cultura
/// se formatea cada cosa. Todos los casos fijan el instante de referencia por parámetro, así que ninguno
/// depende del reloj de la máquina que corre los tests.</summary>
public class NoteTemplateRendererTests
{
    private static readonly DateTime Reference = new(2026, 9, 18, 21, 5, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Render_ReplacesTitleDateAndTime()
    {
        var result = NoteTemplateRenderer.Render("# {{title}}\n{{date}} {{time}}", "Acta", Reference);

        Assert.Equal("# Acta\n2026-09-18 21:05", result.Content);
        Assert.Null(result.CaretOffset);
    }

    [Fact]
    public void Render_DefaultDateFormat_IsInvariantRegardlessOfActiveCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Una cultura cuyo formato corto de fecha no se parece en nada al ISO: si {{date}} siguiera a
            // la cultura activa, el nombre de la nota diaria cambiaría de forma al cambiar de idioma.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal("2026-09-18", NoteTemplateRenderer.Render("{{date}}", "T", Reference).Content);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Render_ExplicitFormat_FollowsTheActiveCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");

            Assert.Equal("viernes", NoteTemplateRenderer.Render("{{date:dddd}}", "T", Reference).Content);
            Assert.Equal("viernes", NoteTemplateRenderer.Render("{{weekday}}", "T", Reference).Content);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("{{date:dd/MM/yyyy}}", "18/09/2026")]
    [InlineData("{{time:HH.mm}}", "21.05")]
    [InlineData("{{datetime}}", "2026-09-18 21:05")]
    [InlineData("{{year}}", "2026")]
    public void Render_SupportsExplicitFormats(string template, string expected) =>
        Assert.Equal(expected, NoteTemplateRenderer.Render(template, "T", Reference).Content);

    /// <summary>Un formato de un solo carácter es un patrón personalizado, no el especificador estándar de
    /// .NET: <c>{{date:d}}</c> es "el día", no "la fecha corta de la cultura activa".</summary>
    [Fact]
    public void Render_SingleCharacterFormat_IsTreatedAsCustomPattern() =>
        Assert.Equal("18", NoteTemplateRenderer.Render("{{date:d}}", "T", Reference).Content);

    /// <summary>Los dos puntos del propio formato no se confunden con el separador nombre/formato.</summary>
    [Fact]
    public void Render_FormatCanContainColons() =>
        Assert.Equal("21:05:00", NoteTemplateRenderer.Render("{{time:HH:mm:ss}}", "T", Reference).Content);

    [Theory]
    [InlineData("{{date+1d}}", "2026-09-19")]
    [InlineData("{{date-1d}}", "2026-09-17")]
    [InlineData("{{date+2w}}", "2026-10-02")]
    [InlineData("{{date+1m}}", "2026-10-18")]
    [InlineData("{{date-1y}}", "2025-09-18")]
    [InlineData("{{time+2h}}", "23:05")]
    public void Render_AppliesOffsets(string template, string expected) =>
        Assert.Equal(expected, NoteTemplateRenderer.Render(template, "T", Reference).Content);

    [Fact]
    public void Render_CombinesOffsetAndFormat() =>
        Assert.Equal("19/09/2026", NoteTemplateRenderer.Render("{{date+1d:dd/MM/yyyy}}", "T", Reference).Content);

    [Theory]
    [InlineData("{{titulo}}")]
    [InlineData("{{título}}")]
    [InlineData("{{ TITLE }}")]
    public void Render_AcceptsAliasesCaseAndSpacing(string template) =>
        Assert.Equal("Acta", NoteTemplateRenderer.Render(template, "Acta", Reference).Content);

    [Fact]
    public void Render_SpanishDateAliases()
    {
        Assert.Equal("2026-09-18", NoteTemplateRenderer.Render("{{fecha}}", "T", Reference).Content);
        Assert.Equal("21:05", NoteTemplateRenderer.Render("{{hora}}", "T", Reference).Content);
        Assert.Equal("2026", NoteTemplateRenderer.Render("{{año}}", "T", Reference).Content);
    }

    /// <summary>Lo que no se reconoce se conserva: ni se borra el texto ni se cuela una cadena vacía donde
    /// el usuario esperaba ver algo.</summary>
    [Theory]
    [InlineData("{{pendiente}}")]
    [InlineData("{{date+2x}}")]
    [InlineData("{{date:'}}")]
    public void Render_LeavesUnknownVariablesUntouched(string template) =>
        Assert.Equal(template, NoteTemplateRenderer.Render(template, "T", Reference).Content);

    [Fact]
    public void Render_TextWithoutVariables_IsUnchanged() =>
        Assert.Equal("Sin variables { } {único}", NoteTemplateRenderer.Render("Sin variables { } {único}", "T", Reference).Content);

    [Fact]
    public void Render_Cursor_IsRemovedAndReportsItsOffset()
    {
        var result = NoteTemplateRenderer.Render("# {{title}}\n\n{{cursor}}\n\nFin", "Acta", Reference);

        Assert.Equal("# Acta\n\n\n\nFin", result.Content);
        Assert.Equal("# Acta\n\n".Length, result.CaretOffset);
    }

    /// <summary>Con varias marcas manda la primera, pero todas desaparecen del texto: un cursor solo puede
    /// estar en un sitio, y dejar las demás impresas en la nota sería peor que ignorarlas.</summary>
    [Fact]
    public void Render_MultipleCursors_FirstWinsAndAllAreRemoved()
    {
        var result = NoteTemplateRenderer.Render("A{{cursor}}B{{cursor}}C", "T", Reference);

        Assert.Equal("ABC", result.Content);
        Assert.Equal(1, result.CaretOffset);
    }

    [Fact]
    public void Render_Uuid_IsAValidGuidAndDiffersBetweenCalls()
    {
        var first = NoteTemplateRenderer.Render("{{uuid}}", "T", Reference).Content;
        var second = NoteTemplateRenderer.Render("{{guid}}", "T", Reference).Content;

        Assert.True(Guid.TryParse(first, out _));
        Assert.True(Guid.TryParse(second, out _));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Render_NullOrEmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NoteTemplateRenderer.Render(null, "T", Reference).Content);
        Assert.Equal(string.Empty, NoteTemplateRenderer.Render(string.Empty, "T", Reference).Content);
    }
}
