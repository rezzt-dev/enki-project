using System;
using Avalonia;

namespace enki.Views.Sessions;

/// <summary>Parámetros del visor de sesiones en 3D, reunidos en un único lugar. El documento web los recibe
/// en su carga útil (<see cref="SessionGraphRenderer"/>) para que ambos renderizadores compartan exactamente
/// la misma geometría, velocidad y perspectiva sin repetir los valores en JavaScript.</summary>
internal static class SessionSphereParameters
{
    /// <summary>Separación mínima deseada entre dos notas de una misma esfera, en unidades de mundo.</summary>
    public const double NodeSpacing = 26;

    /// <summary>Fracción orientativa de notas en la corteza exterior; el resto ocupa el volumen interior.
    /// <see cref="SessionSphereDistribution"/> la adapta al tamaño y a la densidad de cada sesión.</summary>
    public const double ShellFraction = 0.65;

    /// <summary>Banda radial de la corteza, como fracción del radio: [ShellInnerRatio·R, R].</summary>
    public const double ShellInnerRatio = 0.8;

    /// <summary>Radio máximo del interior, como fracción del radio; deja una transición visible con la corteza
    /// sin convertir la esfera en una cáscara hueca.</summary>
    public const double CoreOuterRatio = 0.72;

    /// <summary>Radio mínimo del interior: ninguna nota queda exactamente en el centro geométrico.</summary>
    public const double CoreInnerRatio = 0.08;

    /// <summary>Radio de una sesión de una sola nota: suficiente para reservar su etiqueta y su área de clic.</summary>
    public const double SingleNoteRadius = 22;

    public const double MinRadius = 36;
    public const double MaxRadius = 320;

    /// <summary>Escalado no lineal del radio con el número de notas: <c>R = RadiusScale · n^RadiusExponent</c>,
    /// acotado entre <see cref="MinRadius"/> y <see cref="MaxRadius"/>.</summary>
    public const double RadiusScale = 24;
    public const double RadiusExponent = 0.4;

    /// <summary>Espacio libre entre las envolventes de dos esferas vecinas.</summary>
    public const double SphereGap = 90;

    /// <summary>Margen alrededor de cada esfera reservado para su etiqueta de sesión.</summary>
    public const double LabelClearance = 46;

    /// <summary>Velocidad de la rotación automática: una vuelta cada ~63 s.</summary>
    public const double RotationSpeed = 0.10;

    /// <summary>Inclinación estática del eje de giro hacia el observador, en radianes.</summary>
    public const double AxisTilt = 0.32;

    /// <summary>Límite de la inclinación manual al girar una esfera arrastrándola.</summary>
    public const double MaxPitch = 1.35;

    /// <summary>Distancia de la cámara al centro de cada esfera, en radios de esa esfera. Con 3,2 la nota más
    /// cercana se ve ~1,45 veces mayor que el centro y la más lejana ~0,76 veces: perspectiva perceptible
    /// sin deformar la silueta.</summary>
    public const double PerspectiveDepth = 3.2;

    /// <summary>Vecinos espaciales por nota en la malla decorativa de sesión.</summary>
    public const int MeshNeighbors = 3;

    /// <summary>Radianes de giro por píxel arrastrado al rotar una esfera a mano.</summary>
    public const double ManualRotationPerPixel = 0.008;

    /// <summary>Tolerancia al leer posiciones guardadas: se almacenan normalizadas al radio de su esfera, así
    /// que cualquier valor más allá de este módulo procede de un layout anterior (2D o de mundo) y se descarta.</summary>
    public const double StoredPositionTolerance = 1.1;
}

/// <summary>Punto o vector en el espacio local de una esfera (o en el mundo), en unidades de mundo.</summary>
internal readonly record struct Point3(double X, double Y, double Z)
{
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double SquaredLength => X * X + Y * Y + Z * Z;

    public static Point3 operator +(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3 operator -(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3 operator *(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    /// <summary>Devuelve el punto con un módulo máximo de <paramref name="radius"/>.</summary>
    public Point3 ClampLength(double radius)
    {
        var length = Length;
        return length > radius && length > 0 ? this * (radius / length) : this;
    }
}

/// <summary>Orientación de una esfera: giro alrededor de su eje vertical (<see cref="Yaw"/>) seguido de la
/// inclinación de ese eje hacia el observador (<see cref="Pitch"/>).</summary>
internal readonly record struct SphereOrientation(double Yaw, double Pitch);

/// <summary>Proyección de una nota desde el espacio local de su esfera a la pantalla, y su inversa para el
/// arrastre. Es la misma matemática que ejecuta el documento web; aquí la usa el renderizador nativo.</summary>
internal static class SessionSphereProjection
{
    /// <summary>Aplica <c>Rx(pitch) · Ry(yaw)</c>: la esfera gira sobre su propio eje y después ese eje se inclina.
    /// Con la Y de pantalla hacia abajo y la Z hacia el observador, un pitch positivo acerca el polo superior: la
    /// esfera se ve ligeramente desde arriba.</summary>
    public static Point3 Rotate(Point3 p, SphereOrientation o)
    {
        var (sinYaw, cosYaw) = Math.SinCos(o.Yaw);
        var (sinPitch, cosPitch) = Math.SinCos(o.Pitch);
        var x = p.X * cosYaw + p.Z * sinYaw;
        var z = -p.X * sinYaw + p.Z * cosYaw;
        return new Point3(x, p.Y * cosPitch + z * sinPitch, -p.Y * sinPitch + z * cosPitch);
    }

    /// <summary>Inversa exacta de <see cref="Rotate"/>.</summary>
    public static Point3 Unrotate(Point3 p, SphereOrientation o)
    {
        var (sinYaw, cosYaw) = Math.SinCos(o.Yaw);
        var (sinPitch, cosPitch) = Math.SinCos(o.Pitch);
        var y = p.Y * cosPitch - p.Z * sinPitch;
        var z = p.Y * sinPitch + p.Z * cosPitch;
        return new Point3(p.X * cosYaw - z * sinYaw, y, p.X * sinYaw + z * cosYaw);
    }

    /// <summary>Factor de perspectiva para una profundidad rotada <paramref name="z"/> (positiva hacia el
    /// observador) en una esfera de radio <paramref name="radius"/>.</summary>
    public static double Perspective(double z, double radius)
    {
        var distance = SessionSphereParameters.PerspectiveDepth * Math.Max(radius, 1);
        return distance / Math.Max(distance * 0.2, distance - z);
    }

    /// <summary>Desplazamiento proyectado respecto al centro de la esfera (unidades de mundo antes del zoom),
    /// profundidad normalizada en [-1, 1] y factor de escala aparente.</summary>
    public static (Vector Offset, double Depth, double Scale) Project(Point3 local, double radius, SphereOrientation o)
    {
        var rotated = Rotate(local, o);
        var scale = Perspective(rotated.Z, radius);
        return (new Vector(rotated.X * scale, rotated.Y * scale), Math.Clamp(rotated.Z / Math.Max(radius, 1), -1, 1), scale);
    }

    /// <summary>Recupera el punto local cuya proyección cae en <paramref name="offset"/> conservando la
    /// profundidad rotada <paramref name="rotatedZ"/>: arrastrar mueve la nota en el plano de la pantalla.</summary>
    public static Point3 FromScreenOffset(Vector offset, double rotatedZ, double radius, SphereOrientation o)
    {
        var scale = Perspective(rotatedZ, radius);
        return Unrotate(new Point3(offset.X / scale, offset.Y / scale, rotatedZ), o);
    }
}
