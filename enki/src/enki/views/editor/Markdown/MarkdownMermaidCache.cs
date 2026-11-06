using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using enki.Services;
using enki.ViewModels;

namespace enki.Views.Editor.Markdown;

internal sealed class MarkdownMermaidEntry
{
    public MarkdownRenderState State { get; set; }
    public Bitmap? Bitmap { get; set; }
}

/// <summary>Motor único de Mermaid para todo el editor. El WebView solo ejecuta el bundle empaquetado y
/// devuelve SVG; nunca navega fuera, y el SVG se rasteriza con Svg.Skia antes de entrar en el documento.</summary>
internal sealed class MarkdownMermaidCache : IDisposable
{
    private const int Capacity = 48;
    private const int MaxSourceLength = 64 * 1024;
    private const int MaxSvgLength = 2 * 1024 * 1024;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(4);

    /// <summary>El bundle de Mermaid pesa varios megabytes y el documento anfitrión no depende de la nota
    /// ni del tema: se arma una sola vez por proceso, no en cada editor que se monta.</summary>
    private static readonly Lazy<string> HostDocument = new(BuildHostDocument);

    private readonly ContentControl _host;
    private readonly Dictionary<string, MarkdownMermaidEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRender> _pending = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private NativeWebView? _webView;
    private bool _ready;
    private bool _disposed;
    private int _nextId;

    private sealed record PendingRender(string Key, MarkdownMermaidEntry Entry, string Source, DispatcherTimer Timer);

    public MarkdownMermaidCache(ContentControl host) => _host = host;

    public event Action? Changed;

    public void Attach()
    {
        if (_disposed || _webView is not null) return;
        if (!WebViewHost.IsEngineAvailable())
        {
            FailAll();
            return;
        }

        try
        {
            _webView = new NativeWebView();
            _webView.WebMessageReceived += OnWebMessageReceived;
            _host.Content = _webView;
            _webView.NavigateToString(HostDocument.Value, null);
        }
        catch (EntryPointNotFoundException ex)
        {
            // La verificación de WebViewHost.IsEngineAvailable() puede no capturar todos los casos
            // donde la librería nativa falta símbolos críticos. Si el constructor de NativeWebView
            // falla con EntryPointNotFoundException, es que la librería WPE/WebKitGTK no tiene el
            // símbolo webkit_web_view_backend_new requerido. Marcamos el WebView como no disponible
            // para que los tests y la aplicación puedan continuar sin él.
            LogService.Warning("La librería WebView nativa no tiene el símbolo requerido (webkit_web_view_backend_new). " +
                "El renderizado de diagramas Mermaid no estará disponible.", ex);
            DisposeWebView();
            FailAll();
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo iniciar el motor local de Mermaid", ex);
            DisposeWebView();
            FailAll();
        }
    }

    public MarkdownMermaidEntry GetOrRequest(string source, MarkdownStyle style)
    {
        var foreground = CssColor(style.TextPrimary, "#f2f2f3");
        var secondary = CssColor(style.TextSecondary, "#9797a1");
        var background = CssColor(style.CodeBlockBackground, "#141414");
        var border = CssColor(style.CodeBlockBorder, "#2a2a2e");
        var key = $"{foreground}|{secondary}|{background}|{border}|{source}";
        if (_entries.TryGetValue(key, out var existing)) return existing;

        var entry = new MarkdownMermaidEntry
        {
            State = string.IsNullOrWhiteSpace(source) || source.Length > MaxSourceLength
                ? MarkdownRenderState.Failed
                : MarkdownRenderState.Loading
        };
        _entries[key] = entry;
        _order.Enqueue(key);
        Trim();

        if (entry.State == MarkdownRenderState.Loading)
            QueueRender(key, entry, source, foreground, secondary, background, border);
        return entry;
    }

    private void QueueRender(string key, MarkdownMermaidEntry entry, string source,
        string foreground, string secondary, string background, string border)
    {
        var id = (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var timer = new DispatcherTimer { Interval = RenderTimeout };
        timer.Tick += (_, _) => Fail(id);
        _pending[id] = new PendingRender(key, entry, source, timer);
        timer.Start();

        if (_ready) Send(id, source, foreground, secondary, background, border);
    }

    private void Send(string id, string source, string foreground, string secondary, string background, string border)
    {
        if (_webView is null) return;
        var payload = JsonSerializer.Serialize(new { id, source, foreground, secondary, background, border });
        _ = _webView.InvokeScript($"window.enkiRenderMermaid({payload})");
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body)) return;
        Dispatcher.UIThread.Post(() => HandleMessage(e.Body));
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "ready")
            {
                _ready = true;
                foreach (var pair in _pending)
                {
                    // Los colores forman parte de la clave, en el mismo orden y sin '|': recupéralos de
                    // la entrada pendiente no compensa guardar cuatro campos más; el documento acepta el
                    // tema activo por petición y las nuevas solicitudes ya lo incluyen.
                    var parts = pair.Value.Key.Split('|', 5);
                    Send(pair.Key, pair.Value.Source, parts[0], parts[1], parts[2], parts[3]);
                }
                return;
            }
            if (!root.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id) return;
            if (type == "failed")
            {
                Fail(id);
                return;
            }
            if (type != "rendered" || !root.TryGetProperty("svg", out var svgElement)) return;
            Complete(id, svgElement.GetString());
        }
        catch (JsonException ex)
        {
            LogService.Warning("El motor Mermaid devolvió un mensaje no válido", ex);
        }
    }

    private void Complete(string id, string? svg)
    {
        if (!_pending.Remove(id, out var pending)) return;
        pending.Timer.Stop();
        if (string.IsNullOrWhiteSpace(svg) || svg.Length > MaxSvgLength
            || svg.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || svg.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase))
        {
            pending.Entry.State = MarkdownRenderState.Failed;
            Changed?.Invoke();
            return;
        }

        try
        {
            pending.Entry.Bitmap = SvgRenderHelper.RenderSvg(Encoding.UTF8.GetBytes(svg), 1400, allowUpscale: false);
            pending.Entry.State = MarkdownRenderState.Ready;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            pending.Entry.State = MarkdownRenderState.Failed;
            LogService.Warning("No se pudo rasterizar la salida SVG de Mermaid", ex);
        }
        Changed?.Invoke();
    }

    private void Fail(string id)
    {
        if (!_pending.Remove(id, out var pending)) return;
        pending.Timer.Stop();
        pending.Entry.State = MarkdownRenderState.Failed;
        Changed?.Invoke();
    }

    private void FailAll()
    {
        foreach (var pending in _pending.Values)
        {
            pending.Timer.Stop();
            pending.Entry.State = MarkdownRenderState.Failed;
        }
        _pending.Clear();
        Changed?.Invoke();
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

    private static string BuildHostDocument()
    {
        var bundle = LoadAssetText("mermaid.min.js");
        return """
               <!doctype html><html><head><meta charset="utf-8"></head><body>
               <script>
               """ + bundle + """
               </script><script>
               const post = value => {
                 const body = JSON.stringify(value);
                 try { if (window.invokeCSharpAction) { window.invokeCSharpAction(body); return; } } catch (_) {}
                 try { if (window.webkit?.messageHandlers?.postAvWebViewMessage) { window.webkit.messageHandlers.postAvWebViewMessage.postMessage(body); } } catch (_) {}
               };
               window.enkiRenderMermaid = async request => {
                 try {
                   mermaid.initialize({
                     startOnLoad: false, securityLevel: 'strict', theme: 'base', htmlLabels: false,
                     flowchart: { htmlLabels: false, useMaxWidth: true },
                     themeVariables: {
                       primaryColor: request.background, primaryTextColor: request.foreground,
                       primaryBorderColor: request.border, lineColor: request.secondary,
                       secondaryColor: request.background, tertiaryColor: request.background,
                       background: request.background, mainBkg: request.background,
                       textColor: request.foreground, fontFamily: 'sans-serif'
                     }
                   });
                   const result = await mermaid.render('enki_mermaid_' + request.id, request.source);
                   post({ type: 'rendered', id: request.id, svg: result.svg });
                 } catch (error) {
                   post({ type: 'failed', id: request.id, error: String(error) });
                 }
               };
               post({ type: 'ready' });
               </script></body></html>
               """;
    }

    private static string LoadAssetText(string fileName)
    {
        using var source = AssetLoader.Open(new Uri($"avares://enki/assets/js/{fileName}"));
        using var reader = new StreamReader(source);
        return reader.ReadToEnd().Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
    }

    private static string CssColor(IBrush brush, string fallback)
    {
        if (brush is not ISolidColorBrush solid) return fallback;
        var c = solid.Color;
        return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    }

    private void DisposeWebView()
    {
        if (_webView is not { } webView) return;
        webView.WebMessageReceived -= OnWebMessageReceived;
        (webView as IDisposable)?.Dispose();
        _webView = null;
        _host.Content = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FailAll();
        foreach (var entry in _entries.Values) entry.Bitmap?.Dispose();
        _entries.Clear();
        _order.Clear();
        DisposeWebView();
    }
}
