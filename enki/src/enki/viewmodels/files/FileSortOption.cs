namespace enki.ViewModels;

/// <summary>Cómo ordena la rejilla de Archivos los archivos de la carpeta actual. Las carpetas van siempre
/// delante y por nombre, sea cual sea el orden elegido. Se guarda como su nombre en
/// <see cref="Models.AppSettings.FilesSortMode"/>.</summary>
public enum FileSortMode
{
    /// <summary>Más recientes primero — el orden de siempre de la sección.</summary>
    Modified,
    Name,
    /// <summary>Más grandes primero.</summary>
    Size,
    /// <summary>Agrupados por tipo y, dentro de cada tipo, por extensión y nombre.</summary>
    Type
}

/// <summary>Una opción del desplegable de orden de la rejilla de Archivos.</summary>
public sealed class FileSortOption(FileSortMode mode, string label)
{
    public FileSortMode Mode { get; } = mode;

    public string Label { get; } = label;

    public override string ToString() => Label;
}
