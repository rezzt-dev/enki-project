using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AvaloniaEdit.Document;

namespace enki.Views.Editor.Markdown;

/// <summary>Un cursor o una selección, en offsets del documento. Es lo que estas operaciones reciben y
/// devuelven: quien las llama solo tiene que volcar el resultado en el editor.</summary>
public readonly record struct MdSelection(int Start, int Length)
{
    public int End => Start + Length;
    public static MdSelection Caret(int offset) => new(offset, 0);
}

/// <summary>
/// Las operaciones de edición Markdown del editor, separadas de la vista para poder probarlas sobre un
/// <see cref="TextDocument"/> pelado: son puro texto, sin nada de Avalonia visual.
///
/// El criterio de todas ellas es el de un editor Markdown profesional (Obsidian, Typora, VS Code): un
/// atajo de formato <b>alterna</b> en vez de acumular —pulsar negrita sobre texto ya en negrita lo
/// quita—, un marcador de bloque <b>sustituye</b> al que hubiera —«H2» sobre un H1 deja un H2, no
/// <c>#&#32;##</c>—, y cualquier cambio que toque varias líneas entra en el historial como una sola
/// acción deshacible.
/// </summary>
public static class MarkdownEditing
{
    /// <summary>Marcadores en línea que se alternan, de más largo a más corto: el orden importa al
    /// detectar cuál envuelve al cursor, porque <c>**</c> contiene a <c>*</c>.</summary>
    private static readonly (string Marker, MdSpanKind Kind)[] InlineMarkers =
    [
        ("***", MdSpanKind.BoldItalic),
        ("**", MdSpanKind.Bold),
        ("~~", MdSpanKind.Strikethrough),
        ("==", MdSpanKind.Highlight),
        ("`", MdSpanKind.InlineCode),
        ("_", MdSpanKind.Italic),
        ("*", MdSpanKind.Italic)
    ];

    // ===================== Formato en línea =====================

    /// <summary>Pone o quita <paramref name="marker"/> (<c>**</c>, <c>_</c>, <c>~~</c>, <c>==</c>,
    /// <c>`</c>) sobre la selección. Sin selección actúa sobre la palabra donde esté el cursor —que es lo
    /// que se quiere decir el 90 % de las veces— y, si tampoco hay palabra, deja el par abierto con el
    /// cursor dentro, listo para escribir.</summary>
    public static MdSelection ToggleInline(TextDocument document, MdSelection selection, string marker)
    {
        using var _ = document.RunUpdate();

        if (FindEnclosing(document, selection, marker) is { } enclosing) return Unwrap(document, enclosing, marker);

        var target = selection.Length > 0 ? selection : WordAt(document, selection.Start);
        document.Insert(target.End, marker);
        document.Insert(target.Start, marker);
        // La selección se queda sobre el contenido, no sobre los marcadores: volver a pulsar el atajo lo
        // desenvuelve, y seguir escribiendo sustituye el texto y no el formato.
        return new MdSelection(target.Start + marker.Length, target.Length);
    }

    /// <summary>El rango <c>marcador+contenido+marcador</c> que ya envuelve a <paramref name="selection"/>,
    /// o null si no hay ninguno. Cubre las tres formas de "esto ya está en negrita": la selección incluye
    /// los marcadores, los marcadores están justo fuera de ella, o el cursor está suelto dentro del
    /// span.</summary>
    private static MdSelection? FindEnclosing(TextDocument document, MdSelection selection, string marker)
    {
        var length = marker.Length;

        if (selection.Length >= length * 2 &&
            document.GetText(selection.Start, length) == marker &&
            document.GetText(selection.End - length, length) == marker)
            return selection;

        if (selection.Start >= length && selection.End + length <= document.TextLength &&
            document.GetText(selection.Start - length, length) == marker &&
            document.GetText(selection.End, length) == marker)
            return new MdSelection(selection.Start - length, selection.Length + length * 2);

        if (selection.Length > 0) return null;

        // Cursor suelto: se pregunta al analizador, que ya sabe qué es un marcador de verdad y qué es un
        // guion bajo en mitad de un identificador.
        var kind = KindOf(marker);
        var line = document.GetLineByOffset(selection.Start);
        var model = MarkdownLineParser.Parse(document.GetText(line), line.Offset, default);
        foreach (var span in model.Spans)
        {
            if (span.Kind != kind || selection.Start < span.Start || selection.Start > span.End) continue;
            if (span.ContentStart - span.Start != length) continue;
            return new MdSelection(span.Start, span.End - span.Start);
        }
        return null;
    }

    private static MdSpanKind KindOf(string marker)
    {
        foreach (var (candidate, kind) in InlineMarkers)
        {
            if (candidate == marker) return kind;
        }
        return MdSpanKind.Bold;
    }

    /// <summary>Quita los marcadores de <paramref name="span"/>, de fuera hacia dentro para que el primer
    /// borrado no desplace al segundo.</summary>
    private static MdSelection Unwrap(TextDocument document, MdSelection span, string marker)
    {
        var length = marker.Length;
        document.Remove(span.End - length, length);
        document.Remove(span.Start, length);
        return new MdSelection(span.Start, span.Length - length * 2);
    }

    /// <summary>La palabra sobre la que está el cursor, o un rango vacío si está en un espacio. Se cuenta
    /// como palabra cualquier cadena de letras, dígitos, guiones y guiones bajos: "no-break" y
    /// "snake_case" se ponen en negrita enteros y no a trozos.</summary>
    private static MdSelection WordAt(TextDocument document, int offset)
    {
        var line = document.GetLineByOffset(offset);
        var start = offset;
        var end = offset;
        while (start > line.Offset && IsWordChar(document.GetCharAt(start - 1))) start--;
        while (end < line.EndOffset && IsWordChar(document.GetCharAt(end))) end++;
        return new MdSelection(start, end - start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    // ===================== Pares automáticos =====================

    /// <summary>El cierre que corresponde a <paramref name="opening"/> al escribirlo con texto
    /// seleccionado: el texto queda envuelto en vez de sustituido. Incluye los marcadores Markdown, así
    /// que seleccionar y pulsar <c>*</c> pone la palabra en cursiva.</summary>
    public static string? ClosingForWrap(char opening) => opening switch
    {
        '(' => ")",
        '[' => "]",
        '{' => "}",
        '<' => ">",
        '"' => "\"",
        '\'' => "'",
        '`' => "`",
        '*' => "*",
        '_' => "_",
        '~' => "~",
        '=' => "=",
        '«' => "»",
        '¿' => "?",
        '¡' => "!",
        _ => null
    };

    /// <summary>El cierre que se inserta solo al escribir <paramref name="opening"/> sin selección.
    /// Deliberadamente más corto que <see cref="ClosingForWrap"/>: cerrar paréntesis y comillas por el
    /// usuario ayuda, pero hacerlo con <c>*</c> o <c>_</c> estorbaría cada vez que se escribe una
    /// multiplicación o un nombre_con_guiones.</summary>
    public static string? ClosingForAutoPair(char opening) => opening switch
    {
        '(' => ")",
        '[' => "]",
        '{' => "}",
        '"' => "\"",
        '«' => "»",
        _ => null
    };

    /// <summary>Envuelve la selección y la deja seleccionada, para poder encadenar (envolver en comillas y
    /// luego en paréntesis sin volver a seleccionar).</summary>
    public static MdSelection Wrap(TextDocument document, MdSelection selection, string opening, string closing)
    {
        using var _ = document.RunUpdate();
        document.Insert(selection.End, closing);
        document.Insert(selection.Start, opening);
        return new MdSelection(selection.Start + opening.Length, selection.Length);
    }

    /// <summary>Si en <paramref name="offset"/> ya está el carácter que se acaba de escribir, de forma que
    /// escribirlo deba limitarse a saltarlo — lo que evita el <c>())</c> de teclear el cierre de un par que
    /// el editor ya había puesto.</summary>
    public static bool ShouldStepOver(TextDocument document, int offset, char typed) =>
        typed is ')' or ']' or '}' or '"' or '`' or '»' &&
        offset < document.TextLength && document.GetCharAt(offset) == typed;

    /// <summary>Inserta un bloque en el lugar de la selección, conservando el texto a ambos lados.
    /// La cerca supera cualquier secuencia de acentos del contenido para no cerrarse antes de tiempo.</summary>
    public static MdSelection InsertCodeBlock(TextDocument document, MdSelection selection)
    {
        using var update = document.RunUpdate();
        var line = document.GetLineByOffset(selection.Start);
        var delimiter = LineDelimiter(document, line);
        var body = document.GetText(selection.Start, selection.Length);
        var longest = 0;
        var run = 0;
        foreach (var character in body)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        var prefix = selection.Start == line.Offset ? string.Empty : delimiter;
        var suffix = selection.End < document.TextLength && document.GetCharAt(selection.End) is not ('\r' or '\n')
            ? delimiter : string.Empty;
        document.Replace(selection.Start, selection.Length,
            prefix + fence + delimiter + body + delimiter + fence + suffix);
        return MdSelection.Caret(selection.Start + prefix.Length + fence.Length);
    }

    // ===================== Bloques =====================

    /// <summary>Pone, cambia o quita el nivel de encabezado de las líneas tocadas. Sustituye el marcador
    /// que hubiera en vez de anteponer otro, y repetir el mismo nivel lo quita: el mismo atajo sirve para
    /// poner y para deshacer.</summary>
    public static MdSelection ToggleHeading(TextDocument document, MdSelection selection, int level)
    {
        using var _ = document.RunUpdate();
        return EditLines(document, selection, (doc, line) =>
        {
            var text = doc.GetText(line);
            var model = MarkdownLineParser.Parse(text, line.Offset, default);
            var indent = LeadingWhitespace(text);
            var replaced = model.Kind == MdBlockKind.Heading ? model.ContentStart - line.Offset : indent;
            var marker = model.Kind == MdBlockKind.Heading && model.HeadingLevel == level
                ? string.Empty
                : new string('#', level) + " ";
            doc.Replace(line.Offset + indent, replaced - indent, marker);
        });
    }

    /// <summary>Convierte las líneas tocadas al tipo de lista <paramref name="kind"/>, o se lo quita si ya
    /// lo tienen. Una lista se convierte en otra directamente (una viñeta pasa a tarea sin dejar por medio
    /// un <c>-&#32;-&#32;[&#32;]</c>), y al numerar se continúa la numeración de la línea anterior si la
    /// hay.</summary>
    public static MdSelection ToggleList(TextDocument document, MdSelection selection, MdBlockKind kind)
    {
        using var _ = document.RunUpdate();
        var remove = AllLines(document, selection, (doc, line) =>
            MarkdownLineParser.Parse(doc.GetText(line), line.Offset, default).Kind == kind);
        return EditLines(document, selection, (doc, line) =>
        {
            var text = doc.GetText(line);
            var model = MarkdownLineParser.Parse(text, line.Offset, default);
            var indent = LeadingWhitespace(text);
            var replaced = IsListKind(model.Kind) ? model.ContentStart - line.Offset : indent;
            var marker = remove ? string.Empty : MarkerFor(doc, line, kind);
            doc.Replace(line.Offset + indent, replaced - indent, marker);
        }, () => RenumberOrderedLists(document, selection.Start));
    }

    private static bool IsListKind(MdBlockKind kind) =>
        kind is MdBlockKind.BulletList or MdBlockKind.OrderedList or MdBlockKind.TaskList;

    private static string MarkerFor(TextDocument document, DocumentLine line, MdBlockKind kind) => kind switch
    {
        MdBlockKind.BulletList => "- ",
        MdBlockKind.TaskList => "- [ ] ",
        MdBlockKind.OrderedList => PreviousNumber(document, line) + ". ",
        _ => string.Empty
    };

    /// <summary>El número que le toca a la línea si arriba ya hay una lista numerada; 1 si empieza una.</summary>
    private static int PreviousNumber(TextDocument document, DocumentLine line)
    {
        if (line.PreviousLine is not { } previous) return 1;
        var model = MarkdownLineParser.Parse(document.GetText(previous), previous.Offset, default);
        if (model.Kind != MdBlockKind.OrderedList) return 1;
        var text = document.GetText(previous.Offset, model.MarkerEnd - previous.Offset).TrimStart();
        var digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits])) digits++;
        return int.TryParse(text[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number + 1
            : 1;
    }

    /// <summary>Añade o quita un nivel de cita a las líneas tocadas.</summary>
    public static MdSelection ToggleQuote(TextDocument document, MdSelection selection)
    {
        using var _ = document.RunUpdate();
        // Si toda la selección ya está citada, el gesto quita un nivel; si hay alguna línea sin citar, los
        // iguala citándolas todas. Es lo que hace cualquier editor con las listas anidadas y evita el
        // resultado desconcertante de "unas suben y otras bajan".
        var quoted = AllLines(document, selection, (doc, line) =>
            MarkdownLineParser.Parse(doc.GetText(line), line.Offset, default).Kind == MdBlockKind.Blockquote);

        return EditLines(document, selection, (doc, line) =>
        {
            var text = doc.GetText(line);
            var indent = LeadingWhitespace(text);
            if (!quoted)
            {
                doc.Insert(line.Offset + indent, "> ");
                return;
            }
            var model = MarkdownLineParser.Parse(text, line.Offset, default);
            if (model.Kind != MdBlockKind.Blockquote) return;
            // Un solo nivel: "> > texto" pasa a "> texto".
            var marker = indent + 1;
            if (marker < text.Length && text[marker] == ' ') marker++;
            doc.Remove(line.Offset + indent, marker - indent);
        });
    }

    // ===================== Listas: sangría, numeración y tareas =====================

    /// <summary>Tabulador dentro de una lista: anida un nivel (o lo saca, con Mayús). Devuelve null si
    /// ninguna línea tocada es un elemento de lista, para que el tabulador siga indentando como siempre
    /// fuera de ellas.</summary>
    public static MdSelection? IndentList(TextDocument document, MdSelection selection, int tabSize, bool outdent)
    {
        var fences = MarkdownLineParser.ComputeFences(document.Text);
        if (!AnyLine(document, selection, (doc, line) =>
                !fences.GetValueOrDefault(line.LineNumber).IsCode &&
                IsListKind(MarkdownLineParser.Parse(doc.GetText(line), line.Offset, default).Kind)))
            return null;

        using var _ = document.RunUpdate();
        return EditLines(document, selection, (doc, line) =>
        {
            var text = doc.GetText(line);
            if (fences.GetValueOrDefault(line.LineNumber).IsCode ||
                !IsListKind(MarkdownLineParser.Parse(text, line.Offset, default).Kind)) return;

            if (!outdent)
            {
                doc.Insert(line.Offset, new string(' ', tabSize));
                return;
            }
            var remove = 0;
            while (remove < tabSize && remove < text.Length && text[remove] == ' ') remove++;
            if (remove == 0 && text.Length > 0 && text[0] == '\t') remove = 1;
            if (remove > 0) doc.Remove(line.Offset, remove);
        }, () => RenumberOrderedLists(document, selection.Start));
    }

    /// <summary>Renumera de corrido las listas numeradas del bloque que contiene
    /// <paramref name="offset"/>, nivel a nivel: insertar un elemento en medio no deja detrás una lista
    /// «1. 2. 2. 3.», y sacar uno de nivel no deja huecos en la numeración del nivel al que vuelve.
    ///
    /// El bloque se delimita por líneas en blanco, que es donde acaba una lista en Markdown. Cada nivel de
    /// sangría lleva su propio contador y hereda el primer número que el autor escribiera (una lista que
    /// empieza en 3 sigue en 4), y al cerrarse un nivel su contador se olvida, para que la siguiente
    /// sublista vuelva a empezar por su cuenta.</summary>
    public static void RenumberOrderedLists(TextDocument document, int offset)
    {
        if (document.TextLength == 0) return;
        var line = document.GetLineByOffset(Math.Clamp(offset, 0, document.TextLength));

        var fences = MarkdownLineParser.ComputeFences(document.Text);
        if (fences.GetValueOrDefault(line.LineNumber).IsCode) return;
        var first = line.LineNumber;
        while (first > 1 && !fences.GetValueOrDefault(first - 1).IsCode && IsListBlockLine(document, first - 1)) first--;
        var last = line.LineNumber;
        while (last < document.LineCount && !fences.GetValueOrDefault(last + 1).IsCode && IsListBlockLine(document, last + 1)) last++;

        using var _ = document.RunUpdate();
        var counters = new List<(int Indent, int Number)>();
        for (var number = first; number <= last; number++)
        {
            var current = document.GetLineByNumber(number);
            var text = document.GetText(current);
            var model = MarkdownLineParser.Parse(text, current.Offset, default);
            var indent = LeadingWhitespace(text);

            if (!IsListKind(model.Kind))
            {
                // Una línea suelta de texto (la continuación de un elemento) no corta la lista, pero
                // tampoco cuenta: se salta sin tocar los contadores.
                continue;
            }

            while (counters.Count > 0 && counters[^1].Indent > indent) counters.RemoveAt(counters.Count - 1);

            if (model.Kind != MdBlockKind.OrderedList)
            {
                // Una viñeta al mismo nivel rompe la serie numerada: la siguiente numerada de ese nivel
                // vuelve a arrancar donde su autor diga.
                if (counters.Count > 0 && counters[^1].Indent == indent) counters.RemoveAt(counters.Count - 1);
                continue;
            }

            var marker = text[indent..(model.MarkerEnd - current.Offset)];
            var digits = 0;
            while (digits < marker.Length && char.IsDigit(marker[digits])) digits++;
            if (digits == 0) continue;

            int expected;
            if (counters.Count > 0 && counters[^1].Indent == indent)
            {
                expected = counters[^1].Number + 1;
                counters[^1] = (indent, expected);
            }
            else
            {
                expected = int.TryParse(marker[..digits], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var written) ? written : 1;
                counters.Add((indent, expected));
            }

            var replacement = expected.ToString(CultureInfo.InvariantCulture);
            if (marker[..digits] == replacement) continue;
            document.Replace(current.Offset + indent, digits, replacement);
        }
    }

    /// <summary>Si la línea sigue perteneciendo al mismo bloque de lista: cualquier cosa menos una línea en
    /// blanco, que es lo que cierra una lista en Markdown.</summary>
    private static bool IsListBlockLine(TextDocument document, int lineNumber) =>
        document.GetText(document.GetLineByNumber(lineNumber)).Trim().Length > 0;

    /// <summary>Marca o desmarca las tareas de las líneas tocadas. Una viñeta o una línea normal se
    /// convierten primero en tarea, así que el mismo gesto sirve para "esto es una tarea" y para "esta
    /// tarea ya está".</summary>
    public static MdSelection ToggleTask(TextDocument document, MdSelection selection)
    {
        using var _ = document.RunUpdate();
        // Se decide una sola vez para todas: con varias líneas seleccionadas, el gesto las marca todas o
        // las desmarca todas, en vez de invertir cada una por su lado.
        var allChecked = AllLines(document, selection, (doc, line) =>
            MarkdownLineParser.Parse(doc.GetText(line), line.Offset, default) is
                { Kind: MdBlockKind.TaskList, TaskChecked: true });

        return EditLines(document, selection, (doc, line) =>
        {
            var text = doc.GetText(line);
            var model = MarkdownLineParser.Parse(text, line.Offset, default);
            if (model.Kind == MdBlockKind.TaskList && model.CheckboxCharOffset >= 0)
            {
                doc.Replace(model.CheckboxCharOffset, 1, allChecked ? " " : "x");
                return;
            }
            var indent = LeadingWhitespace(text);
            var replaced = IsListKind(model.Kind) ? model.ContentStart - line.Offset : indent;
            doc.Replace(line.Offset + indent, replaced - indent, "- [ ] ");
        });
    }

    // ===================== Operaciones de línea =====================

    /// <summary>Sube o baja las líneas tocadas, arrastrando la selección con ellas.</summary>
    public static MdSelection MoveLines(TextDocument document, MdSelection selection, bool up)
    {
        var (first, last) = LineRange(document, selection);
        if (up && first.LineNumber == 1) return selection;
        if (!up && last.LineNumber == document.LineCount) return selection;

        using var _ = document.RunUpdate();
        var block = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var neighbour = up ? first.PreviousLine! : last.NextLine!;
        var neighbourText = document.GetText(neighbour);

        var separatorLine = up ? neighbour : last;
        var separator = document.GetText(separatorLine.EndOffset, separatorLine.DelimiterLength);
        var start = Math.Min(first.Offset, neighbour.Offset);
        var end = Math.Max(last.EndOffset, neighbour.EndOffset);
        document.Replace(start, end - start, up
            ? block + separator + neighbourText
            : neighbourText + separator + block);

        var delta = (neighbourText.Length + separator.Length) * (up ? -1 : 1);
        return new MdSelection(selection.Start + delta, selection.Length);
    }

    /// <summary>Duplica las líneas tocadas justo debajo y deja la selección sobre la copia — que es la que
    /// se va a editar.</summary>
    public static MdSelection DuplicateLines(TextDocument document, MdSelection selection)
    {
        using var _ = document.RunUpdate();
        var (first, last) = LineRange(document, selection);
        var block = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var separator = LineDelimiter(document, last);
        document.Insert(last.EndOffset, separator + block);
        return new MdSelection(selection.Start + block.Length + separator.Length, selection.Length);
    }

    /// <summary>Borra las líneas tocadas enteras, salto incluido, y deja el cursor al principio de la que
    /// ocupa su lugar.</summary>
    public static MdSelection DeleteLines(TextDocument document, MdSelection selection)
    {
        using var _ = document.RunUpdate();
        var (first, last) = LineRange(document, selection);
        // El salto que se llevan es el de después; en la última línea del documento, el de antes — si no,
        // borrar la última dejaría una línea vacía donde estaba.
        var start = first.Offset;
        var end = last.NextLine is not null ? last.EndOffset + last.DelimiterLength : last.EndOffset;
        if (last.NextLine is null && first.PreviousLine is { } previous) start = previous.EndOffset;
        document.Remove(start, end - start);
        return MdSelection.Caret(Math.Min(start, document.TextLength));
    }

    /// <summary>Selecciona la línea entera; repetido, va sumando la siguiente.</summary>
    public static MdSelection ExpandToLines(TextDocument document, MdSelection selection)
    {
        var (first, last) = LineRange(document, selection);
        var whole = new MdSelection(first.Offset, last.EndOffset - first.Offset);
        if (selection != whole || last.NextLine is not { } next) return whole;
        return new MdSelection(first.Offset, next.EndOffset - first.Offset);
    }

    /// <summary>Inicio "inteligente": primero al principio del contenido —después de la sangría y del
    /// marcador de lista o cita—, y solo al pulsarlo de nuevo, al principio de la línea. Es lo que hace
    /// que Inicio lleve al texto y no al guion de la viñeta.</summary>
    public static int SmartHome(TextDocument document, int caret)
    {
        var line = document.GetLineByOffset(caret);
        var text = document.GetText(line);
        var fences = MarkdownLineParser.ComputeFences(document.Text);
        var model = MarkdownLineParser.Parse(text, line.Offset, fences.GetValueOrDefault(line.LineNumber));
        var content = model.Kind is MdBlockKind.Paragraph or MdBlockKind.CodeFence
            ? line.Offset + LeadingWhitespace(text)
            : model.ContentStart;
        if (content > line.EndOffset) content = line.EndOffset;
        return caret == content ? line.Offset : content;
    }

    /// <summary>El rango que borra un Retroceso pegado al principio del contenido de una lista o una
    /// cita: el marcador entero, no su último carácter. Null en cualquier otro sitio, donde el Retroceso
    /// hace lo de siempre.</summary>
    public static MdSelection? BackspaceOverMarker(TextDocument document, int caret)
    {
        var line = document.GetLineByOffset(caret);
        if (caret <= line.Offset) return null;
        var fences = MarkdownLineParser.ComputeFences(document.Text);
        if (fences.GetValueOrDefault(line.LineNumber).IsCode) return null;
        var model = MarkdownLineParser.Parse(document.GetText(line), line.Offset, default);
        if (model.Kind is MdBlockKind.Paragraph or MdBlockKind.CodeFence or MdBlockKind.HorizontalRule) return null;
        return caret == model.ContentStart && model.ContentStart > model.MarkerStart
            ? new MdSelection(model.MarkerStart, model.ContentStart - model.MarkerStart)
            : null;
    }

    // ===================== Utilidades =====================

    /// <summary>Conserva el salto de la línea o el de su vecina al insertar texto nuevo.</summary>
    public static string LineDelimiter(TextDocument document, DocumentLine line)
    {
        var source = line.DelimiterLength > 0 ? line : line.PreviousLine;
        return source is { DelimiterLength: > 0 }
            ? document.GetText(source.EndOffset, source.DelimiterLength)
            : "\n";
    }

    private static int LeadingWhitespace(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }

    private static (DocumentLine First, DocumentLine Last) LineRange(TextDocument document, MdSelection selection)
    {
        var first = document.GetLineByOffset(Math.Clamp(selection.Start, 0, document.TextLength));
        var lastOffset = Math.Clamp(selection.End, 0, document.TextLength);
        var last = document.GetLineByOffset(lastOffset);
        // Una selección que termina justo en el salto de línea no incluye la línea siguiente: seleccionar
        // "de aquí a final de línea" y pulsar borrar-línea no puede llevarse también la de abajo.
        if (last.LineNumber > first.LineNumber && lastOffset == last.Offset) last = last.PreviousLine!;
        return (first, last);
    }

    /// <summary>Aplica <paramref name="edit"/> a cada línea tocada, de abajo arriba para que ninguna
    /// edición desplace las líneas que aún quedan por tratar, y devuelve la selección reajustada al texto
    /// que ahora ocupa el mismo sitio.</summary>
    private static MdSelection EditLines(TextDocument document, MdSelection selection,
        Action<TextDocument, DocumentLine> edit, Action? afterEdit = null)
    {
        var (first, last) = LineRange(document, selection);
        var firstNumber = first.LineNumber;
        var lastNumber = last.LineNumber;

        // Anclas en vez de offsets: cada edición mueve el texto que va detrás, y el documento ya sabe
        // arrastrarlas consigo.
        var startAnchor = document.CreateAnchor(Math.Clamp(selection.Start, 0, document.TextLength));
        startAnchor.MovementType = AnchorMovementType.AfterInsertion;
        startAnchor.SurviveDeletion = true;
        var endAnchor = document.CreateAnchor(Math.Clamp(selection.End, 0, document.TextLength));
        endAnchor.MovementType = AnchorMovementType.AfterInsertion;
        endAnchor.SurviveDeletion = true;

        for (var number = lastNumber; number >= firstNumber; number--)
            edit(document, document.GetLineByNumber(number));

        afterEdit?.Invoke();
        var start = startAnchor.Offset;
        return new MdSelection(start, Math.Max(0, endAnchor.Offset - start));
    }

    private static bool AllLines(TextDocument document, MdSelection selection,
        Func<TextDocument, DocumentLine, bool> predicate)
    {
        var (first, last) = LineRange(document, selection);
        for (var number = first.LineNumber; number <= last.LineNumber; number++)
        {
            if (!predicate(document, document.GetLineByNumber(number))) return false;
        }
        return true;
    }

    private static bool AnyLine(TextDocument document, MdSelection selection,
        Func<TextDocument, DocumentLine, bool> predicate)
    {
        var (first, last) = LineRange(document, selection);
        for (var number = first.LineNumber; number <= last.LineNumber; number++)
        {
            if (predicate(document, document.GetLineByNumber(number))) return true;
        }
        return false;
    }

    /// <summary>Convierte un pegado en un enlace cuando tiene sentido: una URL pegada sobre texto
    /// seleccionado lo deja como <c>[texto](url)</c>, igual que en Obsidian. Null = pégalo tal cual.</summary>
    public static string? LinkFromPaste(string selectedText, string pasted)
    {
        if (selectedText.Length == 0 || pasted.Length == 0) return null;
        if (selectedText.Contains('\n') || pasted.Contains('\n') || pasted.Contains(' ')) return null;
        if (!Uri.TryCreate(pasted.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "mailto" or "ftp")) return null;
        return $"[{selectedText}]({pasted.Trim()})";
    }

    /// <summary>La plantilla de tabla con <paramref name="columns"/> columnas y una fila vacía.</summary>
    public static string EmptyTableRow(int columns)
    {
        var builder = new StringBuilder("|");
        for (var i = 0; i < columns; i++) builder.Append("  |");
        return builder.ToString();
    }
}
