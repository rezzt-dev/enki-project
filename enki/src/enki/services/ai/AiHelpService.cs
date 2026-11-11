using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using enki.Localization;

namespace enki.Services;

/// <summary>Un apartado del manual: su título y su texto en Markdown.</summary>
public sealed record AiHelpSection(string Title, string Content);

/// <summary>Resultado de una consulta al manual.</summary>
/// <param name="Language">Idioma del manual consultado (<c>es</c> o <c>en</c>).</param>
/// <param name="Sections">Apartados que responden a la consulta, el más pertinente primero.</param>
/// <param name="Topics">Todos los títulos del manual, para afinar una consulta sin resultados.</param>
public sealed record AiHelpResult(string Language, IReadOnlyList<AiHelpSection> Sections, IReadOnlyList<string> Topics);

/// <summary>Manual de usuario de Enki que el asistente consulta con <c>search_help</c> para contestar «¿cómo
/// hago X?». Va empaquetado en el ejecutable (<c>help/manual-es.md</c> y <c>help/manual-en.md</c>), así que
/// funciona sin red y sin vault, y no envía nada del usuario: solo se devuelve texto del propio manual.
///
/// <para>Cada apartado es un encabezado <c>## </c> del Markdown. A ellos se suman, en cada consulta, los atajos
/// de teclado leídos de <see cref="ShortcutCatalog"/> con el gesto vigente de <see cref="ShortcutRegistry"/>:
/// son remapeables, y un manual estático diría el predeterminado en vez del que el usuario tiene de verdad.</para>
///
/// <para>El manual existe en castellano y en inglés; el resto de idiomas de la interfaz usan el inglés. Los
/// nombres de los atajos salen ya traducidos de los recursos de la app.</para></summary>
public static class AiHelpService
{
    public const int DefaultLimit = 4;
    public const int MaxLimit = 8;

    /// <summary>Lo más que se devuelve de un apartado; ninguno del manual se acerca, es un tope de seguridad.</summary>
    private const int MaxSectionChars = 6_000;

    private static readonly Dictionary<string, IReadOnlyList<AiHelpSection>> Manuals = new(StringComparer.Ordinal);

    /// <summary>Versión de la app a la que corresponde el manual, para que la respuesta pueda decirla.</summary>
    public static string AppVersion { get; } =
        typeof(AiHelpService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AiHelpService).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    /// <summary>Busca en el manual del idioma de la interfaz. Una consulta vacía no devuelve apartados, solo
    /// los temas.</summary>
    public static AiHelpResult Search(string query, int limit = DefaultLimit)
    {
        var language = LanguageFor(LocalizationService.Instance.CurrentCulture);
        var sections = ManualFor(language).Concat(ShortcutSections()).ToList();
        var topics = sections.Select(s => s.Title).ToList();
        var terms = Terms(query);
        if (terms.Count == 0) return new AiHelpResult(language, [], topics);

        var matches = sections
            .Select((section, index) => (Section: section, Index: index, Score: Score(section, terms)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score).ThenBy(m => m.Index)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(m => m.Section with { Content = Truncate(m.Section.Content) })
            .ToList();
        return new AiHelpResult(language, matches, topics);
    }

    internal static string LanguageFor(CultureInfo culture) => culture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    private static IReadOnlyList<AiHelpSection> ManualFor(string language)
    {
        lock (Manuals)
        {
            if (Manuals.TryGetValue(language, out var cached)) return cached;
            return Manuals[language] = Parse(ReadManual(language));
        }
    }

    private static string ReadManual(string language)
    {
        using var stream = typeof(AiHelpService).Assembly.GetManifestResourceStream($"enki.help.manual-{language}.md");
        if (stream is null)
        {
            // Un build sin el manual empaquetado no rompe el chat: la herramienta contesta solo con los atajos.
            LogService.Warning($"[ia] falta el manual integrado 'manual-{language}.md'");
            return string.Empty;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Trocea el manual por sus encabezados <c>## </c>. Lo que haya antes del primero (el título del
    /// documento) no es un apartado.</summary>
    internal static IReadOnlyList<AiHelpSection> Parse(string markdown)
    {
        var sections = new List<AiHelpSection>();
        string? title = null;
        var body = new StringBuilder();
        void Flush()
        {
            if (title is not null && body.ToString().Trim() is { Length: > 0 } content) sections.Add(new AiHelpSection(title, content));
            body.Clear();
        }
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                title = line[3..].Trim();
            }
            else if (title is not null) body.Append(line).Append('\n');
        }
        Flush();
        return sections;
    }

    /// <summary>Un apartado por categoría de atajos, con el gesto que el usuario tiene ahora mismo.</summary>
    private static IEnumerable<AiHelpSection> ShortcutSections()
    {
        var text = LocalizationService.Instance;
        return ShortcutCatalog.All
            .GroupBy(shortcut => shortcut.CategoryKey)
            .Select(group => new AiHelpSection(
                $"{text["Assistant_Help_Shortcuts"]} · {text[group.Key]}",
                string.Join('\n', group.Select(shortcut =>
                    $"- {text[shortcut.LabelKey]}: {ShortcutRegistry.GetGestureDisplay(shortcut.Id)}"))
                + "\n\n" + text["Assistant_Help_ShortcutsHint"]));
    }

    // ===================== Búsqueda =====================

    /// <summary>Palabras de la consulta sin tildes ni mayúsculas, sin las de relleno y sin la «s» final, para
    /// que «atajos» encuentre «atajo». La «ñ» se conserva: es otra letra.</summary>
    internal static List<string> Terms(string query) =>
        Fold(query).Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 1 && !StopWords.Contains(word))
            .Select(word => word.Length > 4 && word.EndsWith('s') ? word[..^1] : word)
            .Distinct()
            .ToList();

    private static int Score(AiHelpSection section, List<string> terms)
    {
        var title = Fold(section.Title);
        var content = Fold(section.Content);
        var score = 0;
        var matched = 0;
        foreach (var term in terms)
        {
            var inTitle = title.Contains(term, StringComparison.Ordinal);
            var inContent = Math.Min(3, Count(content, term));
            if (!inTitle && inContent == 0) continue;
            matched++;
            score += (inTitle ? 6 : 0) + inContent;
        }
        // Un apartado que responde a toda la consulta gana a otro que repite mucho una sola palabra.
        return matched == 0 ? 0 : score + matched * 4;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.ToLowerInvariant())
        {
            if (character == 'ñ')
            {
                builder.Append(character);
                continue;
            }
            foreach (var part in character.ToString().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark) builder.Append(part);
        }
        return builder.ToString();
    }

    private static string Truncate(string content) =>
        content.Length <= MaxSectionChars ? content : content[..MaxSectionChars] + "…";

    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '¿', '!', '¡', '"', '\'', '(', ')', '«', '»', '/', '-', '+', '`', '*', '#', '[', ']'];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "de", "la", "el", "los", "las", "un", "una", "unos", "unas", "en", "con", "por", "para", "que", "como", "se", "mi", "mis",
        "al", "del", "es", "hay", "hago", "hacer", "puedo", "donde", "esta", "the", "an", "to", "of", "in", "on", "is", "it",
        "do", "how", "can", "my", "for", "and", "or", "where", "what", "with", "does", "there"
    };
}
