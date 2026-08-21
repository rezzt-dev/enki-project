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

/// <summary>Backs the "Skills" section — listar, importar, exportar, duplicar, editar y eliminar skills
/// almacenadas en <c>&lt;vault&gt;/.enki/skills/</c> (docs/ia-skills/especificacion-desarrollo.md).
/// Construye su propio <see cref="SkillService"/> a partir de <see cref="VaultService"/> en vez de
/// recibirlo ya construido — mismo motivo por el que <see cref="SkillService.SkillsDirectory"/> se calcula
/// en vivo: la instancia sobrevive a un cambio de vault sin necesitar reconstrucción.</summary>
public sealed partial class SkillsViewModel : ViewModelBase
{
    private readonly SkillService _skillService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService? _settingsService;

    private IReadOnlyList<string>? _pendingImportPaths;
    private Action<string>? _pendingNamePromptConfirm;

    /// <summary>Todas las skills cargadas del vault, sin filtrar — fuente de verdad para exportar
    /// (una skill marcada no debe perderse al filtrar) y para reconstruir <see cref="Skills"/> en
    /// cada tecleo de <see cref="SearchText"/>.</summary>
    private List<SkillViewModel> _allSkills = [];

    /// <summary>Colección mostrada en la rejilla — subconjunto de <see cref="_allSkills"/> que coincide
    /// con <see cref="SearchText"/> (o el total si no hay búsqueda activa).</summary>
    public ObservableCollection<SkillViewModel> Skills { get; } = [];

    [ObservableProperty]
    private SkillViewModel? _selectedSkill;

    [ObservableProperty]
    private bool _isEmpty;

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

    public string NoSearchResultsMessage => !string.IsNullOrWhiteSpace(SearchText)
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Skills_NoResults_ForQuery"], SearchText)
        : LocalizationService.Instance["Skills_NoResults_ForTags"];

    /// <summary>Etiqueta del contador de la franja de estadísticas (p. ej. "12 SKILLS") — propiedad
    /// computada en vez de bindear <c>Skills.Count</c> directamente más un literal fijo en XAML, porque
    /// el plural depende del idioma activo (§1.3/§1.4). Notificada manualmente en <see cref="ApplyFilter"/>,
    /// que es el único sitio donde cambia <see cref="Skills"/>.</summary>
    public string SkillsCountLabel => Plural.Get("Skills_Count", Skills.Count, Skills.Count);

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(NoSearchResultsMessage));
        ApplyFilter();
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

    /// <summary>No nulo mientras se edita una skill — <c>SkillsView</c> alterna entre modo lista y modo
    /// editor según este valor (Opción A, docs/ia-skills/especificacion-desarrollo.md §10).</summary>
    [ObservableProperty]
    private SkillEditorViewModel? _activeEditor;

    [ObservableProperty]
    private SkillViewModel? _pendingDeleteSkill;

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
        TagFilter.Changed += Reload;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(NoSearchResultsMessage));
        OnPropertyChanged(nameof(SkillsCountLabel));
        Reload();
    }

    /// <summary>Copia las skills por defecto la primera vez que se activa la sección — llamado desde
    /// <c>MainWindowViewModel.OnSkillsPanelModeChanged</c> al recibir el toggle de Ajustes.</summary>
    public void EnsureDefaultSkills() => _skillService.EnsureDefaultSkills();

    public void Reload()
    {
        var previouslySelectedId = SelectedSkill?.Id;

        _allSkills = _skillService.LoadSkills().Select(skill => new SkillViewModel(skill)).ToList();
        ApplyFilter();
        TagFilter.Reload(_allSkills.SelectMany(s => s.Model.Tags).Distinct(StringComparer.OrdinalIgnoreCase));

        OnPropertyChanged(nameof(TotalSkillsCount));
        OnPropertyChanged(nameof(DefaultSkillsCount));
        OnPropertyChanged(nameof(CustomSkillsCount));
        OnPropertyChanged(nameof(TaggedSkillsCount));

        SelectedSkill = previouslySelectedId is null ? null : _allSkills.FirstOrDefault(s => s.Id == previouslySelectedId);
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<SkillViewModel> filtered = string.IsNullOrEmpty(query)
            ? _allSkills
            : _allSkills.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || s.Model.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));

        var selectedTags = TagFilter.SelectedTags.ToList();
        if (selectedTags.Count > 0)
        {
            filtered = filtered.Where(s => s.Model.Tags.Any(tag => selectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        Skills.Clear();
        foreach (var skill in filtered) Skills.Add(skill);

        IsEmpty = _allSkills.Count == 0;
        HasNoSearchResults = !IsEmpty && Skills.Count == 0;
        OnPropertyChanged(nameof(NoSearchResultsMessage));
        OnPropertyChanged(nameof(SkillsCountLabel));
    }

    [RelayCommand]
    private void SelectSkill(SkillViewModel? skill) => SelectedSkill = skill;

    partial void OnSelectedSkillChanged(SkillViewModel? oldValue, SkillViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
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
                    var copyVm = _allSkills.FirstOrDefault(s => s.Id == copy.Id);
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

    private void OpenEditor(SkillViewModel skill) =>
        ActiveEditor = new SkillEditorViewModel(_skillService, skill.Model, skill.Refresh, _notificationService);

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
    // docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md §3 — "preguntar antes de borrar" salta
    // directo al borrado cuando está desactivado.

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

    private void BeginImport(IReadOnlyList<string> paths)
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
        var skillsDirectory = _skillService.SkillsDirectory;
        if (!Directory.Exists(skillsDirectory)) return [];

        var candidates = paths.SelectMany(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.md", SearchOption.TopDirectoryOnly)
            : File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? new[] { path }
                : Array.Empty<string>());

        return candidates
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => File.Exists(Path.Combine(skillsDirectory, name)))
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
            var result = _skillService.Import(paths, resolution);
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
