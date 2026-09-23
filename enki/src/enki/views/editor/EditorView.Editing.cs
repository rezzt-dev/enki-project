using System;
using Avalonia.Input;
using enki.Services;
using enki.Views.Editor.Markdown;

namespace enki.Views;

/// <summary>
/// La parte de <see cref="EditorView"/> que conecta el editor con
/// <see cref="MarkdownEditing"/>: atajos de formato, pares automáticos, sangría de listas y operaciones
/// de línea. Está aquí y no en el archivo principal porque es un bloque coherente y cerrado —todo lo que
/// pasa al pulsar una tecla que no es "escribir una letra"—, mientras que <c>EditorView.axaml.cs</c>
/// lleva el ciclo de vida de la vista, los popups y el panel de buscar/reemplazar.
///
/// Toda la lógica de texto vive en <see cref="MarkdownEditing"/>, que trabaja sobre un documento pelado y
/// se prueba sin abrir una ventana; aquí solo se traduce entre eso y el <c>TextArea</c>.
/// </summary>
public partial class EditorView
{
    /// <summary>Vuelca en el editor la selección que devuelve una operación y le devuelve el foco. Se
    /// asigna la longitud antes que el inicio para no pasar por un estado intermedio donde la selección
    /// se saldría del documento recién acortado.</summary>
    private void Apply(MdSelection selection)
    {
        var length = Math.Clamp(selection.Length, 0, Editor.Document.TextLength);
        var start = Math.Clamp(selection.Start, 0, Editor.Document.TextLength - length);
        Editor.SelectionLength = 0;
        Editor.CaretOffset = start;
        Editor.SelectionStart = start;
        Editor.SelectionLength = length;
        Editor.Focus();
    }

    private MdSelection CurrentSelection() => Editor.SelectionLength > 0
        ? new MdSelection(Editor.SelectionStart, Editor.SelectionLength)
        : MdSelection.Caret(Editor.CaretOffset);

    private void ToggleInline(string marker) =>
        Apply(MarkdownEditing.ToggleInline(Editor.Document, CurrentSelection(), marker));

    private void ToggleHeading(int level) =>
        Apply(MarkdownEditing.ToggleHeading(Editor.Document, CurrentSelection(), level));

    private void ToggleList(MdBlockKind kind) =>
        Apply(MarkdownEditing.ToggleList(Editor.Document, CurrentSelection(), kind));

    private void ToggleQuote() =>
        Apply(MarkdownEditing.ToggleQuote(Editor.Document, CurrentSelection()));

    private void ToggleTask() =>
        Apply(MarkdownEditing.ToggleTask(Editor.Document, CurrentSelection()));

    /// <summary>Los atajos de formato y de línea, todos remapeables desde Ajustes › Atajos. Devuelve si
    /// alguno se ha hecho cargo de la pulsación.</summary>
    private bool HandleFormattingShortcut(KeyEventArgs e)
    {
        if (ShortcutRegistry.Matches(ShortcutIds.EditorBold, e)) { ToggleInline("**"); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorItalic, e)) { ToggleInline("_"); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorStrikethrough, e)) { ToggleInline("~~"); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorInlineCode, e)) { ToggleInline("`"); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorHighlight, e)) { ToggleInline("=="); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorToggleQuote, e)) { ToggleQuote(); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorToggleList, e)) { ToggleList(MdBlockKind.BulletList); return true; }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorToggleTask, e)) { ToggleTask(); return true; }

        if (ShortcutRegistry.Matches(ShortcutIds.EditorMoveLineUp, e))
        {
            Apply(MarkdownEditing.MoveLines(Editor.Document, CurrentSelection(), up: true));
            return true;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorMoveLineDown, e))
        {
            Apply(MarkdownEditing.MoveLines(Editor.Document, CurrentSelection(), up: false));
            return true;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorDuplicateLines, e))
        {
            Apply(MarkdownEditing.DuplicateLines(Editor.Document, CurrentSelection()));
            return true;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorDeleteLines, e))
        {
            Apply(MarkdownEditing.DeleteLines(Editor.Document, CurrentSelection()));
            return true;
        }
        if (ShortcutRegistry.Matches(ShortcutIds.EditorSelectLine, e))
        {
            Apply(MarkdownEditing.ExpandToLines(Editor.Document, CurrentSelection()));
            return true;
        }
        return false;
    }

    /// <summary>Teclas de navegación y borrado con un matiz Markdown: Inicio lleva al contenido antes que
    /// al margen, y Retroceso pegado al contenido se lleva el marcador entero en vez de descomponerlo en
    /// <c>-&#32;[&#32;</c>.</summary>
    private bool HandleCaretKey(KeyEventArgs e)
    {
        if (e.Key == Key.Home && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var target = MarkdownEditing.SmartHome(Editor.Document, Editor.CaretOffset);
            if (target == Editor.CaretOffset) return false;
            if (e.KeyModifiers == KeyModifiers.Shift)
            {
                var anchor = Editor.SelectionLength > 0 && Editor.CaretOffset == Editor.SelectionStart
                    ? Editor.SelectionStart + Editor.SelectionLength
                    : Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
                Editor.CaretOffset = target;
                Editor.SelectionStart = Math.Min(anchor, target);
                Editor.SelectionLength = Math.Abs(anchor - target);
            }
            else
            {
                Editor.SelectionLength = 0;
                Editor.CaretOffset = target;
            }
            return true;
        }

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && Editor.SelectionLength == 0 &&
            MarkdownEditing.BackspaceOverMarker(Editor.Document, Editor.CaretOffset) is { } marker)
        {
            Editor.Document.Remove(marker.Start, marker.Length);
            Editor.CaretOffset = marker.Start;
            return true;
        }
        return false;
    }

    /// <summary>Tabulador dentro de una lista: anida un nivel y renumera; con Mayús, lo saca. Fuera de una
    /// lista devuelve false y el tabulador sigue indentando como siempre.</summary>
    private bool HandleListIndent(bool outdent)
    {
        var result = MarkdownEditing.IndentList(Editor.Document, CurrentSelection(),
            EditorPreferences.TabSize, outdent);
        if (result is not { } selection) return false;
        Apply(selection);
        return true;
    }

    private void OnEditorTextInput(object? sender, TextInputEventArgs e)
    {
        // Con un popup abierto (comandos "/", selector de notas) la tecla es para él: filtrar lo que se
        // escribe, no envolver nada.
        if (_boundVm?.SlashCommands.IsOpen == true || _boundVm?.NoteLinkPicker.IsOpen == true) return;
        if (!EditorPreferences.AutoPairs) return;
        if (HandleAutoPair(e)) e.Handled = true;
    }

    /// <summary>Escribir con texto seleccionado: un carácter que abre par lo envuelve en vez de
    /// sustituirlo —seleccionar una palabra y pulsar <c>(</c> la deja entre paréntesis, y pulsar <c>*</c>
    /// la deja en cursiva—, que es lo que hace cualquier editor moderno y lo que evita el clásico "me he
    /// cargado la selección sin querer".
    ///
    /// Sin selección solo se autocierran paréntesis, corchetes, llaves y comillas
    /// (<see cref="MarkdownEditing.ClosingForAutoPair"/>), y escribir el cierre que el editor ya había
    /// puesto simplemente lo salta.</summary>
    private bool HandleAutoPair(TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 } text) return false;
        var typed = text[0];

        if (Editor.SelectionLength > 0)
        {
            if (MarkdownEditing.ClosingForWrap(typed) is not { } closing) return false;
            Apply(MarkdownEditing.Wrap(Editor.Document, CurrentSelection(), text, closing));
            return true;
        }

        if (MarkdownEditing.ShouldStepOver(Editor.Document, Editor.CaretOffset, typed))
        {
            Editor.CaretOffset++;
            return true;
        }

        if (MarkdownEditing.ClosingForAutoPair(typed) is not { } pair) return false;
        // Solo delante de un final de línea o de un separador: escribir "(" pegado a una palabra casi
        // nunca quiere decir "envuélvela", y cerrar ahí dejaría el paréntesis en mitad del texto.
        var caret = Editor.CaretOffset;
        if (caret < Editor.Document.TextLength)
        {
            var next = Editor.Document.GetCharAt(caret);
            if (!char.IsWhiteSpace(next) && !char.IsPunctuation(next)) return false;
        }
        Editor.Document.Insert(caret, text + pair);
        Editor.CaretOffset = caret + 1;
        return true;
    }

    /// <summary>Pegar una URL con texto seleccionado lo convierte en un enlace Markdown, como en Obsidian.
    /// Devuelve si se ha hecho cargo del pegado.</summary>
    private bool TryPasteAsLink(string pasted)
    {
        if (Editor.SelectionLength == 0) return false;
        var selected = Editor.Document.GetText(Editor.SelectionStart, Editor.SelectionLength);
        if (MarkdownEditing.LinkFromPaste(selected, pasted) is not { } link) return false;

        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, link);
        // El cursor aterriza tras el enlace entero, listo para seguir escribiendo la frase.
        Editor.SelectionLength = 0;
        Editor.CaretOffset = start + link.Length;
        return true;
    }

}
