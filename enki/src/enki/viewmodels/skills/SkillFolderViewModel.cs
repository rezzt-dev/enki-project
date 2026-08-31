using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una fila del panel de carpetas del modo explorador de la sección Skills — mismo papel que
/// <see cref="FileFolderViewModel"/> en Archivos, sobre <see cref="SkillFolder"/> en vez de
/// <see cref="NoteFolder"/> porque el árbol que navega es el de la biblioteca de skills, no el del vault.
/// El modelo se guarda tal cual (no se copia a campos sueltos): este viewmodel solo añade el estado de UI
/// —expandida, seleccionada, renombrando, destino de arrastre— que la fila necesita.</summary>
public sealed partial class SkillFolderViewModel : ObservableObject
{
    public SkillFolder Folder { get; private set; }
    public int Depth { get; }

    public string FullPath => Folder.FullPath;
    public string Name => Folder.Name;

    /// <summary>Skills de esta carpeta y sus subcarpetas — el número que la fila muestra a la derecha,
    /// para que una carpeta se pueda evaluar sin abrirla.</summary>
    public int TotalSkillCount => Folder.TotalSkillCount;

    public ObservableCollection<SkillFolderViewModel> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>Callback que dispara <see cref="SkillsViewModel"/> al expandir/contraer, para persistir
    /// qué carpetas quedan abiertas (<see cref="AppSettings.SkillsExpandedFolderPaths"/>).</summary>
    internal Action? OnExpandedChanged { get; set; }

    public SkillFolderViewModel(SkillFolder folder, int depth = 0)
    {
        Folder = folder;
        Depth = depth;
        foreach (var child in folder.Children) Children.Add(new SkillFolderViewModel(child, depth + 1));
    }

    public void UpdateFolder(SkillFolder folder)
    {
        Folder = folder;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(TotalSkillCount));
    }

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged?.Invoke();

    [RelayCommand]
    private void ToggleExpand()
    {
        if (!IsRenaming) IsExpanded = !IsExpanded;
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
