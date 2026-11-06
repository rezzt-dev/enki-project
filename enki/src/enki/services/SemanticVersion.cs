using System;
using System.Globalization;
using System.Linq;

namespace enki.Services;

/// <summary>Versión SemVer comparable — lo mínimo de <see href="https://semver.org">semver.org</see> que
/// necesita el aviso de versión nueva (T9 de .docs/funcionalidades-pendientes-priorizadas.md §4.5):
/// <c>MAYOR.MENOR.PARCHE</c> con etiqueta de <em>prerelease</em> opcional.
///
/// <para>No vale <see cref="Version"/> de .NET, que es lo que devuelve el ensamblado: su orden de
/// comparación no conoce las <em>prereleases</em> —para él <c>1.0.0-beta</c> no existe— y precisamente la
/// regla de "1.0.0-beta es <b>anterior</b> a 1.0.0" es la que decide si se avisa o no cuando el usuario
/// está en un canal alfa. También acepta la <c>v</c> inicial con la que GitHub etiqueta las releases
/// (<c>v0.2.0</c>), para no tener que recortarla en cada llamada.</para>
///
/// <para>Deliberadamente no implementa los metadatos de compilación (<c>+build</c>): SemVer manda
/// ignorarlos al comparar, así que se descartan al analizar y nunca llegan a influir.</para></summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemanticVersion>
{
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // Los metadatos de compilación no participan en la comparación (SemVer §10).
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) value = value[..plus];

        string? preRelease = null;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..];
            value = value[..dash];
            if (preRelease.Length == 0) return false;
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        // Cuatro componentes es lo que devuelve Assembly.GetName().Version ("0.1.0.0"); el cuarto se
        // ignora, igual que los metadatos de compilación.
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        var minor = 0;
        var patch = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)) return false;
        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
        if (parts.Length > 3 && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // Regla que motiva esta clase: una versión con prerelease va ANTES que la misma sin ella.
        if (!IsPreRelease && !other.IsPreRelease) return 0;
        if (!IsPreRelease) return 1;
        if (!other.IsPreRelease) return -1;

        return ComparePreRelease(PreRelease!, other.PreRelease!);
    }

    /// <summary>SemVer §11.4: se comparan los identificadores separados por puntos, los numéricos entre sí
    /// como números y los alfanuméricos en orden ASCII; un numérico siempre va antes que un alfanumérico, y
    /// a igualdad de prefijo gana el que tenga más identificadores.</summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        foreach (var (x, y) in a.Zip(b))
        {
            var xNumeric = int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xValue);
            var yNumeric = int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yValue);

            if (xNumeric && yNumeric)
            {
                if (xValue != yValue) return xValue.CompareTo(yValue);
                continue;
            }

            if (xNumeric != yNumeric) return xNumeric ? -1 : 1;

            var ordinal = string.CompareOrdinal(x, y);
            if (ordinal != 0) return ordinal < 0 ? -1 : 1;
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        IsPreRelease
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
