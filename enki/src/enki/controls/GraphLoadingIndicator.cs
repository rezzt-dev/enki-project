using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace enki.Controls;

/// <summary>Animación de carga del grafo de Sesiones: una pequeña constelación de notas que se construye
/// sola — el nodo central aparece, las aristas se trazan hacia fuera y cada nota "encaja" al llegar su
/// arista, una señal recorre la red ya formada y todo se disuelve para empezar de nuevo — sobre una
/// línea de progreso indeterminada de 1px. Es el mismo lenguaje visual que el grafo real (nodos
/// circulares, aristas finas, tinta monocroma) para que la transición entre "cargando" y "cargado" se lea
/// como continuidad y no como un cambio de pantalla.
///
/// Pinta directamente sobre el <see cref="DrawingContext"/>, igual que los gráficos de
/// <c>controls/charts/</c>, y el color entra solo por <see cref="Ink"/> para seguir el tema activo. El
/// bucle de fotogramas (<see cref="TopLevel.RequestAnimationFrame"/>) solo corre mientras
/// <see cref="IsActive"/> es verdadero y el control está en el árbol visual: oculto no consume nada.</summary>
public sealed class GraphLoadingIndicator : Control
{
    public static readonly StyledProperty<IBrush?> InkProperty =
        AvaloniaProperty.Register<GraphLoadingIndicator, IBrush?>(nameof(Ink));

    /// <summary>Arranca (desde el principio del ciclo) o detiene la animación.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<GraphLoadingIndicator, bool>(nameof(IsActive));

    private const double CycleSeconds = 3.6;
    private const double HairlineCycleSeconds = 1.8;
    private const double RotationDegreesPerSecond = 8;
    private const double ConstellationRadius = 44;
    private const double HairlineWidth = 132;
    private const double HairlineAreaHeight = 26;

    /// <summary>Topología fija de la constelación, en coordenadas polares relativas a
    /// <see cref="ConstellationRadius"/>. <c>Parent</c> es el nodo del que sale su arista (−1 para el
    /// centro); <c>RevealAt</c> es el instante del ciclo (0–1) en que esa arista empieza a trazarse.</summary>
    private static readonly NodeSpec[] Nodes =
    [
        new(0, 0.00, 5.0, 1.00, -1, 0.00),
        new(-90, 1.00, 3.4, 0.95, 0, 0.08),
        new(-28, 0.80, 2.8, 0.85, 0, 0.135),
        new(32, 1.04, 3.4, 0.95, 0, 0.19),
        new(98, 0.76, 2.6, 0.80, 0, 0.245),
        new(158, 1.00, 3.2, 0.90, 0, 0.30),
        new(214, 0.84, 2.8, 0.85, 0, 0.355),
        new(-64, 1.62, 2.2, 0.60, 1, 0.42),
        new(52, 1.66, 2.0, 0.55, 3, 0.47),
        new(182, 1.64, 2.2, 0.60, 5, 0.52),
    ];

    /// <summary>Enlaces "laterales" entre notas del primer anillo: convierten el árbol en red.</summary>
    private static readonly (int From, int To, double RevealAt)[] Chords =
    [
        (1, 2, 0.50), (3, 4, 0.545), (5, 6, 0.59), (2, 3, 0.635),
    ];

    private const double SpokeDrawDuration = 0.13;
    private const double LeafDrawDuration = 0.10;
    private const double ChordDrawDuration = 0.12;
    private const double NodePopDuration = 0.09;
    private const double SignalStart = 0.66;
    private const double SignalDuration = 0.15;
    private const double CollapseStart = 0.87;

    private readonly Stopwatch _clock = new();
    private bool _frameRequested;

    static GraphLoadingIndicator()
    {
        AffectsRender<GraphLoadingIndicator>(InkProperty);
    }

    public IBrush? Ink { get => GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsActiveProperty) return;

        if (IsActive)
        {
            _clock.Restart();
            RequestFrame();
        }
        else
        {
            _clock.Stop();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsActive) return;
        _clock.Start();
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _clock.Stop();
    }

    private void RequestFrame()
    {
        if (_frameRequested || !IsActive) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        _frameRequested = true;
        topLevel.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (!IsActive || TopLevel.GetTopLevel(this) is null) return;
            InvalidateVisual();
            RequestFrame();
        });
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        var ink = (Ink as ISolidColorBrush)?.Color ?? Colors.White;
        var seconds = _clock.Elapsed.TotalSeconds;
        var center = new Point(size.Width / 2, (size.Height - HairlineAreaHeight) / 2);

        DrawConstellation(context, ink, center, seconds);
        DrawHairline(context, ink, new Rect(size.Width / 2 - HairlineWidth / 2, size.Height - 1, HairlineWidth, 1), seconds);
    }

    // ===================== Constelación =====================

    private static void DrawConstellation(DrawingContext context, Color ink, Point center, double seconds)
    {
        var t = seconds % CycleSeconds / CycleSeconds;

        // Disolución al final de cada ciclo: todo se apaga y encoge un poco a la vez, así el reinicio
        // ocurre sobre un lienzo vacío y nunca se ve un salto.
        var collapse = EaseInCubic(Phase(t, CollapseStart, 1 - CollapseStart));
        var alpha = 1 - collapse;
        var scale = 1 - 0.07 * collapse;
        if (alpha <= 0.001) return;

        // Giro lento continuo (sobre el tiempo absoluto, no el del ciclo) + una deriva mínima por nodo, que
        // imita el asentamiento de la simulación de fuerzas del grafo real.
        var rotation = seconds * RotationDegreesPerSecond * Math.PI / 180;
        var positions = new Point[Nodes.Length];
        for (var i = 0; i < Nodes.Length; i++)
        {
            var spec = Nodes[i];
            var angle = spec.AngleDegrees * Math.PI / 180 + rotation + Math.Sin(seconds * 0.9 + i * 2.3) * 0.035;
            var distance = spec.Distance * (1 + Math.Sin(seconds * 1.3 + i * 1.7) * 0.04) * ConstellationRadius * scale;
            positions[i] = new Point(center.X + Math.Cos(angle) * distance, center.Y + Math.Sin(angle) * distance);
        }

        DrawSignalRipples(context, ink, center, t, alpha);

        foreach (var (from, to, revealAt) in Chords)
            DrawEdge(context, ink, positions[from], positions[to], Phase(t, revealAt, ChordDrawDuration), 0.13 * alpha, alpha);

        for (var i = 1; i < Nodes.Length; i++)
        {
            var spec = Nodes[i];
            var duration = spec.Parent == 0 ? SpokeDrawDuration : LeafDrawDuration;
            DrawEdge(context, ink, positions[spec.Parent], positions[i], Phase(t, spec.RevealAt, duration), 0.28 * alpha, alpha);
        }

        DrawSignalPulses(context, ink, positions, t, alpha);

        for (var i = 0; i < Nodes.Length; i++)
        {
            var spec = Nodes[i];
            var popStart = i == 0
                ? 0
                : spec.RevealAt + (spec.Parent == 0 ? SpokeDrawDuration : LeafDrawDuration) * 0.85;
            DrawNode(context, ink, positions[i], spec, Phase(t, popStart, i == 0 ? 0.11 : NodePopDuration), seconds, alpha);
        }
    }

    private static void DrawEdge(DrawingContext context, Color ink, Point from, Point to, double progress, double edgeAlpha, double alpha)
    {
        if (progress <= 0) return;

        var eased = EaseInOutCubic(progress);
        var head = Lerp(from, to, eased);
        context.DrawLine(new ImmutablePen(Brush(ink, edgeAlpha), 1), from, head);

        // Cabeza luminosa mientras la arista se está trazando: da la sensación de que la red "se teje".
        if (progress < 1)
            context.DrawEllipse(Brush(ink, 0.9 * alpha * Math.Sin(Math.PI * progress)), null, head, 1.4, 1.4);
    }

    private static void DrawNode(DrawingContext context, Color ink, Point position, NodeSpec spec, double pop, double seconds, double alpha)
    {
        if (pop <= 0) return;

        var isCenter = spec.Parent < 0;
        var radius = spec.Radius * EaseOutBack(pop);
        if (isCenter && pop >= 1)
            radius *= 1 + Math.Sin(seconds * Math.PI * 2 / 1.4) * 0.07;

        // Destello de "encaje": un anillo que se abre y apaga justo cuando aparece la nota.
        if (pop < 1)
        {
            var flash = spec.Radius + 7 * EaseOutCubic(pop);
            context.DrawEllipse(null, new ImmutablePen(Brush(ink, 0.45 * (1 - pop) * alpha), 1), position, flash, flash);
        }

        if (isCenter)
        {
            var ring = 10 * EaseOutCubic(pop);
            context.DrawEllipse(null, new ImmutablePen(Brush(ink, 0.2 * alpha), 1), position, ring, ring);
        }

        context.DrawEllipse(Brush(ink, spec.Alpha * alpha * Math.Min(1, pop * 2)), null, position, radius, radius);
    }

    /// <summary>Con la red ya formada, dos ondas concéntricas salen del centro.</summary>
    private static void DrawSignalRipples(DrawingContext context, Color ink, Point center, double t, double alpha)
    {
        for (var k = 0; k < 2; k++)
        {
            var u = Phase(t, SignalStart + k * 0.07, 0.2);
            if (u <= 0 || u >= 1) continue;

            var radius = 8 + (ConstellationRadius * 1.8 - 8) * EaseOutCubic(u);
            var ringAlpha = (k == 0 ? 0.3 : 0.16) * Math.Pow(1 - u, 1.5) * alpha;
            context.DrawEllipse(null, new ImmutablePen(Brush(ink, ringAlpha), 1), center, radius, radius);
        }
    }

    /// <summary>A la vez que las ondas, un punto de luz recorre cada radio desde el centro y, si la nota
    /// tiene una hoja colgando, continúa hasta ella.</summary>
    private static void DrawSignalPulses(DrawingContext context, Color ink, Point[] positions, double t, double alpha)
    {
        for (var i = 1; i < Nodes.Length; i++)
        {
            if (Nodes[i].Parent != 0) continue;

            var u = (t - (SignalStart + (i - 1) * 0.018)) / SignalDuration;
            if (u <= 0) continue;

            if (u < 1)
            {
                DrawPulse(context, ink, Lerp(positions[0], positions[i], EaseInOutSine(u)), u, alpha);
                continue;
            }

            var leaf = Array.FindIndex(Nodes, n => n.Parent == i);
            if (leaf < 0 || u >= 2) continue;
            DrawPulse(context, ink, Lerp(positions[i], positions[leaf], EaseInOutSine(u - 1)), u - 1, alpha * 0.7);
        }
    }

    private static void DrawPulse(DrawingContext context, Color ink, Point position, double u, double alpha) =>
        context.DrawEllipse(Brush(ink, 0.95 * Math.Sin(Math.PI * u) * alpha), null, position, 1.7, 1.7);

    // ===================== Línea de progreso =====================

    /// <summary>Progreso indeterminado de 1px: un segmento cuya cabeza y cola avanzan con la misma curva
    /// pero desfasadas, de modo que se estira al arrancar y se recoge al llegar al final.</summary>
    private static void DrawHairline(DrawingContext context, Color ink, Rect track, double seconds)
    {
        context.FillRectangle(Brush(ink, 0.08), track);

        var t = seconds % HairlineCycleSeconds / HairlineCycleSeconds;
        var head = EaseInOutCubic(Math.Clamp(t / 0.72, 0, 1));
        var tail = EaseInOutCubic(Math.Clamp((t - 0.28) / 0.72, 0, 1));
        if (head - tail <= 0.001) return;

        context.FillRectangle(Brush(ink, 0.75),
            new Rect(track.X + tail * track.Width, track.Y, (head - tail) * track.Width, track.Height));
    }

    // ===================== Utilidades =====================

    private static ImmutableSolidColorBrush Brush(Color ink, double opacity) =>
        new ImmutableSolidColorBrush(ink, Math.Clamp(opacity, 0, 1));

    private static double Phase(double t, double start, double duration) => Math.Clamp((t - start) / duration, 0, 1);

    private static Point Lerp(Point a, Point b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

    private static double EaseInCubic(double x) => x * x * x;

    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

    private static double EaseInOutCubic(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    private static double EaseInOutSine(double x) => -(Math.Cos(Math.PI * x) - 1) / 2;

    private static double EaseOutBack(double x)
    {
        const double overshoot = 1.70158;
        return 1 + (overshoot + 1) * Math.Pow(x - 1, 3) + overshoot * Math.Pow(x - 1, 2);
    }

    private readonly record struct NodeSpec(double AngleDegrees, double Distance, double Radius, double Alpha, int Parent, double RevealAt);
}
