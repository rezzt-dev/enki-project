using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using enki.Models;

namespace enki.Services;

/// <summary>Descubre qué notas del vault conviene enlazar con la nota activa: las que comparten etiquetas
/// o enlaces con ella (<see cref="FindRelated"/>) y las que la nombran en texto plano sin enlazarla
/// (<see cref="FindUnlinkedMentions"/>). Trabaja sobre el contenido ya leído por
/// <see cref="VaultService.GetAllNotesAcrossVaultWithContent"/> —el mismo barrido que ya hace el panel
/// de enlaces entrantes— y resuelve los wikilinks con el criterio de <see cref="WikiLinkParser"/>, así
/// que el panel y el Ctrl+click nunca discrepan sobre a qué nota apunta un enlace. No escribe nada: el
/// enlazado en un clic lo hace <see cref="VaultService.LinkMention"/> por el camino comprobado.</summary>
public static class NoteRelationsService
{
    /// <summary>Longitud mínima de un nombre para buscarlo como mención; «a» o «de» no son menciones.</summary>
    public const int MinimumMentionLength = 3;

    private const int MaxContextLength = 120;

    /// <summary>Notas que comparten etiquetas o enlaces con <paramref name="targetNote"/>. Excluye las que
    /// ya enlazan a ella y las que ella ya enlaza —esas no hay nada que descubrir—. Ordena por la fuerza de
    /// la relación (un enlace compartido pesa el doble que una etiqueta), después por recientes.</summary>
    public static IReadOnlyList<RelatedNote> FindRelated(
        IReadOnlyList<(NoteFile Note, string Content)> allNotesWithContent, NoteFile targetNote, int maxResults = 8)
    {
        var index = new NoteIndex(allNotesWithContent.Select(t => t.Note));

        var targetEntry = allNotesWithContent.FirstOrDefault(t => VaultService.PathsEqual(t.Note.FullPath, targetNote.FullPath));
        var targetContent = targetEntry.Note is null ? string.Empty : targetEntry.Content;
        var targetTags = (targetEntry.Note?.Tags ?? targetNote.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetLinks = OutgoingPaths(index, targetContent, targetNote.FullPath);
        if (targetTags.Count == 0 && targetLinks.Count == 0) return [];

        var related = new List<(RelatedNote Item, DateTime ModifiedUtc)>();
        foreach (var (note, content) in allNotesWithContent)
        {
            if (VaultService.PathsEqual(note.FullPath, targetNote.FullPath)) continue;

            var outgoing = OutgoingPaths(index, content, note.FullPath);
            if (outgoing.Contains(Normalize(targetNote.FullPath))) continue;
            if (targetLinks.Contains(Normalize(note.FullPath))) continue;

            var sharedTags = note.Tags.Where(targetTags.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            var sharedLinks = outgoing.Where(targetLinks.Contains)
                .Select(p => index.ByPath.TryGetValue(p, out var linked) ? linked.Title : null)
                .OfType<string>().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (sharedTags.Count == 0 && sharedLinks.Count == 0) continue;

            related.Add((new RelatedNote
            {
                FullPath = note.FullPath,
                Title = note.Title,
                LinkName = index.LinkNameFor(note),
                SharedTags = sharedTags,
                SharedLinks = sharedLinks
            }, note.ModifiedUtc));
        }

        return related
            .OrderByDescending(r => r.Item.Score)
            .ThenByDescending(r => r.ModifiedUtc)
            .ThenBy(r => r.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(r => r.Item)
            .ToList();
    }

    /// <summary>Notas que escriben el título (o el nombre de archivo) de <paramref name="targetNote"/> como
    /// texto plano. Una nota que ya enlaza a la activa no se ofrece: el enlace ya existe. Se ignoran el
    /// frontmatter, el código (en bloque y en línea), los enlaces de cualquier tipo, las direcciones y las
    /// fórmulas. Devuelve la primera mención de cada nota.</summary>
    public static IReadOnlyList<UnlinkedMention> FindUnlinkedMentions(
        IReadOnlyList<(NoteFile Note, string Content)> allNotesWithContent, NoteFile targetNote, int maxResults = 20)
    {
        var pattern = BuildMentionPattern(targetNote);
        if (pattern is null) return [];

        var index = new NoteIndex(allNotesWithContent.Select(t => t.Note));
        var linkName = index.LinkNameFor(targetNote);
        // Sin un nombre que apunte inequívocamente a la nota, un enlace nuevo iría a otra: no se ofrece.
        if (linkName is null) return [];

        var result = new List<UnlinkedMention>();
        foreach (var (note, content) in allNotesWithContent)
        {
            if (VaultService.PathsEqual(note.FullPath, targetNote.FullPath)) continue;
            if (!ContainsAnyName(content, targetNote)) continue;
            if (OutgoingPaths(index, content, note.FullPath).Contains(Normalize(targetNote.FullPath))) continue;

            if (FindFirstMention(content, pattern) is not { } match) continue;

            result.Add(new UnlinkedMention
            {
                FullPath = note.FullPath,
                Title = note.Title,
                Context = ExtractContext(content, match.Index, match.Length),
                MatchedText = match.Text,
                LinkName = linkName
            });
            if (result.Count >= maxResults) break;
        }

        return result;
    }

    /// <summary>Sustituye la primera mención sin enlazar de <paramref name="targetNote"/> en
    /// <paramref name="content"/> por un wikilink: <c>[[Nombre]]</c> si el texto coincide con el nombre, o
    /// <c>[[Nombre|texto]]</c> si solo difiere en mayúsculas o es el título frente al archivo, de modo que
    /// la frase se lee igual. Devuelve <see langword="null"/> si ya no hay ninguna mención que enlazar.</summary>
    public static string? LinkFirstMention(string content, NoteFile targetNote, string linkName)
    {
        var pattern = BuildMentionPattern(targetNote);
        if (pattern is null) return null;

        if (FindFirstMention(content, pattern) is not { } match) return null;

        var link = string.Equals(match.Text, linkName, StringComparison.Ordinal)
            ? "[[" + linkName + "]]"
            : "[[" + linkName + "|" + match.Text + "]]";
        return string.Concat(content.AsSpan(0, match.Index), link, content.AsSpan(match.Index + match.Length));
    }

    /// <summary>Copia de <paramref name="content"/> con el mismo largo y los mismos saltos de línea, pero
    /// con un espacio donde hay texto que no puede ser una mención: frontmatter, código, enlaces,
    /// direcciones y fórmulas. Los desplazamientos de la copia valen para el original.</summary>
    public static string MaskNonProse(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var chars = content.ToCharArray();
        MaskFrontmatter(content, chars);
        MaskFencedCode(content, chars);
        foreach (var pattern in MaskPatterns) MaskMatches(pattern, new string(chars), chars);
        return new string(chars);
    }

    private static readonly Regex[] MaskPatterns =
    [
        new(@"`+[^`\n]*`+", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"!?\[\[[^\]\n]*\]\]", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"!?\[[^\]\n]*\]\([^)\n]*\)", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"!?\[[^\]\n]*\]\[[^\]\n]*\]", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"<[^>\n]+>", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"\b(?:https?|ftp|file|webcal)://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexSafety.MatchTimeout),
        new(@"\$\$[\s\S]*?\$\$", RegexOptions.Compiled, RegexSafety.MatchTimeout),
        new(@"(?<![\\$])\$[^$\n]+\$", RegexOptions.Compiled, RegexSafety.MatchTimeout)
    ];

    private static void MaskMatches(Regex pattern, string text, char[] chars)
    {
        foreach (Match match in pattern.Matches(text))
            for (var i = match.Index; i < match.Index + match.Length; i++)
                if (chars[i] is not ('\n' or '\r')) chars[i] = ' ';
    }

    private static void MaskFrontmatter(string content, char[] chars)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return;
        var firstEol = content.IndexOf('\n');
        if (firstEol < 0 || content[..firstEol].TrimEnd() != "---") return;

        var cursor = firstEol + 1;
        while (cursor < content.Length)
        {
            var eol = content.IndexOf('\n', cursor);
            var end = eol < 0 ? content.Length : eol;
            if (content[cursor..end].TrimEnd() == "---")
            {
                Blank(chars, 0, end);
                return;
            }
            cursor = end + 1;
        }
    }

    private static void MaskFencedCode(string content, char[] chars)
    {
        var cursor = 0;
        char fence = '\0';
        var fenceLength = 0;
        var blockStart = 0;

        while (cursor < content.Length)
        {
            var eol = content.IndexOf('\n', cursor);
            var end = eol < 0 ? content.Length : eol;
            var line = content.AsSpan(cursor, end - cursor).TrimStart();

            if (fence == '\0')
            {
                if (line.Length >= 3 && (line[0] == '`' || line[0] == '~'))
                {
                    var run = 0;
                    while (run < line.Length && line[run] == line[0]) run++;
                    if (run >= 3) { fence = line[0]; fenceLength = run; blockStart = cursor; }
                }
            }
            else
            {
                var run = 0;
                while (run < line.Length && line[run] == fence) run++;
                if (run >= fenceLength && line[run..].Trim().IsEmpty)
                {
                    Blank(chars, blockStart, end);
                    fence = '\0';
                }
            }

            cursor = end + 1;
        }

        // Un bloque sin cerrar se come el resto de la nota, igual que en el editor.
        if (fence != '\0') Blank(chars, blockStart, content.Length);
    }

    private static void Blank(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
            if (chars[i] is not ('\n' or '\r')) chars[i] = ' ';
    }

    /// <summary>Nombres con los que se puede mencionar una nota: título y nombre de archivo, si son
    /// distintos y no demasiado cortos, del más largo al más corto para que «Nota larga» gane a «Nota».</summary>
    private static List<string> MentionNames(NoteFile note) =>
        new[] { note.Title, Path.GetFileNameWithoutExtension(note.FullPath) }
            .Select(n => n?.Trim() ?? string.Empty)
            .Where(n => n.Length >= MinimumMentionLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
            .ToList();

    private static Regex? BuildMentionPattern(NoteFile note)
    {
        var names = MentionNames(note);
        if (names.Count == 0) return null;

        // Límites de palabra Unicode: «Tarea» no es una mención dentro de «Tareas» ni de «subtarea».
        var alternatives = string.Join('|', names.Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}_])(?:{alternatives})(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexSafety.MatchTimeout);
    }

    /// <summary>Descarte barato antes de enmascarar: la mayoría de las notas no contienen el nombre.</summary>
    private static bool ContainsAnyName(string content, NoteFile note) =>
        MentionNames(note).Any(n => content.Contains(n, StringComparison.OrdinalIgnoreCase));

    private readonly record struct Mention(int Index, int Length, string Text);

    private static Mention? FindFirstMention(string content, Regex pattern)
    {
        try
        {
            // Se busca en la copia enmascarada; el texto sale del original, que solo difiere donde la
            // máscara puso espacios y ahí el patrón (letras y dígitos) nunca puede haber coincidido.
            var match = pattern.Match(MaskNonProse(content));
            return match.Success ? new Mention(match.Index, match.Length, content.Substring(match.Index, match.Length)) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string ExtractContext(string content, int start, int length)
    {
        var lineStart = start == 0 ? -1 : content.LastIndexOf('\n', start - 1);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = content.IndexOf('\n', start + length);
        if (lineEnd < 0) lineEnd = content.Length;

        var context = content[lineStart..lineEnd].Trim();
        return context.Length > MaxContextLength ? context[..MaxContextLength] + "…" : context;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static HashSet<string> OutgoingPaths(NoteIndex index, string content, string ownPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in WikiLinkParser.ExtractTargets(content))
        {
            if (index.Resolve(target) is { } resolved && !VaultService.PathsEqual(resolved.FullPath, ownPath))
                result.Add(Normalize(resolved.FullPath));
        }
        return result;
    }

    /// <summary>Resolución de wikilinks con el mismo criterio que <see cref="WikiLinkParser.ResolveTarget"/>
    /// (nombre de archivo único primero, título único como respaldo) pero con diccionarios construidos una
    /// vez: resolver cada enlace de cada nota con un barrido de la lista sería cuadrático en vaults grandes.</summary>
    private sealed class NoteIndex
    {
        private readonly Dictionary<string, List<NoteFile>> _byFileName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<NoteFile>> _byTitle = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, NoteFile> ByPath { get; } = new(StringComparer.Ordinal);

        public NoteIndex(IEnumerable<NoteFile> notes)
        {
            foreach (var note in notes)
            {
                ByPath[Normalize(note.FullPath)] = note;
                Add(_byFileName, Path.GetFileNameWithoutExtension(note.FullPath), note);
                Add(_byTitle, note.Title, note);
            }
        }

        public NoteFile? Resolve(string target)
        {
            if (_byFileName.TryGetValue(target, out var byName)) return byName.Count == 1 ? byName[0] : null;
            if (_byTitle.TryGetValue(target, out var byTitle) && byTitle.Count == 1) return byTitle[0];
            return null;
        }

        /// <summary>El nombre que, escrito dentro de <c>[[…]]</c>, resuelve a <paramref name="note"/> y a
        /// ninguna otra: el nombre de archivo si es único, el título en su defecto.</summary>
        public string? LinkNameFor(NoteFile note)
        {
            var fileName = Path.GetFileNameWithoutExtension(note.FullPath);
            if (Resolve(fileName) is { } byName && VaultService.PathsEqual(byName.FullPath, note.FullPath)) return fileName;
            if (!string.IsNullOrWhiteSpace(note.Title)
                && Resolve(note.Title) is { } byTitle && VaultService.PathsEqual(byTitle.FullPath, note.FullPath)) return note.Title;
            return null;
        }

        private static void Add(Dictionary<string, List<NoteFile>> map, string key, NoteFile note)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(note);
        }
    }
}
