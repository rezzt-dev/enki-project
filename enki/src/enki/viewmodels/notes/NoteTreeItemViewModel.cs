using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Un nodo del árbol de la vista simplificada de Notas: o una carpeta (con hijos) o una nota
/// markdown (hoja). Deliberadamente es un tipo aparte de <see cref="NoteFolderViewModel"/> y
/// <see cref="NoteCardViewModel"/> en vez de reutilizarlos: aquel árbol solo tiene carpetas y este
/// entremezcla ambas cosas en una misma lista de hijos, que es justo lo que hace navegable un árbol al
/// estilo Obsidian. Los datos siguen siendo las entidades reales (<see cref="Models.NoteFolder"/> /
/// <see cref="Models.NoteFile"/>), no copias — este viewmodel solo aporta el estado de UI
/// (expandido, seleccionado, renombrando) que el árbol necesita.</summary>
public sealed partial class NoteTreeItemViewModel : ObservableObject
{
    /// <summary>La carpeta que representa este nodo, o null si es una nota.</summary>
    public NoteFolder? Folder { get; private set; }

    /// <summary>La nota que representa este nodo, o null si es una carpeta.</summary>
    public NoteFile? Note { get; private set; }

    public bool IsFolder => Folder is not null;
    public bool IsNote => Note is not null;

    public int Depth { get; }

    public string FullPath => Folder?.FullPath ?? Note!.FullPath;

    public ObservableCollection<NoteTreeItemViewModel> Children { get; } = [];

    /// <summary>Una carpeta vacía sigue siendo expandible visualmente sin sentido, así que la flechita
    /// solo se dibuja cuando hay algo debajo — mismo criterio que el panel de carpetas de la vista
    /// completa (ver <c>Children.Count</c> en NotesView.axaml).</summary>
    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Para una nota: es la pestaña activa. Para una carpeta: contiene la nota activa, o el
    /// usuario acaba de pulsar sobre ella. Lo pinta la clase <c>active</c> de <c>Border.tree-row</c>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>Callback que dispara <see cref="NotesViewModel"/> al expandir/contraer, para persistir
    /// qué carpetas quedan abiertas (<see cref="Models.AppSettings.NotesSimpleExpandedFolderPaths"/>).</summary>
    internal Action? OnExpandedChanged { get; set; }

    private NoteTreeItemViewModel(int depth)
    {
        Depth = depth;
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
    }

    public static NoteTreeItemViewModel ForFolder(NoteFolder folder, int depth) =>
        new(depth) { Folder = folder, Name = folder.Name };

    public static NoteTreeItemViewModel ForNote(NoteFile note, int depth) =>
        new(depth) { Note = note, Name = note.FileNameWithoutExtension };

    public void UpdateFolder(NoteFolder folder)
    {
        Folder = folder;
        Name = folder.Name;
        OnPropertyChanged(nameof(FullPath));
    }

    public void UpdateNote(NoteFile note)
    {
        Note = note;
        Name = note.FileNameWithoutExtension;
        OnPropertyChanged(nameof(FullPath));
    }

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged?.Invoke();

    [RelayCommand]
    private void ToggleExpand()
    {
        // Ignorado mientras se renombra la carpeta: el botón de la flecha comparte fila con el cuadro
        // de edición, y plegarla bajo el cursor a mitad de escribir el nombre es justo lo que la vista
        // simplificada quiere evitar (ver SimpleNotesView.TreeRow_PointerPressed).
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
