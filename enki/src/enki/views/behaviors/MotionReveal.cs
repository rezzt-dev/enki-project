using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using enki.Services;

namespace enki.Views;

/// <summary>Entrada corta sin fase de ocultación animada. Agrupa solicitudes del mismo ciclo,
/// cancela la anterior y libera su reloj al ocultar o desmontar el control.</summary>
public sealed class MotionReveal : AvaloniaObject
{
    public static readonly AttachedProperty<int> RevisionProperty =
        AvaloniaProperty.RegisterAttached<MotionReveal, Control, int>("Revision");

    public static readonly AttachedProperty<bool> AllowTranslationProperty =
        AvaloniaProperty.RegisterAttached<MotionReveal, Control, bool>("AllowTranslation", true);

    public static bool GetAllowTranslation(Control control) => control.GetValue(AllowTranslationProperty);
    public static void SetAllowTranslation(Control control, bool value) => control.SetValue(AllowTranslationProperty, value);

    private static readonly ConditionalWeakTable<Control, RevealState> States = new();

    static MotionReveal()
    {
        RevisionProperty.Changed.AddClassHandler<Control>((control, _) =>
            States.GetValue(control, c => new RevealState(c)).Request());
    }

    public static int GetRevision(Control control) => control.GetValue(RevisionProperty);
    public static void SetRevision(Control control, int value) => control.SetValue(RevisionProperty, value);

    private sealed class RevealState
    {
        private readonly Control _control;
        private CancellationTokenSource? _running;
        private int _version;

        public RevealState(Control control)
        {
            _control = control;
            control.DetachedFromVisualTree += (_, _) => Cancel();
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !control.IsVisible) Cancel();
            };
        }

        public void Request()
        {
            Cancel();
            var version = _version;
            // Loaded permite construir y medir el destino antes de arrancar el reloj.
            Dispatcher.UIThread.Post(() =>
            {
                if (version == _version && _control.IsEffectivelyVisible && TopLevel.GetTopLevel(_control) is not null)
                    _ = RunAsync();
            }, DispatcherPriority.Loaded);
        }

        private void Cancel()
        {
            _version++;
            _running?.Cancel();
            _running = null;
        }

        private async Task RunAsync()
        {
            using var cancellation = new CancellationTokenSource();
            _running = cancellation;
            var animation = new Animation
            {
                Duration = MotionService.RevealDuration,
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(Visual.OpacityProperty, 0.0),
                            new Setter(TranslateTransform.YProperty, GetAllowTranslation(_control) ? MotionService.RevealDistance : 0.0) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(Visual.OpacityProperty, 1.0),
                            new Setter(TranslateTransform.YProperty, 0.0) }
                    }
                }
            };
            try { await animation.RunAsync(_control, cancellation.Token); }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_running, cancellation)) _running = null;
            }
        }
    }
}
