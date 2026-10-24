using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Backs the "Archivos" section — browsing/importing/previewing every non-<c>.md</c> file in the
/// vault (or a session's own root). Same skeleton as <see cref="NotesViewModel"/> (search debounce,
/// session picker, folder tree) but deliberately without a tab system: opening a file replaces
/// <see cref="ActivePreview"/> instead of adding to a collection of open editors, since files are only
/// ever viewed here, never edited — there's no "work in progress" to preserve across several of them at
/// once the way there is for notes (see docs/agent-docs/seccion-archivos.md §4).</summary>
public sealed partial class FilesViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly SessionService _sessionService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService? _settingsService;

    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<FileCardViewModel> Files { get; } = [];

    /// <summary>Elementos mostrados en la rejilla principal de Archivos: la tarjeta "..", las carpetas navegables
    /// y, tras ellas, los archivos de la carpeta actual. <see cref="Files"/> sigue siendo la fuente de las
    /// tarjetas de archivo; esta colección es la vista proyectada para la UI.</summary>
    public ObservableCollection<object> BrowserItems { get; } = [];

    public ObservableCollection<FileFolderViewModel> RootFolders { get; } = [];
    public ObservableCollection<SessionOptionViewModel> SessionOptions { get; } = [];

    /// <summary>Miga de pan de la rejilla: la raíz efectiva y cada carpeta hasta la actual.</summary>
    public ObservableCollection<FileBreadcrumbViewModel> Breadcrumbs { get; } = [];

    /// <summary>Opciones del desplegable de filtro por tipo de archivo — "Todos los archivos" más una entrada por
    /// cada <see cref="VaultFileKind"/> con archivos visibles.</summary>
    public ObservableCollection<FileKindFilterOption> FilterOptions { get; } = [];

    public ObservableCollection<FileSortOption> SortOptions { get; } = [];

    public string EffectiveRootPath => SelectedSessionOption?.Session?.RootPath ?? _vaultService.VaultPath;

    /// <summary>True while browsing a password-protected session that isn't currently unlocked — attachment
    /// file names are metadata, not decrypted content, but they can still be sensitive, so this page
    /// refuses to list them (not just refuses to preview them) while locked, same as the combined graph
    /// excludes a locked session's notes entirely rather than showing anything about them.</summary>
    public bool IsSelectedSessionLocked =>
        SelectedSessionOption?.Session is { IsEncrypted: true } session && !_sessionService.IsUnlocked(session);

    /// <summary>La búsqueda que aplicó la última recarga — no <see cref="SearchText"/> tal cual, que va 200 ms
    /// por delante mientras se teclea: los estados vacíos tienen que describir lo que la rejilla enseña.</summary>
    private string _appliedSearch = string.Empty;

    /// <summary>La rejilla enseña resultados de búsqueda: archivos de la carpeta actual y de todas sus
    /// subcarpetas, sin tarjetas de carpeta.</summary>
    public bool IsSearchActive => _appliedSearch.Length > 0;

    /// <summary>La raíz efectiva no tiene nada: invita a importar el primer archivo.</summary>
    public bool ShowEmptyState => IsEmpty && !IsSelectedSessionLocked && !IsSearchActive && CurrentFolder is null;

    /// <summary>Una subcarpeta vacía: dice que la carpeta existe pero no tiene nada, que no es lo mismo que un
    /// almacén vacío.</summary>
    public bool ShowFolderEmptyState => IsEmpty && !IsSelectedSessionLocked && !IsSearchActive && CurrentFolder is not null;

    /// <summary>Hay búsqueda y nada coincide — distinto de "no hay archivos aquí", que con texto escrito
    /// confundía una búsqueda sin resultados con una carpeta vacía.</summary>
    public bool ShowSearchEmptyState => IsEmpty && !IsSelectedSessionLocked && IsSearchActive;

    public string SearchEmptyMessage =>
        LocalizedTextFormatter.Resolve(LocalizedTextFormatter.Format("Files_SearchEmpty_Message", _appliedSearch));

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private FileCardViewModel? _activePreview;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private FileFolderViewModel? _currentFolder;

    [ObservableProperty]
    private bool _isRootDropTarget;

    /// <summary>Whether an OS drag is currently hovering the drop target that imports into the current
    /// destination — the file grid in the browser layout, anywhere outside a folder row in the tree layout —
    /// as opposed to a folder row, which highlights itself. Drives the drag-over highlight of both.</summary>
    [ObservableProperty]
    private bool _isGridDropTarget;

    [ObservableProperty]
    private SessionOptionViewModel? _selectedSessionOption;

    [ObservableProperty]
    private FileCardViewModel? _filePendingDelete;

    /// <summary>La carpeta que espera confirmación para irse a la papelera, venga de la fila del panel, de la
    /// tarjeta de la rejilla o del árbol. Borrar una carpeta arrastra todo lo que contiene y la sección Papelera
    /// no lista carpetas, así que no se borraba nunca sin preguntar... salvo aquí, donde era un clic.</summary>
    [ObservableProperty]
    private FileFolderCardViewModel? _folderPendingDelete;

    [ObservableProperty]
    private FileKindFilterOption? _selectedFilterOption;

    [ObservableProperty]
    private FileSortOption? _selectedSortOption;

    /// <summary>Confirmación momentánea del botón de copiar del visor de código (el icono pasa a un check).</summary>
    [ObservableProperty]
    private bool _isPreviewContentCopied;

    /// <summary>El <see cref="VaultFileKind"/> actualmente filtrado, o null si "Todos".</summary>
    private VaultFileKind? FilterKind => SelectedFilterOption?.Kind;

    public string CurrentFolderLabel => CurrentFolder?.Name ?? RootLabel;

    /// <summary>Nombre de la raíz efectiva: la sesión elegida, o "Todos los archivos" para el vault.</summary>
    private string RootLabel => SelectedSessionOption is { IsVault: false } option
        ? option.DisplayName
        : LocalizationService.Instance["Files_AllFolder"];

    /// <summary>Composed here (rather than a XAML <c>StringFormat</c>) so the word order around the count
    /// can vary per language — see docs/agent-docs/internacionalizacion-i18n.md §1.3. Kept in sync with
    /// <see cref="Files"/> via the collection-changed subscription below.</summary>
    public string FilesCountLabel => Plural.Get("Files_CountLabel", Files.Count, Files.Count);

    private string CurrentFolderPath => CurrentFolder?.FullPath ?? EffectiveRootPath;

    private bool _rebuildingFilter;
    private bool _rebuildingSort;

    /// <summary>Alzado mientras <see cref="ReloadFolders"/> recoloca <see cref="CurrentFolder"/> sobre la carpeta
    /// equivalente del panel recién montado: es la misma carpeta, así que no hay nada que recargar, y quien llama
    /// a <see cref="ReloadFolders"/> recarga la rejilla justo después.</summary>
    private bool _suspendReload;

    /// <summary>Silencia el guardado de las carpetas expandidas del panel mientras se expanden o restauran en
    /// bloque; se guarda una sola vez al final.</summary>
    private bool _batchingBrowserExpand;

    /// <summary>Renombrado de carpeta en curso (ruta vieja → nueva): mientras está puesto, las rutas recordadas
    /// —carpeta actual, carpetas expandidas en los dos árboles, selección del árbol— se trasladan a la ruta
    /// nueva al recargar, en vez de perderse porque la carpeta de antes ya no existe.</summary>
    private (string OldPath, string NewPath)? _folderRemap;

    private int _copyFeedbackToken;

    public FilesViewModel(VaultService vaultService, SessionService sessionService, NotificationService notificationService,
        SettingsService? settingsService = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _notificationService = notificationService;
        InitializeFolderManager();
        _settingsService = settingsService;
        if (_settingsService is not null)
            _settingsService.WorkspaceSessionChanged += SelectSession;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            Reload();
        };
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilesCountLabel));

        // Antes de la primera carga: así la sección aparece ya en la disposición y el orden que el usuario dejó
        // (ver FilesViewModel.Tree.cs), sin un parpadeo intermedio.
        InitializeNavigationPreferences();
        BuildSortOptions();
        ReloadSessions();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        FolderManager.RefreshLocalizedLabels();
        BuildSortOptions();
        OnPropertyChanged(nameof(CurrentFolderLabel));
        OnPropertyChanged(nameof(FilesCountLabel));
        OnPropertyChanged(nameof(TreeFilesCountLabel));
        ActivePreview?.RefreshLocalizedLabels();
        Reload();
    }

    /// <summary>Vuelve a leer del disco el panel de carpetas y la rejilla. Lo usa el shell al entrar en la
    /// sección: una carpeta creada desde Notas, o desde el explorador del sistema, tiene que aparecer también
    /// en el panel, no solo como tarjeta.</summary>
    public void Refresh()
    {
        ReloadFolders();
        Reload();
    }

    partial void OnSelectedFilterOptionChanged(FileKindFilterOption? value)
    {
        if (!_rebuildingFilter) Reload();
    }

    partial void OnSelectedSortOptionChanged(FileSortOption? value)
    {
        if (_rebuildingSort || value is null) return;

        if (_settingsService is not null)
        {
            _settingsService.Settings.FilesSortMode = value.Mode.ToString();
            _settingsService.Save();
        }
        Reload();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnCurrentFolderChanged(FileFolderViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentFolderLabel));
        SyncBrowserFolderSelection();
        if (!_suspendReload) Reload();
    }

    partial void OnSelectedSessionOptionChanged(SessionOptionViewModel? value)
    {
        FolderManager.CloseCommand.Execute(null);
        OnPropertyChanged(nameof(EffectiveRootPath));
        OnPropertyChanged(nameof(IsSelectedSessionLocked));
        OnPropertyChanged(nameof(ShowTreeEmptyState));
        SetCurrentFolderQuietly(null);
        PersistSelectedSession();
        ReloadFolders();
        Reload();
    }

    /// <summary>Publica la sesión común a Notas y Archivos y la guarda entre arranques.</summary>
    private void PersistSelectedSession()
    {
        _settingsService?.SelectWorkspaceSession(SelectedSessionOption?.Session?.Id);
    }

    partial void OnIsEmptyChanged(bool value) => NotifyBrowserStateChanged();

    /// <summary>Rehace el selector de espacio de trabajo (el vault más cada sesión) conservando la elección
    /// actual si sigue existiendo. Se llama en el arranque y cada vez que se crea/renombra/borra una sesión,
    /// haya pasado aquí o en la página de Sesiones.</summary>
    public void ReloadSessions(bool restoreStoredSelection = false)
    {
        // En el arranque todavía no hay selección viva, y ahí es donde entra la guardada: la sesión con la
        // que se cerró la app. Las recargas normales conservan la selección viva; al cambiar de vault
        // se restaura expresamente la elección guardada para el nuevo vault.
        var targetId = restoreStoredSelection
            ? _settingsService?.Settings.WorkspaceSessionId
            : SelectedSessionOption?.Session?.Id ?? _settingsService?.Settings.WorkspaceSessionId;

        SessionOptions.Clear();
        SessionOptions.Add(new SessionOptionViewModel(null));
        foreach (var session in _sessionService.List()) SessionOptions.Add(new SessionOptionViewModel(session));

        // Una sesión protegida arranca siempre bloqueada y aun así se vuelve a elegir: la sección ya sabe
        // enseñar ese estado, y devolver al usuario al vault sería perder justo la elección que se quería
        // recordar. Una que ya no exista —borrada, o de otro vault— cae al vault en silencio.
        SelectedSessionOption = targetId is null
            ? SessionOptions[0]
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == targetId) ?? SessionOptions[0];
    }

    public void SelectSession(string? sessionId)
    {
        var match = sessionId is null
            ? SessionOptions.FirstOrDefault(o => o.IsVault)
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == sessionId);
        if (match is not null) SelectedSessionOption = match;
    }

    // ===================== Rejilla =====================

    public void Reload()
    {
        if (IsSelectedSessionLocked) FolderManager.CloseCommand.Execute(null);
        PasteEntryCommand.NotifyCanExecuteChanged();
        CopyEntryCommand.NotifyCanExecuteChanged();
        CutEntryCommand.NotifyCanExecuteChanged();
        _appliedSearch = SearchText.Trim();
        if (ActivePreview is { } open && (_vaultService.IsPathInLockedSession(open.File.FullPath)
            || !IsSameOrUnder(open.File.FullPath, EffectiveRootPath)))
        {
            open.PdfDocument?.Close();
            ActivePreview = null;
        }

        // Drop any decrypted PDF temp copies the outgoing cards were holding onto before discarding them —
        // Reload() runs on every folder switch/search keystroke, so this is the main point at which those
        // temp files would otherwise leak for the lifetime of the process. La tarjeta que se está
        // previsualizando queda fuera: su PdfDocument sujeta la copia descifrada que el visor sigue leyendo
        // página a página, y además se reutiliza tal cual en la rejilla nueva (ver abajo).
        foreach (var card in Files)
        {
            if (!ReferenceEquals(card, ActivePreview)) card.PdfDocument?.Close();
        }

        if (IsSelectedSessionLocked)
        {
            Files.Clear();
            BrowserItems.Clear();
            Breadcrumbs.Clear();
            IsEmpty = true;
            NotifyBrowserStateChanged();
            AfterReload();
            return;
        }

        var folderPath = CurrentFolderPath;

        // Buscando se baja a las subcarpetas: buscar un archivo que no se sabe dónde está es justo para lo que
        // se busca, y limitarlo a la carpeta abierta devolvía "sin resultados" para algo que sí existe.
        var all = IsSearchActive ? _vaultService.GetFilesRecursive(folderPath) : _vaultService.GetFiles(folderPath);

        IEnumerable<VaultFile> visible = FilterKind is { } kind ? all.Where(f => f.Kind == kind) : all;
        if (IsSearchActive)
            visible = visible.Where(f => f.FileName.Contains(_appliedSearch, StringComparison.OrdinalIgnoreCase));

        Files.Clear();
        foreach (var file in SortFiles(visible))
        {
            // La tarjeta abierta se reutiliza, igual que en el árbol: recargar (importar, mover, cambiar el orden)
            // no debe dejar la previsualización colgando de una tarjeta que ya no está en la rejilla, que es de
            // donde salen el archivo anterior y el siguiente.
            var card = ActivePreview is { } preview && PathsEqual(preview.File.FullPath, file.FullPath)
                ? preview
                : new FileCardViewModel(_vaultService, file);
            card.LocationLabel = IsSearchActive ? RelativeLocation(folderPath, Path.GetDirectoryName(file.FullPath)) : string.Empty;
            Files.Add(card);
        }

        BrowserItems.Clear();
        var hasFolderCards = false;
        if (!IsSearchActive)
        {
            if (CurrentFolder is not null)
            {
                // La tarjeta de subir lleva el nombre de a dónde sube —"Todos los archivos", la sesión o la carpeta
                // padre—, con "Subir un nivel" debajo: un ".." en negrita se leía como un fallo de renderizado.
                var parentPath = Path.GetDirectoryName(CurrentFolder.FullPath) ?? EffectiveRootPath;
                var parentName = PathsEqual(parentPath, EffectiveRootPath) ? RootLabel : Path.GetFileName(parentPath);
                BrowserItems.Add(new FileFolderCardViewModel(parentName, parentPath, isParent: true));
            }

            foreach (var folder in _vaultService.GetFolderTree(folderPath))
            {
                BrowserItems.Add(new FileFolderCardViewModel(folder.Name, folder.FullPath,
                    entryCount: VaultService.CountFolderEntries(folder.FullPath)));
                hasFolderCards = true;
            }
        }

        foreach (var file in Files) BrowserItems.Add(file);

        IsEmpty = Files.Count == 0 && !hasFolderCards;
        RebuildFilterOptions(all);
        RebuildBreadcrumbs();
        NotifyBrowserStateChanged();
        AfterReload();
        UpdateCutAppearance();
    }

    /// <summary>Lo que va detrás de cada recarga en las dos disposiciones. El árbol se reconstruye aquí, pero
    /// solo mientras ese modo esté activo: montarlo enumera carpeta por carpeta toda la raíz efectiva, y
    /// Reload() corre en cada cambio de carpeta, de filtro y de búsqueda de la rejilla. Al volver a entrar en el
    /// modo árbol se reconstruye de nuevo (ver OnIsTreeViewChanged en FilesViewModel.Tree.cs).</summary>
    private void AfterReload()
    {
        if (IsTreeView) RebuildTree();
        else RefreshPreviewNavigation();
    }

    private void NotifyBrowserStateChanged()
    {
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowFolderEmptyState));
        OnPropertyChanged(nameof(ShowSearchEmptyState));
        OnPropertyChanged(nameof(SearchEmptyMessage));
        OnPropertyChanged(nameof(CurrentFolderLabel));
    }

    private IEnumerable<VaultFile> SortFiles(IEnumerable<VaultFile> files) => (SelectedSortOption?.Mode ?? FileSortMode.Modified) switch
    {
        FileSortMode.Name => files.OrderBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase),
        FileSortMode.Size => files.OrderByDescending(f => f.SizeBytes).ThenBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase),
        FileSortMode.Type => files.OrderBy(f => f.Kind)
            .ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase),
        _ => files.OrderByDescending(f => f.ModifiedUtc)
    };

    /// <summary>Carpeta de <paramref name="folderPath"/> relativa a <paramref name="basePath"/>, con "›" entre
    /// niveles, o vacía si es la misma carpeta.</summary>
    private static string RelativeLocation(string basePath, string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || PathsEqual(folderPath, basePath)) return string.Empty;

        var relative = Path.GetRelativePath(basePath, folderPath);
        return relative == "."
            ? string.Empty
            : string.Join(" › ", relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private void BuildSortOptions()
    {
        _rebuildingSort = true;
        try
        {
            var current = SelectedSortOption?.Mode
                          ?? (Enum.TryParse<FileSortMode>(_settingsService?.Settings.FilesSortMode, out var saved)
                              ? saved
                              : FileSortMode.Modified);

            SortOptions.Clear();
            SortOptions.Add(new FileSortOption(FileSortMode.Modified, LocalizationService.Instance["Files_Sort_Modified"]));
            SortOptions.Add(new FileSortOption(FileSortMode.Name, LocalizationService.Instance["Files_Sort_Name"]));
            SortOptions.Add(new FileSortOption(FileSortMode.Size, LocalizationService.Instance["Files_Sort_Size"]));
            SortOptions.Add(new FileSortOption(FileSortMode.Type, LocalizationService.Instance["Files_Sort_Type"]));

            SelectedSortOption = SortOptions.FirstOrDefault(o => o.Mode == current) ?? SortOptions[0];
        }
        finally
        {
            _rebuildingSort = false;
        }
    }

    /// <summary>Reconstruye las opciones del filtro por tipo cada vez que cambia la carpeta actual,
    /// mostrando solo los <see cref="VaultFileKind"/> que realmente tienen archivos en esta vista.</summary>
    private void RebuildFilterOptions(List<VaultFile> allFiles)
    {
        _rebuildingFilter = true;
        try
        {
            var currentFilter = FilterKind;
            FilterOptions.Clear();
            FilterOptions.Add(new FileKindFilterOption(null, LocalizationService.Instance["Files_FilterAll"]));

            foreach (VaultFileKind kind in Enum.GetValues<VaultFileKind>())
            {
                if (!allFiles.Any(f => f.Kind == kind)) continue;
                var label = kind switch
                {
                    VaultFileKind.Image => LocalizationService.Instance["Files_FilterImages"],
                    VaultFileKind.Pdf => LocalizationService.Instance["Files_FilterPdfs"],
                    VaultFileKind.Audio => LocalizationService.Instance["Files_FilterAudio"],
                    VaultFileKind.Video => LocalizationService.Instance["Files_FilterVideo"],
                    VaultFileKind.Code => LocalizationService.Instance["Files_FilterCode"],
                    VaultFileKind.Other => LocalizationService.Instance["Files_FilterOther"],
                    _ => kind.ToString()
                };
                FilterOptions.Add(new FileKindFilterOption(kind, label));
            }

            // Mantener el filtro actual si aún es válido; si no, volver a "Todos"
            SelectedFilterOption = currentFilter is not null && FilterOptions.Any(o => o.Kind == currentFilter)
                ? FilterOptions.First(o => o.Kind == currentFilter)
                : FilterOptions.FirstOrDefault();
        }
        finally
        {
            _rebuildingFilter = false;
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();

        var segments = new List<string>();
        if (CurrentFolder is not null)
        {
            var root = EffectiveRootPath;
            var current = CurrentFolder.FullPath;
            while (!string.IsNullOrEmpty(current) && !PathsEqual(current, root))
            {
                segments.Insert(0, current);
                current = Path.GetDirectoryName(current);
            }
        }

        Breadcrumbs.Add(new FileBreadcrumbViewModel(RootLabel, null, isLast: segments.Count == 0));
        for (var i = 0; i < segments.Count; i++)
            Breadcrumbs.Add(new FileBreadcrumbViewModel(Path.GetFileName(segments[i]), segments[i], isLast: i == segments.Count - 1));
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(FileBreadcrumbViewModel? crumb)
    {
        if (crumb is null || crumb.IsLast) return;
        CurrentFolder = crumb.FullPath is null ? null : FindFolder(RootFolders, crumb.FullPath);
    }

    /// <summary>Sube un nivel desde la carpeta actual (Retroceso en la rejilla). No-op en la raíz efectiva.</summary>
    [RelayCommand]
    private void NavigateUp()
    {
        if (CurrentFolder is null) return;
        var parentPath = Path.GetDirectoryName(CurrentFolder.FullPath);
        CurrentFolder = parentPath is null || PathsEqual(parentPath, EffectiveRootPath) ? null : FindFolder(RootFolders, parentPath);
    }

    // ===================== Panel de carpetas =====================

    public void ReloadFolders()
    {
        if (IsSelectedSessionLocked)
        {
            RootFolders.Clear();
            SetCurrentFolderQuietly(null);
            return;
        }

        // On the very first call RootFolders is empty, so there is no in-memory expand state to
        // preserve — fall back to whatever the user had last time (persisted in settings). On
        // every subsequent call the in-memory state (kept up-to-date by the OnExpandedChanged
        // callback) is authoritative.
        var expandedPaths = (RootFolders.Count > 0
                ? CollectExpandedPaths(RootFolders)
                : _settingsService?.Settings.FilesExpandedFolderPaths ?? [])
            .Select(RemapPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPath = CurrentFolder?.FullPath is { } current ? RemapPath(current) : null;

        _batchingBrowserExpand = true;
        try
        {
            RootFolders.Clear();
            foreach (var folder in _vaultService.GetFolderTree(EffectiveRootPath))
            {
                var vm = new FileFolderViewModel(folder);
                ApplyExpandedState(vm, expandedPaths);
                RootFolders.Add(vm);
            }
        }
        finally
        {
            _batchingBrowserExpand = false;
        }

        SetCurrentFolderQuietly(selectedPath is null ? null : FindFolder(RootFolders, selectedPath));
        if (_folderRemap is not null) PersistExpandedFolders();
    }

    private void SetCurrentFolderQuietly(FileFolderViewModel? folder)
    {
        _suspendReload = true;
        try
        {
            CurrentFolder = folder;
        }
        finally
        {
            _suspendReload = false;
        }
    }

    private static IEnumerable<string> CollectExpandedPaths(IEnumerable<FileFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            if (folder.IsExpanded) yield return folder.FullPath;
            foreach (var path in CollectExpandedPaths(folder.Children)) yield return path;
        }
    }

    private void ApplyExpandedState(FileFolderViewModel folder, HashSet<string> expandedPaths)
    {
        folder.IsExpanded = expandedPaths.Contains(folder.FullPath);
        folder.OnExpandedChanged = PersistExpandedFolders;
        foreach (var child in folder.Children) ApplyExpandedState(child, expandedPaths);
    }

    /// <summary>Writes the currently expanded folder paths to <see cref="AppSettings"/> so they
    /// survive an app restart — called from <see cref="FileFolderViewModel.OnExpandedChanged"/>
    /// whenever the user toggles any folder in the sidebar.</summary>
    private void PersistExpandedFolders()
    {
        if (_settingsService is null || _batchingBrowserExpand) return;
        _settingsService.Settings.FilesExpandedFolderPaths = CollectExpandedPaths(RootFolders).ToList();
        _settingsService.Save();
    }

    /// <summary>Resalta en el panel la fila de la carpeta en la que se está —la fila tenía el estilo, pero nadie
    /// le decía que estaba seleccionada— y abre las carpetas que llevan hasta ella, para que entrar desde una
    /// tarjeta de la rejilla o desde la miga de pan deje visible en el panel dónde se ha llegado.</summary>
    private void SyncBrowserFolderSelection()
    {
        foreach (var folder in FlattenFolders(RootFolders)) folder.IsSelected = ReferenceEquals(folder, CurrentFolder);
        if (CurrentFolder is null) return;

        var chain = new List<FileFolderViewModel>();
        if (!TryFindFolderChain(RootFolders, CurrentFolder, chain)) return;

        var changed = false;
        _batchingBrowserExpand = true;
        try
        {
            foreach (var ancestor in chain.Where(a => !a.IsExpanded))
            {
                ancestor.IsExpanded = true;
                changed = true;
            }
        }
        finally
        {
            _batchingBrowserExpand = false;
        }

        if (changed) PersistExpandedFolders();
    }

    private static IEnumerable<FileFolderViewModel> FlattenFolders(IEnumerable<FileFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;
            foreach (var child in FlattenFolders(folder.Children)) yield return child;
        }
    }

    /// <summary>Rellena <paramref name="chain"/> con los antepasados de <paramref name="target"/>, de la raíz
    /// hacia abajo, sin incluirlo a él.</summary>
    private static bool TryFindFolderChain(IEnumerable<FileFolderViewModel> folders, FileFolderViewModel target,
        List<FileFolderViewModel> chain)
    {
        foreach (var folder in folders)
        {
            if (ReferenceEquals(folder, target)) return true;
            chain.Add(folder);
            if (TryFindFolderChain(folder.Children, target, chain)) return true;
            chain.RemoveAt(chain.Count - 1);
        }
        return false;
    }

    private static FileFolderViewModel? FindFolder(IEnumerable<FileFolderViewModel> folders, string path)
    {
        foreach (var folder in folders)
        {
            if (PathsEqual(folder.FullPath, path)) return folder;
            var found = FindFolder(folder.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    // ===================== Importar =====================

    /// <summary>Abre el selector de archivos del sistema e importa lo elegido. El destino es la carpeta
    /// actual de la rejilla salvo que se pase uno explícito: el modo árbol pasa su raíz efectiva o la carpeta
    /// de la fila desde cuyo menú se importa, y el panel del explorador, la carpeta de la fila.</summary>
    [RelayCommand]
    private async Task Import(string? destinationFolderPath)
    {
        var topLevel = App.MainWindowRef is not null
            ? Avalonia.Controls.TopLevel.GetTopLevel(App.MainWindowRef)
            : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Files_ImportFilesDialog_Title"],
            AllowMultiple = true
        });

        ImportPaths(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!),
            destinationFolderPath);
    }

    /// <summary>Shared entry point for every way a file can land in the vault — the "IMPORTAR" picker and
    /// dropping files from the OS file explorer onto the grid, a folder row or the tree — all funnel through
    /// the same <see cref="VaultService.ImportFile"/> call, just with a different source.</summary>
    public void ImportPaths(IEnumerable<string> sourcePaths, string? destinationFolderPath = null)
    {
        var destination = destinationFolderPath ?? CurrentFolderPath;
        var importedPaths = new List<string>();
        Exception? failure = null;

        foreach (var path in sourcePaths)
        {
            try
            {
                importedPaths.Add(_vaultService.ImportFile(path, destination).FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
            {
                // Importar a una sesión protegida y bloqueada cifra con una clave que no está en memoria, y
                // eso lanza SessionLockedException desde dentro de un manejador de Drop: sin este catch la
                // excepción salía a la cara del usuario en vez de contarle qué pasa. Se guarda solo la
                // primera —un lote entero suele fallar por el mismo motivo, y no tiene sentido apilar un
                // aviso por archivo— pero se sigue intentando el resto, que puede que sí entre.
                failure ??= ex;
            }
        }

        if (importedPaths.Count > 0)
        {
            Reload();

            // En el modo árbol el panel derecho es la previsualización: con un solo archivo importado se abre
            // directamente, en vez de dejar al usuario buscándolo en el árbol. Con varios se revela dónde han
            // caído sin elegir uno por él.
            if (IsTreeView)
            {
                ExpandTreeAncestorsOf(importedPaths[0]);
                if (importedPaths.Count == 1 && FindTreeCardByPath(importedPaths[0]) is { } card) OpenCard(card);
            }
        }

        if (failure is not null) NotifyFilesFailure("Files_Notify_ImportFailed_Title", failure);
    }

    // ===================== Previsualización =====================

    [RelayCommand]
    private void OpenCard(FileCardViewModel card)
    {
        LoadPreviewContent(card);
        ActivePreview = card;
    }

    /// <summary>Carga perezosa de lo que la previsualización necesita para dibujar esta tarjeta —cada
    /// llamada es no-op si ya está cargado, así que se puede pedir tantas veces como haga falta. Está
    /// aparte de <see cref="OpenCard"/> porque hay más momentos en que hace falta: renombrar o mover el
    /// archivo abierto, que dejan la tarjeta en blanco (ver <see cref="FileCardViewModel.UpdateFile"/>).</summary>
    private void LoadPreviewContent(FileCardViewModel card)
    {
        card.EnsureThumbnailLoaded();
        card.EnsurePreviewImageLoaded();
        card.EnsurePdfDocumentLoaded();
        card.EnsureCodeContentLoaded();
        card.EnsureMediaPreviewLoaded(message => _notificationService.Push(
            LocalizationService.Instance["Files_MediaErrorTitle"], message, NotificationSeverity.Error));
    }

    [RelayCommand]
    private void ClosePreview() => ActivePreview = null;

    /// <summary>Los archivos entre los que se mueven "anterior" y "siguiente": en el modo árbol, todos los del
    /// árbol en el orden en que se leen; en el explorador, los de la rejilla con su orden, filtro y búsqueda.</summary>
    private IReadOnlyList<FileCardViewModel> PreviewSequence => IsTreeView ? _treeCards : Files;

    private int PreviewIndex()
    {
        if (ActivePreview is not { } preview) return -1;

        var sequence = PreviewSequence;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (ReferenceEquals(sequence[i], preview)) return i;
        }
        for (var i = 0; i < sequence.Count; i++)
        {
            if (PathsEqual(sequence[i].File.FullPath, preview.File.FullPath)) return i;
        }
        return -1;
    }

    public bool CanPreviewPrevious => PreviewIndex() > 0;

    public bool CanPreviewNext => PreviewIndex() is var index && index >= 0 && index < PreviewSequence.Count - 1;

    /// <summary>"3 / 12": en qué punto de la carpeta (o del árbol) está el archivo abierto.</summary>
    public string PreviewPositionLabel => PreviewIndex() is var index && index >= 0
        ? $"{index + 1} / {PreviewSequence.Count}"
        : string.Empty;

    [RelayCommand(CanExecute = nameof(CanPreviewPrevious))]
    private void PreviewPrevious() => StepPreview(-1);

    [RelayCommand(CanExecute = nameof(CanPreviewNext))]
    private void PreviewNext() => StepPreview(1);

    private void StepPreview(int delta)
    {
        var sequence = PreviewSequence;
        var index = PreviewIndex();
        var target = index + delta;
        if (index < 0 || target < 0 || target >= sequence.Count) return;
        OpenCard(sequence[target]);
    }

    private void RefreshPreviewNavigation()
    {
        OnPropertyChanged(nameof(CanPreviewPrevious));
        OnPropertyChanged(nameof(CanPreviewNext));
        OnPropertyChanged(nameof(PreviewPositionLabel));
        PreviewPreviousCommand.NotifyCanExecuteChanged();
        PreviewNextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Abrir con la aplicación del sistema" — the universal fallback for anything this section
    /// doesn't render an in-app preview for (audio, video, every <see cref="VaultFileKind.Other"/> file), and
    /// a second option for everything else. Unlike the editor's identical link-opening call (which ignores a
    /// failure silently because a broken markdown link is low-stakes), this is the one action a lot of this
    /// screen exists to perform, so a failure here is surfaced instead of swallowed.</summary>
    [RelayCommand]
    private void OpenWithSystem(FileCardViewModel? card)
    {
        if (card is null) return;

        var readablePath = card.File.FullPath;
        var isTempCopy = false;
        try
        {
            // A file inside a protected session is ciphertext on disk — the OS shell can't open that
            // directly, so hand it a decrypted temp copy instead. Throws SessionLockedException if the
            // session isn't unlocked, caught below alongside every other failure this command can hit.
            readablePath = _vaultService.ResolveReadablePath(card.File.FullPath);
            isTempCopy = readablePath != card.File.FullPath;

            Process.Start(new ProcessStartInfo(readablePath) { UseShellExecute = true });

            // UseShellExecute hands the file to whatever app the OS picks and returns immediately — there's
            // no reliable cross-platform signal for "that app is done reading it," so an immediate delete
            // would race a slow-to-open viewer. Cleaning up after a delay is a documented best-effort
            // tradeoff, not a leak-proof guarantee (see docs plan for "Sesiones protegidas con contraseña").
            if (isTempCopy)
            {
                var tempPath = readablePath;
                _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => VaultService.TryCleanupDecryptedTempFile(tempPath));
            }
        }
        catch (Exception ex)
        {
            if (isTempCopy) VaultService.TryCleanupDecryptedTempFile(readablePath);
            LogService.Warning($"No se pudo abrir '{card.File.FullPath}' con la aplicación del sistema", ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Files_OpenWithSystem_FailedTitle"),
                LocalizedTextFormatter.Format("Files_OpenWithSystem_FailedMessage", card.FileName),
                NotificationSeverity.Warning);
        }
    }

    /// <summary>Copia al portapapeles la ruta completa de un archivo o carpeta — para pegarla en una nota, en un
    /// terminal o en otra aplicación sin tener que ir a buscarla al explorador del sistema.</summary>
    [RelayCommand]
    private async Task CopyPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || GetClipboard() is not { } clipboard) return;

        await clipboard.SetTextAsync(path);
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Files_Notify_PathCopied_Title"),
            LocalizedTextFormatter.Literal(path),
            NotificationSeverity.Success);
    }

    /// <summary>Copia el texto del archivo que enseña el visor de código. La confirmación es el propio botón —
    /// su icono pasa a un check un momento— y no una notificación: es un gesto que se repite a menudo y
    /// apilaría ruido en el centro de notificaciones.</summary>
    [RelayCommand]
    private async Task CopyPreviewContent()
    {
        if (ActivePreview?.CodeDocument is not { } document || GetClipboard() is not { } clipboard) return;

        await clipboard.SetTextAsync(document.Text);
        var token = ++_copyFeedbackToken;
        IsPreviewContentCopied = true;
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        if (token == _copyFeedbackToken) IsPreviewContentCopied = false;
    }

    private static IClipboard? GetClipboard() =>
        App.MainWindowRef is not null ? Avalonia.Controls.TopLevel.GetTopLevel(App.MainWindowRef)?.Clipboard : null;

    /// <summary>"Mostrar en el explorador de archivos" para una carpeta. CA1822 sugiere marcarlo <c>static</c>
    /// porque no toca estado de instancia, pero el generador de <c>[RelayCommand]</c> necesita un método de
    /// instancia para exponer <c>RevealFolderCommand</c> a los menús contextuales.</summary>
#pragma warning disable CA1822
    [RelayCommand]
    private void RevealFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path)) FolderOpener.Open(path);
    }
#pragma warning restore CA1822

    // ===================== Carpetas: navegar, crear, renombrar, borrar =====================

    [RelayCommand]
    private void SelectFolder(FileFolderViewModel? folder) => CurrentFolder = folder;

    [RelayCommand]
    private void OpenFolderCard(FileFolderCardViewModel card)
    {
        if (card.IsRenaming) return;

        CurrentFolder = card.IsParent && PathsEqual(card.FullPath, EffectiveRootPath)
            ? null
            : FindFolder(RootFolders, card.FullPath);
    }

    /// <summary>"Crear subcarpeta" desde el menú de una fila del panel: la nueva carpeta aparece dentro de esa
    /// fila, que se despliega para que el cuadro de renombrar sea visible — si no, quedaba escondido bajo una
    /// carpeta plegada, sin foco y sin forma de salir de él.</summary>
    [RelayCommand]
    private void CreateSubfolder(FileFolderViewModel folder)
    {
        if (TryCreateFolder(folder.FullPath) is not { } created) return;
        Refresh();

        if (FindFolder(RootFolders, created.FullPath) is not { } vm) return;
        if (FindFolder(RootFolders, folder.FullPath) is { IsExpanded: false } parent) parent.IsExpanded = true;
        var chain = new List<FileFolderViewModel>();
        if (TryFindFolderChain(RootFolders, vm, chain))
        {
            foreach (var ancestor in chain) ancestor.IsExpanded = true;
        }
        vm.EditingName = vm.Name;
        vm.IsRenaming = true;
    }

    /// <summary>"Nueva carpeta" de la barra del explorador: se crea en la carpeta que se está viendo y se
    /// renombra en su tarjeta de la rejilla, que es donde el usuario está mirando.</summary>
    [RelayCommand]
    private void NewFolder()
    {
        if (TryCreateFolder(CurrentFolderPath) is not { } created) return;
        Refresh();

        var card = BrowserItems.OfType<FileFolderCardViewModel>()
            .FirstOrDefault(c => !c.IsParent && PathsEqual(c.FullPath, created.FullPath));
        card?.BeginRenameCommand.Execute(null);
    }

    private NoteFolder? TryCreateFolder(string parentPath)
    {
        NoteFolder? created = null;
        TryVaultOperation("Files_Notify_FolderCreateFailed_Title", () => created =
            _vaultService.CreateManagedFolder(parentPath, LocalizationService.Instance["Vault_DefaultFolderName"]));
        return created;
    }

    [RelayCommand]
    private void CommitRenameFolder(FileFolderViewModel folder)
    {
        var newName = folder.EditingName.Trim();
        folder.IsRenaming = false;
        if (newName.Length == 0 || newName == folder.Name) return;
        RenameFolderAt(folder.Folder, newName);
    }

    [RelayCommand]
    private void CommitRenameFolderCard(FileFolderCardViewModel card)
    {
        var newName = card.EditingName.Trim();
        card.IsRenaming = false;
        if (card.IsParent || newName.Length == 0 || newName == card.Name) return;
        RenameFolderAt(FindFolder(RootFolders, card.FullPath)?.Folder ?? new NoteFolder { FullPath = card.FullPath }, newName);
    }

    /// <summary>Renombra una carpeta y lleva a la ruta nueva todo lo que apuntaba a la vieja. Antes, renombrar la
    /// carpeta en la que se estaba devolvía al usuario a la raíz (la carpeta actual ya no existía con esa ruta), y
    /// renombrar la que contenía el archivo abierto dejaba la previsualización leyendo de una ruta muerta.</summary>
    private void RenameFolderAt(NoteFolder folder, string newName)
    {
        TryVaultOperation("Files_Notify_FolderRenameFailed_Title", () => _vaultService.RenameManagedFolder(folder.FullPath, newName));
    }

    /// <summary>Pide confirmación antes de mandar una carpeta a la papelera, venga de donde venga: la fila del
    /// panel del explorador, la tarjeta de la rejilla o un nodo del árbol. Respeta el ajuste
    /// <c>ConfirmBeforeDelete</c>, igual que el borrado de archivos.</summary>
    [RelayCommand]
    private void RequestDeleteFolder(object? target)
    {
        (string? Name, string? Path) folder = target switch
        {
            FileFolderViewModel row => (row.Name, row.FullPath),
            FileFolderCardViewModel { IsParent: false } card => (card.Name, card.FullPath),
            FileTreeItemViewModel { IsFolder: true } node => (node.Name, node.FullPath),
            _ => (null, null)
        };
        if (folder.Name is null || folder.Path is null) return;

        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true)
            FolderPendingDelete = new FileFolderCardViewModel(folder.Name, folder.Path);
        else
            DeleteFolderAt(folder.Path);
    }

    [RelayCommand]
    private void CancelDeleteFolder() => FolderPendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteFolder()
    {
        if (FolderPendingDelete is not { } pending) return;
        FolderPendingDelete = null;
        DeleteFolderAt(pending.FullPath);
    }

    private void DeleteFolderAt(string folderPath)
    {
        if (ActivePreview is { } playing && IsSameOrUnder(playing.File.FullPath, folderPath)) playing.MediaPreview?.Close();
        if (!TryVaultOperation("Files_Notify_FolderDeleteFailed_Title",
                () => _vaultService.DeleteFolder(new NoteFolder { FullPath = folderPath })))
            return;

        // El archivo abierto se ha ido a la papelera con su carpeta: dejar la previsualización abierta enseñaría
        // algo que ya no está donde dice, y "abrir con el sistema" fallaría.
        if (ActivePreview is { } preview && IsSameOrUnder(preview.File.FullPath, folderPath))
        {
            preview.PdfDocument?.Close();
            ActivePreview = null;
        }
        if (CurrentFolder is not null && IsSameOrUnder(CurrentFolder.FullPath, folderPath)) SetCurrentFolderQuietly(null);

        _notificationService.Push(LocalizationService.Instance["Folders_Done"], Path.GetFileName(folderPath), NotificationSeverity.Warning);
        Refresh();
    }

    // ===================== Archivos: renombrar, borrar, mover =====================

    [RelayCommand]
    private void CommitRenameFile(FileCardViewModel card)
    {
        var newName = card.EditingName.Trim();
        card.IsRenaming = false;
        if (newName.Length == 0 || newName == card.FileName) return;

        card.MediaPreview?.Close();
        if (!TryVaultOperation("Files_Notify_FileRenameFailed_Title",
                () => card.UpdateFile(_vaultService.RenameFile(card.File, newName))))
            return;

        // UpdateFile deja la tarjeta como recién creada —sin miniatura, sin PDF y sin código—, y en el modo
        // árbol se renombra en línea con la previsualización abierta al lado: sin rearmarla, el panel derecho
        // se queda en blanco hasta que se vuelve a pulsar el archivo.
        if (ReferenceEquals(ActivePreview, card)) LoadPreviewContent(card);
        else card.EnsureThumbnailLoaded();
        Reload();
    }

    // análisis alfa-3 §4.5 — "preguntar antes de borrar" se saltaba en silencio para Archivos, a
    // diferencia de Hábitos/Polar/Skills/Kanban/Calendario, que sí lo respetan.

    [RelayCommand]
    private void RequestDeleteFile(FileCardViewModel? card)
    {
        if (card is null) return;
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) FilePendingDelete = card;
        else DeleteFile(card);
    }

    [RelayCommand]
    private void CancelDeleteFile() => FilePendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteFile()
    {
        if (FilePendingDelete is not { } card) return;
        FilePendingDelete = null;
        DeleteFile(card);
    }

    /// <summary>Manda un archivo a la papelera del vault. Si era el que se estaba previsualizando, se abre en su
    /// lugar el siguiente (o el anterior, si era el último): borrar mientras se repasa una carpeta archivo a
    /// archivo no debe expulsar al usuario de la previsualización en cada borrado.</summary>
    private void DeleteFile(FileCardViewModel card)
    {
        var wasPreviewed = ActivePreview is { } preview && PathsEqual(preview.File.FullPath, card.File.FullPath);
        string? neighbourPath = null;
        if (wasPreviewed)
        {
            var sequence = PreviewSequence;
            var index = PreviewIndex();
            if (index >= 0 && sequence.Count > 1)
                neighbourPath = sequence[index + 1 < sequence.Count ? index + 1 : index - 1].File.FullPath;
        }

        card.MediaPreview?.Close();
        if (!TryVaultOperation("Files_Notify_DeleteFailed_Title", () => _vaultService.DeleteFile(card.File))) return;

        card.PdfDocument?.Close();
        if (wasPreviewed) ActivePreview = null;
        Reload();

        if (neighbourPath is not null && PreviewSequence.FirstOrDefault(c => PathsEqual(c.File.FullPath, neighbourPath)) is { } next)
            OpenCard(next);
    }

    public void MoveFileToFolder(FileCardViewModel card, string destinationFolderPath)
    {
        var currentFolderPath = Path.GetDirectoryName(card.File.FullPath);
        if (PathsEqual(currentFolderPath, destinationFolderPath)) return;

        card.MediaPreview?.Close();
        if (!TryVaultOperation("Files_Notify_MoveFailed_Title", () => _vaultService.MoveFile(card.File, destinationFolderPath)))
            return;

        // MoveFile cambia la ruta del mismo VaultFile que sujeta la tarjeta, pero el visor de PDF ya había
        // resuelto la ruta vieja y seguía leyendo de ella: las páginas dejaban de pasar en silencio.
        if (ReferenceEquals(ActivePreview, card))
        {
            card.UpdateFile(card.File);
            LoadPreviewContent(card);
        }
        Reload();
    }

    // ===================== Utilidades =====================

    /// <summary>Ejecuta una escritura sobre el vault y, si falla por un motivo de disco (sin permisos, archivo en
    /// uso, sesión bloqueada, vault desaparecido), avisa en vez de dejar escapar la excepción desde un comando o
    /// un manejador de arrastre, que es donde se llamaba sin protección.</summary>
    private bool TryVaultOperation(string failureTitleKey, Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyFilesFailure(failureTitleKey, ex);
            return false;
        }
    }

    /// <summary>Aviso de "esto no se ha podido hacer" para toda la sección —el árbol y la rejilla—, con la
    /// razón traducida que <see cref="VaultFailureReasons"/> ya reparte por igual en Notas y el editor.</summary>
    private void NotifyFilesFailure(string titleKey, Exception ex)
    {
        LogService.Error("Operación de Archivos fallida", ex);
        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format(titleKey),
            VaultFailureReasons.DescribeLocalized(ex),
            NotificationSeverity.Error);
    }

    private string RemapPath(string path)
    {
        if (_folderRemap is not { } remap || !IsSameOrUnder(path, remap.OldPath)) return path;
        return remap.NewPath + path[Path.TrimEndingDirectorySeparator(remap.OldPath).Length..];
    }

    private static bool IsSameOrUnder(string path, string folderPath)
    {
        var folder = Path.TrimEndingDirectorySeparator(folderPath);
        return PathsEqual(path, folder)
               || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
