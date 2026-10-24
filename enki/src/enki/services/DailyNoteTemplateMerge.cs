using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace enki.Services;

/// <summary>Una sección de la plantilla que la nota diaria todavía no tiene.</summary>
/// <param name="Heading">El encabezado tal cual lo escribe la plantilla, sin las almohadillas — lo que se
/// le enseña al usuario antes de añadir nada.</param>
/// <param name="Markdown">El bloque completo (encabezado y cuerpo) que se añadiría a la nota.</param>
public readonly record struct DailyTemplateSection(string Heading, string Markdown);

/// <summary>Combina la nota diaria ya existente con su plantilla (Ajustes › Editor › Plantillas) <b>sin
/// pisar nada de lo que el usuario lleve escrito</b>: compara los encabezados Markdown de las dos y añade
/// al final de la nota únicamente las secciones de la plantilla que faltan.
///
/// <para>Existe porque aplicar la plantilla solo al crear la nota deja fuera los dos casos más normales
/// del día a día: la nota de hoy ya se había creado —a mano, desde un wikilink, desde el buscador— antes
/// de abrirla como nota diaria, o la plantilla ganó una sección nueva después de que la nota naciera. En
/// los dos, lo que el usuario quiere es "ponle lo que le falta", no "empieza de cero".</para>
///
/// <para>Estático y puro, en la línea de <see cref="NoteTemplateRenderer"/>: entra el texto de la nota y el
/// de la plantilla ya expandida, sale el texto resultante. Quien decide si hay que escribirlo, cuándo y con
/// qué aviso es el viewmodel.</para></summary>
public static class DailyNoteTemplateMerge
{
    /// <summary>Las secciones de <paramref name="renderedTemplate"/> que no están en
    /// <paramref name="noteContent"/>, en el orden en que la plantilla las declara. Lista vacía cuando la
    /// nota ya las tiene todas — el caso en el que la acción de combinar no se ofrece siquiera.
    ///
    /// <para>La comparación es por encabezado, no por contenido: un encabezado de la plantilla que ya está
    /// en la nota se respeta entero, con lo que el usuario haya escrito debajo y aunque lo haya movido,
    /// reordenado o cambiado de nivel.</para></summary>
    public static IReadOnlyList<DailyTemplateSection> MissingSections(string? noteContent, string? renderedTemplate)
    {
        if (string.IsNullOrWhiteSpace(renderedTemplate)) return [];

        var existing = Headings(noteContent).ToHashSet(StringComparer.Ordinal);
        return [.. Sections(renderedTemplate)
            .Where(section => section.Key.Length > 0 && !existing.Contains(section.Key))
            .Select(section => new DailyTemplateSection(section.Heading, section.Markdown))];
    }

    /// <summary>El texto que debe quedar en la nota tras combinarla con la plantilla, o
    /// <see langword="null"/> si no hay nada que añadir.
    ///
    /// <para>Una nota vacía (recién creada, o vaciada por el usuario) recibe la plantilla entera, incluido
    /// su preámbulo sin encabezado; una nota con texto recibe solo los bloques que le faltan, siempre al
    /// final y separados por una línea en blanco, nunca intercalados: adivinar dónde "tocaría" cada sección
    /// dentro de un texto que el usuario ya ha organizado a su manera es justo el tipo de automatismo que
    /// haría desconfiar de la acción.</para></summary>
    public static string? Merge(string? noteContent, string? renderedTemplate)
    {
        if (string.IsNullOrWhiteSpace(renderedTemplate)) return null;

        if (string.IsNullOrWhiteSpace(noteContent))
        {
            var whole = Normalize(renderedTemplate).TrimEnd();
            return whole.Length == 0 ? null : whole + "\n";
        }

        var missing = MissingSections(noteContent, renderedTemplate);
        if (missing.Count == 0) return null;

        var builder = new StringBuilder(Normalize(noteContent).TrimEnd());
        foreach (var section in missing)
        {
            builder.Append("\n\n");
            builder.Append(section.Markdown.TrimEnd());
        }
        builder.Append('\n');
        return builder.ToString();
    }

    /// <summary>Los encabezados de un Markdown, normalizados para comparar (ver <see cref="Key"/>).</summary>
    private static IEnumerable<string> Headings(string? content) =>
        content is null ? [] : Sections(content).Select(section => section.Key).Where(key => key.Length > 0);

    /// <summary>Parte un Markdown en bloques por sus encabezados ATX (<c>#</c>…<c>######</c>). El texto
    /// anterior al primer encabezado es un bloque con clave vacía —el preámbulo—, que nunca se compara ni
    /// se añade por su cuenta: no tiene un título con el que decidir si "ya está" en la nota.</summary>
    private static List<(string Key, string Heading, string Markdown)> Sections(string content)
    {
        var lines = Normalize(content).Split('\n');
        var sections = new List<(string Key, string Heading, string Markdown)>();
        var current = new StringBuilder();
        var currentKey = string.Empty;
        var currentHeading = string.Empty;
        // Un "# " dentro de un bloque de código es código, no un encabezado: sin esto, una plantilla con
        // un ejemplo de Markdown dentro de ``` se partiría por sus propias líneas de ejemplo.
        var insideFence = false;

        void Flush()
        {
            var markdown = current.ToString().Trim('\n');
            if (markdown.Length > 0 || currentKey.Length > 0)
                sections.Add((currentKey, currentHeading, markdown));
            current.Clear();
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) insideFence = !insideFence;

            if (!insideFence && TryReadHeading(line, out var heading))
            {
                Flush();
                currentKey = Key(heading);
                currentHeading = heading;
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return sections;
    }

    private static bool TryReadHeading(string line, out string heading)
    {
        heading = string.Empty;
        var trimmed = line.TrimStart();
        var level = trimmed.TakeWhile(c => c == '#').Count();
        if (level is 0 or > 6) return false;
        if (trimmed.Length > level && trimmed[level] is not (' ' or '\t')) return false;

        // La forma cerrada del encabezado ATX ("## Hoy ##") es el mismo encabezado que su forma abierta.
        heading = trimmed[level..].Trim().TrimEnd('#').Trim();
        return heading.Length > 0;
    }

    /// <summary>Clave de comparación de un encabezado: sin mayúsculas, sin espacios de más y sin el nivel
    /// (<c>## Hoy</c> y <c>### Hoy</c> son la misma sección) — que la plantilla cambie de <c>##</c> a
    /// <c>###</c> no debe duplicar en la nota una sección que ya estaba.</summary>
    private static string Key(string heading) =>
        string.Join(' ', heading.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string Normalize(string content) => content.Replace("\r\n", "\n").Replace('\r', '\n');
}
