using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using enki.Localization;
using enki.Models;
using enki.Views.Editor.Markdown;
using SkiaSharp;

namespace enki.Services;

/// <summary>Convierte una nota Markdown en un documento HTML autónomo, listo para compartir fuera de
/// Enki (propuesta 6 de docs/propuesta-nuevas-funcionalidades.md — "Exportar notas a PDF/HTML y vista de
/// impresión"; por ahora solo la mitad de HTML está implementada, ver <see cref="NoteExportService"/>).
/// El render de bloques e inline reutiliza <see cref="MarkdownLineParser"/>, el mismo parser ligero que
/// alimenta el live preview del editor, aplicado aquí a un documento estático — lo que la nota muestra
/// mientras se edita es lo que sale en la exportación. La plantilla es monocroma (blanco/negro/grises,
/// Space Grotesk + JetBrains Mono con fallbacks de sistema) y trae reglas <c>@page</c>/<c>@media print</c>
/// para que el propio navegador del usuario pueda imprimir el <c>.html</c> exportado o guardarlo como PDF
/// desde su diálogo de impresión — Enki no abre ningún visor ni WebView propio para eso.</summary>
public static class NoteExportRenderer
{
    /// <summary>Tope de tamaño para incrustar una imagen local como data URI — una foto de 20 MB
    /// convertiría cada exportación en un HTML inmanejable; por encima de este límite se deja la ruta
    /// original tal cual (el navegador la resolverá si el HTML queda junto a la imagen).</summary>
    private const int MaxEmbeddedImageBytes = 8 * 1024 * 1024;

    private static readonly Regex EscapeSequenceRegex = new(@"\\([!""#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~])", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex OrderedListStartRegex = new(@"^\s{0,3}(\d{1,9})[.)]\s", RegexOptions.Compiled, RegexSafety.MatchTimeout);

    /// <summary>Documento HTML completo y autónomo: cabecera con el título de la nota (y sus etiquetas si
    /// <paramref name="includeTags"/> y la nota las tiene — la "opción de incluir o no el
    /// frontmatter/tags" de la propuesta; el bloque YAML en sí nunca se vuelca como texto, no es
    /// contenido), el cuerpo Markdown renderizado y la hoja de estilo monocroma incrustada.</summary>
    public static string RenderDocument(NoteFile note, string markdownContent, bool includeTags, string vaultRootPath)
    {
        var body = RenderBodyHtml(markdownContent, Path.GetDirectoryName(note.FullPath), vaultRootPath);
        var title = WebUtility.HtmlEncode(note.Title);

        var sb = new StringBuilder(body.Length + 4096);
        sb.Append("<!doctype html>\n<html lang=\"").Append(LocalizationService.Instance.CurrentCulture.Name)
            .Append("\">\n<head>\n<meta charset=\"utf-8\"/>\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>\n<meta name=\"generator\" content=\"Enki\"/>\n<title>")
            .Append(title).Append("</title>\n<style>\n").Append(Stylesheet).Append("\n</style>\n</head>\n<body>\n<main class=\"page\">\n<header class=\"doc\">\n<h1 class=\"doc-title\">")
            .Append(title).Append("</h1>\n");

        if (includeTags && note.Tags.Count > 0)
        {
            sb.Append("<div class=\"doc-tags\">");
            foreach (var tag in note.Tags)
                sb.Append("<span class=\"doc-tag\">#").Append(WebUtility.HtmlEncode(tag)).Append("</span>");
            sb.Append("</div>\n");
        }

        sb.Append("<div class=\"doc-rule\"></div>\n</header>\n").Append(body).Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Solo el cuerpo (sin <c>&lt;html&gt;</c>/<c>&lt;style&gt;</c>): recorre el documento línea a
    /// línea con <see cref="MarkdownLineParser"/> y agrupa las líneas consecutivas del mismo tipo en sus
    /// bloques HTML (párrafos, listas, citas, tablas, cercas de código). El frontmatter YAML inicial se
    /// retira antes de renderizar — es metadato (etiquetas), no contenido del documento.</summary>
    /// <param name="noteDirectory">Carpeta de la nota en disco; se usa para resolver (e incrustar como
    /// data URI) las imágenes referenciadas con ruta relativa. Puede ser null en tests.</param>
    public static string RenderBodyHtml(string markdownContent, string? noteDirectory, string? vaultRootPath = null)
    {
        var text = NoteTagHelper.StripFrontMatter(markdownContent ?? string.Empty);
        if (text.Length == 0) return string.Empty;

        var fences = MarkdownLineParser.ComputeFences(text);
        // Qué líneas forman una tabla es, igual que las cercas de código, una pregunta que no se puede
        // contestar mirando una línea suelta: "| a | b |" es texto normal si debajo no hay separadora.
        var tables = MarkdownTableParser.Scan(text, fences);
        // Contexto HTML por línea: qué etiquetas en línea dejó abiertas una línea anterior. Un
        // <i> que cierra tres líneas más abajo tiene que seguir en cursiva aquí igual que en el editor,
        // y cada bloque exportado (<p>, <li>, <h2>…) debe salir balanceado por sí mismo.
        var htmlStates = MarkdownHtmlParser.ComputeLineStates(text, fences);
        // Fórmulas de bloque ($$…$$) y diagramas Mermaid: igual que las tablas, son bloques y no líneas.
        // Solo las fórmulas se dibujan aquí — CSharpMath compone en proceso y da una imagen estática. Un
        // diagrama Mermaid necesita el WebView del editor, que no existe en un servicio de exportación, y
        // por eso sigue saliendo como su bloque de código (T11, .docs/funcionalidades-pendientes-priorizadas.md).
        var specialBlocks = new Dictionary<int, MarkdownSpecialBlock>();
        foreach (var block in MarkdownSpecialBlocks.Scan(text, fences))
        {
            if (block.Kind == MarkdownSpecialBlockKind.Math) specialBlocks[block.StartLine] = block;
        }
        var lines = text.Split('\n');

        // Offsets absolutos de cada línea, precomputados una sola vez: el parser trabaja con posiciones
        // absolutas de documento y los bucles de agrupación necesitan consultar líneas por adelantado sin
        // arrastrar la contabilidad del offset acumulado.
        var lineOffsets = new int[lines.Length];
        var acc = 0;
        for (var n = 0; n < lines.Length; n++)
        {
            lineOffsets[n] = acc;
            acc += lines[n].Length + 1;
        }

        MdLine ParseLine(int i)
        {
            fences.TryGetValue(i + 1, out var fence);
            return MarkdownLineParser.Parse(lines[i], lineOffsets[i], fence);
        }

        bool IsCodeLine(int i) => fences.TryGetValue(i + 1, out var fence) && fence.IsCode;

        IReadOnlyList<string> OpenTags(int i) =>
            htmlStates.TryGetValue(i + 1, out var state) ? state.OpenInlineTags : HtmlLineState.Empty.OpenInlineTags;

        var sb = new StringBuilder(text.Length * 2);
        var inCode = false;
        var i = 0;
        while (i < lines.Length)
        {
            // Dentro de una cerca de código no hay inline ni agrupación: cada línea se vuelca escapada
            // tal cual entre el <pre><code> que abre el delimitador y el que cierra.
            if (inCode)
            {
                if (IsCodeLine(i) && fences[i + 1].IsDelimiter)
                {
                    sb.Append("</code></pre>\n");
                    inCode = false;
                }
                else
                {
                    sb.Append(WebUtility.HtmlEncode(lines[i])).Append('\n');
                }
                i++;
                continue;
            }

            if (IsCodeLine(i) && fences[i + 1].IsDelimiter)
            {
                var languageClass = fences[i + 1].Language is { Length: > 0 } language
                    ? $" class=\"language-{WebUtility.HtmlEncode(language)}\""
                    : string.Empty;
                sb.Append("<pre><code").Append(languageClass).Append('>');
                inCode = true;
                i++;
                continue;
            }

            var parsed = ParseLine(i);

            // Línea con un contenedor HTML ("<div align=\"center\">", suelto o envolviendo su propio
            // texto): se vuelca tal cual, sin envolverla en un <p> — es un bloque por sí misma, y el
            // navegador hereda su text-align a todo lo que quede dentro.
            if (parsed.Kind == MdBlockKind.Paragraph && MarkdownHtmlParser.HasBlockTag(parsed.HtmlTags))
            {
                sb.Append(RenderInline(lines[i], lineOffsets[i], parsed, OpenTags(i), noteDirectory, vaultRootPath)).Append('\n');
                i++;
                continue;
            }

            // Fórmula de bloque: se emite su imagen y el bucle salta más allá del `$$` de cierre.
            if (specialBlocks.TryGetValue(i + 1, out var mathBlock))
            {
                AppendMath(sb, mathBlock.Source, display: true);
                i = mathBlock.EndLine;
                continue;
            }

            // Tabla: se emite entera de una vez (encabezado + cuerpo) y el bucle salta a la primera
            // línea de después. Va antes del switch porque sus filas son párrafos para
            // MarkdownLineParser — lo que las hace tabla es el bloque, no la línea.
            if (tables.TryGetValue(i + 1, out var tableRow) && tableRow.Kind == MdTableRowKind.Header)
            {
                i = EmitTable(lines, lineOffsets, ParseLine, OpenTags, tableRow.Table, sb, noteDirectory, vaultRootPath);
                continue;
            }

            switch (parsed.Kind)
            {
                case MdBlockKind.Heading:
                    sb.Append("<h").Append(parsed.HeadingLevel).Append('>')
                        .Append(RenderInline(lines[i], lineOffsets[i], parsed, OpenTags(i), noteDirectory, vaultRootPath))
                        .Append("</h").Append(parsed.HeadingLevel).Append(">\n");
                    i++;
                    break;

                case MdBlockKind.HorizontalRule:
                    sb.Append("<hr/>\n");
                    i++;
                    break;

                case MdBlockKind.Blockquote:
                    sb.Append("<blockquote><p>");
                    {
                        var first = true;
                        while (i < lines.Length && !IsCodeLine(i))
                        {
                            var line = ParseLine(i);
                            if (line.Kind != MdBlockKind.Blockquote) break;
                            if (!first) sb.Append("<br/>\n");
                            sb.Append(RenderInline(lines[i], lineOffsets[i], line, OpenTags(i), noteDirectory, vaultRootPath));
                            first = false;
                            i++;
                        }
                    }
                    sb.Append("</p></blockquote>\n");
                    break;

                case MdBlockKind.BulletList:
                case MdBlockKind.TaskList:
                case MdBlockKind.OrderedList:
                    i = EmitList(lines, lineOffsets, ParseLine, IsCodeLine, OpenTags, i, parsed, sb, noteDirectory, vaultRootPath);
                    break;

                default:
                    // Línea en blanco: separador de bloques, no genera HTML por sí misma.
                    if (IsBlank(lines[i])) { i++; break; }

                    // Párrafo: agrupa las líneas consecutivas de texto plano, preservando los saltos de
                    // línea simples como <br/> — en el editor cada línea es una línea visible, así que la
                    // exportación los respeta en vez de fundirlos como haría CommonMark estricto.
                    sb.Append("<p>");
                    {
                        var first = true;
                        while (i < lines.Length && !IsCodeLine(i) && !IsBlank(lines[i]))
                        {
                            var line = ParseLine(i);
                            if (line.Kind != MdBlockKind.Paragraph) break;
                            if (MarkdownHtmlParser.HasBlockTag(line.HtmlTags)) break;
                            if (!first) sb.Append("<br/>\n");
                            sb.Append(RenderInline(lines[i], lineOffsets[i], line, OpenTags(i), noteDirectory, vaultRootPath));
                            first = false;
                            i++;
                        }
                    }
                    sb.Append("</p>\n");
                    break;
            }
        }

        // Cerca sin cerrar: ciérrala para no dejar el documento con un <pre> abierto.
        if (inCode) sb.Append("</code></pre>\n");
        return sb.ToString();
    }

    /// <summary>Lista consecutiva de viñetas, numerada o de tareas — cada línea es un
    /// <c>&lt;li&gt;</c>. Un cambio de tipo de lista cierra la actual (la siguiente iteración del bucle
    /// principal abriría otra): el parser del editor no anida listas por indentación, así que la
    /// exportación tampoco lo hace — coherente con lo que el usuario ve al editar. Devuelve el índice de
    /// la primera línea no consumida.</summary>
    private static int EmitList(string[] lines, int[] lineOffsets, Func<int, MdLine> parseLine, Func<int, bool> isCodeLine,
        Func<int, IReadOnlyList<string>> openTags, int startIndex, MdLine firstParsed, StringBuilder sb,
        string? noteDirectory, string? vaultRootPath)
    {
        var kind = firstParsed.Kind;
        if (kind == MdBlockKind.OrderedList)
        {
            // Respeta el número inicial ("3. …" exporta <ol start="3">) — el dígito se recupera del texto
            // crudo de la primera línea, que el parser no conserva.
            var startMatch = OrderedListStartRegex.Match(lines[startIndex]);
            var start = startMatch.Success && startMatch.Groups[1].Value != "1"
                ? $" start=\"{startMatch.Groups[1].Value}\""
                : string.Empty;
            sb.Append("<ol").Append(start).Append(">\n");
        }
        else
        {
            sb.Append(kind == MdBlockKind.TaskList ? "<ul class=\"tasks\">\n" : "<ul>\n");
        }

        var i = startIndex;
        while (i < lines.Length && !isCodeLine(i))
        {
            var line = parseLine(i);
            if (line.Kind != kind) break;

            if (kind == MdBlockKind.TaskList)
            {
                sb.Append("<li class=\"task\"><input type=\"checkbox\" disabled")
                    .Append(line.TaskChecked ? " checked" : string.Empty)
                    .Append("/> ")
                    .Append(RenderInline(lines[i], lineOffsets[i], line, openTags(i), noteDirectory, vaultRootPath))
                    .Append("</li>\n");
            }
            else
            {
                sb.Append("<li>")
                    .Append(RenderInline(lines[i], lineOffsets[i], line, openTags(i), noteDirectory, vaultRootPath))
                    .Append("</li>\n");
            }
            i++;
        }

        sb.Append(kind == MdBlockKind.OrderedList ? "</ol>\n" : "</ul>\n");
        return i;
    }

    /// <summary>Vuelca una tabla completa como <c>&lt;table&gt;</c>: la fila de encabezados en
    /// <c>&lt;thead&gt;</c> con <c>&lt;th&gt;</c>, las de datos en <c>&lt;tbody&gt;</c>, y la fila
    /// separadora en nada — su única información, la alineación de cada columna, viaja como clase CSS de
    /// cada celda. Toda fila se rellena hasta <see cref="MdTable.ColumnCount"/> columnas para que el
    /// documento exportado tenga la misma rejilla rectangular que dibuja el editor, aunque una fila de la
    /// nota tenga menos celdas que las demás. Devuelve el índice de la primera línea no consumida.</summary>
    private static int EmitTable(string[] lines, int[] lineOffsets, Func<int, MdLine> parseLine,
        Func<int, IReadOnlyList<string>> openTags, MdTable table, StringBuilder sb, string? noteDirectory,
        string? vaultRootPath)
    {
        void EmitRow(int lineIndex, string cellTag)
        {
            var lineText = lines[lineIndex];
            var lineOffset = lineOffsets[lineIndex];
            var model = parseLine(lineIndex);
            var cells = MarkdownTableParser.SplitCells(lineText, lineOffset);

            sb.Append("<tr>");
            for (var column = 0; column < table.ColumnCount; column++)
            {
                sb.Append('<').Append(cellTag).Append(AlignClass(table.Alignments[column])).Append('>');
                if (column < cells.Count)
                {
                    sb.Append(RenderInline(lineText, lineOffset, model, openTags(lineIndex), noteDirectory, vaultRootPath,
                        cells[column].Start, cells[column].End));
                }
                sb.Append("</").Append(cellTag).Append('>');
            }
            sb.Append("</tr>\n");
        }

        sb.Append("<table>\n<thead>\n");
        EmitRow(table.StartLine - 1, "th");
        sb.Append("</thead>\n");

        if (table.BodyRowCount > 0)
        {
            sb.Append("<tbody>\n");
            for (var line = table.DelimiterLine + 1; line <= table.EndLine; line++) EmitRow(line - 1, "td");
            sb.Append("</tbody>\n");
        }

        sb.Append("</table>\n");
        return table.EndLine;
    }

    private static string AlignClass(MdTableAlign align) => align switch
    {
        MdTableAlign.Left => " class=\"ta-left\"",
        MdTableAlign.Center => " class=\"ta-center\"",
        MdTableAlign.Right => " class=\"ta-right\"",
        _ => string.Empty
    };

    /// <summary>Renderiza el rango [<paramref name="contentStart"/>, fin de línea) aplicando los spans
    /// inline del parser: escapa el texto llano y emite las etiquetas de cada span. Los spans llegan en
    /// orden y sin solaparse (el parser nunca anida — <c>**negrita _cursiva_**</c> deja los marcadores
    /// internos como texto literal, igual que el live preview), así que basta un recorrido lineal.</summary>
    /// <param name="from">Offset absoluto donde empezar, o null para empezar en el contenido de la línea.
    /// Lo usan las celdas de una tabla, que son trozos de línea y no líneas enteras.</param>
    /// <param name="to">Offset absoluto donde parar, o null para llegar al final de la línea.</param>
    private static string RenderInline(string lineText, int lineOffset, MdLine model,
        IReadOnlyList<string> openAtLineStart, string? noteDirectory, string? vaultRootPath,
        int? from = null, int? to = null)
    {
        var sb = new StringBuilder(lineText.Length * 2);
        var pos = from ?? Math.Max(model.ContentStart, lineOffset);
        var limit = to ?? lineOffset + lineText.Length;

        // Las etiquetas en línea que vengan abiertas de una línea previa se reabren aquí, y las que esta
        // línea deje abiertas se cierran al final: cada bloque HTML que emitimos es autónomo, y un
        // navegador no arrastra un <i> sin cerrar más allá del </p> que lo contiene.
        foreach (var name in openAtLineStart) sb.Append('<').Append(name).Append('>');

        foreach (var span in model.Spans)
        {
            if (span.Start < pos) continue;
            // Un span que se sale del rango (un `**` abierto en una celda y cerrado en la siguiente) no
            // es de esta celda: se deja como texto literal en vez de emitir una etiqueta a medias.
            if (span.End > limit) continue;

            AppendPlainText(sb, lineText, pos - lineOffset, span.Start - lineOffset);
            AppendSpan(sb, span, model, lineText, lineOffset, noteDirectory, vaultRootPath);
            pos = span.End;
        }

        AppendPlainText(sb, lineText, pos - lineOffset, limit - lineOffset);

        var stillOpen = new List<string>(openAtLineStart);
        MarkdownHtmlParser.ApplyInlineTags(stillOpen, model.HtmlTags);
        for (var i = stillOpen.Count - 1; i >= 0; i--) sb.Append("</").Append(stillOpen[i]).Append('>');

        return sb.ToString();
    }

    private static void AppendSpan(StringBuilder sb, MdSpan span, MdLine model, string lineText, int lineOffset,
        string? noteDirectory, string? vaultRootPath)
    {
        var content = Slice(lineText, lineOffset, span.ContentStart, span.ContentEnd);

        switch (span.Kind)
        {
            case MdSpanKind.Bold:
                sb.Append("<strong>").Append(EscapeText(content)).Append("</strong>");
                break;
            case MdSpanKind.Italic:
                sb.Append("<em>").Append(EscapeText(content)).Append("</em>");
                break;
            case MdSpanKind.BoldItalic:
                sb.Append("<strong><em>").Append(EscapeText(content)).Append("</em></strong>");
                break;
            case MdSpanKind.Strikethrough:
                sb.Append("<del>").Append(EscapeText(content)).Append("</del>");
                break;
            case MdSpanKind.InlineCode:
                // El código es verbatim: sin escapes de barra invertida, solo escape HTML.
                sb.Append("<code>").Append(WebUtility.HtmlEncode(content)).Append("</code>");
                break;
            case MdSpanKind.Highlight:
                sb.Append("<mark>").Append(EscapeText(content)).Append("</mark>");
                break;
            case MdSpanKind.HtmlTag:
                // Las etiquetas HTML reconocidas del editor pasan tal cual al documento exportado — es
                // la única forma de que un <div align="justify"> siga alineando aquí lo mismo que allí —
                // pero saneadas (ver MarkdownHtmlParser.SanitizedTag): el contenido de una nota no puede
                // colar atributos arbitrarios en el HTML resultante.
                sb.Append(SanitizedTagAt(model, span.Start));
                break;
            case MdSpanKind.WikiLink:
                // Un wikilink no resuelve a nada fuera del vault — en el documento estático queda como
                // texto destacado (subrayado discontinuo, el "enlace interno" del editor), no como un
                // hiperenlace roto.
                sb.Append("<span class=\"wikilink\">").Append(EscapeText(content)).Append("</span>");
                break;
            case MdSpanKind.Math:
                AppendMath(sb, content, span.IsDisplayMath);
                break;
            case MdSpanKind.Transclusion:
                // La nota embebida no se resuelve fuera del vault, así que el documento estático conserva
                // la referencia en vez de tragársela — mismo criterio que un wikilink.
                sb.Append("<span class=\"wikilink\">").Append(EscapeText(span.LinkTarget ?? content)).Append("</span>");
                break;
            case MdSpanKind.Image:
                AppendImage(sb, span, content, noteDirectory, vaultRootPath);
                break;
            case MdSpanKind.Link:
                AppendLink(sb, span, content);
                break;
        }
    }

    /// <summary>Fórmula LaTeX como imagen estática incrustada (PNG en data URI, negro sobre el fondo
    /// blanco de la plantilla) para que el documento exportado siga siendo un archivo autónomo, sin
    /// script ni CDN. Si el render falla, sale el LaTeX original entre delimitadores, que es legible.</summary>
    private static void AppendMath(StringBuilder sb, string latex, bool display)
    {
        var png = LatexRenderService.RenderPng(latex, display, display ? 26f : 19f, SKColors.Black);
        var delimiter = display ? "$$" : "$";
        if (png is null)
        {
            var raw = WebUtility.HtmlEncode(delimiter + latex + delimiter);
            if (display) sb.Append("<pre><code class=\"math\">").Append(raw).Append("</code></pre>\n");
            else sb.Append("<code class=\"math\">").Append(raw).Append("</code>");
            return;
        }

        var alt = WebUtility.HtmlEncode(latex);
        if (display) sb.Append("<p class=\"math-block\">");
        sb.Append("<img class=\"math\" alt=\"").Append(alt).Append("\" src=\"data:image/png;base64,")
            .Append(Convert.ToBase64String(png)).Append("\"/>");
        if (display) sb.Append("</p>\n");
    }

    private static string SanitizedTagAt(MdLine model, int start)
    {
        foreach (var tag in model.HtmlTags)
        {
            if (tag.Start == start) return MarkdownHtmlParser.SanitizedTag(tag);
        }
        return string.Empty;
    }

    private static void AppendLink(StringBuilder sb, MdSpan span, string text)
    {
        var url = span.LinkTarget ?? string.Empty;
        if (!IsSafeUrl(url) && !LooksLikeLocalPath(url))
        {
            sb.Append(EscapeText(text));
            return;
        }
        sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
            .Append(EscapeText(text)).Append("</a>");
    }

    private static void AppendImage(StringBuilder sb, MdSpan span, string alt, string? noteDirectory, string? vaultRootPath)
    {
        var url = span.LinkTarget ?? string.Empty;
        if (NoteAttachmentService.IsAttachmentTarget(url))
        {
            AppendLink(sb, span, string.IsNullOrWhiteSpace(alt) ? Path.GetFileName(url) : alt);
            return;
        }
        // A pre-existing data: image (pasted straight into the note, or from a previous export) doesn't
        // match SafeSchemes/LooksLikeLocalPath — it's neither an http(s)/mailto URL nor a filesystem path
        // — so without this check it was silently dropped to alt-text-only even though it's already a
        // fully self-contained src that needs no further work (análisis §4.2, Fase 8). Scoped to images
        // only: a data: *link* could carry data:text/html and execute script if opened, which AppendLink's
        // stricter SafeSchemes allowlist correctly keeps rejecting.
        if (!IsSafeUrl(url) && !LooksLikeLocalPath(url) && !IsDataImageUri(url))
        {
            sb.Append(EscapeText(alt));
            return;
        }

        var src = TryEmbedLocalImage(url, noteDirectory, vaultRootPath) ?? url;
        sb.Append("<img src=\"").Append(WebUtility.HtmlEncode(src)).Append("\" alt=\"")
            .Append(WebUtility.HtmlEncode(EscapeSequenceRegex.Replace(alt, "$1"))).Append('"');
        // El tamaño explícito de `![alt|300x200](src)` viaja al HTML como atributos width/height, para que
        // el documento exportado se vea como se veía en el editor y no a tamaño natural.
        if (span.ImageWidth > 0) sb.Append(" width=\"").Append(span.ImageWidth.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (span.ImageHeight > 0) sb.Append(" height=\"").Append(span.ImageHeight.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append("/>");
    }

    /// <summary>Incrusta una imagen local (ruta relativa a la carpeta de la nota) como data URI base64,
    /// para que el HTML exportado sea autocontenido. Devuelve null si la ruta no apunta a una imagen, el
    /// archivo no existe, supera <see cref="MaxEmbeddedImageBytes"/> o no se puede leer — en ese caso el
    /// src original se mantiene tal cual.</summary>
    private static string? TryEmbedLocalImage(string url, string? noteDirectory, string? vaultRootPath)
    {
        if (noteDirectory is null || vaultRootPath is null || url.Length == 0 || IsSafeUrl(url)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        string path;
        try
        {
            path = Path.GetFullPath(Path.Combine(noteDirectory, url));
            var root = Path.GetFullPath(vaultRootPath);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root : root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, VaultService.PathComparison)) return null;
            if (ContainsReparsePoint(root, path)) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            LogService.Debug($"No se pudo resolver la imagen local '{url}' durante la exportación: {ex}");
            return null;
        }

        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => (string?)null
        };
        if (mime is null) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEmbeddedImageBytes) return null;
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>La contención léxica no basta: un enlace dentro del vault puede apuntar fuera y hacer que
    /// <see cref="File.ReadAllBytes(string)"/> termine leyendo otro árbol. Se rechaza cualquier componente
    /// enlazado entre la raíz configurada y la imagen.
    ///
    /// <para><b>Un componente que no existe no es un enlace</b> y no puede redirigir a ninguna parte, así
    /// que el recorrido se detiene ahí y la comprobación pasa. Quien decide entonces es la comprobación de
    /// existencia de <see cref="TryEmbedLocalImage"/>, que devuelve <see langword="null"/> y deja el
    /// <c>src</c> original tal cual — exactamente lo que debe ocurrir con el enlace a una imagen que el
    /// usuario borró o escribió mal. Antes, <see cref="File.GetAttributes(string)"/> lanzaba sobre esa
    /// ruta y la excepción escapaba hasta <see cref="RenderBodyHtml"/>: un único enlace roto tumbaba la
    /// exportación entera de la nota.</para>
    ///
    /// <para>Cualquier <em>otro</em> fallo al leer los atributos (permisos, E/S) sí rechaza. No poder
    /// comprobar si un componente es un enlace no es lo mismo que haber comprobado que no lo es, y esta
    /// función existe precisamente para no acabar leyendo fuera del vault
    /// (.docs/auditoria-seguridad-y-fallos-criticos.md).</para></summary>
    private static bool ContainsReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, component);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Este componente no existe, así que ninguno más abajo existe tampoco: no hay nada que
                // pueda redirigir la lectura. Ver el comentario de este método.
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Debug(
                    $"No se pudieron leer los atributos de '{current}' al exportar; no se incrusta la imagen: {ex}");
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    /// <summary>Esquemas permitidos en enlaces e imágenes, y qué cuenta como ruta local: los dos criterios
    /// viven ya en <see cref="ExternalLinkPolicy"/>, que es donde el Ctrl+clic del editor los aplica para
    /// no entregarle al sistema un destino arbitrario (.docs/auditoria-seguridad-y-fallos-criticos.md,
    /// SEC-01). Aquí el criterio es el mismo pero la consecuencia es otra: lo que no pasa el filtro se
    /// degrada a texto plano, porque el HTML exportado se abre fuera de la app y no debe ejecutar nada.
    /// Una ruta local sí se conserva tal cual (el navegador la resolverá si el documento queda junto a sus
    /// archivos), sin la comprobación de existencia que sí hace la política del editor.</summary>
    private static bool IsSafeUrl(string url) => ExternalLinkPolicy.IsSafeUrl(url);

    private static bool LooksLikeLocalPath(string url) => ExternalLinkPolicy.LooksLikeLocalPath(url);

    private static readonly Regex DataImageUriRegex = new(@"^data:image/[a-zA-Z0-9.+\-]+;base64,", RegexOptions.Compiled, RegexSafety.MatchTimeout);

    private static bool IsDataImageUri(string url) => DataImageUriRegex.IsMatch(url);

    /// <summary>Primero resuelve los escapes de barra invertida de Markdown (<c>\*</c> → <c>*</c>) y
    /// después aplica el escape HTML sobre el resultado — al revés fallaría el caso <c>\&gt;</c> escrito
    /// por el usuario, cuyo <c>&gt;</c> ya escapado no volvería a coincidir con el regex de escapes.</summary>
    private static string EscapeText(string text)
    {
        var unescaped = EscapeSequenceRegex.Replace(text, "$1");
        return WebUtility.HtmlEncode(unescaped);
    }

    private static void AppendPlainText(StringBuilder sb, string lineText, int from, int to)
    {
        if (to <= from) return;
        sb.Append(EscapeText(lineText[from..to]));
    }

    private static string Slice(string lineText, int lineOffset, int absoluteStart, int absoluteEnd)
    {
        var start = Math.Clamp(absoluteStart - lineOffset, 0, lineText.Length);
        var end = Math.Clamp(absoluteEnd - lineOffset, start, lineText.Length);
        return lineText[start..end];
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    /// <summary>Hoja de estilo monocroma de exportación — blanco papel, tinta casi negra, grises para lo
    /// secundario; Space Grotesk para texto y JetBrains Mono para código con fallbacks de sistema (las
    /// fuentes empaquetadas en <c>assets/fonts</c> no se incrustan: pesarían varios MB por documento).
    /// Las reglas <c>@page</c>/<c>@media print</c> hacen que el mismo documento sirva para pantalla,
    /// impresión y PDF sin variante alguna.</summary>
    private const string Stylesheet = """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; }
        body { margin: 0; background: #ffffff; color: #141414;
               font-family: 'Space Grotesk', 'Segoe UI', system-ui, -apple-system, sans-serif;
               font-size: 15px; line-height: 1.65; }
        .page { max-width: 760px; margin: 0 auto; padding: 48px 32px; }
        header.doc { margin-bottom: 30px; }
        h1.doc-title { font-size: 30px; line-height: 1.2; letter-spacing: -0.5px; margin: 0; }
        .doc-tags { margin-top: 12px; display: flex; flex-wrap: wrap; gap: 6px; }
        .doc-tag { font-family: 'JetBrains Mono', 'Cascadia Code', ui-monospace, Consolas, monospace;
                   font-size: 10.5px; letter-spacing: 1px; text-transform: uppercase;
                   border: 1px solid #c9c9c9; padding: 2px 8px; color: #333333; }
        .doc-rule { border-bottom: 1px solid #141414; margin-top: 18px; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; letter-spacing: -0.3px; margin: 26px 0 10px; }
        h1 { font-size: 26px; } h2 { font-size: 22px; } h3 { font-size: 18px; }
        h4 { font-size: 16px; } h5, h6 { font-size: 14px; letter-spacing: 0.4px; text-transform: uppercase; }
        p { margin: 0 0 14px; }
        a { color: inherit; text-decoration: underline; text-underline-offset: 3px; }
        .wikilink { font-weight: 600; border-bottom: 1px dashed #8a8a8a; }
        img.math { vertical-align: middle; max-width: 100%; }
        .math-block { text-align: center; margin: 18px 0; }
        code, pre { font-family: 'JetBrains Mono', 'Cascadia Code', ui-monospace, Consolas, monospace; }
        code { background: #f0f0f0; padding: 1px 5px; font-size: 0.86em; }
        pre { background: #f5f5f5; border: 1px solid #e0e0e0; padding: 14px 16px; margin: 0 0 16px;
              overflow-x: auto; font-size: 13px; line-height: 1.55; }
        pre code { background: none; padding: 0; font-size: inherit; }
        blockquote { margin: 0 0 14px; padding: 4px 16px; border-left: 3px solid #141414; color: #2e2e2e; }
        blockquote p { margin: 0; }
        ul, ol { margin: 0 0 14px; padding-left: 26px; }
        ul.tasks { list-style: none; padding-left: 6px; }
        li.task input[type="checkbox"] { margin: 0 8px 0 0; vertical-align: -1px; }
        table { border-collapse: collapse; margin: 0 0 16px; width: 100%; }
        th, td { border: 1px solid #d4d4d4; padding: 6px 10px; text-align: left; font-size: 0.92em;
                 vertical-align: top; }
        th { background: #f0f0f0; font-weight: 700; }
        .ta-left { text-align: left; } .ta-center { text-align: center; } .ta-right { text-align: right; }
        hr { border: 0; border-top: 1px solid #c9c9c9; margin: 28px 0; }
        mark { background: #e3e3e3; padding: 0 2px; }
        img { max-width: 100%; height: auto; }
        @page { margin: 18mm 16mm; }
        @media print {
          .page { max-width: none; padding: 0; }
          pre, blockquote, table, img { page-break-inside: avoid; }
          h1, h2, h3, h4, h5, h6 { page-break-after: avoid; }
        }
        """;
}
