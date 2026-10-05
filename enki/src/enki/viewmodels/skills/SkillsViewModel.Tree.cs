using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Modo "árbol" de la sección Skills: el panel lateral de la vista simplificada de Notas,
/// adaptado a una biblioteca de skills. Carpetas y skills se entremezclan en una misma lista jerárquica,
/// y el panel derecho enseña el detalle de la seleccionada o su editor.
///
/// No hay un segundo modelo de datos detrás: es el mismo <see cref="SkillsViewModel"/>, las mismas
/// instancias de <see cref="SkillViewModel"/> que usa la rejilla del modo explorador y los mismos
/// comandos de crear/duplicar/eliminar/importar. Lo único propio de este modo es la forma del árbol, su
/// búsqueda y el estado del panel lateral — por eso vive en un parcial aparte y no en un viewmodel nuevo,
/// igual que <c>NotesViewModel.SimpleView.cs</c>.</summary>
public sealed partial class SkillsViewModel
{
    public const double TreeSidebarMinWidth = 200;
    public const double TreeSidebarMaxWidth = 520;

    /// <summary>Raíces del árbol: las carpetas de primer nivel y, tras ellas, las skills sueltas de la
    /// raíz de la biblioteca.</summary>
    public ObservableCollection<SkillTreeItemViewModel> SkillTreeRoots { get; } = [];

    /// <summary>Resultados del modo búsqueda del panel lateral — una lista plana, porque un resultado de
    /// búsqueda no se navega, se abre.</summary>
    public ObservableCollection<SkillViewModel> TreeSearchResults { get; } = [];

    [ObservableProperty]
    private bool _hasTreeItems;

    /// <summary>Qué disposición dibuja la sección. Se elige con el conmutador de la barra de herramientas
    /// y se guarda como el string "Tree"/"Browser" en <see cref="AppSettings.SkillsViewMode"/> —mismo
    /// criterio que <see cref="AppSettings.NotesViewMode"/> de no acoplar <c>Models</c> a un enum de UI.</summary>
    [ObservableProperty]
    private bool _isTreeView;

    [ObservableProperty]
    private bool _isTreeSearchMode;

    [ObservableProperty]
    private string _treeSearchText = string.Empty;

    [ObservableProperty]
    private bool _isTreeSidebarVisible = true;

    [ObservableProperty]
    private double _treeSidebarWidth = 272;

    /// <summary>Sin resultados y con algo escrito — el panel no debe anunciar "sin resultados" mientras el
    /// cuadro está vacío y todavía no se ha buscado nada.</summary>
    public bool ShowTreeSearchEmptyState => IsTreeSearchMode && TreeSearchResults.Count == 0 && TreeSearchText.Trim().Length > 0;

    /// <summary>Silencia el guardado en Ajustes mientras se leen de allí los valores iniciales — si no,
    /// cada asignación del arranque escribiría de vuelta en el JSON lo que se acaba de leer de él.</summary>
    private bool _loadingNavigationPreferences;

    /// <summary>Lee de Ajustes el modo y el estado del panel lateral. Se llama desde el constructor antes
    /// del primer <see cref="Reload"/> para que la sección aparezca ya en la disposición que el usuario
    /// dejó, sin un parpadeo intermedio en la otra.</summary>
    private void InitializeNavigationPreferences()
    {
        if (_settingsService is null) return;

        _loadingNavigationPreferences = true;
        try
        {
            var settings = _settingsService.Settings;
            IsTreeView = settings.SkillsViewMode == "Tree";
            IsTreeSidebarVisible = settings.SkillsTreeSidebarVisible;
            TreeSidebarWidth = Math.Clamp(settings.SkillsTreeSidebarWidth, TreeSidebarMinWidth, TreeSidebarMaxWidth);
        }
        finally
        {
            _loadingNavigationPreferences = false;
        }
    }

    partial void OnIsTreeViewChanged(bool value)
    {
        if (_settingsService is null || _loadingNavigationPreferences) return;
        _settingsService.Settings.SkillsViewMode = value ? "Tree" : "Browser";
        _settingsService.Save();
    }

    partial void OnIsTreeSidebarVisibleChanged(bool value)
    {
        if (_settingsService is null || _loadingNavigationPreferences) return;
        _settingsService.Settings.SkillsTreeSidebarVisible = value;
        _settingsService.Save();
    }

    partial void OnTreeSearchTextChanged(string value) => ApplyTreeSearch();

    partial void OnIsTreeSearchModeChanged(bool value)
    {
        if (!value) TreeSearchText = string.Empty;
        OnPropertyChanged(nameof(ShowTreeSearchEmptyState));
    }

    [RelayCommand]
    private void ShowBrowserView() => IsTreeView = false;

    [RelayCommand]
    private void ShowTreeView() => IsTreeView = true;

    [RelayCommand]
    private void ShowTreeBrowse() => IsTreeSearchMode = false;

    [RelayCommand]
    private void ShowTreeSearch() => IsTreeSearchMode = true;

    [RelayCommand]
    private void ToggleTreeSidebar() => IsTreeSidebarVisible = !IsTreeSidebarVisible;

    /// <summary>Persiste el ancho del panel al soltar el separador, no en cada píxel del arrastre — mismo
    /// criterio que el panel de la vista simplificada de Notas.</summary>
    public void PersistTreeSidebarWidth()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.SkillsTreeSidebarWidth = Math.Clamp(TreeSidebarWidth, TreeSidebarMinWidth, TreeSidebarMaxWidth);
        _settingsService.Save();
    }

    // ===================== Construcción del árbol =====================

    /// <summary>Reconstruye el árbol desde <see cref="RootFolders"/> y la biblioteca completa, conservando
    /// qué carpetas estaban abiertas. Se llama al final de cada <see cref="Reload"/> y no desde el filtro
    /// de la rejilla: el árbol es el navegador de toda la biblioteca, así que ni la búsqueda de la rejilla
    /// ni la carpeta actual deben podarlo.</summary>
    private void RebuildTree()
    {
        var expandedPaths = SkillTreeRoots.Count > 0
            ? CollectExpandedTreePaths(SkillTreeRoots).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_settingsService?.Settings.SkillsTreeExpandedFolderPaths ?? [], StringComparer.OrdinalIgnoreCase);

        var skillsByFolder = _allSkills
            .GroupBy(s => s.Model.FolderPath.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        SkillTreeRoots.Clear();
        foreach (var node in BuildTreeNodes(RootFolders, 0, skillsByFolder, expandedPaths))
            SkillTreeRoots.Add(node);
        foreach (var skill in SkillsIn(skillsByFolder, _skillService.SkillsDirectory))
            SkillTreeRoots.Add(SkillTreeItemViewModel.ForSkill(skill, 0));

        HasTreeItems = SkillTreeRoots.Count > 0;
        ApplyTreeSearch();
        SyncTreeSelection();
    }

    private IEnumerable<SkillTreeItemViewModel> BuildTreeNodes(IEnumerable<SkillFolderViewModel> folders, int depth,
        Dictionary<string, List<SkillViewModel>> skillsByFolder, HashSet<string> expandedPaths)
    {
        foreach (var folder in folders)
        {
            var node = SkillTreeItemViewModel.ForFolder(folder.Folder, depth);
            node.IsExpanded = expandedPaths.Contains(folder.FullPath);
            node.OnExpandedChanged = PersistTreeExpandedFolders;

            // Subcarpetas primero y skills después, para que la jerarquía se lea de un vistazo en vez de
            // quedar las carpetas dispersas entre hojas — mismo orden que el árbol de Notas.
            foreach (var child in BuildTreeNodes(folder.Children, depth + 1, skillsByFolder, expandedPaths))
                node.Children.Add(child);
            foreach (var skill in SkillsIn(skillsByFolder, folder.FullPath))
                node.Children.Add(SkillTreeItemViewModel.ForSkill(skill, depth + 1));

            yield return node;
        }
    }

    private static List<SkillViewModel> SkillsIn(Dictionary<string, List<SkillViewModel>> skillsByFolder, string folderPath) =>
        skillsByFolder.TryGetValue(folderPath.TrimEnd(Path.DirectorySeparatorChar), out var skills) ? skills : [];

    private static IEnumerable<string> CollectExpandedTreePaths(IEnumerable<SkillTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder && node.IsExpanded) yield return node.FullPath;
            foreach (var path in CollectExpandedTreePaths(node.Children)) yield return path;
        }
    }

    private void PersistTreeExpandedFolders()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.SkillsTreeExpandedFolderPaths = CollectExpandedTreePaths(SkillTreeRoots).ToList();
        _settingsService.Save();
    }

    private static IEnumerable<SkillTreeItemViewModel> Flatten(IEnumerable<SkillTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>Marca en el árbol la skill seleccionada y abre las carpetas que la contienen, para que
    /// seleccionarla desde cualquier otro sitio (la rejilla, un resultado de búsqueda, abrir su editor) la
    /// deje visible y resaltada aquí también.</summary>
    private void SyncTreeSelection()
    {
        var selectedPath = SelectedSkill?.FilePath;
        foreach (var node in Flatten(SkillTreeRoots))
            node.IsSelected = node.IsSkill && selectedPath is not null && PathsEqual(node.FullPath, selectedPath);

        if (selectedPath is not null) ExpandTreeAncestorsOf(selectedPath);
    }

    private void RefreshTreeNodeFor(SkillViewModel skill)
    {
        foreach (var node in Flatten(SkillTreeRoots))
        {
            if (node.Skill == skill) node.RefreshFromSkill();
        }
    }

    /// <summary>Abre en el árbol las carpetas que llevan hasta <paramref name="path"/>. Complemento de
    /// <c>ExpandAncestorsOf</c>, que hace lo propio en el panel del modo explorador — los dos árboles
    /// guardan su estado por separado, así que expandir en uno no debe reorganizar el otro.</summary>
    private void ExpandTreeAncestorsOf(string path)
    {
        var root = _skillService.SkillsDirectory;
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && !PathsEqual(current, root))
        {
            ancestors.Add(current);
            current = Path.GetDirectoryName(current);
        }
        if (ancestors.Count == 0) return;

        foreach (var node in Flatten(SkillTreeRoots))
        {
            if (node.IsFolder && ancestors.Contains(node.FullPath)) node.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseTree()
    {
        foreach (var node in Flatten(SkillTreeRoots)) node.IsExpanded = false;
    }

    // ===================== Búsqueda del panel =====================

    private void ApplyTreeSearch()
    {
        TreeSearchResults.Clear();

        var query = TreeSearchText.Trim();
        if (query.Length > 0)
        {
            var matches = _allSkills.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                             || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                                             || s.RelativeFolderPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                                             || s.Model.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
            foreach (var skill in matches) TreeSearchResults.Add(skill);
        }

        OnPropertyChanged(nameof(ShowTreeSearchEmptyState));
    }

    // ===================== Acciones sobre el árbol =====================

    /// <summary>Pulsar un nodo: una skill se selecciona (y su detalle aparece a la derecha), una carpeta
    /// se pliega o despliega. Deliberadamente no abre el editor con un solo clic — igual que la rejilla,
    /// donde seleccionar y editar son dos gestos distintos.</summary>
    [RelayCommand]
    private void SelectTreeItem(SkillTreeItemViewModel? node)
    {
        if (node is null) return;

        if (node.IsSkill)
        {
            SelectSkill(node.Skill);
            return;
        }

        CurrentFolder = FindFolder(RootFolders, node.FullPath);
        node.ToggleExpandCommand.Execute(null);
    }

    /// <summary>Doble clic sobre una skill del árbol: abre su editor, el mismo atajo que el botón de
    /// editar de la tarjeta.</summary>
    [RelayCommand]
    private void OpenTreeItem(SkillTreeItemViewModel? node)
    {
        if (node?.Skill is { } skill) EditSkillCommand.Execute(skill);
    }

    [RelayCommand]
    private void NewTreeFolderAtRoot() => CreateFolderInTree(_skillService.SkillsDirectory);

    [RelayCommand]
    private void CreateTreeSubfolder(SkillTreeItemViewModel? node)
    {
        if (node is null) return;
        CreateFolderInTree(node.ContainerPath);
    }

    private void CreateFolderInTree(string parentPath)
    {
        try
        {
            var created = _skillService.CreateFolder(parentPath);
            ExpandTreeAncestorsOf(created.FullPath);

            var vm = Flatten(SkillTreeRoots).FirstOrDefault(n => n.IsFolder && PathsEqual(n.FullPath, created.FullPath));
            if (vm is null) return;
            vm.EditingName = vm.Name;
            vm.IsRenaming = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_FolderCreateFailed_Title", ex);
        }
    }

    /// <summary>Confirma el renombrado en línea de una carpeta del árbol. Solo las carpetas se renombran
    /// aquí: el nombre de una skill sale de su frontmatter, así que se cambia en su editor y no en el
    /// árbol, que si no dejaría el nombre de archivo y el <c>name</c> del YAML contándose cosas distintas.</summary>
    [RelayCommand]
    private void CommitRenameTreeItem(SkillTreeItemViewModel? node)
    {
        if (node?.Folder is null)
        {
            if (node is not null) node.IsRenaming = false;
            return;
        }

        if (!node.IsRenaming) return;
        node.IsRenaming = false;
        if (!PrepareLibraryMutation()) return;
        var newName = node.EditingName.Trim();
        if (newName.Length > 0 && newName != node.Name)
        {
            try
            {
                _skillService.RenameFolder(node.Folder, newName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NotifyFolderFailure("Skills_Notify_FolderRenameFailed_Title", ex);
            }
        }

        node.IsRenaming = false;
        Reload();
    }

    [RelayCommand]
    private void DeleteTreeItem(SkillTreeItemViewModel? node)
    {
        if (node is null) return;

        if (node.Skill is { } skill)
        {
            RequestDeleteSkillCommand.Execute(skill);
            return;
        }

        var folder = FindFolder(RootFolders, node.FullPath);
        if (folder is not null) RequestDeleteFolderCommand.Execute(folder);
    }

    /// <summary>Soltar una skill sobre un nodo del árbol la mueve a esa carpeta (o a la carpeta que
    /// contiene la skill sobre la que se soltó, que es lo que el usuario ve como "aquí").</summary>
    public void DropOnTreeNode(SkillTreeItemViewModel target, string sourceFilePath)
    {
        var skill = _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, sourceFilePath));
        if (skill is not null) MoveSkillToFolder(skill, target.ContainerPath);
    }

    /// <summary>Soltar sobre el fondo vacío del árbol saca la skill de su carpeta y la devuelve a la raíz
    /// de la biblioteca — la única forma de deshacer una organización sin abrir el panel del otro modo.</summary>
    public void DropOnTreeRoot(string sourceFilePath)
    {
        var skill = _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, sourceFilePath));
        if (skill is not null) MoveSkillToFolder(skill, _skillService.SkillsDirectory);
    }

    /// <summary>Busca una skill por su ruta — lo usan los <c>Drop</c> de las dos vistas, que solo pueden
    /// transportar una cadena en el <c>DataTransfer</c>.</summary>
    public SkillViewModel? FindSkillByPath(string filePath) =>
        _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, filePath));

    /// <summary>Raíz de la biblioteca, para que las vistas puedan usarla como destino de "soltar aquí"
    /// sin conocer <see cref="SkillService"/>.</summary>
    public string LibraryRootPath => _skillService.SkillsDirectory;
}
