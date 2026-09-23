using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="DailyNoteTemplateMerge"/>, la parte de "combinar la nota diaria con su
/// plantilla" que decide qué se añade y qué no. La regla que vertebra todos los casos: <b>nunca se toca lo
/// que el usuario ya escribió</b>.</summary>
public class DailyNoteTemplateMergeTests
{
    private const string Template = "## Hoy\n\n- \n\n## Notas\n\n## Mañana\n";

    [Fact]
    public void MissingSections_ListsOnlyWhatTheNoteDoesNotHave()
    {
        var note = "# 2026-09-18\n\n## Hoy\n\n- Escribir el informe\n";

        var missing = DailyNoteTemplateMerge.MissingSections(note, Template);

        Assert.Equal(["Notas", "Mañana"], missing.Select(section => section.Heading));
    }

    [Fact]
    public void MissingSections_IsEmptyWhenTheNoteAlreadyHasEveryHeading()
    {
        var note = "## Hoy\n\n## Notas\n\n## Mañana\n";

        Assert.Empty(DailyNoteTemplateMerge.MissingSections(note, Template));
    }

    /// <summary>Que la plantilla cambie el nivel del encabezado no puede duplicar en la nota una sección
    /// que ya está.</summary>
    [Fact]
    public void MissingSections_IgnoresTheHeadingLevelAndCase()
    {
        var note = "### hoy\n\ntexto\n";

        var missing = DailyNoteTemplateMerge.MissingSections(note, "## Hoy\n\n- \n");

        Assert.Empty(missing);
    }

    [Fact]
    public void Merge_AppendsTheMissingSectionsAtTheEnd()
    {
        var note = "# 2026-09-18\n\n## Hoy\n\n- Escribir el informe\n";

        var merged = DailyNoteTemplateMerge.Merge(note, Template);

        Assert.NotNull(merged);
        Assert.StartsWith("# 2026-09-18\n\n## Hoy\n\n- Escribir el informe", merged);
        Assert.Contains("## Notas", merged);
        Assert.Contains("## Mañana", merged);
        // Lo escrito sigue exactamente donde estaba, y su sección no se duplica.
        Assert.Single(merged!.Split("## Hoy").Skip(1));
        Assert.Contains("- Escribir el informe", merged);
    }

    [Fact]
    public void Merge_ReturnsNullWhenThereIsNothingToAdd() =>
        Assert.Null(DailyNoteTemplateMerge.Merge("## Hoy\n\n## Notas\n\n## Mañana\n", Template));

    [Fact]
    public void Merge_ReturnsNullWithoutATemplate() =>
        Assert.Null(DailyNoteTemplateMerge.Merge("## Hoy\n", renderedTemplate: null));

    /// <summary>Una nota vacía recibe la plantilla entera, incluido el preámbulo anterior al primer
    /// encabezado, que por sí solo nunca se añade a una nota con texto.</summary>
    [Fact]
    public void Merge_EmptyNote_ReceivesTheWholeTemplate()
    {
        var merged = DailyNoteTemplateMerge.Merge("   \n", "# 2026-09-18\n\n## Hoy\n");

        Assert.Equal("# 2026-09-18\n\n## Hoy\n", merged);
    }

    /// <summary>Un ejemplo de Markdown dentro de un bloque de código es código, no un encabezado de la
    /// plantilla: no se cuenta como sección ni al buscarla ni al añadirla.</summary>
    [Fact]
    public void MissingSections_IgnoresHeadingsInsideCodeFences()
    {
        var template = "## Hoy\n\n```md\n## Ejemplo\n```\n";

        var missing = DailyNoteTemplateMerge.MissingSections("texto suelto", template);

        Assert.Equal(["Hoy"], missing.Select(section => section.Heading));
        Assert.Contains("## Ejemplo", missing[0].Markdown);
    }

    [Fact]
    public void Merge_NormalizesWindowsLineEndings()
    {
        var merged = DailyNoteTemplateMerge.Merge("# 2026-09-18\r\n", "## Hoy\r\n");

        Assert.Equal("# 2026-09-18\n\n## Hoy\n", merged);
    }
}
