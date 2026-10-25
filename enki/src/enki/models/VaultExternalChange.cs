namespace enki.Models;

/// <summary>Qué le pasó a una nota vigilada fuera de Enki.</summary>
public enum VaultExternalChangeKind
{
    /// <summary>El archivo sigue donde estaba pero su contenido ya no es el que Enki leyó por última vez.</summary>
    Modified,

    /// <summary>El archivo ya no existe en su ruta (borrado, o movido a un sitio que el vigilante no cubre).</summary>
    Deleted,

    /// <summary>El archivo se renombró o se movió dentro del vault y se sabe a qué ruta —
    /// <see cref="VaultExternalChange.NewFullPath"/>.</summary>
    Renamed
}

/// <summary>Un cambio hecho sobre una nota abierta desde fuera de Enki, tal y como lo publica
/// <see cref="Services.VaultChangeWatcher.ExternalChangeDetected"/>.</summary>
/// <param name="FullPath">Ruta de la nota vigilada a la que se refiere el cambio (la <em>anterior</em>
/// cuando <paramref name="Kind"/> es <see cref="VaultExternalChangeKind.Renamed"/>).</param>
/// <param name="Kind">Qué clase de cambio fue.</param>
/// <param name="NewFullPath">Ruta nueva, solo para <see cref="VaultExternalChangeKind.Renamed"/>.</param>
public sealed record VaultExternalChange(
    string FullPath,
    VaultExternalChangeKind Kind,
    string? NewFullPath = null);
