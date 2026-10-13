using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using enki.Services;

namespace enki.Views;

/// <summary>Pantalla de arranque de <c>MainWindow</c>: la marca y el progreso real de la precarga mientras
/// la shell se termina de construir y cargar (ver <c>MainWindowViewModel.RunSectionWarmUpAsync</c>).
///
/// <para>Sustituye al velo anterior —un fondo liso con un spinner girando— por dos motivos. El primero es
/// de lectura: durante un arranque largo no había nada que dijera qué programa era ni cuánto faltaba. El
/// segundo es de fluidez: un giro continuo es la animación que peor tolera un hilo de UI ocupado, porque
/// cualquier trabajo pesado del arranque lo deja a tirones, y el avance de una barra que se mueve a pasos
/// discretos no se nota aunque un fotograma se pierda por el camino.</para>
///
/// <para>La visibilidad la lleva el propio control y no un <c>IsVisible</c> enlazado, porque así la cortina
/// puede fundirse al entrar y al salir (un corte seco se lee como un parpadeo) sin que nadie de fuera tenga
/// que orquestar dos propiedades. El enlace externo es un único booleano de estado.</para>
///
/// <para>Como esto tapa la aplicación entera, su regla más importante no es estética: <b>nunca puede
/// quedarse puesta</b>. Por eso cada fundido se espera con un límite de tiempo y, pase lo que pase, el
/// estado final (<c>Opacity</c> y <c>IsVisible</c>) se fija a mano en vez de darlo por hecho.</para></summary>
public partial class StartupScreen : UserControl
{
    /// <summary>Espejo de <c>MainWindowViewModel.IsSectionWarmUpActive</c>.</summary>
    public static readonly StyledProperty<bool> IsWarmUpActiveProperty =
        AvaloniaProperty.Register<StartupScreen, bool>(nameof(IsWarmUpActive));

    /// <summary>La cortina ya está opaca y su fundido de entrada ha terminado.
    ///
    /// <para>Es la señal con la que la ventana sabe cuándo puede empezar a trabajar en serio (ver
    /// <c>MainWindow.BuildShell</c>): antes de este momento el hilo de interfaz está dedicado al fundido de
    /// entrada, y cualquier trabajo pesado lo dejaría a tirones. Se dispara también cuando la animación no
    /// llega a completarse y <see cref="Settle"/> fija el estado a mano, así que no depende de que el reloj
    /// de animación avance: lo que garantiza es el estado, no el fotograma.</para></summary>
    public event Action? Entered;

    /// <summary>Duración de cada fundido. Más corta al salir que al entrar: la salida es lo que el usuario
    /// espera para poder usar la aplicación.</summary>
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(130);

    /// <summary>Margen que se le concede a la animación por encima de su duración antes de dar su estado por
    /// fijado a mano (ver <see cref="PlayAsync"/>).</summary>
    private static readonly TimeSpan AnimationGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>Transición en curso. Nunca se libera: una transición posterior solo la cancela, y liberar un
    /// <c>Cancel()</c> que otro hilo/comando pudiera volver a invocar daría un <c>ObjectDisposedException</c>.
    /// Son dos objetos por sesión de la aplicación, así que no compensa arriesgar eso por liberarlos.</summary>
    private CancellationTokenSource? _current;
    private int _version;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsWarmUpActiveProperty) _ = TransitionAsync(IsWarmUpActive);
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
            if (active) IsVisible = true;
            else if (!IsVisible) return;

            var duration = active ? EnterDuration : ExitDuration;
            await PlayAsync(active, duration, cancellation.Token);

            // Si otra cortina tomó el relevo, el estado final no es cosa de esta pasada.
            if (version == _version) Settle(active, cancellation);
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

    /// <summary>Lanza los dos fundidos —la opacidad del control y el desplazamiento de su contenido, que en
    /// el perfil de animaciones sutiles es cero— y espera a que terminen o, como mucho, a su duración más
    /// <see cref="AnimationGrace"/>. Esa espera acotada es lo que impide que la cortina siga puesta si el
    /// reloj de animación no avanza o el compositor no llega a dibujar.</summary>
    private async Task PlayAsync(bool enter, TimeSpan duration, CancellationToken token)
    {
        var lift = MotionService.RevealDistance;
        var fade = BuildFade(enter, duration);

        // El desplazamiento se anima sobre el propio control (un Visual), no sobre su TranslateTransform:
        // TransformAnimator resuelve la propiedad de transformada buscando el RenderTransform del Visual que
        // se le pasa, así que animar la transformada suelta revienta con una conversión inválida. Es el mismo
        // reparto que usa MotionReveal. El RenderTransform declarado en el XAML es lo que hace que la
        // propiedad enrute a algo; sin él, este control simplemente no se desplaza.
        var running = StartupContent.RenderTransform is not TranslateTransform
            ? Observe(fade.RunAsync(this, token))
            : Task.WhenAll(
                Observe(fade.RunAsync(this, token)),
                Observe(BuildRise(enter, duration, lift).RunAsync(StartupContent, token)));

        // El límite se cancela con el token a propósito: cancelado significa que otra cortina tomó el
        // relevo o que ya hemos fijado el estado final, y entonces esperar aquí no aporta nada.
        var bounded = Task.Delay(duration + AnimationGrace, token);
        await Task.WhenAny(running, bounded);
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

    /// <summary>Desplazamiento del bloque central, animado sobre <c>StartupContent</c> (ver
    /// <see cref="PlayAsync"/> para por qué el destino es el control y no su transformada).</summary>
    private static Animation BuildRise(bool enter, TimeSpan duration, double lift) => new()
    {
        Duration = duration,
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters = { new Setter(TranslateTransform.YProperty, enter ? lift : 0.0) }
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(TranslateTransform.YProperty, enter ? 0.0 : -lift) }
            }
        }
    };
}
