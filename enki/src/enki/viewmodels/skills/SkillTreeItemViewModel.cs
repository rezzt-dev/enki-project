using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Un nodo del árbol del modo "árbol" de la sección Skills: o una carpeta (con hijos) o una
/// skill (hoja). Es el equivalente de <see cref="NoteTreeItemViewModel"/> en la vista simplificada de
/// Notas, y existe por el mismo motivo por el que aquél no reutiliza el viewmodel de fila de carpeta: ese
/// otro árbol solo tiene carpetas, y éste entremezcla carpetas y skills en una misma lista de hijos, que
/// es justo lo que hace navegable un árbol al estilo Obsidian.
///
/// Los datos siguen siendo las entidades reales (<see cref="SkillFolder"/> / <see cref="SkillFile"/>), no
/// copias. Para una skill, además, el nodo envuelve el mismo <see cref="SkillViewModel"/> que usa la
/// rejilla del modo explorador, de forma que seleccionar, editar o exportar desde el árbol opera sobre la
/// misma instancia y no sobre un duplicado que se desincronizaría.</summary>
public sealed partial class SkillTreeItemViewModel : ObservableObject
{
    /// <summary>La carpeta que representa este nodo, o null si es una skill.</summary>
    public SkillFolder? Folder { get; private set; }

    /// <summary>La skill que representa este nodo, o null si es una carpeta.</summary>
    public SkillViewModel? Skill { get; private set; }

    public bool IsFolder => Folder is not null;
    public bool IsSkill => Skill is not null;

    public int Depth { get; }

    public string FullPath => Folder?.FullPath ?? Skill!.Model.FilePath;

    /// <summary>Carpeta en la que se crea/suelta algo al actuar sobre este nodo: la propia carpeta si lo
    /// es, o la carpeta que contiene la skill si es una hoja.</summary>
    public string ContainerPath => Folder?.FullPath ?? Skill!.Model.FolderPath;

    public ObservableCollection<SkillTreeItemViewModel> Children { get; } = [];

    /// <summary>Una carpeta vacía sigue siendo expandible visualmente sin sentido, así que la flechita
    /// solo se dibuja cuando hay algo debajo — mismo criterio que el árbol de la vista simplificada de
    /// Notas.</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>Emoji del frontmatter de la skill, si lo tiene — el árbol lo usa en lugar del icono
    /// genérico de documento, que es lo que distingue de un vistazo una biblioteca de skills de una lista
    /// de archivos cualquiera.</summary>
    public string? Emoji => Skill?.Emoji;
    public bool HasEmoji => !string.IsNullOrWhiteSpace(Emoji);

    public bool IsDefault => Skill?.IsDefault ?? false;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Para una skill: es la seleccionada. Para una carpeta: contiene la seleccionada, o el
    /// usuario acaba de pulsar sobre ella. Lo pinta la clase <c>active</c> de <c>Border.folder-row</c>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>Callback que dispara <see cref="SkillsViewModel"/> al expandir/contraer, para persistir
    /// qué carpetas quedan abiertas (<see cref="AppSettings.SkillsTreeExpandedFolderPaths"/>).</summary>
    internal Action? OnExpandedChanged { get; set; }

    private SkillTreeItemViewModel(int depth)
    {
        Depth = depth;
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
    }

    public static SkillTreeItemViewModel ForFolder(SkillFolder folder, int depth) =>
        new(depth) { Folder = folder, Name = folder.Name };

    public static SkillTreeItemViewModel ForSkill(SkillViewModel skill, int depth) =>
        new(depth) { Skill = skill, Name = skill.Name };

    public void UpdateFolder(SkillFolder folder)
    {
        Folder = folder;
        Name = folder.Name;
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(ContainerPath));
    }

    /// <summary>Refleja en el nodo un cambio del modelo subyacente (p. ej. tras guardar en el editor, que
    /// puede haber cambiado el <c>name</c> del frontmatter).</summary>
    public void RefreshFromSkill()
    {
        if (Skill is null) return;
        Name = Skill.Name;
        OnPropertyChanged(nameof(Emoji));
        OnPropertyChanged(nameof(HasEmoji));
    }

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged?.Invoke();

    [RelayCommand]
    private void ToggleExpand()
    {
        // Ignorado mientras se renombra la carpeta: el botón de la flecha comparte fila con el cuadro de
        // edición, y plegarla bajo el cursor a mitad de escribir el nombre es justo lo que hay que evitar
        // (mismo criterio que NoteTreeItemViewModel.ToggleExpand).
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
