using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using enki.Services;
using SkiaSharp;

namespace enki.Views.Editor.Markdown;

internal enum MarkdownRenderState { Loading, Ready, Failed }

internal sealed class MarkdownFormulaEntry
{
    public MarkdownRenderState State { get; set; }
    public Bitmap? Bitmap { get; set; }
}

/// <summary>Caché acotada y asíncrona de fórmulas. El parser y la composición CSharpMath se ejecutan fuera
/// del hilo de interfaz; a los dos segundos se marca fallo y el editor recupera el LaTeX crudo.</summary>
internal sealed class MarkdownFormulaCache : IDisposable
{
    private const int Capacity = 96;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, MarkdownFormulaEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private bool _disposed;

    public event Action? Changed;

    public MarkdownFormulaEntry GetOrRequest(string latex, bool display, MarkdownStyle style)
    {
        var color = style.TextPrimary is ISolidColorBrush solid ? solid.Color : Colors.White;
        var key = $"{(display ? 'D' : 'I')}|{style.BaseFontSize:0.###}|{color}|{latex}";
        if (_entries.TryGetValue(key, out var existing)) return existing;

        var entry = new MarkdownFormulaEntry { State = MarkdownRenderState.Loading };
        _entries[key] = entry;
        _order.Enqueue(key);
        Trim();
        _ = RenderAsync(key, entry, latex, display, style.BaseFontSize,
            new SKColor(color.R, color.G, color.B, color.A));
        return entry;
    }

    private async Task RenderAsync(string key, MarkdownFormulaEntry entry, string latex, bool display,
        double baseFontSize, SKColor color)
    {
        var render = Task.Run(() => LatexRenderService.RenderPng(
            latex, display, (float)(baseFontSize * (display ? 1.45 : 1.12)), color));
        var completed = await Task.WhenAny(render, Task.Delay(RenderTimeout)).ConfigureAwait(false);
        var bytes = completed == render ? await render.ConfigureAwait(false) : null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            if (bytes is null)
            {
                entry.State = MarkdownRenderState.Failed;
            }
            else
            {
                try
                {
                    entry.Bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
                    entry.State = MarkdownRenderState.Ready;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    entry.State = MarkdownRenderState.Failed;
                    LogService.Warning("No se pudo decodificar la fórmula renderizada", ex);
                }
            }
            Changed?.Invoke();
        });
    }

    private void Trim()
    {
        while (_entries.Count > Capacity && _order.Count > 0)
        {
            var key = _order.Dequeue();
            if (!_entries.Remove(key, out var old)) continue;
            old.Bitmap?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) entry.Bitmap?.Dispose();
        _entries.Clear();
        _order.Clear();
    }
}
