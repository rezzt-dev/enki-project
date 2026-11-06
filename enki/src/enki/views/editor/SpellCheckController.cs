using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using enki.Localization;
using enki.Services;

namespace enki.Views;

/// <summary>Subraya en el editor las palabras que <see cref="SpellCheckService"/> no reconoce (MED-05).
///
/// <para><b>Sin frenar la escritura.</b> La revisión no corre en el hilo de interfaz: tras cada cambio se
/// espera un instante (<see cref="Debounce"/>), se copia el texto y se revisa en un hilo de fondo; al
/// volver, si el documento no cambió entretanto, se sustituyen los tramos marcados. Mientras dura la
/// revisión los tramos anteriores se desplazan solos con las ediciones
/// (<see cref="TextSegmentCollection{T}"/>), así que escribir en medio de una nota no hace saltar los
/// subrayados. El repintado solo recorre los tramos de las líneas visibles.</para>
///
/// <para>Se dibuja como capa de fondo con una línea ondulada: no toca los atributos del texto, de modo que
/// no choca con el resaltado de Markdown ni con el subrayado de los enlaces.</para></summary>
public sealed class SpellCheckController : IBackgroundRenderer, IDisposable
{
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);

    private readonly TextView _textView;
    private readonly SpellCheckService _service;
    private readonly DispatcherTimer _timer;
    private TextDocument? _document;
    private TextSegmentCollection<SpellSegment>? _segments;
    private CancellationTokenSource? _scan;
    private int _revision;
    private string _cultureName;
    private readonly bool _followAppLanguage;
    private bool _disposed;

    /// <param name="followAppLanguage">Elige el diccionario del idioma de la aplicación y lo cambia con él.
    /// Con <see langword="false"/> se usa el que ya tenga cargado el servicio (pruebas).</param>
    public SpellCheckController(TextView textView, SpellCheckService service, bool followAppLanguage = true)
    {
        _textView = textView;
        _service = service;
        _cultureName = LocalizationService.Instance.CurrentCulture.Name;
        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += (_, _) => { _timer.Stop(); Scan(); };

        _service.DictionaryChanged += OnDictionaryChanged;
        EditorPreferences.Changed += OnPreferencesChanged;
        _followAppLanguage = followAppLanguage;
        if (!_followAppLanguage) return;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        _ = SelectLanguageAsync();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Empieza a vigilar <paramref name="document"/> (la nota que muestra el editor) y suelta la
    /// anterior. Programa una revisión inmediata.</summary>
    public void Attach(TextDocument document)
    {
        if (ReferenceEquals(_document, document)) return;
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _document = document;
        _document.Changed += OnDocumentChanged;
        Unmark();
        _revision++;
        Schedule(TimeSpan.FromMilliseconds(120));
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _revision++;
        Schedule(Debounce);
    }

    private void OnDictionaryChanged() => Dispatcher.UIThread.Post(() => { _revision++; Schedule(TimeSpan.FromMilliseconds(120)); });

    private void OnPreferencesChanged() => Dispatcher.UIThread.Post(() => { _revision++; Schedule(TimeSpan.FromMilliseconds(50)); });

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Cambiar de idioma cambia de diccionario; el resto de avisos del servicio no importa.
        if (LocalizationService.Instance.CurrentCulture.Name == _cultureName) return;
        _cultureName = LocalizationService.Instance.CurrentCulture.Name;
        _ = SelectLanguageAsync();
    }

    private Task<bool> SelectLanguageAsync()
    {
        var culture = LocalizationService.Instance.CurrentCulture;
        // Cargar el diccionario lee y analiza archivos de varios megas: nunca en el hilo de interfaz.
        return Task.Run(() => _service.TrySelectLanguage(culture));
    }

    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void Scan()
    {
        if (_disposed || _document is not { } document) return;
        if (!EditorPreferences.SpellCheckEnabled || !_service.IsReady)
        {
            Unmark();
            return;
        }

        var revision = _revision;
        var text = document.Text;
        _scan?.Cancel();
        _scan?.Dispose();
        var scan = _scan = new CancellationTokenSource();

        // Sin depender del contexto de sincronización de quien llama: el resultado vuelve al hilo de
        // interfaz con Post, que es lo único que toca el documento y el repintado.
        Task.Run(() => _service.FindMisspellings(text, scan.Token)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                LogService.Warning("Falló la revisión ortográfica", task.Exception?.GetBaseException());
                return;
            }
            Dispatcher.UIThread.Post(() => Apply(document, revision, scan, task.Result));
        }, TaskScheduler.Default);
    }

    private void Apply(TextDocument document, int revision, CancellationTokenSource scan, IReadOnlyList<SpellRange> ranges)
    {
        // El documento cambió mientras se revisaba, o se cerró la nota: esta revisión ya no vale.
        if (_disposed || scan.IsCancellationRequested || revision != _revision || !ReferenceEquals(document, _document)) return;

        var segments = new TextSegmentCollection<SpellSegment>(document);
        foreach (var range in ranges)
            segments.Add(new SpellSegment { StartOffset = range.Start, Length = range.Length });
        _segments = segments;
        _textView.InvalidateLayer(Layer);
    }

    private void Unmark()
    {
        _scan?.Cancel();
        if (_segments is null) return;
        _segments = null;
        _textView.InvalidateLayer(Layer);
    }

    /// <summary>Palabras marcadas ahora mismo. Pública para las pruebas de interfaz.</summary>
    public int MarkedCount => _segments?.Count ?? 0;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_segments is null || _segments.Count == 0 || !textView.VisualLinesValid) return;
        var lines = textView.VisualLines;
        if (lines.Count == 0) return;

        var start = lines[0].FirstDocumentLine.Offset;
        var end = lines[^1].LastDocumentLine.EndOffset;
        var pen = new Pen(ResolveBrush(), 1);
        foreach (var segment in _segments.FindOverlappingSegments(start, end - start))
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            DrawSquiggle(drawingContext, pen, rect.Left, rect.Right, rect.Bottom - 1);
    }

    /// <summary>Onda de tres píxeles de altura que cubre de <paramref name="left"/> a
    /// <paramref name="right"/>.</summary>
    private static void DrawSquiggle(DrawingContext context, Pen pen, double left, double right, double y)
    {
        if (right - left < 1) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left, y), false);
            var up = false;
            for (var x = left + 2; x < right + 2; x += 2)
            {
                ctx.LineTo(new Point(Math.Min(x, right), up ? y : y + 2));
                up = !up;
            }
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static IBrush ResolveBrush() =>
        Application.Current is { } app && app.TryFindResource("DangerBrush", app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.IndianRed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _scan?.Cancel();
        _scan?.Dispose();
        _service.DictionaryChanged -= OnDictionaryChanged;
        EditorPreferences.Changed -= OnPreferencesChanged;
        if (_followAppLanguage) LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        if (_document is not null) _document.Changed -= OnDocumentChanged;
    }

    private sealed class SpellSegment : TextSegment;
}
