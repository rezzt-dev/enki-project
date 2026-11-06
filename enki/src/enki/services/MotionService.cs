using System;
using Avalonia;
using Avalonia.Media.Transformation;

namespace enki.Services;

/// <summary>Perfil de movimiento compartido por estilos y entradas de sección. No cambia el diseño.</summary>
public static class MotionService
{
    public static bool UseSubtleAnimations { get; private set; }
    public static TimeSpan InteractionDuration => TimeSpan.FromMilliseconds(UseSubtleAnimations ? 90 : 150);
    public static TimeSpan RevealDuration => TimeSpan.FromMilliseconds(UseSubtleAnimations ? 100 : 180);
    public static double RevealDistance => UseSubtleAnimations ? 0 : 6;

    public static void Apply(bool subtle)
    {
        UseSubtleAnimations = subtle;
        if (Application.Current is not { } app) return;
        app.Resources["MotionInteractionDuration"] = InteractionDuration;
        app.Resources["MotionCardLift"] = TransformOperations.Parse(subtle ? "translateY(0px)" : "translateY(-4px)");
        app.Resources["MotionSmallLift"] = TransformOperations.Parse(subtle ? "translateY(0px)" : "translateY(-1px)");
        app.Resources["MotionSubtle"] = subtle;
    }
}
