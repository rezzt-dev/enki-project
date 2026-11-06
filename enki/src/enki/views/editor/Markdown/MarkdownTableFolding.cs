using System;
using System.Collections.Generic;
using AvaloniaEdit.Rendering;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Quita de la vista la fila separadora (<c>| --- | --- |</c>) de cada tabla dibujada.
///
/// <para>Hace falta un mecanismo aparte porque una línea del documento no puede medir menos que
/// <c>TextView.DefaultLineHeight</c>: por mucho que se le oculte el texto, deja una banda vacía tan alta
/// como cualquier otra línea, y una tabla con un hueco en blanco entre el encabezado y la primera fila no
/// es una tabla bien dibujada. La única forma de que una línea deje de ocupar alto en AvaloniaEdit es
/// plegarla (<see cref="TextView.CollapseLines"/>), que es lo que este objeto administra — el mismo
/// mecanismo con el que un editor de código pliega una función.</para>
///
/// <para>Plegar tiene dos mitades y las dos son obligatorias: esto se ocupa del alto, y
/// <see cref="MarkdownElementGenerator"/> de ocultar el texto de la línea plegada, que al perder su línea
/// visual propia pasa a formar parte de la del encabezado. Por eso el generador pregunta aquí
/// (<see cref="IsCollapsed"/>) en vez de decidirlo por su cuenta: si las dos mitades no coincidieran, o
/// sobraría un hueco o el <c>| --- |</c> aparecería pegado al final del encabezado.</para>
///
/// <para>Nada queda plegado si el cursor está dentro de la tabla (entonces se edita en crudo, filas
/// separadoras incluidas) ni si la tabla no cabe a lo ancho y se muestra como texto. Y como
/// <see cref="Refresh"/> vuelve a decidirlo entero en cada movimiento del cursor y en cada reescaneo del
/// documento, cualquier pliegue que deje de estar justificado se deshace en el siguiente evento: no hay
/// forma de que una línea se quede escondida.</para>
/// </summary>
internal sealed class MarkdownTableFolding
{
    private readonly TextView _textView;
    private readonly LivePreviewState _state;
    private readonly MarkdownStyle _style;
    private readonly List<CollapsedLineSection> _sections = new();
    private bool _refreshing;

    public MarkdownTableFolding(TextView textView, LivePreviewState state, MarkdownStyle style)
    {
        _textView = textView;
        _state = state;
        _style = style;
    }

    /// <summary>Si la línea <paramref name="lineNumber"/> está plegada ahora mismo. Se contesta desde las
    /// propias secciones (cuyas <c>DocumentLine</c> siguen a las ediciones del documento), no desde una
    /// lista de números de línea que se quedaría obsoleta en cuanto se inserta un salto de línea encima.</summary>
    public bool IsCollapsed(int lineNumber)
    {
        foreach (var section in _sections)
        {
            if (section.IsCollapsed && section.Start?.LineNumber == lineNumber) return true;
        }
        return false;
    }

    /// <summary>Recalcula qué filas separadoras deben estar plegadas y ajusta los pliegues a esa lista.
    /// La llama la vista tras cada cambio de cursor/selección, tras cada reescaneo del documento y tras
    /// cada cambio de tamaño — nunca desde el dibujado, que no puede tocar el árbol de alturas.</summary>
    public void Refresh()
    {
        if (_refreshing) return;
        var document = _textView.Document;
        if (document is null)
        {
            _sections.Clear();
            return;
        }

        if (!_state.Tables.HasTables && _sections.Count == 0) return;

        _refreshing = true;
        try
        {
            var wanted = new HashSet<int>();
            foreach (var table in _state.Tables.Tables)
            {
                if (table.DelimiterLine > document.LineCount) continue;
                if (_state.IsTableActive(table, document)) continue;
                if (_state.Tables.GetGeometry(table, _textView, _style, _state) is null) continue;
                wanted.Add(table.DelimiterLine);
            }

            var changed = false;
            for (var i = _sections.Count - 1; i >= 0; i--)
            {
                var section = _sections[i];
                var lineNumber = section.Start?.LineNumber ?? -1;
                if (section.IsCollapsed && lineNumber > 0 && wanted.Remove(lineNumber)) continue;

                Uncollapse(section);
                _sections.RemoveAt(i);
                changed = true;
            }

            foreach (var lineNumber in wanted)
            {
                var line = document.GetLineByNumber(lineNumber);
                try
                {
                    _sections.Add(_textView.CollapseLines(line, line));
                    changed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    // Plegar es una mejora visual, no una función crítica: si el árbol de alturas no está
                    // en condiciones de aceptarlo (documento a medio cambiar), la tabla se dibuja con su
                    // banda vacía y el siguiente Refresh lo reintenta.
                    LogService.Warning("No se pudo plegar la fila separadora de una tabla", ex);
                }
            }

            if (changed) _textView.Redraw();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Deshace todos los pliegues — la vista la llama antes de cambiar de documento (cada pestaña
    /// tiene el suyo y el árbol de alturas se reconstruye entero con el cambio, así que una sección de
    /// antes ya no significa nada).</summary>
    public void Reset()
    {
        foreach (var section in _sections) Uncollapse(section);
        _sections.Clear();
    }

    private static void Uncollapse(CollapsedLineSection section)
    {
        try
        {
            if (section.IsCollapsed) section.Uncollapse();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // La sección puede haber quedado invalidada por una edición que borró su línea; el pliegue se
            // ha ido con ella y no hay nada que deshacer.
            LogService.Warning("No se pudo desplegar la fila separadora de una tabla", ex);
        }
    }
}
