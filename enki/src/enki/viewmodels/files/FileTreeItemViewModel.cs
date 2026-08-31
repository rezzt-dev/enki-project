using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Un nodo del árbol del modo "árbol" de la sección Archivos: o una carpeta (con hijos) o un
/// archivo (hoja). Es el equivalente de <see cref="SkillTreeItemViewModel"/> en Skills y de
/// <see cref="NoteTreeItemViewModel"/> en la vista simplificada de Notas, y existe por el mismo motivo por
/// el que ninguno de los dos reutiliza el viewmodel de fila de carpeta: el panel del modo explorador
/// (<see cref="FileFolderViewModel"/>) solo tiene carpetas, y éste entremezcla carpetas y archivos en una
/// misma lista de hijos, que es justo lo que hace navegable un árbol al estilo Obsidian.
///
/// Los datos siguen siendo las entidades reales, no copias: una carpeta envuelve el mismo
/// <see cref="NoteFolder"/> que comparten Notas y Archivos, y un archivo envuelve un
/// <see cref="FileCardViewModel"/> —el mismo tipo de tarjeta que abre la rejilla del explorador— de forma
/// que abrir, renombrar o borrar desde el árbol opera sobre la misma clase de objeto y por los mismos
/// comandos. Lo único que aporta este nodo es estado de UI: expandido, seleccionado, renombrando y
/// destino de arrastre.</summary>
public sealed partial class FileTreeItemViewModel : ObservableObject
{
    /// <summary>La carpeta que representa este nodo, o null si es un archivo.</summary>
    public NoteFolder? Folder { get; private set; }

    /// <summary>El archivo que representa este nodo, o null si es una carpeta.</summary>
    public FileCardViewModel? Card { get; private set; }

    public bool IsFolder => Folder is not null;
    public bool IsFile => Card is not null;

    public int Depth { get; }

    public string FullPath => Folder?.FullPath ?? Card!.File.FullPath;

    /// <summary>Carpeta en la que se crea/suelta algo al actuar sobre este nodo: la propia carpeta si lo
    /// es, o la carpeta que contiene el archivo si es una hoja.</summary>
    public string ContainerPath { get; private set; } = string.Empty;

    public ObservableCollection<FileTreeItemViewModel> Children { get; } = [];

    /// <summary>Una carpeta vacía sigue siendo expandible visualmente sin sentido, así que la flechita
    /// solo se dibuja cuando hay algo debajo — mismo criterio que los árboles de Notas y Skills.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Icono por tipo de archivo, reutilizando el que ya resuelve la tarjeta de la rejilla desde
    /// <see cref="VaultFileKind"/> — el árbol no carga miniaturas (ver <c>FilesViewModel.Tree.cs</c>), así
    /// que este icono es lo único que distingue un PDF de un vídeo de un vistazo.</summary>
    public MaterialIconKind IconKind => Card?.IconKind ?? MaterialIconKind.FileOutline;

    /// <summary>Carpeta en la que vive este archivo, relativa a la raíz efectiva. Solo la rellenan los
    /// nodos de los resultados de búsqueda del panel: en el árbol la ubicación ya la dice la sangría, pero
    /// en una lista plana de resultados es la primera pregunta que se hace sobre cada uno.</summary>
    public string LocationLabel { get; private init; } = string.Empty;

    public bool HasLocation => LocationLabel.Length > 0;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Este archivo es el que se está previsualizando — lo pinta la clase <c>active</c> de
    /// <c>Border.folder-row</c>. Solo lo llevan los archivos (ver <c>FilesViewModel.SyncTreeSelection</c>):
    /// una carpeta no se "selecciona", se abre o se cierra, y resaltar también la que contiene al archivo
    /// abierto competiría visualmente con él.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>Callback que dispara <see cref="FilesViewModel"/> al expandir/contraer, para persistir qué
    /// carpetas quedan abiertas (<see cref="AppSettings.FilesTreeExpandedFolderPaths"/>).</summary>
    internal Action? OnExpandedChanged { get; set; }

    private FileTreeItemViewModel(int depth)
    {
        Depth = depth;
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
    }

    public static FileTreeItemViewModel ForFolder(NoteFolder folder, int depth) =>
        new(depth) { Folder = folder, Name = folder.Name, ContainerPath = folder.FullPath };

    public static FileTreeItemViewModel ForFile(FileCardViewModel card, string containerPath, int depth,
        string locationLabel = "") =>
        new(depth) { Card = card, Name = card.FileName, ContainerPath = containerPath, LocationLabel = locationLabel };

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged?.Invoke();

    [RelayCommand]
    private void ToggleExpand()
    {
        // Ignorado mientras se renombra la carpeta: el botón de la flecha comparte fila con el cuadro de
        // edición, y plegarla bajo el cursor a mitad de escribir el nombre es justo lo que hay que evitar
        // (mismo criterio que SkillTreeItemViewModel.ToggleExpand).
        if (IsFolder && !IsRenaming) IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditingName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
