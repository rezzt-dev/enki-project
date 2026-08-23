using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Representa una carpeta dentro de la rejilla de navegación de Archivos,
/// mostrada junto a las tarjetas de archivo para poder entrar/salir de carpetas sin
/// depender exclusivamente del árbol lateral.</summary>
public sealed partial class FileFolderCardViewModel : ObservableObject
{
    public string Name { get; }
    public string FullPath { get; }

    /// <summary>True para la entrada especial ".." que permite subir al padre.</summary>
    public bool IsParent { get; }

    public FileFolderCardViewModel(string name, string fullPath, bool isParent = false)
    {
        Name = name;
        FullPath = fullPath;
        IsParent = isParent;
    }
}
