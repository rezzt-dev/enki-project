using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace enki.Services;

/// <summary>Lo que se consigue sacar de una página web: sus metadatos y su contenido principal ya
/// convertido a Markdown. <see cref="Markdown"/> vacío significa "la página se descargó pero no había
/// texto que extraer" (una página que se pinta entera con JavaScript, un muro de pago), que es un caso
/// previsto y no un error: el clipper crea la nota igual, solo con título y enlace.</summary>
public sealed record HtmlArticle(string? Title, string? Description, string? Author, string? Published, string Markdown);

/// <summary>
/// Extractor de "lo que de verdad se lee" de un documento HTML, y su conversión a Markdown (T3, web
/// clipper).
///
/// <para><b>Por qué está escrito a mano.</b> La alternativa era añadir un parser de HTML y un conversor a
/// Markdown como dependencias (la duda §9.4 del análisis de funcionalidades pendientes). No se ha hecho:
/// esto no necesita un parser conforme a la especificación, necesita recorrer un árbol de etiquetas y
/// puntuar bloques de texto, y a cambio de ~400 líneas propias la app no gana dos paquetes que auditar,
/// actualizar y empaquetar por una funcionalidad opcional. El mismo criterio con el que el proyecto ya
/// tiene su propio parser de Markdown, de tablas y de frontmatter.</para>
///
/// <para><b>Qué no hace.</b> No ejecuta nada. <c>script</c>, <c>style</c>, <c>noscript</c>, <c>iframe</c>,
/// <c>svg</c> y <c>template</c> se descartan enteros —contenido incluido— antes de mirar nada más, y de
/// las imágenes solo se conserva la URL como Markdown: no se descarga ningún recurso remoto, ni aquí ni
/// al pintar la nota resultante (coherente con <see cref="RemoteImagePolicy"/> y
/// <c>EditorPreferences.RemoteImages</c>).</para>
///
/// <para><b>Cómo elige el contenido.</b> Heurística de legibilidad clásica: se puntúa cada elemento
/// contenedor por cuánto texto de párrafo cuelga de él, con bonificación para los contenedores que el
/// propio documento declara como contenido (<c>article</c>, <c>main</c>, <c>role="main"</c>) y penalización
/// para los que se declaran como cualquier otra cosa por su clase o su id (<c>nav</c>, <c>sidebar</c>,
/// <c>comment</c>, <c>footer</c>, <c>share</c>, <c>promo</c>…). Gana el de más puntos; si ninguno llega al
/// mínimo, no se devuelve cuerpo y el clipper aplica su alternativa.</para>
///
/// <para>Es una función pura de una cadena a un <see cref="HtmlArticle"/>: sin red, sin disco y sin
/// estado, así que se prueba con una tabla de documentos — ver <c>HtmlReadabilityTests</c>.</para>
/// </summary>
public static class HtmlReadability
{
    /// <summary>Por debajo de esto, lo que se ha encontrado es un pie de página o un aviso de cookies, no
    /// un artículo. Es el umbral que decide entre "nota con texto" y "nota solo con título y enlace".</summary>
    private const int MinimumContentLength = 200;

    /// <summary>Tope de nodos del árbol. Un HTML de varios megas con anidamiento patológico no debe poder
    /// tener a la interfaz esperando: pasado el tope se deja de construir árbol y se extrae con lo que
    /// haya, que para una página normal no se alcanza ni de lejos.</summary>
    private const int MaxNodes = 200_000;

    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source",
        "track", "wbr"
    };

    /// <summary>Elementos cuyo contenido no es HTML sino texto crudo hasta su etiqueta de cierre: hay que
    /// saltárselos como un bloque, porque un <c>&lt;</c> dentro de un script no abre ninguna etiqueta.</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title"
    };

    /// <summary>Se tiran enteros, con su contenido. <c>title</c> no está aquí porque su texto es el título
    /// de la página: se lee antes de descartar nada.</summary>
    private static readonly HashSet<string> DroppedElements = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "iframe", "svg", "canvas", "template", "form", "button", "select",
        "object", "embed", "map", "audio", "video", "dialog"
    };

    /// <summary>Bloques que casi nunca son el artículo. No se borran del documento —a veces un sitio mete
    /// el cuerpo dentro de un <c>header</c>— sino que restan puntos a quien cuelgue de ellos.</summary>
    private static readonly HashSet<string> ChromeElements = new(StringComparer.Ordinal)
    {
        "nav", "footer", "aside", "header"
    };

    private static readonly string[] NegativeHints =
    [
        "nav", "menu", "sidebar", "side-bar", "footer", "header", "comment", "share", "social", "promo",
        "advert", "banner", "cookie", "consent", "newsletter", "subscribe", "related", "recommend",
        "breadcrumb", "pagination", "popup", "modal", "widget", "masthead", "skip-link"
    ];

    private static readonly string[] PositiveHints =
    [
        "article", "content", "post", "entry", "story", "markdown", "prose", "body-text", "main"
    ];

    public static HtmlArticle Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new HtmlArticle(null, null, null, null, string.Empty);

        var root = Parse(html);
        var meta = ReadMetadata(root);
        Drop(root);

        var candidate = SelectContentRoot(root);
        var markdown = candidate is null ? string.Empty : MarkdownRenderer.Render(candidate);
        if (markdown.Length < MinimumContentLength) markdown = string.Empty;

        return meta with { Markdown = markdown };
    }

    // ===================== Árbol =====================

    private sealed class Node
    {
        /// <summary>Nombre en minúsculas del elemento, o cadena vacía para un nodo de texto.</summary>
        public string Name { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public Dictionary<string, string>? Attributes { get; set; }
        public List<Node> Children { get; } = [];
        public Node? Parent { get; set; }

        public bool IsText => Name.Length == 0;

        public string Attribute(string name) =>
            Attributes is not null && Attributes.TryGetValue(name, out var value) ? value : string.Empty;
    }

    /// <summary>Construye el árbol con un solo recorrido del documento. No es un parser conforme (no
    /// reconstruye tablas mal cerradas ni aplica el algoritmo de <em>foster parenting</em> del estándar);
    /// sí cubre lo que rompe un recorrido ingenuo: comentarios, doctype, elementos vacíos, elementos de
    /// texto crudo, atributos sin comillas y etiquetas de cierre que no casan con nada abierto.</summary>
    private static Node Parse(string html)
    {
        var root = new Node { Name = "#document" };
        var stack = new List<Node> { root };
        var nodes = 0;
        var i = 0;

        void Append(Node node)
        {
            var parent = stack[^1];
            node.Parent = parent;
            parent.Children.Add(node);
        }

        while (i < html.Length && nodes < MaxNodes)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                Append(new Node { Text = html[i..] });
                break;
            }
            if (lt > i)
            {
                Append(new Node { Text = html[i..lt] });
                nodes++;
            }

            if (Starts(html, lt, "<!--"))
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            if (lt + 1 < html.Length && (html[lt + 1] == '!' || html[lt + 1] == '?'))
            {
                var end = html.IndexOf('>', lt);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            var isClosing = lt + 1 < html.Length && html[lt + 1] == '/';
            var nameStart = lt + (isClosing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < html.Length && (char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':'))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                // Un '<' que no abre ninguna etiqueta ("a < b"): es texto.
                Append(new Node { Text = "<" });
                i = lt + 1;
                continue;
            }

            var name = html[nameStart..nameEnd].ToLowerInvariant();
            var tagEnd = FindTagEnd(html, nameEnd);
            var inside = html[nameEnd..Math.Min(tagEnd, html.Length)];
            i = tagEnd + 1;

            if (isClosing)
            {
                var index = stack.FindLastIndex(n => string.Equals(n.Name, name, StringComparison.Ordinal));
                if (index > 0) stack.RemoveRange(index, stack.Count - index);
                continue;
            }

            var element = new Node { Name = name, Attributes = ParseAttributes(inside) };
            nodes++;
            CloseImplicitly(stack, name);
            Append(element);

            if (VoidElements.Contains(name) || inside.TrimEnd().EndsWith('/')) continue;

            if (RawTextElements.Contains(name))
            {
                var close = IndexOfClosingTag(html, name, i);
                var text = close < 0 ? html[i..] : html[i..close];
                element.Children.Add(new Node { Text = text, Parent = element });
                i = close < 0 ? html.Length : IndexAfterTag(html, close);
                continue;
            }

            stack.Add(element);
        }

        return root;
    }

    /// <summary>Etiquetas que el HTML permite dejar sin cerrar y que, si no se cierran a mano al abrir la
    /// siguiente, anidan una dentro de otra hasta convertir una lista de diez párrafos en diez niveles de
    /// profundidad — con lo que la puntuación por bloques deja de significar nada.</summary>
    private static void CloseImplicitly(List<Node> stack, string opening)
    {
        var closes = opening switch
        {
            "p" => new[] { "p" },
            "li" => ["li"],
            "dt" or "dd" => ["dt", "dd"],
            "tr" => ["tr", "td", "th"],
            "td" or "th" => ["td", "th"],
            "option" => ["option"],
            _ => []
        };
        if (closes.Length == 0) return;

        var index = stack.FindLastIndex(n => closes.Contains(n.Name, StringComparer.Ordinal));
        if (index > 0) stack.RemoveRange(index, stack.Count - index);
    }

    private static bool Starts(string text, int at, string value) =>
        at + value.Length <= text.Length && string.CompareOrdinal(text, at, value, 0, value.Length) == 0;

    /// <summary>Posición del <c>&gt;</c> que cierra la etiqueta, saltándose los que caigan dentro de un
    /// valor entrecomillado (<c>&lt;a title="1 &gt; 2"&gt;</c>).</summary>
    private static int FindTagEnd(string html, int from)
    {
        var quote = '\0';
        for (var i = from; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return html.Length;
    }

    private static int IndexOfClosingTag(string html, string name, int from)
    {
        for (var i = from; i < html.Length; i++)
        {
            i = html.IndexOf("</", i, StringComparison.Ordinal);
            if (i < 0) return -1;
            var after = i + 2;
            if (after + name.Length <= html.Length &&
                string.Compare(html, after, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return i;
        }
        return -1;
    }

    private static int IndexAfterTag(string html, int tagStart)
    {
        var end = html.IndexOf('>', tagStart);
        return end < 0 ? html.Length : end + 1;
    }

    private static Dictionary<string, string>? ParseAttributes(string inside)
    {
        Dictionary<string, string>? attributes = null;
        var i = 0;
        while (i < inside.Length)
        {
            while (i < inside.Length && (char.IsWhiteSpace(inside[i]) || inside[i] == '/')) i++;
            var start = i;
            while (i < inside.Length && !char.IsWhiteSpace(inside[i]) && inside[i] is not ('=' or '/' or '>')) i++;
            if (i == start) break;

            var name = inside[start..i].ToLowerInvariant();
            while (i < inside.Length && char.IsWhiteSpace(inside[i])) i++;

            var value = string.Empty;
            if (i < inside.Length && inside[i] == '=')
            {
                i++;
                while (i < inside.Length && char.IsWhiteSpace(inside[i])) i++;
                if (i < inside.Length && inside[i] is '"' or '\'')
                {
                    var quote = inside[i++];
                    var valueStart = i;
                    while (i < inside.Length && inside[i] != quote) i++;
                    value = inside[valueStart..Math.Min(i, inside.Length)];
                    if (i < inside.Length) i++;
                }
                else
                {
                    var valueStart = i;
                    while (i < inside.Length && !char.IsWhiteSpace(inside[i]) && inside[i] != '>') i++;
                    value = inside[valueStart..i];
                }
            }

            attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
            attributes[name] = WebUtility.HtmlDecode(value);
        }
        return attributes;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    // ===================== Metadatos =====================

    private static HtmlArticle ReadMetadata(Node root)
    {
        string? title = null, description = null, author = null, published = null;

        foreach (var node in Descendants(root))
        {
            if (node.Name == "title" && title is null)
            {
                var text = WebUtility.HtmlDecode(string.Concat(node.Children.Where(c => c.IsText).Select(c => c.Text))).Trim();
                if (text.Length > 0) title = Collapse(text);
                continue;
            }
            if (node.Name != "meta") continue;

            // og:/twitter: van en "property", los meta clásicos en "name"; unos cuantos sitios los cruzan.
            var key = (node.Attribute("property").Length > 0 ? node.Attribute("property") : node.Attribute("name"))
                .Trim().ToLowerInvariant();
            var content = Collapse(node.Attribute("content").Trim());
            if (key.Length == 0 || content.Length == 0) continue;

            switch (key)
            {
                // og:title manda sobre <title>, que suele venir con el nombre del sitio pegado detrás.
                case "og:title" or "twitter:title":
                    title = content;
                    break;
                case "og:description" or "twitter:description" or "description" when description is null:
                    description = content;
                    break;
                case "author" or "article:author" or "twitter:creator" when author is null:
                    author = content;
                    break;
                case "article:published_time" or "date" or "dc.date" or "citation_publication_date" when published is null:
                    published = NormalizeDate(content);
                    break;
            }
        }

        // og:title se lee antes que <title> en el recorrido si va antes en el documento, pero no al revés:
        // esta segunda pasada es la que garantiza que gane og:title esté donde esté.
        foreach (var node in Descendants(root).Where(n => n.Name == "meta"))
        {
            var key = (node.Attribute("property").Length > 0 ? node.Attribute("property") : node.Attribute("name"))
                .Trim().ToLowerInvariant();
            if (key is "og:title" or "twitter:title" && node.Attribute("content").Trim().Length > 0)
            {
                title = Collapse(node.Attribute("content").Trim());
                break;
            }
        }

        // Sin ningún meta ni <title>: el primer <h1> con texto es mejor que nada.
        title ??= Descendants(root).Where(n => n.Name == "h1")
            .Select(n => Collapse(TextOf(n))).FirstOrDefault(t => t.Length > 0);

        return new HtmlArticle(title, description, author, published, string.Empty);
    }

    /// <summary>Deja la fecha en ISO-8601 corto cuando se reconoce, y tal cual cuando no: el frontmatter
    /// de la nota prefiere una fecha legible a una cadena exacta pero indescifrable, y prefiere lo que
    /// venía a inventarse nada.</summary>
    private static string NormalizeDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;

    // ===================== Selección del contenido =====================

    private static void Drop(Node node)
    {
        node.Children.RemoveAll(child => DroppedElements.Contains(child.Name));
        foreach (var child in node.Children) Drop(child);
    }

    private static Node? SelectContentRoot(Node root)
    {
        Node? best = null;
        var bestScore = 0.0;

        foreach (var node in Descendants(root))
        {
            if (node.IsText || node.Name is "html" or "head") continue;

            var score = Score(node);
            if (score <= bestScore) continue;
            bestScore = score;
            best = node;
        }

        if (best is not null) return best;
        return Descendants(root).FirstOrDefault(n => n.Name == "body") ?? root;
    }

    /// <summary>Puntuación de un contenedor: cuánto texto de párrafo cuelga de él, con el matiz de que el
    /// texto muy troceado (menús, listas de enlaces) cuenta menos que la prosa. El <c>Math.Sqrt</c> sobre
    /// la profundidad es lo que evita que gane siempre el <c>body</c>: entre dos contenedores con el mismo
    /// texto, se prefiere el más interior, que es el que menos chrome arrastra.</summary>
    private static double Score(Node node)
    {
        if (node.Name is not ("article" or "main" or "div" or "section" or "td" or "body")) return 0;

        var textLength = 0;
        var paragraphs = 0;
        var linkLength = 0;
        foreach (var descendant in Descendants(node))
        {
            if (descendant.IsText) continue;
            if (descendant.Name is "p" or "blockquote" or "pre" or "li")
            {
                var length = TextOf(descendant).Trim().Length;
                if (descendant.Name != "li" && length >= 40) paragraphs++;
                textLength += length;
            }
            if (descendant.Name == "a") linkLength += TextOf(descendant).Trim().Length;
        }

        if (textLength == 0) return 0;

        var score = textLength + paragraphs * 25.0;
        // Una "densidad de enlaces" alta es la firma de un menú o de un bloque de "artículos
        // relacionados": mismo volumen de texto, ninguna prosa.
        var linkDensity = Math.Min(1.0, linkLength / (double)textLength);
        score *= 1.0 - (linkDensity * 0.9);

        if (node.Name is "article" or "main" || node.Attribute("role") == "main") score *= 3.0;
        if (HasHint(node, PositiveHints)) score *= 1.5;
        if (HasHint(node, NegativeHints)) score *= 0.2;
        if (Ancestors(node).Any(a => ChromeElements.Contains(a.Name))) score *= 0.3;

        return score / Math.Sqrt(Depth(node) + 1);
    }

    private static bool HasHint(Node node, string[] hints)
    {
        var haystack = (node.Attribute("class") + " " + node.Attribute("id")).ToLowerInvariant();
        return haystack.Trim().Length > 0 && hints.Any(hint => haystack.Contains(hint, StringComparison.Ordinal));
    }

    private static IEnumerable<Node> Ancestors(Node node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent) yield return current;
    }

    private static int Depth(Node node) => Ancestors(node).Count();

    private static string TextOf(Node node)
    {
        var builder = new StringBuilder();
        Collect(node, builder);
        return WebUtility.HtmlDecode(builder.ToString());

        static void Collect(Node node, StringBuilder into)
        {
            if (node.IsText) { into.Append(node.Text); return; }
            foreach (var child in node.Children) Collect(child, into);
        }
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == ' ') { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ===================== HTML → Markdown =====================

    /// <summary>Recorre el subárbol elegido y lo escribe como Markdown. Solo traduce lo que un documento
    /// de texto sabe representar —encabezados, párrafos, listas, citas, código, enlaces, imágenes, reglas
    /// y tablas—; cualquier otro elemento se atraviesa y aporta su texto, así que un <c>&lt;span&gt;</c> o
    /// un <c>&lt;figure&gt;</c> desconocido no hacen perder contenido.</summary>
    private static class MarkdownRenderer
    {
        public static string Render(Node root)
        {
            var builder = new StringBuilder();
            RenderBlock(root, builder, string.Empty);
            return Tidy(builder.ToString());
        }

        private static void RenderBlock(Node node, StringBuilder output, string indent)
        {
            foreach (var child in node.Children)
            {
                if (child.IsText)
                {
                    var text = Collapse(WebUtility.HtmlDecode(child.Text));
                    if (text.Length > 0) AppendParagraph(output, indent, EscapeMarkdown(text));
                    continue;
                }

                switch (child.Name)
                {
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                        var level = child.Name[1] - '0';
                        var heading = Inline(child).Trim();
                        if (heading.Length > 0) AppendParagraph(output, indent, new string('#', level) + " " + heading);
                        break;

                    case "p":
                        var paragraph = Inline(child).Trim();
                        if (paragraph.Length > 0) AppendParagraph(output, indent, paragraph);
                        break;

                    case "br":
                        break;

                    case "hr":
                        AppendParagraph(output, indent, "---");
                        break;

                    case "ul" or "ol":
                        RenderList(child, output, indent);
                        break;

                    case "blockquote":
                        var quoted = new StringBuilder();
                        RenderBlock(child, quoted, string.Empty);
                        var body = Tidy(quoted.ToString());
                        if (body.Length > 0)
                            AppendParagraph(output, indent,
                                string.Join('\n', body.Split('\n').Select(line => line.Length == 0 ? ">" : "> " + line)));
                        break;

                    case "pre":
                        var code = TextOf(child).Trim('\n');
                        if (code.Trim().Length > 0)
                            AppendParagraph(output, indent, "```\n" + code + "\n```");
                        break;

                    case "table":
                        RenderTable(child, output, indent);
                        break;

                    case "img":
                        var image = RenderImage(child);
                        if (image.Length > 0) AppendParagraph(output, indent, image);
                        break;

                    case "figcaption":
                        var caption = Inline(child).Trim();
                        if (caption.Length > 0) AppendParagraph(output, indent, "*" + caption + "*");
                        break;

                    default:
                        if (IsInline(child.Name))
                        {
                            var inline = Inline(child).Trim();
                            if (inline.Length > 0) AppendParagraph(output, indent, inline);
                        }
                        else RenderBlock(child, output, indent);
                        break;
                }
            }
        }

        private static void RenderList(Node list, StringBuilder output, string indent)
        {
            var ordered = list.Name == "ol";
            var number = 1;
            foreach (var item in list.Children.Where(c => c.Name == "li"))
            {
                var marker = ordered
                    ? number++.ToString(CultureInfo.InvariantCulture) + ". "
                    : "- ";
                var itemBuilder = new StringBuilder();
                RenderBlock(item, itemBuilder, string.Empty);
                var body = Tidy(itemBuilder.ToString());
                if (body.Length == 0) continue;

                var lines = body.Split('\n');
                // La continuación de un punto de lista se sangra al ancho de su marca, que es lo que la
                // mantiene dentro del punto y no como un párrafo suelto detrás de la lista.
                var continuation = indent + new string(' ', marker.Length);
                AppendParagraph(output, string.Empty, indent + marker + lines[0] +
                    (lines.Length > 1
                        ? "\n" + string.Join('\n', lines.Skip(1).Select(l => l.Length == 0 ? string.Empty : continuation + l))
                        : string.Empty));
            }
        }

        private static void RenderTable(Node table, StringBuilder output, string indent)
        {
            var rows = Descendants(table).Where(n => n.Name == "tr").ToList();
            if (rows.Count == 0) return;

            var cells = rows
                .Select(row => row.Children.Where(c => c.Name is "td" or "th")
                    .Select(c => Inline(c).Trim().Replace("|", "\\|", StringComparison.Ordinal))
                    .ToList())
                .Where(row => row.Count > 0)
                .ToList();
            if (cells.Count == 0) return;

            var columns = cells.Max(row => row.Count);
            var builder = new StringBuilder();
            for (var r = 0; r < cells.Count; r++)
            {
                var row = cells[r];
                builder.Append("| ");
                for (var c = 0; c < columns; c++)
                {
                    builder.Append(c < row.Count ? row[c] : string.Empty);
                    builder.Append(" | ");
                }
                builder.Length -= 1;
                builder.Append('\n');
                // Markdown exige la fila separadora justo después de la cabecera; una tabla sin <th> usa
                // igualmente su primera fila como cabecera, que es como la pintan casi todos los sitios.
                if (r == 0) builder.Append("| ").AppendJoin(" | ", Enumerable.Repeat("---", columns)).Append(" |\n");
            }

            AppendParagraph(output, indent, builder.ToString().TrimEnd('\n'));
        }

        private static string RenderImage(Node image)
        {
            var source = image.Attribute("src");
            if (source.Length == 0) source = image.Attribute("data-src");
            if (source.Length == 0) return string.Empty;

            var alt = Collapse(image.Attribute("alt"));
            return $"![{alt}]({source})";
        }

        private static bool IsInline(string name) => name is "a" or "b" or "strong" or "i" or "em" or "u"
            or "code" or "span" or "small" or "mark" or "del" or "s" or "sub" or "sup" or "abbr" or "cite"
            or "q" or "time" or "label";

        /// <summary>Contenido en línea de un elemento, con los marcadores de Markdown puestos. Un
        /// <c>&lt;br&gt;</c> se traduce al salto de línea forzado de Markdown (dos espacios) para no
        /// fundir versos o direcciones en un solo renglón.</summary>
        private static string Inline(Node node)
        {
            var builder = new StringBuilder();
            foreach (var child in node.Children)
            {
                if (child.IsText)
                {
                    builder.Append(EscapeMarkdown(WebUtility.HtmlDecode(child.Text)));
                    continue;
                }

                switch (child.Name)
                {
                    case "br":
                        builder.Append("  \n");
                        break;
                    case "b" or "strong":
                        Wrap(builder, "**", Inline(child));
                        break;
                    case "i" or "em" or "cite":
                        Wrap(builder, "*", Inline(child));
                        break;
                    case "del" or "s":
                        Wrap(builder, "~~", Inline(child));
                        break;
                    case "mark":
                        Wrap(builder, "==", Inline(child));
                        break;
                    case "code":
                        var code = Collapse(TextOf(child));
                        if (code.Length > 0) builder.Append('`').Append(code).Append('`');
                        break;
                    case "img":
                        builder.Append(RenderImage(child));
                        break;
                    case "a":
                        var label = Inline(child).Trim();
                        var href = child.Attribute("href");
                        // Un enlace sin destino utilizable (un "#" de JavaScript, un javascript:) aporta
                        // su texto y nada más: la nota no debe acabar con destinos que no se pueden abrir.
                        if (label.Length == 0) break;
                        if (href.Length == 0 || href.StartsWith('#') || !ExternalLinkPolicy.IsSafeUrl(href))
                            builder.Append(label);
                        else builder.Append('[').Append(label).Append("](").Append(href).Append(')');
                        break;
                    default:
                        builder.Append(Inline(child));
                        break;
                }
            }
            return Collapse(builder.ToString().Replace("  \n", "\u0001", StringComparison.Ordinal))
                .Replace("\u0001", "  \n", StringComparison.Ordinal);
        }

        private static void Wrap(StringBuilder builder, string marker, string content)
        {
            var trimmed = content.Trim();
            if (trimmed.Length == 0) return;
            // El espacio que se come el Trim se devuelve fuera del marcador: "**negrita** y" y no
            // "**negrita **y", que Markdown ya no reconoce como énfasis.
            if (content.Length > 0 && char.IsWhiteSpace(content[0])) builder.Append(' ');
            builder.Append(marker).Append(trimmed).Append(marker);
            if (content.Length > 1 && char.IsWhiteSpace(content[^1])) builder.Append(' ');
        }

        /// <summary>Protege los caracteres que en Markdown significan algo y en el texto de la página no.
        /// Deliberadamente corto: escapar todo el repertorio llenaría de barras invertidas un texto que se
        /// va a leer y a editar a mano. Solo se escapan los que empiezan una construcción de bloque al
        /// principio de una línea o los que pasan por un enlace/énfasis accidental.</summary>
        private static string EscapeMarkdown(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c is '[' or ']' or '`' or '*' or '_' or '\\') builder.Append('\\');
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static void AppendParagraph(StringBuilder output, string indent, string block)
        {
            if (block.Length == 0) return;
            if (output.Length > 0) output.Append("\n\n");
            output.Append(indent.Length > 0 ? indent + block : block);
        }

        /// <summary>Normaliza el resultado: nunca más de una línea en blanco seguida, sin espacios al
        /// final de línea salvo el salto forzado de Markdown, y sin blancos al principio ni al final.</summary>
        private static string Tidy(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var builder = new StringBuilder();
            var blanks = 0;
            foreach (var raw in lines)
            {
                var line = raw.EndsWith("  ", StringComparison.Ordinal) ? raw.TrimEnd() + "  " : raw.TrimEnd();
                if (line.Trim().Length == 0)
                {
                    blanks++;
                    continue;
                }
                if (builder.Length > 0) builder.Append(blanks > 0 ? "\n\n" : "\n");
                blanks = 0;
                builder.Append(line);
            }
            return builder.ToString().Trim();
        }
    }
}
