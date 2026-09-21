using System;
using System.Collections.Generic;
using AvaloniaEdit.Rendering;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>Retira del layout las líneas posteriores a la cabecera de un bloque rico. Su texto queda
/// unido a la primera línea y el generador lo consume con un único elemento visual de longitud completa.</summary>
internal sealed class MarkdownSpecialBlockFolding
{
    private readonly TextView _textView;
    private readonly LivePreviewState _state;
    private readonly MarkdownStyle _style;
    private readonly Dictionary<int, CollapsedLineSection> _sections = new();
    private bool _refreshing;

    public MarkdownSpecialBlockFolding(TextView textView, LivePreviewState state, MarkdownStyle style)
    {
        _textView = textView;
        _state = state;
        _style = style;
    }

    public bool IsCollapsed(MarkdownSpecialBlock block) =>
        _sections.TryGetValue(block.StartLine, out var section) && section.IsCollapsed;

    public void Refresh()
    {
        if (_refreshing || _textView.Document is not { } document) return;
        _refreshing = true;
        try
        {
            var wanted = new HashSet<int>();
            foreach (var block in _state.SpecialBlocks)
            {
                if (block.EndLine <= block.StartLine || block.EndLine > document.LineCount) continue;
                if (_state.Intersects(block.StartOffset, block.EndOffset)) continue;
                if (!_state.CanRender(block, _style)) continue;
                wanted.Add(block.StartLine);
            }

            foreach (var pair in new List<KeyValuePair<int, CollapsedLineSection>>(_sections))
            {
                if (pair.Value.IsCollapsed && wanted.Remove(pair.Key)) continue;
                Uncollapse(pair.Value);
                _sections.Remove(pair.Key);
            }

            foreach (var startLine in wanted)
            {
                var block = _state.SpecialBlockStartingAt(startLine);
                if (block is null) continue;
                try
                {
                    var firstHidden = document.GetLineByNumber(startLine + 1);
                    var lastHidden = document.GetLineByNumber(block.EndLine);
                    _sections[startLine] = _textView.CollapseLines(firstHidden, lastHidden);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    LogService.Warning("No se pudo plegar un bloque enriquecido de Markdown", ex);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Reset()
    {
        foreach (var section in _sections.Values) Uncollapse(section);
        _sections.Clear();
    }

    private static void Uncollapse(CollapsedLineSection section)
    {
        try { if (section.IsCollapsed) section.Uncollapse(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            LogService.Warning("No se pudo desplegar un bloque enriquecido de Markdown", ex);
        }
    }
}
