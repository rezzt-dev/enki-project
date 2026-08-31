using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Backs the "Skills" section — listar, organizar en carpetas, importar, exportar, duplicar,
/// editar y eliminar skills almacenadas en <c>&lt;vault&gt;/.enki/skills/</c>
/// (docs/agent-docs/ia-skills/especificacion-desarrollo.md). Construye su propio
/// <see cref="SkillService"/> a partir de <see cref="VaultService"/> en vez de recibirlo ya construido —
/// mismo motivo por el que <see cref="SkillService.SkillsDirectory"/> se calcula en vivo: la instancia
/// sobrevive a un cambio de vault sin necesitar reconstrucción.
///
/// La sección se puede navegar de dos formas, elegidas con el conmutador de su propia barra de
/// herramientas y persistidas en <see cref="AppSettings.SkillsViewMode"/>:
/// <list type="bullet">
/// <item><b>Explorador</b> — panel de carpetas a la izquierda y rejilla de tarjetas a la derecha, igual
/// que la sección Archivos.</item>
/// <item><b>Árbol</b> — un panel lateral que entremezcla carpetas y skills, y el detalle/editor a la
/// derecha, igual que la vista simplificada de Notas.</item>
/// </list>
/// Las dos comparten este mismo viewmodel y el mismo estado (carpeta actual, selección, editor abierto):
/// cambiar de modo reordena la pantalla, nunca reinicia lo que se estaba haciendo. El código propio del
/// modo árbol vive en <c>SkillsViewModel.Tree.cs</c>.</summary>
public sealed partial class SkillsViewModel : ViewModelBase
{
    private readonly SkillService _skillService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService? _settingsService;

    private IReadOnlyList<string>? _pendingImportPaths;
    private Action<string>? _pendingNamePromptConfirm;

    /// <summary>Evita que <see cref="Reload"/> se reentre a sí mismo: dentro de él se reasignan
    /// <see cref="CurrentFolder"/> y <see cref="SelectedSkill"/>, cuyos <c>partial void On...Changed</c>
    /// volverían a filtrar y a reconstruir el árbol a media reconstrucción.</summary>
    private bool _reloading;

    /// <summary>Todas las skills del vault, de cualquier subcarpeta y sin filtrar — fuente de verdad para
    /// exportar (una skill marcada no debe perderse al cambiar de carpeta), para las estadísticas del
    /// encabezado y para reconstruir <see cref="Skills"/> en cada tecleo de <see cref="SearchText"/>.</summary>
    private List<SkillViewModel> _allSkills = [];

    /// <summary>Colección de skills mostrada — las de <see cref="CurrentFolder"/> mientras se navega, o
    /// las de toda la biblioteca cuando hay búsqueda o filtro de etiquetas activos (ver
    /// <see cref="IsFlatResults"/>).</summary>
    public ObservableCollection<SkillViewModel> Skills { get; } = [];

    /// <summary>Lo que dibuja la rejilla del modo explorador: primero las carpetas navegables (con la
    /// entrada ".." si no estamos en la raíz) y después las skills de <see cref="Skills"/>. Se mantiene
    /// <see cref="Skills"/> aparte, y no solo esta colección mixta, porque el contador, la exportación y
    /// los tests razonan sobre skills, no sobre filas de rejilla — mismo reparto que
    /// <c>FilesViewModel.Files</c>/<c>BrowserItems</c>.</summary>
    public ObservableCollection<object> BrowserItems { get; } = [];

    /// <summary>Árbol de carpetas de la biblioteca, para el panel lateral del modo explorador.</summary>
    public ObservableCollection<SkillFolderViewModel> RootFolders { get; } = [];

    [ObservableProperty]
    private SkillViewModel? _selectedSkill;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Carpeta que se está navegando, o null para la raíz de la biblioteca ("TODAS").</summary>
    [ObservableProperty]
    private SkillFolderViewModel? _currentFolder;

    [ObservableProperty]
    private bool _isRootDropTarget;

    /// <summary>Si un arrastre del explorador del sistema está sobrevolando la rejilla — pinta el borde de
    /// "soltar aquí para importar", igual que la rejilla de Archivos.</summary>
    [ObservableProperty]
    private bool _isGridDropTarget;

    /// <summary>La rejilla está enseñando resultados de toda la biblioteca en vez del contenido de una
    /// carpeta, porque hay búsqueda o filtro de etiquetas activos. En ese estado no se pintan tarjetas de
    /// carpeta —serían navegación dentro de un resultado que ya es global— y cada tarjeta muestra en qué
    /// carpeta vive, que es la información que si no se perdería.</summary>
    [ObservableProperty]
    private bool _isFlatResults;

    /// <summary>Alterna el cuadro de búsqueda junto al contador — <c>ToggleSearchCommand</c> limpia
    /// <see cref="SearchText"/> al cerrarlo, así que reabrir siempre arranca desde la lista completa.</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Distinto de <see cref="IsEmpty"/>: esto es "hay skills pero ninguna coincide con la
    /// búsqueda", que debe mostrar un mensaje de "sin resultados" en vez del onboarding de "importa tu
    /// primera skill".</summary>
    [ObservableProperty]
    private bool _hasNoSearchResults;

    /// <summary>La biblioteca tiene contenido pero la carpeta abierta no — un tercer estado vacío,
    /// distinto del onboarding y del "sin resultados", porque la salida es otra: volver atrás o traer
    /// skills aquí, no importar la primera ni cambiar la búsqueda.</summary>
    [ObservableProperty]
    private bool _isCurrentFolderEmpty;

    public string NoSearchResultsMessage => !string.IsNullOrWhiteSpace(SearchText)
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Skills_NoResults_ForQuery"], SearchText)
        : LocalizationService.Instance["Skills_NoResults_ForTags"];

    /// <summary>Etiqueta del contador de la franja de estadísticas (p. ej. "12 SKILLS") — propiedad
    /// computada en vez de bindear <c>Skills.Count</c> directamente más un literal fijo en XAML, porque
    /// el plural depende del idioma activo (§1.3/§1.4). Notificada manualmente en <see cref="ApplyFilter"/>,
    /// que es el único sitio donde cambia <see cref="Skills"/>.</summary>
    public string SkillsCountLabel => Plural.Get("Skills_Count", Skills.Count, Skills.Count);

    /// <summary>Nombre de la carpeta abierta, o "TODAS" en la raíz — la miga de pan de la barra de la
    /// rejilla.</summary>
    public string CurrentFolderLabel => CurrentFolder?.Name ?? LocalizationService.Instance["Skills_AllFolder"];

    /// <summary>Carpeta activa en disco. Es a donde van a parar las importaciones y las carpetas nuevas
    /// creadas desde la rejilla, igual que en Archivos.</summary>
    public string CurrentFolderPath => CurrentFolder?.FullPath ?? _skillService.SkillsDirectory;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(NoSearchResultsMessage));
        ApplyFilter();
    }

    partial void OnCurrentFolderChanged(SkillFolderViewModel? oldValue, SkillFolderViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;

        OnPropertyChanged(nameof(CurrentFolderLabel));
        OnPropertyChanged(nameof(CurrentFolderPath));
        if (!_reloading) ApplyFilter();
    }

    /// <summary>Filtro de etiquetas compartido (misma pieza reutilizable que Notas/Kanban/Polar) —
    /// se recarga con las etiquetas de todas las skills del vault en cada <see cref="Reload"/> y
    /// conserva la selección del usuario mientras las etiquetas sigan existiendo.</summary>
    public TagFilterViewModel TagFilter { get; } = new();

    /// <summary>Totales de la biblioteca completa (sin filtrar) para la franja de estadísticas del
    /// encabezado — recalculados y notificados manualmente en <see cref="Reload"/>, igual que
    /// <see cref="IsEmpty"/>, porque dependen de <see cref="_allSkills"/> y no de una propiedad observable.</summary>
    public int TotalSkillsCount => _allSkills.Count;
    public int DefaultSkillsCount => _allSkills.Count(s => s.IsDefault);
    public int CustomSkillsCount => _allSkills.Count(s => !s.IsDefault);
    public int TaggedSkillsCount => _allSkills.Count(s => s.Model.Tags.Count > 0);


    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen) SearchText = string.Empty;
    }

    /// <summary>No nulo mientras se edita una skill — el modo explorador cambia a pantalla completa de
    /// editor y el modo árbol lo abre en su panel derecho, conservando el árbol a la izquierda
    /// (Opción A, docs/agent-docs/ia-skills/especificacion-desarrollo.md §10).</summary>
    [ObservableProperty]
    private SkillEditorViewModel? _activeEditor;

    [ObservableProperty]
    private SkillViewModel? _pendingDeleteSkill;

    [ObservableProperty]
    private SkillFolderViewModel? _pendingDeleteFolder;

    [ObservableProperty]
    private SkillNamePromptViewModel? _namePrompt;

    /// <summary>Nombres de archivo en conflicto detectados en la última llamada a importar — no nulo
    /// mientras el diálogo "Cancelar / Cambiar nombre / Sobrescribir" está abierto.</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _importConflictFileNames;

    public bool HasImportConflicts => ImportConflictFileNames is { Count: > 0 };

    partial void OnImportConflictFileNamesChanged(IReadOnlyList<string>? value) => OnPropertyChanged(nameof(HasImportConflicts));

    /// <summary>Directorio de skills de la comunidad enlazado desde la tarjeta de recomendación de esta
    /// sección y desde Ajustes › Skills (mismo destino en ambos sitios). Única fuente de verdad para la
    /// URL — cambiarla solo requiere tocar esta constante, nunca los `.axaml` (ver
    /// <see cref="CommunitySkillsUrlDisplay"/> para el texto de host mostrado en ambas vistas).</summary>
    public const string CommunitySkillsUrl = "https://skillsclaude.org/skills";

    /// <summary>Host legible de <see cref="CommunitySkillsUrl"/> (p. ej. "skillsclaude.org") — bindeado
    /// desde SkillsView y SettingsView en vez de repetir el dominio como literal en dos `.axaml`, para que
    /// ambos textos no puedan desincronizarse de la URL real que abre <see cref="OpenCommunitySkills"/>.
    /// Propiedad de instancia (aunque el valor es constante) porque los bindings normales de estas vistas
    /// resuelven contra el DataContext, no contra miembros estáticos.</summary>
    public string CommunitySkillsUrlDisplay { get; } = new Uri(CommunitySkillsUrl).Host;

    [RelayCommand]
    private static void OpenCommunitySkills() => UrlOpener.Open(CommunitySkillsUrl);

    public SkillsViewModel(VaultService vaultService, NotificationService notificationService, SettingsService? settingsService = null)
    {
        _skillService = new SkillService(vaultService);
        _notificationService = notificationService;
        _settingsService = settingsService;
        _skillService.SkillsChanged += Reload;
        TagFilter.Changed += ApplyFilter;

        InitializeNavigationPreferences();
        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(NoSearchResultsMessage));
        OnPropertyChanged(nameof(SkillsCountLabel));
        OnPropertyChanged(nameof(CurrentFolderLabel));
        Reload();
    }

    /// <summary>Copia las skills por defecto la primera vez que se activa la sección — llamado desde
    /// <c>MainWindowViewModel.OnSidebarSectionChanged</c> al recibir el toggle de Ajustes.</summary>
    public void EnsureDefaultSkills() => _skillService.EnsureDefaultSkills();

    // ===================== Carga =====================

    public void Reload()
    {
        var previouslySelectedPath = SelectedSkill?.FilePath;

        _reloading = true;
        try
        {
            _allSkills = _skillService.LoadSkills().Select(skill => new SkillViewModel(skill)).ToList();
            ReloadFolders();
        }
        finally
        {
            _reloading = false;
        }

        ApplyFilter();
        RebuildTree();
        TagFilter.Reload(_allSkills.SelectMany(s => s.Model.Tags).Distinct(StringComparer.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(TotalSkillsCount));
        OnPropertyChanged(nameof(DefaultSkillsCount));
        OnPropertyChanged(nameof(CustomSkillsCount));
        OnPropertyChanged(nameof(TaggedSkillsCount));

        SelectedSkill = previouslySelectedPath is null
            ? null
            : _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, previouslySelectedPath));
    }

    /// <summary>Reconstruye <see cref="RootFolders"/> conservando qué carpetas estaban expandidas y cuál
    /// era la actual. En la primera llamada no hay estado en memoria que conservar, así que se parte del
    /// que el usuario dejó la última vez (persistido en Ajustes) — mismo reparto de autoridad que
    /// <c>FilesViewModel.ReloadFolders</c>.</summary>
    private void ReloadFolders()
    {
        var expandedPaths = RootFolders.Count > 0
            ? CollectExpandedPaths(RootFolders).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_settingsService?.Settings.SkillsExpandedFolderPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var selectedPath = CurrentFolder?.FullPath;

        RootFolders.Clear();
        foreach (var folder in _skillService.GetFolderTree())
        {
            var vm = new SkillFolderViewModel(folder);
            ApplyExpandedState(vm, expandedPaths);
            AttachExpandedCallback(vm);
            RootFolders.Add(vm);
        }

        CurrentFolder = selectedPath is null ? null : FindFolder(RootFolders, selectedPath);
    }

    private void AttachExpandedCallback(SkillFolderViewModel folder)
    {
        folder.OnExpandedChanged = PersistExpandedFolders;
        foreach (var child in folder.Children) AttachExpandedCallback(child);
    }

    private static IEnumerable<string> CollectExpandedPaths(IEnumerable<SkillFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            if (folder.IsExpanded) yield return folder.FullPath;
            foreach (var path in CollectExpandedPaths(folder.Children)) yield return path;
        }
    }

    private static void ApplyExpandedState(SkillFolderViewModel folder, HashSet<string> expandedPaths)
    {
        folder.IsExpanded = expandedPaths.Contains(folder.FullPath);
        foreach (var child in folder.Children) ApplyExpandedState(child, expandedPaths);
    }

    private void PersistExpandedFolders()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.SkillsExpandedFolderPaths = CollectExpandedPaths(RootFolders).ToList();
        _settingsService.Save();
    }

    private static SkillFolderViewModel? FindFolder(IEnumerable<SkillFolderViewModel> folders, string path)
    {
        foreach (var folder in folders)
        {
            if (PathsEqual(folder.FullPath, path)) return folder;
            var found = FindFolder(folder.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>Único punto donde se decide qué se ve: aplica búsqueda y etiquetas, elige entre "contenido
    /// de la carpeta actual" y "resultados de toda la biblioteca", y de ahí salen tanto
    /// <see cref="Skills"/> como <see cref="BrowserItems"/> y el árbol del modo árbol.</summary>
    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var selectedTags = TagFilter.SelectedTags.ToList();
        IsFlatResults = query.Length > 0 || selectedTags.Count > 0;

        IEnumerable<SkillViewModel> filtered = IsFlatResults
            ? _allSkills
            : _allSkills.Where(s => PathsEqual(s.Model.FolderPath, CurrentFolderPath));

        if (query.Length > 0)
        {
            filtered = filtered.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        || s.RelativeFolderPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        || s.Model.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (selectedTags.Count > 0)
        {
            filtered = filtered.Where(s => s.Model.Tags.Any(tag => selectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        Skills.Clear();
        foreach (var skill in filtered) Skills.Add(skill);

        BrowserItems.Clear();
        if (!IsFlatResults)
        {
            if (CurrentFolder is not null)
            {
                var parentPath = Path.GetDirectoryName(CurrentFolder.FullPath);
                if (!string.IsNullOrEmpty(parentPath))
                    BrowserItems.Add(new SkillFolderCardViewModel("..", parentPath, isParent: true));
            }

            var childFolders = CurrentFolder?.Children ?? (IEnumerable<SkillFolderViewModel>)RootFolders;
            foreach (var folder in childFolders)
                BrowserItems.Add(new SkillFolderCardViewModel(folder.Name, folder.FullPath, totalSkillCount: folder.TotalSkillCount));
        }

        foreach (var skill in Skills) BrowserItems.Add(skill);

        IsEmpty = _allSkills.Count == 0 && RootFolders.Count == 0;
        HasNoSearchResults = IsFlatResults && Skills.Count == 0 && !IsEmpty;
        IsCurrentFolderEmpty = !IsFlatResults && !IsEmpty && BrowserItems.Count == 0;

        OnPropertyChanged(nameof(NoSearchResultsMessage));
        OnPropertyChanged(nameof(SkillsCountLabel));
    }

    [RelayCommand]
    private void SelectSkill(SkillViewModel? skill) => SelectedSkill = skill;

    partial void OnSelectedSkillChanged(SkillViewModel? oldValue, SkillViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        SyncTreeSelection();
    }

    [RelayCommand]
    private async Task CopySkillContent(SkillViewModel? skill)
    {
        if (skill is null) return;

        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(skill.Model.Content);
        _notificationService.Push(LocalizationService.Instance["Skills_Notify_ContentCopied_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Skills_Notify_ContentCopied_Message"], skill.Name),
            NotificationSeverity.Success);
    }

    // ===================== Navegación por carpetas =====================

    [RelayCommand]
    private void SelectFolder(SkillFolderViewModel? folder) => CurrentFolder = folder;

    /// <summary>Abre una tarjeta de carpeta de la rejilla. La entrada ".." sube al padre, que puede ser la
    /// propia raíz de la biblioteca — en cuyo caso <see cref="FindFolder"/> no encuentra nada y
    /// <see cref="CurrentFolder"/> queda en null, que es exactamente "TODAS".</summary>
    [RelayCommand]
    private void OpenFolderCard(SkillFolderCardViewModel card) =>
        CurrentFolder = string.IsNullOrEmpty(card.FullPath) ? null : FindFolder(RootFolders, card.FullPath);

    /// <summary>Crea una carpeta dentro de la que se está navegando y la deja en modo renombrar, para que
    /// el usuario escriba su nombre sin un diálogo intermedio — mismo gesto que "nueva carpeta" en
    /// Archivos.</summary>
    [RelayCommand]
    private void NewFolder() => CreateFolderIn(CurrentFolderPath);

    [RelayCommand]
    private void CreateSubfolder(SkillFolderViewModel? folder)
    {
        if (folder is null) return;
        folder.IsExpanded = true;
        CreateFolderIn(folder.FullPath);
    }

    private void CreateFolderIn(string parentPath)
    {
        try
        {
            // CreateFolder dispara SkillsChanged, que ya recarga el árbol: para cuando vuelve, la carpeta
            // nueva existe como fila y solo hay que localizarla para abrir su cuadro de renombrado.
            var created = _skillService.CreateFolder(parentPath);
            var vm = FindFolder(RootFolders, created.FullPath);
            if (vm is null) return;
            vm.EditingName = vm.Name;
            vm.IsRenaming = true;
            ExpandAncestorsOf(created.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_FolderCreateFailed_Title", ex);
        }
    }

    [RelayCommand]
    private void CommitRenameFolder(SkillFolderViewModel? folder)
    {
        if (folder is null) return;

        var newName = folder.EditingName.Trim();
        if (newName.Length > 0 && newName != folder.Name)
        {
            try
            {
                var wasCurrent = CurrentFolder == folder;
                var updated = _skillService.RenameFolder(folder.Folder, newName);
                if (wasCurrent) CurrentFolder = FindFolder(RootFolders, updated.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NotifyFolderFailure("Skills_Notify_FolderRenameFailed_Title", ex);
            }
        }

        folder.IsRenaming = false;
        Reload();
    }

    /// <summary>Eliminar una carpeta arrastra consigo todo lo que hay dentro y la biblioteca de skills no
    /// tiene papelera, así que siempre pasa por confirmación salvo que el usuario haya desactivado
    /// "preguntar antes de borrar" (docs/agent-docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3).</summary>
    [RelayCommand]
    private void RequestDeleteFolder(SkillFolderViewModel? folder)
    {
        if (folder is null) return;
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) PendingDeleteFolder = folder;
        else DeleteFolder(folder);
    }

    [RelayCommand]
    private void CancelDeleteFolder() => PendingDeleteFolder = null;

    [RelayCommand]
    private void ConfirmDeleteFolder()
    {
        if (PendingDeleteFolder is not { } folder) return;
        DeleteFolder(folder);
        PendingDeleteFolder = null;
    }

    private void DeleteFolder(SkillFolderViewModel folder)
    {
        try
        {
            var affectsCurrent = CurrentFolder is not null &&
                (CurrentFolder == folder || IsDescendant(folder, CurrentFolder));

            _skillService.DeleteFolder(folder.Folder);
            if (affectsCurrent) CurrentFolder = null;
            Reload();
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_FolderDeleteFailed_Title"], ex.Message, NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_FolderDeleteFailed_Title", ex);
        }
    }

    private static bool IsDescendant(SkillFolderViewModel ancestor, SkillFolderViewModel node) =>
        ancestor.Children.Any(child => child == node || IsDescendant(child, node));

    /// <summary>Mueve una skill a otra carpeta (arrastrar y soltar sobre una fila del panel, sobre un nodo
    /// del árbol o sobre la raíz). Es el gesto principal para organizar una biblioteca que ya existe, así
    /// que un fallo se avisa en vez de dejar la skill donde estaba sin explicación.</summary>
    public void MoveSkillToFolder(SkillViewModel skill, string destinationFolderPath)
    {
        if (PathsEqual(skill.Model.FolderPath, destinationFolderPath)) return;

        try
        {
            var moved = _skillService.MoveSkill(skill.Model, destinationFolderPath);
            Reload();
            SelectedSkill = _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, moved.FilePath));
            ExpandAncestorsOf(destinationFolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_MoveFailed_Title", ex);
        }
    }

    /// <summary>Mueve una carpeta entera dentro de otra. Soltar una carpeta dentro de sí misma lo rechaza
    /// el servicio con un <see cref="InvalidOperationException"/> explicado, no un fallo de E/S opaco.</summary>
    public void MoveFolderToFolder(SkillFolderViewModel folder, string destinationParentPath)
    {
        try
        {
            _skillService.MoveFolder(folder.Folder, destinationParentPath);
            Reload();
            ExpandAncestorsOf(destinationParentPath);
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_MoveFailed_Title"], ex.Message, NotificationSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyFolderFailure("Skills_Notify_MoveFailed_Title", ex);
        }
    }

    /// <summary>Abre en el panel/árbol todas las carpetas que llevan hasta <paramref name="path"/>, para
    /// que lo que se acaba de crear o mover quede a la vista en vez de dentro de una rama plegada.</summary>
    private void ExpandAncestorsOf(string path)
    {
        var root = _skillService.SkillsDirectory;
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && !PathsEqual(current, root))
        {
            if (FindFolder(RootFolders, current) is { } vm) vm.IsExpanded = true;
            current = Path.GetDirectoryName(current);
        }
        ExpandTreeAncestorsOf(path);
    }

    private void NotifyFolderFailure(string titleKey, Exception ex)
    {
        LogService.Error("Operación de carpeta de skills fallida", ex);
        _notificationService.Push(LocalizationService.Instance[titleKey],
            VaultFailureReasons.Describe(ex), NotificationSeverity.Error);
    }

    // ===================== Editar =====================

    [RelayCommand]
    private void EditSkill(SkillViewModel? skill)
    {
        if (skill is null) return;

        if (skill.IsDefault)
        {
            BeginNamePrompt(LocalizationService.Instance["Skills_NamePrompt_EditDefault_Title"],
                LocalizationService.Instance["Skills_NamePrompt_CreateCopy_Confirm"],
                $"{skill.Id}{LocalizationService.Instance["Skills_NamePrompt_CopyIdSuffix"]}", newId =>
            {
                try
                {
                    var copy = _skillService.DuplicateSkill(skill.Model, newId);
                    Reload();
                    var copyVm = _allSkills.FirstOrDefault(s => PathsEqual(s.FilePath, copy.FilePath));
                    if (copyVm is not null) OpenEditor(copyVm);
                }
                catch (InvalidOperationException ex)
                {
                    _notificationService.Push(LocalizationService.Instance["Skills_Notify_CopyFailed_Title"], ex.Message, NotificationSeverity.Warning);
                }
            });
            return;
        }

        OpenEditor(skill);
    }

    private void OpenEditor(SkillViewModel skill)
    {
        SelectedSkill = skill;
        ActiveEditor = new SkillEditorViewModel(_skillService, skill.Model, () =>
        {
            skill.Refresh();
            RefreshTreeNodeFor(skill);
        }, _notificationService);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        ActiveEditor?.FlushPendingSave();
        ActiveEditor = null;
        Reload();
    }

    // ===================== Duplicar =====================

    [RelayCommand]
    private void DuplicateSkill(SkillViewModel? skill)
    {
        if (skill is null) return;

        BeginNamePrompt(LocalizationService.Instance["Skills_NamePrompt_Duplicate_Title"],
            LocalizationService.Instance["Skills_Detail_DuplicateButton"],
            $"{skill.Id}{LocalizationService.Instance["Skills_NamePrompt_CopyIdSuffix"]}", newId =>
        {
            try
            {
                _skillService.DuplicateSkill(skill.Model, newId);
                Reload();
            }
            catch (InvalidOperationException ex)
            {
                _notificationService.Push(LocalizationService.Instance["Skills_Notify_DuplicateFailed_Title"], ex.Message, NotificationSeverity.Warning);
            }
        });
    }

    private void BeginNamePrompt(string title, string confirmLabel, string initialText, Action<string> onConfirm)
    {
        _pendingNamePromptConfirm = onConfirm;
        NamePrompt = new SkillNamePromptViewModel(title, confirmLabel, initialText);
    }

    [RelayCommand]
    private void ConfirmNamePrompt()
    {
        if (NamePrompt is not { } prompt || _pendingNamePromptConfirm is not { } onConfirm) return;

        var value = prompt.InputText.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            prompt.ErrorMessage = LocalizationService.Instance["Skills_NamePrompt_EmptyNameError"];
            return;
        }

        NamePrompt = null;
        _pendingNamePromptConfirm = null;
        onConfirm(value);
    }

    [RelayCommand]
    private void CancelNamePrompt()
    {
        NamePrompt = null;
        _pendingNamePromptConfirm = null;
    }

    // ===================== Eliminar =====================
    // docs/agent-docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de
    // borrar" salta directo al borrado cuando está desactivado.

    [RelayCommand]
    private void RequestDeleteSkill(SkillViewModel? skill)
    {
        if (skill is null || skill.IsDefault) return;
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) PendingDeleteSkill = skill;
        else DeleteSkill(skill);
    }

    [RelayCommand]
    private void CancelDeleteSkill() => PendingDeleteSkill = null;

    [RelayCommand]
    private void ConfirmDeleteSkill()
    {
        if (PendingDeleteSkill is not { } skill) return;
        DeleteSkill(skill);
        PendingDeleteSkill = null;
    }

    private void DeleteSkill(SkillViewModel skill)
    {
        try
        {
            _skillService.DeleteSkill(skill.Model);
            if (SelectedSkill == skill) SelectedSkill = null;
            if (ActiveEditor?.Skill == skill.Model) ActiveEditor = null;
            Reload();
        }
        catch (InvalidOperationException ex)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_DeleteFailed_Title"], ex.Message, NotificationSeverity.Warning);
        }
    }

    // ===================== Importar =====================

    [RelayCommand]
    private async Task ImportSkills()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Skills_ImportFilesDialog_Title"],
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
        });

        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        if (paths.Count > 0) BeginImport(paths);
    }

    [RelayCommand]
    private async Task ImportSkillsFolder()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Skills_ImportFolderDialog_Title"],
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(folder)) BeginImport([folder]);
    }

    /// <summary>Punto de entrada común de las dos formas de importar. El destino es siempre
    /// <see cref="CurrentFolderPath"/>: importar mientras se navega un tema deja las skills nuevas en ese
    /// tema, que es lo que se espera de una biblioteca con carpetas.</summary>
    public void BeginImport(IReadOnlyList<string> paths)
    {
        var conflicts = DetectImportConflicts(paths);
        if (conflicts.Count == 0)
        {
            RunImport(paths, ConflictResolution.Overwrite);
            return;
        }

        _pendingImportPaths = paths;
        ImportConflictFileNames = conflicts;
    }

    private List<string> DetectImportConflicts(IReadOnlyList<string> paths)
    {
        var target = CurrentFolderPath;
        if (!Directory.Exists(target)) return [];

        return SkillService.ExpandMarkdownFiles(paths)
            .Select(pair => pair.RelativeDestination)
            .Where(relative => File.Exists(Path.Combine(target, relative)))
            .Select(relative => relative.Replace(Path.DirectorySeparatorChar, '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [RelayCommand]
    private void ResolveImportCancel()
    {
        _pendingImportPaths = null;
        ImportConflictFileNames = null;
    }

    [RelayCommand]
    private void ResolveImportRename() => ResolveImportConflict(ConflictResolution.Rename);

    [RelayCommand]
    private void ResolveImportOverwrite() => ResolveImportConflict(ConflictResolution.Overwrite);

    private void ResolveImportConflict(ConflictResolution resolution)
    {
        if (_pendingImportPaths is not { } paths) return;
        ImportConflictFileNames = null;
        _pendingImportPaths = null;
        RunImport(paths, resolution);
    }

    private void RunImport(IReadOnlyList<string> paths, ConflictResolution resolution)
    {
        try
        {
            var result = _skillService.Import(paths, resolution, CurrentFolderPath);
            Reload();

            if (result.Imported.Count > 0)
            {
                _notificationService.Push(LocalizationService.Instance["Skills_Notify_Imported_Title"],
                    Plural.Get("Skills_Notify_Imported_Message", result.Imported.Count, result.Imported.Count),
                    NotificationSeverity.Success);
            }
            if (result.Skipped.Count > 0)
            {
                _notificationService.Push(LocalizationService.Instance["Skills_Notify_SomeSkipped_Title"],
                    Plural.Get("Skills_Notify_Skipped_Message", result.Skipped.Count, result.Skipped.Count), NotificationSeverity.Warning);
            }
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_ImportFailed_Title"],
                LocalizationService.Instance["Common_Error_AccessDenied"], NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_ImportFailed_Title"],
                LocalizationService.Instance["Common_Error_IOFailure"], NotificationSeverity.Error);
        }
    }

    // ===================== Exportar =====================

    [RelayCommand]
    private async Task ExportSkills()
    {
        var selected = _allSkills.Where(s => s.IsCheckedForExport).ToList();
        if (selected.Count == 0 && SelectedSkill is not null) selected.Add(SelectedSkill);

        if (selected.Count == 0)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_NothingToExport_Title"],
                LocalizationService.Instance["Skills_Notify_NothingToExport_Message"],
                NotificationSeverity.Info);
            return;
        }

        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Skills_ExportDialog_Title"],
            AllowMultiple = false
        });

        var destination = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            SkillService.Export(selected.Select(s => s.Model).ToList(), destination);
            foreach (var skill in selected) skill.IsCheckedForExport = false;
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_Exported_Title"],
                Plural.Get("Skills_Notify_Exported_Message", selected.Count, selected.Count, destination),
                NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_ExportFailed_Title"],
                LocalizationService.Instance["Common_Error_AccessDenied"], NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(LocalizationService.Instance["Skills_Notify_ExportFailed_Title"],
                LocalizationService.Instance["Common_Error_IOFailure"], NotificationSeverity.Error);
        }
    }
}
