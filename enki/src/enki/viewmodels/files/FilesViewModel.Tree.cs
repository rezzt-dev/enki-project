using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Modo "árbol" de la sección Archivos: el panel lateral de la vista simplificada de Notas,
/// adaptado a los adjuntos del vault. Carpetas y archivos se entremezclan en una misma lista jerárquica a
/// la izquierda, y el panel derecho enseña la previsualización del archivo elegido.
///
/// No hay un segundo modelo de datos detrás: es el mismo <see cref="FilesViewModel"/>, las mismas
/// <see cref="FileCardViewModel"/> que abre la rejilla del modo explorador y los mismos comandos de
/// importar/renombrar/mover/borrar. Lo único propio de este modo es la forma del árbol, su búsqueda, su
/// selección con el teclado y el estado del panel lateral — por eso vive en un parcial aparte y no en un
/// viewmodel nuevo, igual que <c>SkillsViewModel.Tree.cs</c> y <c>NotesViewModel.SimpleView.cs</c>.
///
/// Dos cosas que Archivos tiene y Skills no, y que este árbol trata explícitamente:
/// <list type="bullet">
/// <item>El árbol cuelga de <see cref="EffectiveRootPath"/> —la sesión elegida—, no del vault entero, y
/// una sesión protegida y bloqueada no lista ni nombres de archivo, igual que la rejilla.</item>
/// <item>El filtro por tipo de archivo (<see cref="SelectedFilterOption"/>) y el orden
/// (<see cref="SelectedSortOption"/>) se quedan deliberadamente en el modo explorador: el árbol es el mapa de
/// toda la raíz efectiva, ordenado por nombre, y podarlo por extensión dejaría carpetas aparentemente vacías —
/// la jerarquía mentiría sobre lo que hay en disco. Mismo criterio que el filtro de etiquetas de Skills.</item>
/// </list></summary>
public sealed partial class FilesViewModel
{
    public const double TreeSidebarMinWidth = 200;
    public const double TreeSidebarMaxWidth = 520;

    /// <summary>Raíces del árbol: las carpetas de primer nivel de la raíz efectiva y, tras ellas, los
    /// archivos sueltos de esa misma raíz.</summary>
    public ObservableCollection<FileTreeItemViewModel> FileTreeRoots { get; } = [];

    /// <summary>Resultados del modo búsqueda del panel lateral — una lista plana, porque un resultado de
    /// búsqueda no se navega, se abre. Son nodos y no tarjetas para poder enseñar además en qué carpeta
    /// vive cada uno, que es la primera pregunta que se hace sobre un resultado.</summary>
    public ObservableCollection<FileTreeItemViewModel> TreeSearchResults { get; } = [];

    /// <summary>Todas las tarjetas montadas en el árbol, en el orden en que se leen de arriba abajo — fuente
    /// de la búsqueda del panel, del contador, de "anterior/siguiente" en la previsualización y de la búsqueda
    /// de una tarjeta por ruta al soltar un arrastre.</summary>
    private readonly List<FileCardViewModel> _treeCards = [];

    /// <summary>Ruta del nodo seleccionado en el árbol: el archivo abierto o la última carpeta pulsada.</summary>
    private string? _treeSelectionPath;

    [ObservableProperty]
    private bool _hasTreeItems;

    /// <summary>Qué disposición dibuja la sección. Se elige con el conmutador de la barra de herramientas
    /// (y de la barra de la previsualización) y se guarda como el string "Tree"/"Browser" en
    /// <see cref="AppSettings.FilesViewMode"/> —mismo criterio que <see cref="AppSettings.NotesViewMode"/>
    /// y <see cref="AppSettings.SkillsViewMode"/> de no acoplar <c>Models</c> a un enum de UI.</summary>
    [ObservableProperty]
    private bool _isTreeView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreePeekActive))]
    [NotifyPropertyChangedFor(nameof(IsSearchPeekActive))]
    private bool _isTreeSearchMode;

    [ObservableProperty]
    private string _treeSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeNavigatorVisible))]
    [NotifyPropertyChangedFor(nameof(TreeNavigatorColumn))]
    private bool _isTreeSidebarVisible = true;

    /// <summary>Navegación temporal sobre la previsualización, sin alterar el panel guardado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeNavigatorVisible))]
    [NotifyPropertyChangedFor(nameof(IsTreePeekActive))]
    [NotifyPropertyChangedFor(nameof(IsSearchPeekActive))]
    private bool _isTreeNavigatorOpen;

    [ObservableProperty]
    private bool _isTreeSessionMenuOpen;

    public bool IsTreeNavigatorVisible => IsTreeSidebarVisible || IsTreeNavigatorOpen;
    public int TreeNavigatorColumn => IsTreeSidebarVisible ? 0 : 2;
    public bool IsTreePeekActive => IsTreeNavigatorOpen && !IsTreeSearchMode;
    public bool IsSearchPeekActive => IsTreeNavigatorOpen && IsTreeSearchMode;

    [ObservableProperty]
    private double _treeSidebarWidth = 272;

    /// <summary>Sin resultados y con algo escrito — el panel no debe anunciar "sin resultados" mientras el
    /// cuadro está vacío y todavía no se ha buscado nada.</summary>
    public bool ShowTreeSearchEmptyState => IsTreeSearchMode && TreeSearchResults.Count == 0 && TreeSearchText.Trim().Length > 0;

    /// <summary>El árbol está vacío por serlo de verdad, no porque la sesión esté bloqueada — ésta última
    /// tiene su propio aviso, igual que en la rejilla del explorador.</summary>
    public bool ShowTreeEmptyState => !HasTreeItems && !IsSelectedSessionLocked;

    /// <summary>Cuántos archivos hay en todo el árbol. Antes el pie del panel enseñaba <see cref="FilesCountLabel"/>,
    /// que cuenta los de la carpeta abierta en el otro modo con su búsqueda y su filtro: un número que no tenía
    /// nada que ver con lo que se veía encima.</summary>
    public string TreeFilesCountLabel => Plural.Get("Files_CountLabel", _treeCards.Count, _treeCards.Count);

    /// <summary>Silencia el guardado en Ajustes mientras se leen de allí los valores iniciales — si no,
    /// cada asignación del arranque escribiría de vuelta en el JSON lo que se acaba de leer de él.</summary>
    private bool _loadingNavigationPreferences;

    /// <summary>Lee de Ajustes el modo y el estado del panel lateral. Se llama desde el constructor antes
    /// de la primera carga para que la sección aparezca ya en la disposición que el usuario dejó, sin un
    /// parpadeo intermedio en la otra.</summary>
    private void InitializeNavigationPreferences()
    {
        if (_settingsService is null) return;

        _loadingNavigationPreferences = true;
        try
        {
            var settings = _settingsService.Settings;
            IsTreeView = settings.FilesViewMode == "Tree";
            IsTreeSidebarVisible = settings.FilesTreeSidebarVisible;
            TreeSidebarWidth = Math.Clamp(settings.FilesTreeSidebarWidth, TreeSidebarMinWidth, TreeSidebarMaxWidth);
        }
        finally
        {
            _loadingNavigationPreferences = false;
        }
    }

    partial void OnIsTreeViewChanged(bool value)
    {
        CloseTreeNavigator();
        IsTreeSessionMenuOpen = false;
        if (_loadingNavigationPreferences) return;

        // Entrar en el modo árbol lo reconstruye: mientras está fuera de pantalla no se mantiene al día
        // (ver Reload), así que lo que se dejó atrás puede haber cambiado desde entonces —importar, mover
        // o borrar desde el explorador, por ejemplo—. Reconstruir no reinicia nada: conserva qué carpetas
        // estaban abiertas y reutiliza la tarjeta de la previsualización que hubiera abierta.
        if (value) RebuildTree();
        RefreshPreviewNavigation();
        UpdateCutAppearance();

        if (_settingsService is null) return;
        _settingsService.Settings.FilesViewMode = value ? "Tree" : "Browser";
        _settingsService.Save();
    }

    partial void OnIsTreeSidebarVisibleChanged(bool value)
    {
        CloseTreeNavigator();
        IsTreeSessionMenuOpen = false;
        if (_settingsService is null || _loadingNavigationPreferences) return;
        _settingsService.Settings.FilesTreeSidebarVisible = value;
        _settingsService.Save();
    }

    partial void OnTreeSearchTextChanged(string value) => ApplyTreeSearch();

    partial void OnIsTreeSearchModeChanged(bool value)
    {
        if (!value) TreeSearchText = string.Empty;
        OnPropertyChanged(nameof(ShowTreeSearchEmptyState));
        UpdateCutAppearance();
    }

    partial void OnHasTreeItemsChanged(bool value) => OnPropertyChanged(nameof(ShowTreeEmptyState));

    /// <summary>Mantiene marcado en el árbol el archivo que se está previsualizando, venga de donde venga
    /// la orden de abrirlo (el propio árbol, la rejilla del otro modo, un resultado de búsqueda o los botones
    /// de anterior/siguiente), y al día esos mismos botones.</summary>
    partial void OnActivePreviewChanging(FileCardViewModel? value)
    {
        if (!ReferenceEquals(ActivePreview, value)) ActivePreview?.MediaPreview?.Close();
    }

    partial void OnActivePreviewChanged(FileCardViewModel? value)
    {
        _treeSelectionPath = value?.File.FullPath;
        SyncTreeSelection();
        RefreshPreviewNavigation();
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

    [RelayCommand]
    private void ToggleTreePeek() => ToggleTreeNavigator(search: false);

    [RelayCommand]
    private void ToggleTreeSearchPeek() => ToggleTreeNavigator(search: true);

    private void ToggleTreeNavigator(bool search)
    {
        if (IsTreeNavigatorOpen && IsTreeSearchMode == search)
        {
            CloseTreeNavigator();
            return;
        }
        IsTreeSessionMenuOpen = false;
        IsTreeSearchMode = search;
        IsTreeNavigatorOpen = true;
    }

    [RelayCommand]
    private void CloseTreeNavigator() => IsTreeNavigatorOpen = false;

    [RelayCommand]
    private void ToggleTreeSessionMenu()
    {
        CloseTreeNavigator();
        IsTreeSessionMenuOpen = !IsTreeSessionMenuOpen;
    }

    [RelayCommand]
    private void SelectTreeSessionOption(SessionOptionViewModel option)
    {
        IsTreeSessionMenuOpen = false;
        SelectedSessionOption = option;
    }

    /// <summary>Persiste el ancho del panel al soltar el separador, no en cada píxel del arrastre — mismo
    /// criterio que el panel de la vista simplificada de Notas y el del árbol de Skills.</summary>
    public void PersistTreeSidebarWidth()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.FilesTreeSidebarWidth = Math.Clamp(TreeSidebarWidth, TreeSidebarMinWidth, TreeSidebarMaxWidth);
        _settingsService.Save();
    }

    // ===================== Construcción del árbol =====================

    /// <summary>Reconstruye el árbol desde <see cref="RootFolders"/> y los archivos de cada carpeta,
    /// conservando cuáles estaban abiertas. Se llama al final de cada <see cref="Reload"/>.
    ///
    /// Ni la búsqueda de la rejilla, ni el filtro por tipo, ni la carpeta actual lo podan: el árbol es el
    /// navegador de toda la raíz efectiva. <see cref="VaultService.GetFiles"/> enumera un solo nivel (y
    /// excluye los <c>.md</c>, que son cosa de "Documentos"), así que aquí se compone una llamada por
    /// carpeta del árbol ya construido en vez de inventar una enumeración recursiva paralela.</summary>
    private void RebuildTree()
    {
        var expandedPaths = (FileTreeRoots.Count > 0
                ? CollectExpandedTreePaths(FileTreeRoots)
                : _settingsService?.Settings.FilesTreeExpandedFolderPaths ?? [])
            .Select(RemapPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Mismo motivo que el bucle equivalente de Reload(): las copias temporales descifradas de los PDF
        // que sujetaban las tarjetas salientes se liberan aquí, o quedarían vivas hasta cerrar la app. La
        // que se está previsualizando se salva, porque se reutiliza tal cual en el árbol nuevo.
        foreach (var card in _treeCards)
        {
            if (!ReferenceEquals(card, ActivePreview)) card.PdfDocument?.Close();
        }
        _treeCards.Clear();
        FileTreeRoots.Clear();

        if (!IsSelectedSessionLocked)
        {
            foreach (var node in BuildTreeNodes(RootFolders, 0, expandedPaths)) FileTreeRoots.Add(node);
            foreach (var card in TreeCardsIn(EffectiveRootPath))
                FileTreeRoots.Add(FileTreeItemViewModel.ForFile(card, EffectiveRootPath, 0));
        }

        HasTreeItems = FileTreeRoots.Count > 0;
        ApplyTreeSearch();
        SyncTreeSelection();
        OnPropertyChanged(nameof(TreeFilesCountLabel));
        RefreshPreviewNavigation();

        if (_folderRemap is not null) PersistTreeExpandedFolders();
    }

    private IEnumerable<FileTreeItemViewModel> BuildTreeNodes(IEnumerable<FileFolderViewModel> folders, int depth,
        HashSet<string> expandedPaths)
    {
        foreach (var folder in folders)
        {
            var node = FileTreeItemViewModel.ForFolder(folder.Folder, depth);
            node.IsExpanded = expandedPaths.Contains(folder.FullPath);
            node.OnExpandedChanged = PersistTreeExpandedFolders;

            // Subcarpetas primero y archivos después, para que la jerarquía se lea de un vistazo en vez de
            // quedar las carpetas dispersas entre hojas — mismo orden que los árboles de Notas y Skills.
            foreach (var child in BuildTreeNodes(folder.Children, depth + 1, expandedPaths))
                node.Children.Add(child);
            foreach (var card in TreeCardsIn(folder.FullPath))
                node.Children.Add(FileTreeItemViewModel.ForFile(card, folder.FullPath, depth + 1));

            yield return node;
        }
    }

    /// <summary>Tarjetas de los archivos de una carpeta, en orden alfabético — el árbol es un mapa de
    /// dónde está cada cosa, así que se ordena por nombre y no por el orden elegido en la rejilla. La tarjeta
    /// que se está previsualizando se reutiliza tal cual en vez de recrearse, para que recargar (renombrar,
    /// mover, importar) no cierre lo que hay abierto. No se llama a <c>EnsureThumbnailLoaded</c>: el árbol
    /// enseña iconos por tipo, no miniaturas, así que decodificar imágenes o rasterizar PDFs aquí sería
    /// trabajo tirado.</summary>
    private IEnumerable<FileCardViewModel> TreeCardsIn(string folderPath)
    {
        foreach (var file in _vaultService.GetFiles(folderPath).OrderBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var card = ActivePreview is { } preview && PathsEqual(preview.File.FullPath, file.FullPath)
                ? preview
                : new FileCardViewModel(_vaultService, file);
            _treeCards.Add(card);
            yield return card;
        }
    }

    private static IEnumerable<string> CollectExpandedTreePaths(IEnumerable<FileTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder && node.IsExpanded) yield return node.FullPath;
            foreach (var path in CollectExpandedTreePaths(node.Children)) yield return path;
        }
    }

    /// <summary>Silencia el guardado por nodo mientras se pliega o despliega el árbol en bloque — ver
    /// <see cref="BatchExpandedFolderChange"/>.</summary>
    private bool _batchingExpandedFolders;

    private void PersistTreeExpandedFolders()
    {
        if (_settingsService is null || _batchingExpandedFolders) return;
        _settingsService.Settings.FilesTreeExpandedFolderPaths = CollectExpandedTreePaths(FileTreeRoots).ToList();
        _settingsService.Save();
    }

    /// <summary>Aplica un cambio de expansión que toca varias carpetas de una vez y guarda al final, una
    /// sola vez y solo si algo cambió de verdad. Cada nodo avisa por su cuenta al expandirse
    /// (<see cref="FileTreeItemViewModel.OnExpandedChanged"/>), así que sin esto plegar un árbol con veinte
    /// carpetas abiertas reescribiría el JSON de ajustes veinte veces seguidas.</summary>
    private void BatchExpandedFolderChange(Func<bool> change)
    {
        bool changed;
        _batchingExpandedFolders = true;
        try
        {
            changed = change();
        }
        finally
        {
            _batchingExpandedFolders = false;
        }

        if (changed) PersistTreeExpandedFolders();
    }

    private static IEnumerable<FileTreeItemViewModel> Flatten(IEnumerable<FileTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    /// <summary>Los nodos que se ven ahora mismo, en el orden en que se leen: no se baja a las carpetas
    /// plegadas. Es el recorrido de las flechas arriba/abajo.</summary>
    private static IEnumerable<FileTreeItemViewModel> VisibleTreeNodes(IEnumerable<FileTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (!node.IsFolder || !node.IsExpanded) continue;
            foreach (var child in VisibleTreeNodes(node.Children)) yield return child;
        }
    }

    /// <summary>Marca en el árbol el nodo seleccionado y abre las carpetas que lo contienen, para que abrir
    /// un archivo desde cualquier otro sitio lo deje visible y resaltado aquí también.</summary>
    private void SyncTreeSelection()
    {
        var selectedPath = _treeSelectionPath;
        foreach (var node in Flatten(FileTreeRoots).Concat(TreeSearchResults))
            node.IsSelected = selectedPath is not null && PathsEqual(node.FullPath, selectedPath);

        if (selectedPath is not null) ExpandTreeAncestorsOf(selectedPath);
    }

    /// <summary>Tras renombrar una carpeta, la selección del árbol sigue al nodo a su ruta nueva.</summary>
    private void RemapTreeSelection()
    {
        if (_treeSelectionPath is not null) _treeSelectionPath = RemapPath(_treeSelectionPath);
    }

    /// <summary>Abre en el árbol las carpetas que llevan hasta <paramref name="path"/>. El panel del modo
    /// explorador guarda su estado de expansión por separado, así que expandir aquí no reorganiza aquél.</summary>
    private void ExpandTreeAncestorsOf(string path)
    {
        var root = EffectiveRootPath;
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && !PathsEqual(current, root))
        {
            ancestors.Add(current);
            current = Path.GetDirectoryName(current);
        }
        if (ancestors.Count == 0) return;

        BatchExpandedFolderChange(() =>
        {
            var changed = false;
            foreach (var node in Flatten(FileTreeRoots))
            {
                if (!node.IsFolder || node.IsExpanded || !ancestors.Contains(node.FullPath)) continue;
                node.IsExpanded = true;
                changed = true;
            }
            return changed;
        });
    }

    [RelayCommand]
    private void CollapseTree() => BatchExpandedFolderChange(() =>
    {
        var changed = false;
        foreach (var node in Flatten(FileTreeRoots))
        {
            if (!node.IsExpanded) continue;
            node.IsExpanded = false;
            changed = true;
        }
        return changed;
    });

    // ===================== Búsqueda del panel =====================

    /// <summary>Búsqueda propia del panel, acotada a la raíz efectiva por construcción (solo recorre lo
    /// que hay montado en el árbol). Deliberadamente independiente de <see cref="SearchText"/> y de su
    /// debounce de 200 ms: aquélla recarga la rejilla desde disco, y ésta filtra en memoria lo ya
    /// enumerado, así que no necesita esperar a que el usuario deje de teclear.</summary>
    private void ApplyTreeSearch()
    {
        TreeSearchResults.Clear();

        var query = TreeSearchText.Trim();
        if (query.Length > 0 && !IsSelectedSessionLocked)
        {
            var matches = _treeCards
                .Where(card => card.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.FileName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var card in matches)
            {
                var containerPath = Path.GetDirectoryName(card.File.FullPath) ?? EffectiveRootPath;
                var result = FileTreeItemViewModel.ForFile(card, containerPath, 0, RelativeLocationOf(containerPath));
                result.IsSelected = _treeSelectionPath is not null && PathsEqual(card.File.FullPath, _treeSelectionPath);
                TreeSearchResults.Add(result);
            }
        }

        OnPropertyChanged(nameof(ShowTreeSearchEmptyState));
    }

    /// <summary>Carpeta de un resultado de búsqueda, relativa a la raíz efectiva — cadena vacía para los
    /// archivos que ya están en la raíz, que no necesitan que se les diga dónde viven.</summary>
    private string RelativeLocationOf(string folderPath) => RelativeLocation(EffectiveRootPath, folderPath);

    /// <summary>Intro en el cuadro de búsqueda del panel abre el primer resultado.</summary>
    public void OpenFirstTreeSearchResult()
    {
        if (TreeSearchResults.Count > 0) SelectTreeItem(TreeSearchResults[0]);
    }

    // ===================== Acciones sobre el árbol =====================

    /// <summary>Pulsar un nodo: un archivo se abre en la previsualización de la derecha, una carpeta se
    /// pliega o despliega y queda seleccionada. A diferencia de Skills —donde seleccionar y editar son dos
    /// gestos distintos— aquí un clic basta: los archivos solo se visualizan, así que no hay un segundo paso
    /// que reservar.
    ///
    /// No se toca <see cref="CurrentFolder"/> al abrir un archivo: cambiarla dispararía un
    /// <see cref="Reload"/> —y con él una reconstrucción entera del árbol— en cada clic, y la carpeta
    /// actual es estado del modo explorador que el usuario dejó donde quería.</summary>
    [RelayCommand]
    private void SelectTreeItem(FileTreeItemViewModel? node)
    {
        if (node is null) return;

        if (node.Card is { } card)
        {
            OpenCard(card);
            CloseTreeNavigator();
            return;
        }

        node.ToggleExpandCommand.Execute(null);
        SelectTreeFolder(node);
    }

    private void SelectTreeFolder(FileTreeItemViewModel folderNode)
    {
        _treeSelectionPath = folderNode.FullPath;
        SyncTreeSelection();
    }

    // ===================== Teclado =====================
    // Lo invoca el code-behind del árbol (FilesTreeView) mientras el panel tiene el foco. Cada método devuelve
    // el nodo que queda seleccionado para que la vista lo lleve a la zona visible; la selección en sí vive aquí.

    /// <summary>El nodo seleccionado, si sigue montado en el árbol.</summary>
    public FileTreeItemViewModel? SelectedTreeNode => _treeSelectionPath is null
        ? null
        : Flatten(FileTreeRoots).FirstOrDefault(n => PathsEqual(n.FullPath, _treeSelectionPath));

    /// <summary>Flecha arriba/abajo (y Inicio/Fin con un salto que se sale del árbol): mueve la selección entre
    /// los nodos visibles. Un archivo se abre al llegar a él, como en el explorador de un editor de código;
    /// una carpeta solo se selecciona, sin plegarla ni desplegarla.</summary>
    public FileTreeItemViewModel? MoveTreeSelection(int delta)
    {
        var visible = VisibleTreeNodes(FileTreeRoots).ToList();
        if (visible.Count == 0) return null;

        var index = _treeSelectionPath is null ? -1 : visible.FindIndex(n => PathsEqual(n.FullPath, _treeSelectionPath));
        var target = index < 0
            ? (delta >= 0 ? 0 : visible.Count - 1)
            : Math.Clamp(index + delta, 0, visible.Count - 1);
        return SelectTreeNodeFromKeyboard(visible[target]);
    }

    /// <summary>Flecha derecha: despliega la carpeta seleccionada o, si ya lo está, baja a su primer hijo.</summary>
    public FileTreeItemViewModel? ExpandTreeSelection()
    {
        if (SelectedTreeNode is not { IsFolder: true } node) return null;

        if (!node.IsExpanded)
        {
            if (node.HasChildren) node.IsExpanded = true;
            return node;
        }
        return node.Children.Count > 0 ? SelectTreeNodeFromKeyboard(node.Children[0]) : node;
    }

    /// <summary>Flecha izquierda: pliega la carpeta seleccionada o, si no hay nada que plegar, sube a la carpeta
    /// que la contiene.</summary>
    public FileTreeItemViewModel? CollapseTreeSelection()
    {
        if (SelectedTreeNode is not { } node) return null;

        if (node.IsFolder && node.IsExpanded)
        {
            node.IsExpanded = false;
            return node;
        }
        return FindTreeParent(FileTreeRoots, node) is { } parent ? SelectTreeNodeFromKeyboard(parent) : node;
    }

    /// <summary>Intro: abre el archivo seleccionado, o pliega/despliega la carpeta seleccionada.</summary>
    public void ActivateTreeSelection()
    {
        switch (SelectedTreeNode)
        {
            case { Card: { } card }:
                OpenCard(card);
                break;
            case { IsFolder: true } folder:
                folder.ToggleExpandCommand.Execute(null);
                break;
        }
    }

    private FileTreeItemViewModel SelectTreeNodeFromKeyboard(FileTreeItemViewModel node)
    {
        if (node.Card is { } card) OpenCard(card);
        else SelectTreeFolder(node);
        return node;
    }

    private static FileTreeItemViewModel? FindTreeParent(IEnumerable<FileTreeItemViewModel> nodes, FileTreeItemViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(target)) return node;
            if (FindTreeParent(node.Children, target) is { } found) return found;
        }
        return null;
    }

    // ===================== Crear, renombrar, borrar, mover =====================

    [RelayCommand]
    private void NewTreeFolderAtRoot() => CreateFolderInTree(EffectiveRootPath);

    [RelayCommand]
    private void CreateTreeSubfolder(FileTreeItemViewModel? node)
    {
        if (node is null) return;
        CreateFolderInTree(node.ContainerPath);
    }

    private void CreateFolderInTree(string parentPath)
    {
        if (TryCreateFolder(parentPath) is not { } created) return;
        IsTreeSearchMode = false;
        IsTreeSessionMenuOpen = false;
        if (!IsTreeSidebarVisible) IsTreeNavigatorOpen = true;
        Refresh();
        ExpandTreeAncestorsOf(created.FullPath);

        var node = Flatten(FileTreeRoots).FirstOrDefault(n => n.IsFolder && PathsEqual(n.FullPath, created.FullPath));
        if (node is null) return;
        SelectTreeFolder(node);
        node.EditingName = node.Name;
        node.IsRenaming = true;
    }

    /// <summary>Confirma el renombrado en línea de un nodo. Carpetas y archivos se renombran los dos, cada
    /// uno por el camino que ya existía (<see cref="RenameFolderAt"/> / <see cref="CommitRenameFileCommand"/>),
    /// que son los que aplican la política de colisión del vault, llevan a la ruta nueva lo que apuntaba a la
    /// vieja y recargan la pantalla — el árbol solo traslada el nombre editado a la entidad real.</summary>
    [RelayCommand]
    private void CommitRenameTreeItem(FileTreeItemViewModel? node)
    {
        if (node is null) return;

        var newName = node.EditingName.Trim();
        node.IsRenaming = false;

        if (node.Card is { } card)
        {
            if (newName.Length == 0 || newName == card.FileName) return;
            card.EditingName = newName;
            CommitRenameFileCommand.Execute(card);
            return;
        }

        if (newName.Length == 0 || newName == node.Name) return;
        RenameFolderAt(FindFolder(RootFolders, node.FullPath)?.Folder ?? new NoteFolder { FullPath = node.FullPath }, newName);
    }

    /// <summary>Borrar desde el árbol pasa por los mismos comandos que la rejilla: un archivo o una carpeta
    /// piden confirmación (si <c>ConfirmBeforeDelete</c> está activo) y van a la papelera del vault.</summary>
    [RelayCommand]
    private void DeleteTreeItem(FileTreeItemViewModel? node)
    {
        if (node is null) return;

        if (node.Card is { } card) RequestDeleteFileCommand.Execute(card);
        else RequestDeleteFolderCommand.Execute(node);
    }

    /// <summary>Soltar un archivo sobre un nodo del árbol lo mueve a esa carpeta (o a la carpeta que
    /// contiene el archivo sobre el que se soltó, que es lo que el usuario ve como "aquí").</summary>
    public void DropOnTreeNode(FileTreeItemViewModel target, string sourceFilePath)
    {
        if (Directory.Exists(sourceFilePath)) MoveFolderTo(sourceFilePath, target.ContainerPath);
        else MoveTreeFileTo(sourceFilePath, target.ContainerPath);
    }

    /// <summary>Soltar sobre el fondo vacío del árbol saca el archivo de su carpeta y lo devuelve a la
    /// raíz efectiva —la del vault, o la de la sesión elegida—, que es el equivalente de la fila "TODOS"
    /// del panel del modo explorador.</summary>
    public void DropOnTreeRoot(string sourceFilePath)
    {
        if (Directory.Exists(sourceFilePath)) MoveFolderTo(sourceFilePath, EffectiveRootPath);
        else MoveTreeFileTo(sourceFilePath, EffectiveRootPath);
    }

    private void MoveTreeFileTo(string sourceFilePath, string destinationFolderPath)
    {
        if (FindTreeCardByPath(sourceFilePath) is not { } card) return;

        MoveFileToFolder(card, destinationFolderPath);
        ExpandTreeAncestorsOf(card.File.FullPath);
    }

    /// <summary>Busca una tarjeta del árbol por su ruta — lo usan los <c>Drop</c> de las vistas, que solo
    /// pueden transportar una cadena en el <c>DataTransfer</c>. Mira primero entre las tarjetas del árbol
    /// y después entre las de la rejilla, para que un arrastre iniciado en una disposición valga también
    /// si se suelta sobre la otra.</summary>
    public FileCardViewModel? FindTreeCardByPath(string filePath) =>
        _treeCards.FirstOrDefault(card => PathsEqual(card.File.FullPath, filePath))
        ?? Files.FirstOrDefault(card => PathsEqual(card.File.FullPath, filePath));
}
