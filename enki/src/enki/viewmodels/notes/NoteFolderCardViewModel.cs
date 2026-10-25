using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Representa una carpeta dentro de la rejilla de navegación de Notas,
/// mostrada junto a las tarjetas de nota para poder entrar/salir de carpetas sin
/// depender exclusivamente del árbol lateral.</summary>
public sealed partial class NoteFolderCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isCut;

    public string Name { get; }
    public string FullPath { get; }

    /// <summary>True para la entrada especial ".." que permite subir al padre.</summary>
    public bool IsParent { get; }

    [ObservableProperty] private bool _isDropTarget;

    public NoteFolderCardViewModel(string name, string fullPath, bool isParent = false)
    {
        Name = name;
        FullPath = fullPath;
        IsParent = isParent;
    }
}
