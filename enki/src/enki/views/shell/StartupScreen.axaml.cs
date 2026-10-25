using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using enki.Services;

namespace enki.Views;

/// <summary>Launcher único: logo personalizado desde el primer fotograma, barra de dos segundos en
/// el compositor y fundido hacia la shell preparada. La duración de la barra no depende de las fases
/// internas de carga. Las esperas ceden la UI y las transiciones se cancelan al cerrar la ventana.</summary>
public partial class StartupScreen : UserControl
{
    /// <summary>Espejo de <c>MainWindowViewModel.IsSectionWarmUpActive</c>.</summary>
    public static readonly StyledProperty<bool> IsWarmUpActiveProperty =
        AvaloniaProperty.Register<StartupScreen, bool>(nameof(IsWarmUpActive));

    /// <summary>El primer fotograma ya se ha cedido al compositor; la ventana puede construir la shell
    /// detrás del logo y de la barra, que continúa animándose en el hilo de renderizado.</summary>
    public event Action? Entered;

    /// <summary>Margen para presentar el primer fotograma antes de construir la shell.</summary>
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(32);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(130);

    /// <summary>Margen que se le concede a la animación por encima de su duración antes de dar su estado por
    /// fijado a mano (ver <see cref="PlayAsync"/>).</summary>
    private static readonly TimeSpan AnimationGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>Transición en curso. Nunca se libera: una transición posterior solo la cancela, y liberar un
    /// <c>Cancel()</c> que otro hilo/comando pudiera volver a invocar daría un <c>ObjectDisposedException</c>.
    /// Son dos objetos por sesión de la aplicación, así que no compensa arriesgar eso por liberarlos.</summary>
    private CancellationTokenSource? _current;
    private int _version;
    private readonly Stopwatch _visibleTime = new();
    private CancellationTokenSource? _pendingExit;
    private Task _entrance = Task.CompletedTask;
    private bool _openingPrepared;
    private CancellationTokenSource? _progressCancellation;

    public StartupScreen()
    {
        InitializeComponent();
        // Arranca escondida: el enlace de IsWarmUpActive la enseñará si y cuando el arranque lo pida.
        Opacity = 0;
        IsVisible = false;
    }

    public bool IsWarmUpActive
    {
        get => GetValue(IsWarmUpActiveProperty);
        set => SetValue(IsWarmUpActiveProperty, value);
    }

    /// <summary>Logo de la pantalla. Lo fija <c>MainWindow.UpdateBrandIcon</c>, que es quien sabe si el
    /// usuario tiene logo personalizado y cuál de las dos versiones (clara u oscura) toca por tema — el
    /// mismo criterio que ya usa para el logo del sidebar.</summary>
    public void SetLogo(IImage? logo) => StartupLogo.Source = logo;

    /// <summary>Prepara el primer fotograma antes de mostrar la ventana. No inicia relojes ni carga:
    /// el logo y el fondo ya están presentes cuando el sistema presenta la superficie por primera vez.</summary>
    public void PrepareForOpening()
    {
        _openingPrepared = true;
        Opacity = 1;
        IsVisible = true;
    }

    /// <summary>Inicia la animación y la permanencia mínima solo cuando la ventana ya está abierta.</summary>
    public void BeginOpening()
    {
        _openingPrepared = false;
        SetCurrentValue(IsWarmUpActiveProperty, true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsWarmUpActiveProperty) return;
        RequestTransition();
    }

    private void RequestTransition()
    {
        if (_openingPrepared) return;
        _pendingExit?.Cancel();
        if (IsWarmUpActive)
        {
            _visibleTime.Restart();
            _entrance = TransitionAsync(true);
            _progressCancellation?.Cancel();
            var progressCancellation = new CancellationTokenSource();
            _progressCancellation = progressCancellation;
            _ = AnimateProgressAsync(progressCancellation);
        }
        else
        {
            var pendingExit = new CancellationTokenSource();
            _pendingExit = pendingExit;
            _ = HideWhenReadyAsync(pendingExit);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Al restaurar una ventana que se ocultó durante la carga, retoma la transición cancelada.
        if (IsWarmUpActive || IsVisible) RequestTransition();
    }

    /// <summary>No interrumpe la entrada aunque la carga termine enseguida. La espera se cuenta desde
    /// que aparece la pantalla y deja libre la UI; una reactivación cancela la salida pendiente.</summary>
    private async Task HideWhenReadyAsync(CancellationTokenSource pendingExit)
    {
        try
        {
            await _entrance;
            pendingExit.Token.ThrowIfCancellationRequested();
            if (!IsVisible) return;

            // Primero se completa la barra de dos segundos; después comienza el fundido.
            var remaining = StartupGate.MinimumDisplayDuration - _visibleTime.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, pendingExit.Token);
            pendingExit.Token.ThrowIfCancellationRequested();
            await TransitionAsync(false);
        }
        catch (OperationCanceledException)
        {
            // Otra entrada o el cierre de la ventana ha descartado esta salida.
        }
        finally
        {
            if (ReferenceEquals(_pendingExit, pendingExit)) _pendingExit = null;
            pendingExit.Dispose();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _version++;
        _pendingExit?.Cancel();
        _current?.Cancel();
        _progressCancellation?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Una única barra temporal de principio a fin. Animar la escala evita medir el layout en
    /// cada fotograma; los cambios de fase de los servicios no reinician ni sustituyen la barra.</summary>
    private async Task AnimateProgressAsync(CancellationTokenSource cancellation)
    {
        CompositionVisual? visual = null;
        try
        {
            // Un control recién hecho visible todavía puede no tener visual de composición.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            cancellation.Token.ThrowIfCancellationRequested();
            visual = ElementComposition.GetElementVisual(StartupProgress);
            if (visual is null) return;
            _visibleTime.Restart();
            visual.Scale = new Vector3D(0, 1, 1);
            StartupProgress.Opacity = 1;
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Duration = StartupGate.MinimumDisplayDuration;
            animation.InsertKeyFrame(0, new Vector3D(0, 1, 1), new LinearEasing());
            animation.InsertKeyFrame(1, new Vector3D(1, 1, 1), new LinearEasing());
            visual.StartAnimation(nameof(CompositionVisual.Scale), animation);
            // La interpolación ocurre en el compositor, incluso si construir la shell ocupa la UI.
            await Task.Delay(StartupGate.MinimumDisplayDuration, cancellation.Token);
            visual.Scale = new Vector3D(1, 1, 1);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo animar la barra de arranque", ex);
        }
        finally
        {
            if (ReferenceEquals(_progressCancellation, cancellation))
            {
                visual?.StopAnimation(nameof(CompositionVisual.Scale));
                _progressCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task TransitionAsync(bool active)
    {
        // Una cortina que se estaba fundiendo se descarta: quien manda es el último estado.
        _current?.Cancel();
        var cancellation = new CancellationTokenSource();
        _current = cancellation;
        var version = ++_version;

        try
        {
            if (active)
            {
                // El fondo nunca se funde al entrar: dejarlo transparente revela la shell vacía.
                Opacity = 1;
                IsVisible = true;
            }
            else if (!IsVisible) return;

            var duration = active ? EnterDuration : ExitDuration;
            await PlayAsync(active, duration, cancellation.Token);
            if (!active)
            {
                // El reloj gráfico puede completar un fotograma algo antes que el reloj real.
                var remaining = StartupGate.MinimumDisplayDuration - _visibleTime.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation.Token);
            }

            // Si otra cortina tomó el relevo, el estado final no es cosa de esta pasada.
            if (version == _version) Settle(active, cancellation);
        }
        catch (OperationCanceledException)
        {
            // La ventana se cerró o una nueva transición tomó el relevo.
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo animar la pantalla de arranque", ex);
            if (version == _version) Settle(active, cancellation);
        }
    }

    /// <summary>Deja el control en su estado final: corta lo que esté animándose y escribe el valor que
    /// toca. El corte va primero a propósito — una animación que sigue viva reescribe sus fotogramas encima
    /// del valor que se le acabe de poner, así que si su reloj no avanza (o avanza a saltos) dejaría puesta
    /// una opacidad intermedia, o la cortina entera, indefinidamente. Es la diferencia entre "la animación
    /// termina" y "el estado termina".</summary>
    private void Settle(bool active, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        if (ReferenceEquals(_current, cancellation)) _current = null;
        Opacity = active ? 1 : 0;
        IsVisible = active;
        // Hito para el cronómetro del arranque: es el momento en el que la cortina queda opaca y, por
        // tanto, el único desde el que ya se puede trabajar pesado sin que se note (ver StartupTrace).
        if (!active) return;
        StartupTrace.Mark("cortina opaca");
        Entered?.Invoke();
    }

    /// <summary>Cede el primer fotograma al entrar y anima solo la opacidad al salir.
    /// Espera a que termine o, como mucho, a su duración más
    /// <see cref="AnimationGrace"/>. Esa espera acotada es lo que impide que la cortina siga puesta si el
    /// reloj de animación no avanza o el compositor no llega a dibujar.</summary>
    private async Task PlayAsync(bool enter, TimeSpan duration, CancellationToken token)
    {
        // Antes de construir la shell se cede un fotograma para entregar la barra al compositor.
        // La entrada no desplaza ni desvanece el logo; la única animación es el relleno de la barra.
        if (enter)
        {
            await Task.Delay(duration, token);
            return;
        }
        var running = Observe(BuildFade(false, duration).RunAsync(this, token));

        // El límite se cancela con el token a propósito: cancelado significa que otra cortina tomó el
        // relevo o que ya hemos fijado el estado final, y entonces esperar aquí no aporta nada.
        var bounded = Task.Delay(duration + AnimationGrace, token);
        var completed = await Task.WhenAny(running, bounded);
        await completed;
    }

    /// <summary>Consume el resultado de una animación. Una cancelada no es un fallo —es justo lo que pasa
    /// cuando otra cortina toma el relevo—, y dejarla sin observar solo añadiría ruido al log.</summary>
    private static async Task Observe(Task animation)
    {
        try
        {
            await animation;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Se observa también si ya venció el límite de espera de PlayAsync.
            LogService.Warning("No se pudo animar la pantalla de arranque", ex);
        }
    }

    private static Animation BuildFade(bool enter, TimeSpan duration) => new()
    {
        Duration = duration,
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters = { new Setter(Visual.OpacityProperty, enter ? 0.0 : 1.0) }
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(Visual.OpacityProperty, enter ? 1.0 : 0.0) }
            }
        }
    };

}
