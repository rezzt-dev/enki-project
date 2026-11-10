using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace enki.Services;

/// <summary>Word (DOCX) y PowerPoint (PPTX), y sus variantes con macros y de plantilla, como texto estructurado y
/// compacto para el asistente. A diferencia del visor de Archivos (<see cref="ContainerPreviewService"/>), que
/// solo enseña párrafos, conserva lo que da sentido al documento sin gastar tokens en el formato:
///
/// <list type="bullet">
/// <item>Word: títulos como <c>#</c> según su estilo o nivel de esquema, listas con <c>-</c> y sangría, tablas
/// como filas <c>| a | b |</c>, cuadros de texto, cabecera y pie (una vez), notas al pie y al final
/// (<c>[^1]</c>) y comentarios con su autor. El texto eliminado con control de cambios y los códigos de campo
/// no se incluyen.</item>
/// <item>PowerPoint: una sección por diapositiva en el orden de la presentación (no el de los archivos), con su
/// título, el texto de cada forma con su nivel, las tablas y las notas del orador; las ocultas se marcan.</item>
/// </list>
///
/// No ejecuta macros ni sigue vínculos externos, el XML prohíbe DTD y entidades externas, y las ramas
/// <c>mc:Fallback</c> se omiten para no duplicar el contenido que Office guarda dos veces.</summary>
internal static class OfficeTextExtractor
{
    private const long MaxXmlBytes = 64L * 1024 * 1024;
    private const int MaxParts = 2000;
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static bool Supports(string path) => IsWord(path) || IsPowerPoint(path);

    public static bool IsWord(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".dotm";

    public static bool IsPowerPoint(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".pptx" or ".pptm" or ".potx" or ".potm" or ".ppsx" or ".ppsm";

    /// <exception cref="InvalidDataException">No es un documento de Office válido o supera los límites.</exception>
    /// <exception cref="XmlException">El XML está mal formado o declara DTD.</exception>
    public static string Read(byte[] bytes, string path, int maxChars, out bool truncated)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var output = new Output(maxChars);
        var budget = new XmlBudget();
        if (IsWord(path)) ReadWord(archive, output, budget);
        else ReadPowerPoint(archive, output, budget);
        truncated = output.Full;
        return output.ToString();
    }

    // ===================== Word =====================

    private static void ReadWord(ZipArchive archive, Output output, XmlBudget budget)
    {
        var document = Load(archive, "word/document.xml", budget) ?? throw new InvalidDataException("The document has no main part.");
        var headings = ReadHeadingStyles(archive, budget);

        var headers = SectionTexts(archive, "word/header", headings, budget);
        foreach (var header in headers) output.Line("[Header] " + header);

        var body = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "body")
            ?? throw new InvalidDataException("The document has no body.");
        WriteBlocks(body, output, headings, 0);

        foreach (var footer in SectionTexts(archive, "word/footer", headings, budget)) output.Line("[Footer] " + footer);
        WriteNotes(archive, "word/footnotes.xml", "footnote", "^", output, headings, budget);
        WriteNotes(archive, "word/endnotes.xml", "endnote", "^e", output, headings, budget);

        if (Load(archive, "word/comments.xml", budget)?.Root is { } comments)
            foreach (var comment in comments.Elements().Where(e => e.Name.LocalName == "comment"))
            {
                var text = BlockText(comment, headings);
                if (text.Length == 0) continue;
                var author = Attr(comment, "author");
                output.Line(string.IsNullOrWhiteSpace(author) ? $"[Comment] {text}" : $"[Comment by {author}] {text}");
            }
    }

    /// <summary>Recorre los bloques de un contenedor (cuerpo, celda, cuadro de texto): párrafos, tablas y los
    /// envoltorios de controles de contenido, que no aportan texto propio.</summary>
    private static void WriteBlocks(XElement container, Output output, WordStyles headings, int depth)
    {
        foreach (var element in container.Elements())
        {
            if (output.Full) return;
            switch (element.Name.LocalName)
            {
                case "p":
                    WriteParagraph(element, output, headings, depth);
                    break;
                case "tbl":
                    WriteWordTable(element, output, headings);
                    break;
                case "sdt" or "sdtContent" or "customXml" or "smartTag" or "ins" or "AlternateContent" or "Choice":
                    WriteBlocks(element, output, headings, depth);
                    break;
            }
        }
    }

    private static void WriteParagraph(XElement paragraph, Output output, WordStyles headings, int depth)
    {
        var boxes = new List<XElement>();
        var text = new StringBuilder();
        Inline(paragraph, text, boxes);
        var line = Clean(text.ToString());
        if (line.Length > 0)
        {
            var properties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            var level = HeadingLevel(properties, headings);
            if (level > 0)
            {
                output.Blank();
                output.Line(new string('#', level) + " " + line);
            }
            else if (ListIndent(properties, headings) is { } indent)
                output.Line(new string(' ', indent * 2) + "- " + line);
            else output.Line(line);
        }
        // Un cuadro de texto es un contenedor de bloques dentro del párrafo: se escribe detrás, una sola vez.
        if (depth < 4)
            foreach (var box in boxes) WriteBlocks(box, output, headings, depth + 1);
    }

    private static void Inline(XElement element, StringBuilder text, List<XElement> boxes)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "t":
                    text.Append(child.Value);
                    break;
                case "tab" or "br" or "cr" or "ptab":
                    text.Append(' ');
                    break;
                case "noBreakHyphen":
                    text.Append('-');
                    break;
                case "footnoteReference":
                    text.Append("[^").Append(Attr(child, "id")).Append(']');
                    break;
                case "endnoteReference":
                    text.Append("[^e").Append(Attr(child, "id")).Append(']');
                    break;
                case "txbxContent":
                    boxes.Add(child);
                    break;
                // Propiedades, texto borrado con control de cambios, códigos de campo y la copia de compatibilidad.
                case "pPr" or "rPr" or "del" or "delText" or "instrText" or "Fallback":
                    break;
                default:
                    Inline(child, text, boxes);
                    break;
            }
        }
    }

    private static void WriteWordTable(XElement table, Output output, WordStyles headings)
    {
        output.Blank();
        foreach (var row in table.Elements().Where(e => e.Name.LocalName == "tr"))
        {
            if (output.Full) return;
            var cells = row.Elements().Where(e => e.Name.LocalName == "tc").Select(cell => BlockText(cell, headings).Replace("|", "/")).ToList();
            if (cells.All(c => c.Length == 0)) continue;
            output.Line("| " + string.Join(" | ", cells) + " |");
        }
        output.Blank();
    }

    /// <summary>Todo el texto de un contenedor en una sola línea (una celda, un comentario, una nota).</summary>
    private static string BlockText(XElement container, WordStyles headings)
    {
        var parts = new List<string>();
        foreach (var paragraph in container.Descendants().Where(e => e.Name.LocalName == "p" && !UnderFallback(e)))
        {
            // Los párrafos de una tabla anidada se recogen como los demás; los de un cuadro de texto, también.
            var text = new StringBuilder();
            InlineOwn(paragraph, text);
            var line = Clean(text.ToString());
            if (line.Length > 0) parts.Add(line);
        }
        return string.Join(" ", parts);
    }

    /// <summary>Como <see cref="Inline"/>, pero sin entrar en párrafos anidados (ya se recorren por separado).</summary>
    private static void InlineOwn(XElement element, StringBuilder text)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "t": text.Append(child.Value); break;
                case "tab" or "br" or "cr": text.Append(' '); break;
                case "p" or "pPr" or "rPr" or "del" or "delText" or "instrText" or "Fallback" or "txbxContent": break;
                default: InlineOwn(child, text); break;
            }
        }
    }

    private static bool UnderFallback(XElement element) => element.Ancestors().Any(a => a.Name.LocalName == "Fallback");

    /// <summary>Cabeceras o pies distintos (las secciones suelen repetir el mismo).</summary>
    private static List<string> SectionTexts(ZipArchive archive, string prefix, WordStyles headings, XmlBudget budget)
    {
        var texts = new List<string>();
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal).Take(64))
        {
            if (Load(entry, budget)?.Root is not { } root) continue;
            var text = BlockText(root, headings);
            if (text.Length > 0 && !texts.Contains(text, StringComparer.Ordinal)) texts.Add(text);
        }
        return texts;
    }

    private static void WriteNotes(ZipArchive archive, string part, string element, string marker, Output output,
        WordStyles headings, XmlBudget budget)
    {
        if (Load(archive, part, budget)?.Root is not { } root) return;
        var first = true;
        foreach (var note in root.Elements().Where(e => e.Name.LocalName == element))
        {
            // Los separadores que Word guarda como notas no son contenido.
            if (Attr(note, "type") is "separator" or "continuationSeparator" or "continuationNotice") continue;
            var text = BlockText(note, headings);
            if (text.Length == 0) continue;
            if (first) output.Blank();
            first = false;
            output.Line($"[{marker}{Attr(note, "id")}] {text}");
        }
    }

    /// <summary>Lo que los estilos de párrafo aportan al texto: su nivel de título y, en los estilos de lista
    /// ("List Bullet", "List Number 2"…), su nivel de sangría.</summary>
    private sealed class WordStyles
    {
        public Dictionary<string, int> Headings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Lists { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Nivel de título de cada estilo de párrafo: "Title" es 1, "heading N" es N, y si no, su nivel de
    /// esquema; y los estilos que numeran o ponen viñeta. Se resuelve por el nombre del estilo, que Word guarda
    /// en inglés aunque el identificador esté traducido ("Ttulo1").</summary>
    private static WordStyles ReadHeadingStyles(ZipArchive archive, XmlBudget budget)
    {
        var styles = new WordStyles();
        var levels = styles.Headings;
        if (Load(archive, "word/styles.xml", budget)?.Root is not { } root) return styles;
        foreach (var style in root.Elements().Where(e => e.Name.LocalName == "style" && Attr(e, "type") == "paragraph"))
        {
            if (Attr(style, "styleId") is not { } id) continue;
            var name = Attr(style.Elements().FirstOrDefault(e => e.Name.LocalName == "name"), "val")?.ToLowerInvariant() ?? string.Empty;
            var level = name == "title" ? 1
                : name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                : OutlineLevel(style.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr"));
            if (level > 0) levels[id] = Math.Clamp(level, 1, 6);
            else if (style.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")?.Elements().FirstOrDefault(e => e.Name.LocalName == "numPr") is { } numbering)
                styles.Lists[id] = ListLevel(numbering);
        }
        return styles;
    }

    private static int ListLevel(XElement numbering) =>
        int.TryParse(Attr(numbering.Elements().FirstOrDefault(e => e.Name.LocalName == "ilvl"), "val"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? Math.Clamp(level, 0, 8) : 0;

    private static int HeadingLevel(XElement? properties, WordStyles headings)
    {
        if (properties is null) return 0;
        var direct = OutlineLevel(properties);
        if (direct > 0) return Math.Clamp(direct, 1, 6);
        var style = Attr(properties.Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle"), "val");
        return style is not null && headings.Headings.TryGetValue(style, out var level) ? level : 0;
    }

    /// <summary>Sangría de un elemento de lista, por su propia numeración o por la de su estilo; <see langword="null"/>
    /// si el párrafo no es de lista. Un <c>numId</c> 0 quita la numeración que pondría el estilo.</summary>
    private static int? ListIndent(XElement? properties, WordStyles styles)
    {
        if (properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "numPr") is { } numbering)
            return Attr(numbering.Elements().FirstOrDefault(e => e.Name.LocalName == "numId"), "val") == "0" ? null : ListLevel(numbering);
        var style = Attr(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle"), "val");
        return style is not null && styles.Lists.TryGetValue(style, out var level) ? level : null;
    }

    /// <summary>Nivel de esquema de Word (0 = nivel 1; 9 = texto independiente) como nivel de título.</summary>
    private static int OutlineLevel(XElement? properties) =>
        int.TryParse(Attr(properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "outlineLvl"), "val"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 0 and < 9 ? level + 1 : 0;

    // ===================== PowerPoint =====================

    private static void ReadPowerPoint(ZipArchive archive, Output output, XmlBudget budget)
    {
        var slides = ResolveSlides(archive, budget);
        if (slides.Count == 0) throw new InvalidDataException("The presentation has no slides.");
        for (var i = 0; i < slides.Count && !output.Full; i++)
        {
            if (Load(slides[i], budget)?.Root is not { } slide) continue;
            var tree = slide.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
            var title = tree is null ? string.Empty : string.Join(" ", tree.Descendants()
                .Where(e => e.Name.LocalName == "sp" && PlaceholderType(e) is "title" or "ctrTitle")
                .Select(ShapeText).Where(t => t.Length > 0));
            output.Blank();
            output.Line($"## Slide {i + 1}" + (Attr(slide, "show") == "0" ? " (hidden)" : string.Empty)
                + (title.Length > 0 ? ": " + title : string.Empty));
            if (tree is not null) WriteShapes(tree, output);
            if (NotesFor(archive, slides[i], budget) is { Length: > 0 } notes) output.Line("Notes: " + notes);
        }
    }

    private static void WriteShapes(XElement tree, Output output)
    {
        foreach (var shape in tree.Elements())
        {
            if (output.Full) return;
            switch (shape.Name.LocalName)
            {
                case "sp" when PlaceholderType(shape) is not ("title" or "ctrTitle" or "sldNum" or "dt" or "ftr"):
                    WriteTextBody(shape, output);
                    break;
                case "grpSp" or "AlternateContent" or "Choice":
                    WriteShapes(shape, output);
                    break;
                case "graphicFrame":
                    foreach (var table in shape.Descendants().Where(e => e.Name.LocalName == "tbl"))
                        foreach (var row in table.Elements().Where(e => e.Name.LocalName == "tr"))
                        {
                            var cells = row.Elements().Where(e => e.Name.LocalName == "tc").Select(c => ParagraphsText(c).Replace("|", "/")).ToList();
                            if (cells.Any(c => c.Length > 0)) output.Line("| " + string.Join(" | ", cells) + " |");
                        }
                    break;
            }
        }
    }

    private static void WriteTextBody(XElement shape, Output output)
    {
        if (shape.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody") is not { } body) return;
        foreach (var paragraph in body.Elements().Where(e => e.Name.LocalName == "p"))
        {
            var line = RunText(paragraph);
            if (line.Length == 0) continue;
            var level = int.TryParse(Attr(paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr"), "lvl"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl) ? Math.Clamp(lvl, 0, 8) : 0;
            output.Line(new string(' ', level * 2) + line);
        }
    }

    private static string ShapeText(XElement shape) => ParagraphsText(shape);

    private static string ParagraphsText(XElement container) => string.Join(" ",
        container.Descendants().Where(e => e.Name.LocalName == "p" && !UnderFallback(e)).Select(RunText).Where(t => t.Length > 0));

    /// <summary>Texto de un párrafo de DrawingML: sus ejecuciones, campos y saltos de línea.</summary>
    private static string RunText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var child in paragraph.Elements())
            switch (child.Name.LocalName)
            {
                case "r" or "fld":
                    text.Append(string.Concat(child.Elements().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
                    break;
                case "br":
                    text.Append(' ');
                    break;
            }
        return Clean(text.ToString());
    }

    private static string? PlaceholderType(XElement shape) =>
        shape.Elements().FirstOrDefault(e => e.Name.LocalName == "nvSpPr")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph") is { } placeholder
            ? Attr(placeholder, "type") ?? "body"
            : null;

    /// <summary>Diapositivas en el orden de <c>presentation.xml</c>; si falta, las que haya por número.</summary>
    private static List<ZipArchiveEntry> ResolveSlides(ZipArchive archive, XmlBudget budget)
    {
        var targets = ReadRelationships(archive, "ppt/presentation.xml", budget);
        var ordered = new List<ZipArchiveEntry>();
        if (Load(archive, "ppt/presentation.xml", budget)?.Root is { } presentation)
            foreach (var id in presentation.Descendants().Where(e => e.Name.LocalName == "sldId"))
                if (id.Attribute(Relationships + "id")?.Value is { } relation && targets.TryGetValue(relation, out var target)
                    && archive.GetEntry(target) is { } entry)
                    ordered.Add(entry);
        if (ordered.Count > 0) return ordered.Take(MaxParts).ToList();
        return archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
            .Take(MaxParts)
            .ToList();
    }

    private static string? NotesFor(ZipArchive archive, ZipArchiveEntry slide, XmlBudget budget)
    {
        var notes = ReadRelationships(archive, slide.FullName, budget, typeSuffix: "/notesSlide").Values.FirstOrDefault();
        if (notes is null || Load(archive, notes, budget)?.Root is not { } root) return null;
        return string.Join(" ", root.Descendants().Where(e => e.Name.LocalName == "sp" && PlaceholderType(e) == "body")
            .Select(ParagraphsText).Where(t => t.Length > 0));
    }

    // ===================== Comunes =====================

    /// <summary>Destinos de las relaciones de una parte, como rutas dentro del paquete.</summary>
    private static Dictionary<string, string> ReadRelationships(ZipArchive archive, string part, XmlBudget budget, string? typeSuffix = null)
    {
        var folder = part.Contains('/') ? part[..part.LastIndexOf('/')] : string.Empty;
        var rels = $"{folder}/_rels/{part[(part.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Load(archive, rels, budget)?.Root is not { } root) return result;
        foreach (var relation in root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            if (Attr(relation, "TargetMode") == "External") continue;
            if (typeSuffix is not null && Attr(relation, "Type")?.EndsWith(typeSuffix, StringComparison.Ordinal) != true) continue;
            if (Attr(relation, "Id") is { } id && Attr(relation, "Target") is { } target) result[id] = ResolveTarget(folder, target);
        }
        return result;
    }

    internal static string ResolveTarget(string folder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (segment != ".") parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    private static string? Attr(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = builder.Length > 0; continue; }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    private sealed class XmlBudget
    {
        public long Used;
    }

    private static XDocument? Load(ZipArchive archive, string part, XmlBudget budget) =>
        archive.GetEntry(part) is { } entry ? Load(entry, budget) : null;

    private static XDocument? Load(ZipArchiveEntry entry, XmlBudget budget)
    {
        budget.Used += entry.Length;
        if (budget.Used > MaxXmlBytes) throw new InvalidDataException("The document exceeds the XML read limit.");
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes,
            IgnoreComments = true, IgnoreProcessingInstructions = true
        });
        return XDocument.Load(reader);
    }

    /// <summary>Salida con límite de caracteres: una línea por bloque y como mucho una línea en blanco seguida.</summary>
    private sealed class Output(int maxChars)
    {
        private readonly StringBuilder _text = new();
        private bool _blank = true;

        public bool Full { get; private set; }

        public void Line(string line)
        {
            if (Full) return;
            if (_text.Length + line.Length + 1 > maxChars)
            {
                var room = Math.Max(0, maxChars - _text.Length);
                if (room > 0 && char.IsHighSurrogate(line[room - 1])) room--;
                _text.Append(line.AsSpan(0, room));
                Full = true;
                return;
            }
            _text.Append(line).Append('\n');
            _blank = false;
        }

        public void Blank()
        {
            if (Full || _blank) return;
            _text.Append('\n');
            _blank = true;
        }

        public override string ToString() => _text.ToString().TrimEnd();
    }
}
