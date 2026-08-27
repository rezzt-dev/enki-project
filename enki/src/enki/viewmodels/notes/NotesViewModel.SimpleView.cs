using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Vista simplificada de Notas (Ajustes › Editor › Vista del editor): un panel lateral con el
/// árbol de carpetas y notas markdown de la sesión activa, y a la derecha las pestañas abiertas sobre el
/// mismo editor de siempre. Todo lo que hay aquí es *disposición*, no un segundo módulo de notas: el
/// árbol se construye leyendo el mismo <see cref="VaultService"/>, abrir una nota pasa por el mismo
/// <see cref="NotesViewModel.OpenNote"/> (que reutiliza la pestaña si ya existe) y renombrar/borrar
/// reutiliza los mismos comandos de la vista completa. Se separa en su propio archivo parcial —igual que
/// <c>SettingsViewModel.Editor.cs</c>— solo para no entrelazarlo con la vista de rejilla.</summary>
public sealed partial class NotesViewModel
{
    /// <summary>Ancho mínimo/máximo del panel lateral, aplicados tanto al arrastrar el separador
    /// (SimpleNotesView.axaml.cs) como al leer el valor guardado, que podría venir de una versión previa
    /// con otros límites o de un settings.json editado a mano.</summary>
    public const double SimpleSidebarMinWidth = 200;
    public const double SimpleSidebarMaxWidth = 520;

    private readonly DispatcherTimer _simpleSearchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <summary>Raíces del árbol del panel lateral: las carpetas de primer nivel de la sesión activa y,
    /// tras ellas, sus notas sueltas. Solo se rellena mientras <see cref="IsSimplifiedView"/> está
    /// activa — recorrer el vault entero para un árbol que nadie está mirando sería trabajo tirado.</summary>
    public ObservableCollection<NoteTreeItemViewModel> SimpleTreeRoots { get; } = [];

    /// <summary>Resultados de "buscar nota" en el panel lateral. La búsqueda es recursiva pero acotada a
    /// la sesión seleccionada (<see cref="EffectiveRootPath"/>), no a todo el vault — coherente con que
    /// el resto de la vista simplificada tampoco deja salirse de la sesión activa.</summary>
    public ObservableCollection<NoteCardViewModel> SimpleSearchResults { get; } = [];

    [ObservableProperty]
    private bool _isSimplifiedView;

    [ObservableProperty]
    private bool _isSimpleSidebarVisible = true;

    [ObservableProperty]
    private double _simpleSidebarWidth = 272;

    /// <summary>Qué muestra el cuerpo del panel lateral: el árbol (false) o el cuadro de búsqueda con sus
    /// resultados (true). Son los dos únicos iconos de la barra superior del panel — el tercero de
    /// Obsidian ("favoritos") se deja fuera a propósito.</summary>
    [ObservableProperty]
    private bool _isSimpleSearchMode;

    [ObservableProperty]
    private string _simpleSearchText = string.Empty;

    [ObservableProperty]
    private bool _isSessionMenuOpen;

    /// <summary>Distingue "la sesión no tiene ni una carpeta ni una nota" (mensaje de bienvenida en el
    /// árbol) de "hay contenido". Se recalcula al cambiar la colección, no en cada acceso al binding.</summary>
    public bool HasSimpleTreeItems => SimpleTreeRoots.Count > 0;

    public bool ShowSimpleSearchEmptyState =>
        IsSimpleSearchMode && !string.IsNullOrWhiteSpace(SimpleSearchText) && SimpleSearchResults.Count == 0;

    /// <summary>Se llama una vez desde el constructor. Lee el modo de vista y el estado del panel
    /// directamente de <see cref="AppSettings"/> (en vez de esperar a <see cref="EditorPreferences"/>,
    /// que <c>SettingsViewModel</c> todavía no ha inicializado en ese punto del arranque) y deja
    /// sincronizado el valor compartido para que ambos digan lo mismo desde el primer frame.</summary>
    private void InitializeSimpleView()
    {
        _simpleSearchDebounceTimer.Tick += (_, _) =>
        {
            _simpleSearchDebounceTimer.Stop();
            ReloadSimpleSearch();
        };
        SimpleTreeRoots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSimpleTreeItems));

        if (_settingsService is { } settings)
        {
            IsSimpleSidebarVisible = settings.Settings.NotesSimpleSidebarVisible;
            SimpleSidebarWidth = Math.Clamp(settings.Settings.NotesSimpleSidebarWidth,
                SimpleSidebarMinWidth, SimpleSidebarMaxWidth);
            IsSimplifiedView = settings.Settings.NotesViewMode == "Simple";
            EditorPreferences.SimplifiedNotesView = IsSimplifiedView;
        }

        // Sin desuscripción: este viewmodel vive lo que vive la ventana (se construye una sola vez en
        // MainWindowViewModel.InitializeShell), igual que la suscripción equivalente de SettingsViewModel.
        EditorPreferences.Changed += () => IsSimplifiedView = EditorPreferences.SimplifiedNotesView;
    }

    partial void OnIsSimplifiedViewChanged(bool value)
    {
        OnPropertyChanged(nameof(NewTabTooltip));
        if (value) ReloadSimpleTree();
        else SimpleTreeRoots.Clear();
    }

    partial void OnIsSimpleSearchModeChanged(bool value)
    {
        if (value) ReloadSimpleSearch();
        else SimpleSearchText = string.Empty;
        OnPropertyChanged(nameof(ShowSimpleSearchEmptyState));
    }

    partial void OnSimpleSearchTextChanged(string value)
    {
        _simpleSearchDebounceTimer.Stop();
        _simpleSearchDebounceTimer.Start();
    }

    /// <summary>Persistido igual que el resto del estado de ventana de la app (pestañas abiertas,
    /// carpetas expandidas): al vuelo, en cuanto el usuario lo cambia.</summary>
    partial void OnIsSimpleSidebarVisibleChanged(bool value)
    {
        if (value) IsSessionMenuOpen = false;
        if (_settingsService is null) return;
        _settingsService.Settings.NotesSimpleSidebarVisible = value;
        _settingsService.Save();
    }

    /// <summary>Llamado desde el separador arrastrable de SimpleNotesView una vez soltado el ratón —
    /// no en cada píxel del arrastre, que escribiría el JSON de ajustes decenas de veces por gesto.</summary>
    public void PersistSimpleSidebarWidth()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.NotesSimpleSidebarWidth =
            Math.Clamp(SimpleSidebarWidth, SimpleSidebarMinWidth, SimpleSidebarMaxWidth);
        _settingsService.Save();
    }

    // ===================== Árbol de carpetas y notas =====================

    /// <summary>Reconstruye el árbol del panel lateral desde disco, conservando qué carpetas estaban
    /// expandidas (por ruta, no por instancia — los nodos se recrean enteros en cada recarga). Igual que
    /// <see cref="ReloadFolders"/>, la primera vez cae al conjunto persistido en
    /// <see cref="AppSettings.NotesSimpleExpandedFolderPaths"/>.</summary>
    public void ReloadSimpleTree()
    {
        if (!IsSimplifiedView) return;

        var expandedPaths = SimpleTreeRoots.Count > 0
            ? CollectExpandedTreePaths(SimpleTreeRoots).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_settingsService?.Settings.NotesSimpleExpandedFolderPaths ?? [],
                StringComparer.OrdinalIgnoreCase);

        SimpleTreeRoots.Clear();
        if (IsSelectedSessionLocked) return;

        foreach (var item in BuildTreeLevel(EffectiveRootPath, 0, expandedPaths)) SimpleTreeRoots.Add(item);

        SyncSimpleTreeSelection();
    }

    /// <summary>Un nivel del árbol: primero las subcarpetas (recursivamente), después las notas markdown
    /// sueltas de esa carpeta — el mismo orden que usa la rejilla de la vista completa
    /// (<see cref="Reload"/>), y el que hace que un árbol se lea sin saltos entre tipos de nodo. Cualquier
    /// archivo que no sea <c>.md</c> queda fuera: <see cref="VaultService.GetNotes"/> ya solo enumera
    /// markdown, así que no hay que filtrar nada aquí.</summary>
    private List<NoteTreeItemViewModel> BuildTreeLevel(string folderPath, int depth, HashSet<string> expandedPaths)
    {
        var level = new List<NoteTreeItemViewModel>();

        foreach (var folder in _vaultService.GetFolderTree(folderPath))
        {
            var node = NoteTreeItemViewModel.ForFolder(folder, depth);
            foreach (var child in BuildTreeLevel(folder.FullPath, depth + 1, expandedPaths)) node.Children.Add(child);
            node.IsExpanded = expandedPaths.Contains(folder.FullPath);
            node.OnExpandedChanged = PersistSimpleExpandedFolders;
            level.Add(node);
        }

        foreach (var note in _vaultService.GetNotes(folderPath).OrderBy(n => n.FileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase))
            level.Add(NoteTreeItemViewModel.ForNote(note, depth));

        return level;
    }

    private static IEnumerable<string> CollectExpandedTreePaths(IEnumerable<NoteTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder && node.IsExpanded) yield return node.FullPath;
            foreach (var path in CollectExpandedTreePaths(node.Children)) yield return path;
        }
    }

    private void PersistSimpleExpandedFolders()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.NotesSimpleExpandedFolderPaths = CollectExpandedTreePaths(SimpleTreeRoots).ToList();
        _settingsService.Save();
    }

    /// <summary>Marca en el árbol la nota de la pestaña activa y abre las carpetas que la contienen, para
    /// que abrir una nota desde cualquier otro sitio (dashboard, grafo de sesiones, restaurar pestañas)
    /// la deje visible y resaltada en el panel lateral en vez de perdida en una rama cerrada.</summary>
    public void SyncSimpleTreeSelection()
    {
        if (!IsSimplifiedView) return;
        var activePath = ActiveEditor?.Note.FullPath;
        ApplyTreeSelection(SimpleTreeRoots, activePath);
    }

    private static bool ApplyTreeSelection(IEnumerable<NoteTreeItemViewModel> nodes, string? activePath)
    {
        var anyInBranch = false;
        foreach (var node in nodes)
        {
            var isActiveNote = node.IsNote && activePath is not null &&
                                VaultService.PathsEqual(node.FullPath, activePath);
            var childHasActive = ApplyTreeSelection(node.Children, activePath);

            node.IsSelected = isActiveNote;
            if (childHasActive) node.IsExpanded = true;
            if (isActiveNote || childHasActive) anyInBranch = true;
        }
        return anyInBranch;
    }

    private static IEnumerable<NoteTreeItemViewModel> Flatten(IEnumerable<NoteTreeItemViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    // ===================== Acciones del panel lateral =====================

    /// <summary>Botón "+" de la barra de pestañas (EditorTabStripView, compartida por las dos vistas).
    /// En la vista completa vuelve a la rejilla de notas dejando las pestañas abiertas intactas; en la
    /// simplificada no hay rejilla a la que volver, así que crea directamente una nota en la raíz de la
    /// sesión. La barra no tiene por qué saber en cuál de las dos está: pregunta a este comando.</summary>
    [RelayCommand]
    private void NewTab()
    {
        if (IsSimplifiedView) NewNoteAtRoot();
        else ShowBrowsingMode();
    }

    public string NewTabTooltip => LocalizationService.Instance[
        IsSimplifiedView ? "Notes_NewNoteButton" : "Notes_NewTabTooltip"];

    [RelayCommand]
    private void ToggleSimpleSidebar() => IsSimpleSidebarVisible = !IsSimpleSidebarVisible;

    [RelayCommand]
    private void ShowSimpleTree() => IsSimpleSearchMode = false;

    [RelayCommand]
    private void ShowSimpleSearch() => IsSimpleSearchMode = true;

    [RelayCommand]
    private void ToggleSessionMenu() => IsSessionMenuOpen = !IsSessionMenuOpen;

    [RelayCommand]
    private void SelectSessionOption(SessionOptionViewModel option)
    {
        IsSessionMenuOpen = false;
        SelectedSessionOption = option;
    }

    /// <summary>Contrae todo el árbol de una pasada — el cuarto botón de la fila de acciones, y la forma
    /// rápida de recuperar la vista general tras haber ido abriendo ramas.</summary>
    [RelayCommand]
    private void CollapseSimpleTree()
    {
        foreach (var node in Flatten(SimpleTreeRoots)) node.IsExpanded = false;
        PersistSimpleExpandedFolders();
    }

    /// <summary>"Nueva nota" del panel lateral: siempre en la raíz de la sesión, no en la carpeta que se
    /// estuviera mirando — es lo que pide la vista simplificada (una acción, un sitio previsible) y lo
    /// que la distingue de <see cref="NewNote"/>, que sí respeta la carpeta actual de la rejilla.</summary>
    [RelayCommand]
    private void NewNoteAtRoot()
    {
        NoteFile note;
        try
        {
            note = _vaultService.CreateNote(LocalizationService.Instance["Vault_DefaultNoteTitle"], EffectiveRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        Reload(); // recarga también el árbol del panel lateral
        OpenNote(note);
    }

    /// <summary>"Nueva carpeta" del panel lateral — misma regla de raíz que <see cref="NewNoteAtRoot"/>,
    /// y deja el nodo recién creado en modo renombrar para poder escribir el nombre en el sitio.</summary>
    [RelayCommand]
    private void NewFolderAtRoot()
    {
        NoteFolder created;
        try
        {
            created = VaultService.CreateFolder(EffectiveRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        ReloadFolders();
        ReloadSimpleTree();
        BeginRenameTreeNode(created.FullPath);
    }

    /// <summary>"Nueva subcarpeta" del menú contextual de una carpeta del árbol.</summary>
    [RelayCommand]
    private void CreateTreeSubfolder(NoteTreeItemViewModel node)
    {
        if (!node.IsFolder) return;

        NoteFolder created;
        try
        {
            created = VaultService.CreateFolder(node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        node.IsExpanded = true;
        ReloadFolders();
        ReloadSimpleTree();
        BeginRenameTreeNode(created.FullPath);
    }

    /// <summary>"Nueva nota aquí" del menú contextual de una carpeta del árbol.</summary>
    [RelayCommand]
    private void CreateTreeNoteInFolder(NoteTreeItemViewModel node)
    {
        if (!node.IsFolder) return;

        NoteFile note;
        try
        {
            note = _vaultService.CreateNote(LocalizationService.Instance["Vault_DefaultNoteTitle"], node.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        node.IsExpanded = true;
        Reload(); // recarga también el árbol del panel lateral
        OpenNote(note);
    }

    private void BeginRenameTreeNode(string fullPath)
    {
        var node = Flatten(SimpleTreeRoots).FirstOrDefault(n => VaultService.PathsEqual(n.FullPath, fullPath));
        if (node is null) return;
        node.EditingName = node.Name;
        node.IsRenaming = true;
    }

    /// <summary>Clic sobre una fila del árbol: una carpeta se abre/cierra, una nota se abre en pestaña
    /// (o pasa a la que ya la tuviera abierta, que de eso ya se encarga <see cref="OpenNote"/>).</summary>
    [RelayCommand]
    private void ActivateTreeItem(NoteTreeItemViewModel node)
    {
        if (node.IsFolder)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        OpenNote(node.Note!);
        SyncSimpleTreeSelection();
    }

    /// <summary>Confirma el renombrado en el sitio de una fila del árbol, sea carpeta o nota. Delega en
    /// las mismas primitivas que usa la vista completa (<see cref="VaultService.RenameFolder"/> /
    /// <see cref="RenameNoteFile"/>) para que la política de colisiones, el relinkeo de tarjetas Kanban y
    /// la sincronización de pestañas abiertas sean exactamente las mismas en las dos vistas.</summary>
    [RelayCommand]
    private void CommitRenameTreeItem(NoteTreeItemViewModel node)
    {
        var newName = node.EditingName;
        node.IsRenaming = false;
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name) return;

        if (node.IsFolder)
        {
            try
            {
                node.UpdateFolder(VaultService.RenameFolder(node.Folder!, newName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
            {
                NotifyVaultWriteFailed(ex);
                return;
            }
            ReloadFolders();
        }
        else
        {
            var updated = RenameNoteFile(node.Note!, newName);
            if (updated is null) return;
            node.UpdateNote(updated);
        }

        Reload(); // recarga también el árbol del panel lateral
    }

    /// <summary>Borrar desde el árbol. Para una nota reutiliza el mismo diálogo de confirmación de la
    /// rejilla (<see cref="RequestDeleteNote"/>) envolviéndola en un <see cref="NoteCardViewModel"/>
    /// efímero, en vez de duplicar el flujo de confirmación con un segundo estado pendiente.</summary>
    [RelayCommand]
    private void DeleteTreeItem(NoteTreeItemViewModel node)
    {
        if (node.IsNote)
        {
            RequestDeleteNote(new NoteCardViewModel(node.Note!));
            return;
        }

        // Si la carpeta también existe en el panel de la vista completa, se borra por su comando para
        // no duplicar el manejo de "la carpeta actual era esta o una hija suya".
        var folderVm = FindFolder(RootFolders, node.FullPath);
        if (folderVm is not null)
        {
            DeleteFolder(folderVm);
            return;
        }

        _vaultService.DeleteFolder(node.Folder!);
        ReloadFolders();
        Reload(); // recarga también el árbol del panel lateral
    }

    /// <summary>Mover una nota soltándola sobre una carpeta del árbol (o sobre la raíz de la sesión).
    /// Reutiliza <see cref="MoveNoteToFolder"/>, que ya relinkea las tarjetas Kanban que apuntaban a la
    /// ruta antigua.</summary>
    public void MoveTreeNoteToFolder(string notePath, string destinationFolderPath)
    {
        var note = _vaultService.GetNote(notePath);
        if (note is null) return;

        MoveNoteToFolder(new NoteCardViewModel(note), destinationFolderPath); // recarga el árbol al terminar
    }

    // ===================== Búsqueda dentro de la sesión =====================

    private void ReloadSimpleSearch()
    {
        SimpleSearchResults.Clear();

        if (!IsSimpleSearchMode || IsSelectedSessionLocked || string.IsNullOrWhiteSpace(SimpleSearchText))
        {
            OnPropertyChanged(nameof(ShowSimpleSearchEmptyState));
            return;
        }

        // Busca en el contenido completo de cada nota del árbol seleccionado, no en NoteFile.Preview
        // (los primeros 160 caracteres), que es lo que este filtro comparaba antes.
        var matches = _noteSearchService.SearchTree(EffectiveRootPath, SimpleSearchText, maxResults: 200);

        foreach (var hit in matches) SimpleSearchResults.Add(new NoteCardViewModel(hit.Note));
        OnPropertyChanged(nameof(ShowSimpleSearchEmptyState));
    }
}
