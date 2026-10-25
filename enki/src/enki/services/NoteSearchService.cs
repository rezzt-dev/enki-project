using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using enki.Models;

namespace enki.Services;

/// <summary>Búsqueda de texto completo sobre el vault: título, etiquetas y —lo que faltaba— el
/// <b>contenido real</b> de cada nota, con el fragmento de texto donde aparece la coincidencia y su
/// posición exacta en el documento.
///
/// <para>Antes de este servicio, las cuatro superficies de búsqueda de la app (búsqueda global, quick
/// switcher, rejilla de Notas y vista simplificada) comparaban contra <see cref="NoteFile.Preview"/>: los
/// primeros 160 caracteres del cuerpo, aplanados y sin sintaxis Markdown. Todo lo escrito más allá de esos
/// 160 caracteres era imposible de encontrar desde la aplicación, lo que en una app cuyo núcleo es un
/// vault Markdown personal deja el resto del producto sin sentido.</para>
///
/// <para>No mantiene índice propio ni almacén nuevo: lee a través de
/// <see cref="VaultService.GetAllNotesAcrossVaultWithContent"/>, que ya sirve el contenido desde
/// <see cref="VaultNoteCache"/> (invalidado por mtime y por cada escritura de la app), de modo que una
/// búsqueda repetida no vuelve a tocar el disco. Ese mismo recorrido es también el que incluye las notas
/// que viven dentro de sesiones —invisibles hasta ahora para la búsqueda global, que solo miraba la raíz
/// del vault— y el que excluye por completo las de una sesión bloqueada, sin filtrado extra aquí.</para></summary>
public sealed class NoteSearchService
{
    /// <summary>Ancho objetivo del fragmento de contexto, en caracteres. Suficiente para reconocer la frase
    /// en el popup de resultados sin que el subtítulo tape el título de la nota.</summary>
    private const int SnippetWidth = 120;

    private readonly VaultService _vaultService;

    public NoteSearchService(VaultService vaultService)
    {
        _vaultService = vaultService;
    }

    /// <summary>Todas las notas del vault (raíz + sesiones desbloqueadas) que casan con
    /// <paramref name="query"/>, como mucho una entrada por nota: la mejor coincidencia de esa nota. El
    /// orden es por relevancia (título exacto, título que empieza por la consulta, título que la contiene,
    /// etiqueta, contenido) y, a igualdad, alfabético por título, para que el resultado no baile entre
    /// pulsaciones.</summary>
    public List<NoteSearchHit> Search(string query, int maxResults = 200, CancellationToken token = default) =>
        SearchIn(_vaultService.GetAllNotesAcrossVaultWithContent(), query, maxResults, token);

    /// <summary>Igual que <see cref="Search"/> pero acotado a las notas directamente dentro de
    /// <paramref name="folderPath"/>, sin bajar a sus subcarpetas — el alcance del buscador de la rejilla de
    /// Notas, que filtra lo que el usuario está viendo en ese momento.</summary>
    public List<NoteSearchHit> SearchFolder(string folderPath, string query, int maxResults = 200, CancellationToken token = default) =>
        SearchIn(_vaultService.GetNotesWithContent(folderPath), query, maxResults, token);

    /// <summary>Igual que <see cref="Search"/> pero acotado al árbol completo bajo <paramref name="root"/> —
    /// el alcance del buscador de la vista simplificada, que busca en toda la sesión (o en todo el vault)
    /// que el usuario tenga seleccionada.</summary>
    public List<NoteSearchHit> SearchTree(string root, string query, int maxResults = 200, CancellationToken token = default) =>
        SearchIn(_vaultService.GetAllNotesRecursiveWithContent(root), query, maxResults, token);

    private static List<NoteSearchHit> SearchIn(
        IEnumerable<(NoteFile Note, string Content)> notes,
        string query,
        int maxResults,
        CancellationToken token)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0) return [];

        var hits = new List<NoteSearchHit>();
        foreach (var (note, content) in notes)
        {
            token.ThrowIfCancellationRequested();

            var hit = BestHitFor(note, content, query);
            if (hit is not null) hits.Add(hit);
        }

        return hits
            .OrderBy(RelevanceRank)
            .ThenBy(h => h.Note.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>La coincidencia de mayor peso dentro de una sola nota, o null si no hay ninguna. El cuerpo
    /// se examina siempre, incluso cuando el título ya casó, porque <see cref="NoteSearchHit.MatchCount"/>
    /// cuenta las apariciones reales en el texto.</summary>
    private static NoteSearchHit? BestHitFor(NoteFile note, string content, string query)
    {
        var contentOffset = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var matchCount = contentOffset < 0 ? 0 : CountOccurrences(content, query);

        if (note.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new NoteSearchHit
            {
                Note = note,
                Field = NoteSearchField.Title,
                MatchCount = matchCount
            };
        }

        if (note.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return new NoteSearchHit
            {
                Note = note,
                Field = NoteSearchField.Tag,
                MatchCount = matchCount
            };
        }

        if (contentOffset < 0) return null;

        return new NoteSearchHit
        {
            Note = note,
            Field = NoteSearchField.Content,
            Offset = contentOffset,
            LineNumber = LineNumberAt(content, contentOffset),
            MatchCount = matchCount,
            Snippet = BuildSnippet(content, contentOffset, query.Length)
        };
    }

    /// <summary>Peso de ordenación: cuanto menor, más arriba. Un título que <em>es</em> la consulta va
    /// antes que uno que empieza por ella, y ambos antes que cualquier acierto en el cuerpo.</summary>
    private static int RelevanceRank(NoteSearchHit hit) => hit.Field switch
    {
        NoteSearchField.Title => 0,
        NoteSearchField.Tag => 1,
        _ => 2
    };

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>Línea (base 1) en la que cae <paramref name="offset"/>. Cuenta solo <c>\n</c>: el editor
    /// (AvaloniaEdit) trabaja sobre el mismo texto crudo que se lee del disco, así que un <c>\r\n</c>
    /// aporta un único salto de línea en ambos lados.</summary>
    private static int LineNumberAt(string content, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    /// <summary>Recorta la línea que contiene el acierto a <see cref="SnippetWidth"/> caracteres alrededor
    /// de él, con elipsis en los extremos que se hayan cortado. Se queda dentro de la línea a propósito: un
    /// fragmento que cruza saltos de línea se lee como una frase que la nota no contiene.</summary>
    private static string BuildSnippet(string content, int offset, int queryLength)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, Math.Min(offset, content.Length - 1))) + 1;
        var lineEnd = content.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = content.Length;

        var line = content[lineStart..lineEnd].Trim('\r');
        var offsetInLine = offset - lineStart;

        if (line.Length <= SnippetWidth) return line.Trim();

        // Centra la ventana en el acierto, pero sin salirse por ninguno de los dos extremos de la línea.
        var start = Math.Max(0, offsetInLine - (SnippetWidth - queryLength) / 2);
        start = Math.Min(start, Math.Max(0, line.Length - SnippetWidth));
        var length = Math.Min(SnippetWidth, line.Length - start);

        var snippet = line.Substring(start, length).Trim();
        if (start > 0) snippet = "…" + snippet;
        if (start + length < line.Length) snippet += "…";
        return snippet;
    }
}
