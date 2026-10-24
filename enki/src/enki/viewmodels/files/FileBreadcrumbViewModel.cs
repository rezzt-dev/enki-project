namespace enki.ViewModels;

/// <summary>Un tramo de la miga de pan de la rejilla de Archivos: la raíz efectiva (el vault o la sesión elegida)
/// o una de las carpetas que llevan hasta la carpeta actual. Pulsar cualquiera que no sea el último navega hasta
/// ella, que es lo que la tarjeta ".." no resuelve cuando se está varios niveles por debajo.</summary>
public sealed class FileBreadcrumbViewModel(string name, string? fullPath, bool isLast)
{
    public string Name { get; } = name;

    /// <summary>Ruta de la carpeta, o null para la raíz efectiva.</summary>
    public string? FullPath { get; } = fullPath;

    public bool IsRoot => FullPath is null;

    /// <summary>La carpeta en la que se está: se pinta resaltada y no navega a ningún sitio.</summary>
    public bool IsLast { get; } = isLast;

    /// <summary>Todos menos el primero llevan delante el separador "›".</summary>
    public bool ShowsSeparator => !IsRoot;
}
