using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
/// once the way there is for notes (see docs/seccion-archivos.md §5).</summary>
public sealed partial class FilesViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly SessionService _sessionService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService? _settingsService;

    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ObservableCollection<FileCardViewModel> Files { get; } = [];

    /// <summary>Elementos mostrados en la rejilla principal de Archivos: carpetas navegables y, tras ellas,
    /// los archivos del folder actual. Se mantiene <see cref="Files"/> como fuente de datos de las tarjetas
    /// para no romper tests y consumidores existentes; esta colección es la vista proyectada para la UI.</summary>
    public ObservableCollection<object> BrowserItems { get; } = [];

    public ObservableCollection<FileFolderViewModel> RootFolders { get; } = [];
    public ObservableCollection<SessionOptionViewModel> SessionOptions { get; } = [];

    public string EffectiveRootPath => SelectedSessionOption?.Session?.RootPath ?? _vaultService.VaultPath;

    /// <summary>True while browsing a password-protected session that isn't currently unlocked — attachment
    /// file names are metadata, not decrypted content, but they can still be sensitive, so this page
    /// refuses to list them (not just refuses to preview them) while locked, same as the combined graph
    /// excludes a locked session's notes entirely rather than showing anything about them.</summary>
    public bool IsSelectedSessionLocked =>
        SelectedSessionOption?.Session is { IsEncrypted: true } session && !_sessionService.IsUnlocked(session);

    /// <summary>The regular "no files here" empty state — distinct from <see cref="IsSelectedSessionLocked"/>'s
    /// own placeholder, which takes over instead when the reason the grid is empty is that the session is
    /// locked rather than genuinely having no files.</summary>
    public bool ShowEmptyState => IsEmpty && !IsSelectedSessionLocked;

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

    /// <summary>Whether an OS drag is currently hovering the file grid itself (as opposed to a folder row)
    /// — drives the grid border's drag-over highlight while dropping files in from the system explorer.</summary>
    [ObservableProperty]
    private bool _isGridDropTarget;

    [ObservableProperty]
    private SessionOptionViewModel? _selectedSessionOption;

    [ObservableProperty]
    private FileCardViewModel? _filePendingDelete;

    [ObservableProperty]
    private FileKindFilterOption? _selectedFilterOption;

    /// <summary>Opciones del desplegable de filtro por tipo de archivo — "Todos los archivos"
    /// más una entrada por cada <see cref="VaultFileKind"/> con archivos visibles.</summary>
    public ObservableCollection<FileKindFilterOption> FilterOptions { get; } = [];

    /// <summary>El <see cref="VaultFileKind"/> actualmente filtrado, o null si "Todos".
    /// Derivado de <see cref="SelectedFilterOption"/>.</summary>
    private VaultFileKind? FilterKind => SelectedFilterOption?.Kind;

    public string CurrentFolderLabel => CurrentFolder?.Name ?? LocalizationService.Instance["Files_AllFolder"];

    /// <summary>Composed here (rather than a XAML <c>StringFormat</c>) so the word order around the count
    /// can vary per language — see docs/agent-docs/internacionalizacion-i18n.md §1.3. Kept in sync with
    /// <see cref="Files"/> via the collection-changed subscription below.</summary>
    public string FilesCountLabel => Plural.Get("Files_CountLabel", Files.Count, Files.Count);

    private string CurrentFolderPath => CurrentFolder?.FullPath ?? EffectiveRootPath;

    public FilesViewModel(VaultService vaultService, SessionService sessionService, NotificationService notificationService,
        SettingsService? settingsService = null)
    {
        _vaultService = vaultService;
        _sessionService = sessionService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            Reload();
        };
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilesCountLabel));

        ReloadSessions();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(CurrentFolderLabel));
        OnPropertyChanged(nameof(FilesCountLabel));
        Reload();
    }

    private bool _rebuildingFilter;

    partial void OnSelectedFilterOptionChanged(FileKindFilterOption? value)
    {
        if (!_rebuildingFilter) Reload();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnCurrentFolderChanged(FileFolderViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentFolderLabel));
        Reload();
    }

    partial void OnSelectedSessionOptionChanged(SessionOptionViewModel? value)
    {
        OnPropertyChanged(nameof(IsSelectedSessionLocked));
        OnPropertyChanged(nameof(ShowEmptyState));
        CurrentFolder = null;
        ReloadFolders();
        Reload();
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    public void ReloadSessions()
    {
        var previouslySelectedId = SelectedSessionOption?.Session?.Id;

        SessionOptions.Clear();
        SessionOptions.Add(new SessionOptionViewModel(null));
        foreach (var session in _sessionService.List()) SessionOptions.Add(new SessionOptionViewModel(session));

        SelectedSessionOption = previouslySelectedId is null
            ? SessionOptions[0]
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == previouslySelectedId) ?? SessionOptions[0];
    }

    public void SelectSession(string? sessionId)
    {
        var match = sessionId is null
            ? SessionOptions.FirstOrDefault(o => o.IsVault)
            : SessionOptions.FirstOrDefault(o => o.Session?.Id == sessionId);
        if (match is not null) SelectedSessionOption = match;
    }

    public void Reload()
    {
        // Drop any decrypted PDF temp copies the outgoing cards were holding onto before discarding them —
        // Reload() runs on every folder switch/search keystroke, so this is the main point at which those
        // temp files would otherwise leak for the lifetime of the process.
        foreach (var card in Files) card.PdfDocument?.Close();

        if (IsSelectedSessionLocked)
        {
            Files.Clear();
            BrowserItems.Clear();
            IsEmpty = true;
            return;
        }

        var all = _vaultService.GetFiles(CurrentFolderPath);

        // Filtro por tipo de archivo (extensión)
        var kindFiltered = FilterKind is null
            ? all
            : all.Where(f => f.Kind == FilterKind.Value);

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? kindFiltered
            : kindFiltered.Where(f => f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        Files.Clear();
        foreach (var file in filtered) Files.Add(new FileCardViewModel(_vaultService, file));

        BrowserItems.Clear();
        var hasSearch = !string.IsNullOrWhiteSpace(SearchText);
        if (!hasSearch)
        {
            if (CurrentFolder is not null)
            {
                var parentPath = Path.GetDirectoryName(CurrentFolder.FullPath);
                if (!string.IsNullOrEmpty(parentPath))
                    BrowserItems.Add(new FileFolderCardViewModel("..", parentPath, isParent: true));
            }

            foreach (var folder in _vaultService.GetFolderTree(CurrentFolderPath))
                BrowserItems.Add(new FileFolderCardViewModel(folder.Name, folder.FullPath));
        }

        foreach (var file in Files) BrowserItems.Add(file);

        IsEmpty = BrowserItems.Count == 0;
        RebuildFilterOptions(all);
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

    public void ReloadFolders()
    {
        if (IsSelectedSessionLocked)
        {
            RootFolders.Clear();
            CurrentFolder = null;
            return;
        }

        // On the very first call RootFolders is empty, so there is no in-memory expand state to
        // preserve — fall back to whatever the user had last time (persisted in settings). On
        // every subsequent call the in-memory state (kept up-to-date by the OnExpandedChanged
        // callback) is authoritative.
        var expandedPaths = RootFolders.Count > 0
            ? CollectExpandedPaths(RootFolders).ToHashSet()
            : new HashSet<string>(_settingsService?.Settings.FilesExpandedFolderPaths ?? []);
        var selectedPath = CurrentFolder?.FullPath;

        RootFolders.Clear();
        foreach (var folder in _vaultService.GetFolderTree(EffectiveRootPath))
        {
            var vm = new FileFolderViewModel(folder) { OnExpandedChanged = PersistExpandedFolders };
            ApplyExpandedState(vm, expandedPaths);
            RootFolders.Add(vm);
        }

        CurrentFolder = selectedPath is null ? null : FindFolder(RootFolders, selectedPath);
    }

    private static IEnumerable<string> CollectExpandedPaths(IEnumerable<FileFolderViewModel> folders)
    {
        foreach (var folder in folders)
        {
            if (folder.IsExpanded) yield return folder.FullPath;
            foreach (var path in CollectExpandedPaths(folder.Children)) yield return path;
        }
    }

    private static void ApplyExpandedState(FileFolderViewModel folder, HashSet<string> expandedPaths)
    {
        folder.IsExpanded = expandedPaths.Count > 0 && expandedPaths.Contains(folder.FullPath);
        foreach (var child in folder.Children) ApplyExpandedState(child, expandedPaths);
    }

    /// <summary>Writes the currently expanded folder paths to <see cref="AppSettings"/> so they
    /// survive an app restart — called from <see cref="FileFolderViewModel.OnExpandedChanged"/>
    /// whenever the user toggles any folder in the sidebar.</summary>
    private void PersistExpandedFolders()
    {
        if (_settingsService is null) return;
        _settingsService.Settings.FilesExpandedFolderPaths = CollectExpandedPaths(RootFolders).ToList();
        _settingsService.Save();
    }

    private static FileFolderViewModel? FindFolder(IEnumerable<FileFolderViewModel> folders, string path)
    {
        foreach (var folder in folders)
        {
            if (folder.FullPath == path) return folder;
            var found = FindFolder(folder.Children, path);
            if (found is not null) return found;
        }
        return null;
    }

    [RelayCommand]
    private async Task Import()
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

        ImportPaths(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!));
    }

    /// <summary>Shared entry point for every way a file can land in the current folder — the "IMPORTAR"
    /// picker above and dropping files from the OS file explorer onto the grid
    /// (<c>FilesView.axaml.cs</c>), both funnel through the same <see cref="VaultService.ImportFile"/>
    /// call the "IMPORTAR" button already used, just with a different source.</summary>
    public void ImportPaths(IEnumerable<string> sourcePaths)
    {
        var imported = false;
        foreach (var path in sourcePaths)
        {
            _vaultService.ImportFile(path, CurrentFolderPath);
            imported = true;
        }
        if (imported) Reload();
    }

    [RelayCommand]
    private void OpenCard(FileCardViewModel card)
    {
        card.EnsureThumbnailLoaded();
        card.EnsurePdfDocumentLoaded();
        card.EnsureCodeContentLoaded();
        ActivePreview = card;
    }

    [RelayCommand]
    private void ClosePreview() => ActivePreview = null;

    /// <summary>"Abrir con la aplicación del sistema" — the universal fallback for anything this section
    /// doesn't render an in-app preview for (every <see cref="VaultFileKind.Other"/> file, and PDFs, see
    /// docs/seccion-archivos.md §6). Unlike the editor's identical link-opening call (which ignores a
    /// failure silently because a broken markdown link is low-stakes), this is the one action this whole
    /// screen exists to perform, so a failure here is surfaced instead of swallowed.</summary>
    [RelayCommand]
    private void OpenWithSystem(FileCardViewModel card)
    {
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
        catch
        {
            if (isTempCopy) VaultService.TryCleanupDecryptedTempFile(readablePath);
            _notificationService.Push(
                LocalizationService.Instance["Files_OpenWithSystem_FailedTitle"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Files_OpenWithSystem_FailedMessage"], card.FileName),
                Models.NotificationSeverity.Warning);
        }
    }

    [RelayCommand]
    private void SelectFolder(FileFolderViewModel? folder) => CurrentFolder = folder;

    [RelayCommand]
    private void OpenFolderCard(FileFolderCardViewModel card)
    {
        if (card.IsParent)
        {
            CurrentFolder = string.IsNullOrEmpty(card.FullPath)
                ? null
                : FindFolder(RootFolders, card.FullPath);
            return;
        }

        CurrentFolder = FindFolder(RootFolders, card.FullPath);
    }

    [RelayCommand]
    private void CreateSubfolder(FileFolderViewModel folder)
    {
        var created = VaultService.CreateFolder(folder.FullPath);
        ReloadFolders();

        var vm = FindFolder(RootFolders, created.FullPath);
        if (vm is null) return;
        vm.EditingName = vm.Name;
        vm.IsRenaming = true;
    }

    [RelayCommand]
    private void NewFolder()
    {
        var created = VaultService.CreateFolder(CurrentFolderPath);
        ReloadFolders();

        var vm = FindFolder(RootFolders, created.FullPath);
        if (vm is null) return;
        vm.EditingName = vm.Name;
        vm.IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRenameFolder(FileFolderViewModel folder)
    {
        if (!string.IsNullOrWhiteSpace(folder.EditingName) && folder.EditingName != folder.Name)
        {
            var updated = VaultService.RenameFolder(folder.Folder, folder.EditingName);
            folder.UpdateFolder(updated);
        }
        folder.IsRenaming = false;
        ReloadFolders();
        Reload();
    }

    [RelayCommand]
    private void DeleteFolder(FileFolderViewModel folder)
    {
        var affectsCurrent = CurrentFolder is not null &&
                              (CurrentFolder == folder || IsDescendant(folder, CurrentFolder));

        _vaultService.DeleteFolder(folder.Folder);
        if (affectsCurrent) CurrentFolder = null;

        ReloadFolders();
        Reload();
    }

    private static bool IsDescendant(FileFolderViewModel ancestor, FileFolderViewModel node)
    {
        foreach (var child in ancestor.Children)
        {
            if (child == node || IsDescendant(child, node)) return true;
        }
        return false;
    }

    [RelayCommand]
    private void CommitRenameFile(FileCardViewModel card)
    {
        if (!string.IsNullOrWhiteSpace(card.EditingName) && card.EditingName != card.FileName)
        {
            var updated = _vaultService.RenameFile(card.File, card.EditingName);
            card.UpdateFile(updated);
        }
        card.IsRenaming = false;
        Reload();
    }

    // ===================== Delete confirmation dialog =====================
    // análisis alfa-3 §4.5 — "preguntar antes de borrar" se saltaba en silencio para Archivos, a
    // diferencia de Hábitos/Polar/Skills/Kanban/Calendario, que sí lo respetan.

    [RelayCommand]
    private void RequestDeleteFile(FileCardViewModel card)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) FilePendingDelete = card;
        else DeleteFile(card);
    }

    [RelayCommand]
    private void CancelDeleteFile() => FilePendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteFile()
    {
        if (FilePendingDelete is not { } card) return;
        DeleteFile(card);
        FilePendingDelete = null;
    }

    private void DeleteFile(FileCardViewModel card)
    {
        if (ActivePreview == card) ActivePreview = null;
        card.PdfDocument?.Close();
        _vaultService.DeleteFile(card.File);
        Reload();
    }

    public void MoveFileToFolder(FileCardViewModel card, string destinationFolderPath)
    {
        var currentFolderPath = Path.GetDirectoryName(card.File.FullPath);
        if (string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase)) return;

        _vaultService.MoveFile(card.File, destinationFolderPath);
        Reload();
    }

}
