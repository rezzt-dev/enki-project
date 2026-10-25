using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Gestor global de etiquetas — vista única de mantenimiento del vault que agrega las etiquetas de
/// Notas, Kanban y Polar (<see cref="TagManagerService.BuildEntries"/>) y permite renombrarlas, fusionarlas
/// o eliminarlas escribiendo directamente sobre esas mismas entidades
/// (<see cref="TagManagerService.ReplaceTag"/>) — nunca sobre un almacén propio. Misma estructura de
/// request/cancel/confirm que <see cref="TrashViewModel"/> para sus tres acciones.</summary>
public sealed partial class TagManagerViewModel : ViewModelBase
{
    private readonly TagManagerService _tagManagerService;
    private readonly NotificationService _notificationService;
    private readonly Func<bool> _prepareChange;
    private readonly Action _afterChange;

    /// <summary>Todas las filas, sin filtrar por <see cref="SearchQuery"/> — de aquí sale la selección de
    /// fusión y la reconstrucción de <see cref="Items"/> al buscar.</summary>
    private List<TagManagerItemViewModel> _allItems = [];

    public ObservableCollection<TagManagerItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Hay etiquetas en el vault pero ninguna coincide con <see cref="SearchQuery"/> — distinto de
    /// <see cref="IsEmpty"/> (no hay ninguna etiqueta en absoluto) para no mostrar el mismo mensaje de
    /// "el vault no tiene etiquetas" cuando en realidad el usuario solo escribió algo que no existe.</summary>
    [ObservableProperty]
    private bool _hasNoSearchResults;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    public int SelectedCount => _allItems.Count(i => i.IsSelected);
    public string SelectedCountLabel => Plural.Get("TagManager_SelectedCount", SelectedCount, SelectedCount);

    public bool CanMerge => SelectedCount >= 2;

    // ===================== Renombrar =====================

    [ObservableProperty]
    private TagManagerItemViewModel? _pendingRename;

    [ObservableProperty]
    private string _renameInput = string.Empty;

    public bool CanConfirmRename => PendingRename is not null && TagManagerService.IsValidTagName(RenameInput);

    partial void OnRenameInputChanged(string value) => OnPropertyChanged(nameof(CanConfirmRename));

    partial void OnPendingRenameChanged(TagManagerItemViewModel? value) => OnPropertyChanged(nameof(CanConfirmRename));

    // ===================== Fusionar =====================

    [ObservableProperty]
    private bool _isMergeDialogOpen;

    [ObservableProperty]
    private string _mergeTargetInput = string.Empty;

    public bool CanConfirmMerge => IsMergeDialogOpen && TagManagerService.IsValidTagName(MergeTargetInput);

    partial void OnMergeTargetInputChanged(string value) => OnPropertyChanged(nameof(CanConfirmMerge));
    partial void OnIsMergeDialogOpenChanged(bool value) => OnPropertyChanged(nameof(CanConfirmMerge));

    // ===================== Eliminar =====================

    [ObservableProperty]
    private TagManagerItemViewModel? _pendingDelete;

    public string PendingDeleteMessage => PendingDelete is { } item
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["TagManager_DeleteModal_Message"], item.Name)
        : string.Empty;

    partial void OnPendingDeleteChanged(TagManagerItemViewModel? value) => OnPropertyChanged(nameof(PendingDeleteMessage));

    public TagManagerViewModel(TagManagerService tagManagerService, NotificationService notificationService,
        Func<bool>? prepareChange = null, Action? afterChange = null)
    {
        _tagManagerService = tagManagerService;
        _notificationService = notificationService;
        _prepareChange = prepareChange ?? (() => true);
        _afterChange = afterChange ?? (() => { });
        Reload();
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(PendingDeleteMessage));
        OnPropertyChanged(nameof(SelectedCountLabel));
        foreach (var item in _allItems) item.RefreshLocalizedLabels();
    }

    public void Reload()
    {
        foreach (var item in _allItems) item.PropertyChanged -= OnItemPropertyChanged;

        _allItems = _tagManagerService.BuildEntries().Select(entry => new TagManagerItemViewModel(entry)).ToList();
        foreach (var item in _allItems) item.PropertyChanged += OnItemPropertyChanged;

        IsEmpty = _allItems.Count == 0;
        ApplyFilter();
        NotifySelectionChanged();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagManagerItemViewModel.IsSelected)) NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountLabel));
        OnPropertyChanged(nameof(CanMerge));
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        Items.Clear();
        foreach (var item in filtered) Items.Add(item);

        HasNoSearchResults = _allItems.Count > 0 && Items.Count == 0;
    }

    // ===================== Renombrar =====================

    [RelayCommand]
    private void RequestRename(TagManagerItemViewModel item)
    {
        PendingRename = item;
        RenameInput = item.Name;
    }

    [RelayCommand]
    private void CancelRename() => PendingRename = null;

    [RelayCommand]
    private void ConfirmRename()
    {
        if (PendingRename is not { } item || !CanConfirmRename) return;

        var summary = TryReplaceTag([item.Name], RenameInput.Trim());
        if (summary is null) return;
        PendingRename = null;
        NotifySummary(LocalizedTextFormatter.Format("TagManager_Notify_Renamed_Title"), summary.Value);
        Reload();
    }

    // ===================== Fusionar =====================

    [RelayCommand]
    private void RequestMerge()
    {
        if (!CanMerge) return;

        var selected = _allItems.Where(i => i.IsSelected).OrderByDescending(i => i.TotalCount).ToList();
        MergeTargetInput = selected[0].Name;
        IsMergeDialogOpen = true;
    }

    [RelayCommand]
    private void CancelMerge() => IsMergeDialogOpen = false;

    [RelayCommand]
    private void ConfirmMerge()
    {
        if (!CanMerge || !CanConfirmMerge) return;

        var sourceNames = _allItems.Where(i => i.IsSelected).Select(i => i.Name).ToList();
        var summary = TryReplaceTag(sourceNames, MergeTargetInput.Trim());
        if (summary is null) return;
        IsMergeDialogOpen = false;
        NotifySummary(LocalizedTextFormatter.Format("TagManager_Notify_Merged_Title"), summary.Value);
        Reload();
    }

    // ===================== Eliminar =====================

    [RelayCommand]
    private void RequestDelete(TagManagerItemViewModel item) => PendingDelete = item;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (PendingDelete is not { } item) return;

        var summary = TryReplaceTag([item.Name], null);
        if (summary is null) return;
        PendingDelete = null;
        NotifySummary(LocalizedTextFormatter.Format("TagManager_Notify_Deleted_Title"), summary.Value);
        Reload();
    }

    /// <summary>Guarda primero las pestañas abiertas; ante un error de disco, anuncia el fallo y
    /// reconstruye la vista, porque alguna entidad anterior puede haberse guardado ya.</summary>
    private TagChangeSummary? TryReplaceTag(IEnumerable<string> sources, string? target)
    {
        if (!_prepareChange()) return null;
        try
        {
            var summary = _tagManagerService.ReplaceTag(sources, target);
            _afterChange();
            return summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error("No se pudieron actualizar todas las etiquetas", ex);
            // La operación puede haber guardado los dominios anteriores al fallo. Reconciliar las
            // pestañas abiertas evita que un autoguardado vuelva a escribir etiquetas antiguas.
            try { _afterChange(); }
            catch (Exception refreshError) when (refreshError is IOException or UnauthorizedAccessException or SessionLockedException)
            {
                LogService.Error("No se pudo refrescar una nota tras el error de etiquetas", refreshError);
            }
            PendingRename = null;
            PendingDelete = null;
            IsMergeDialogOpen = false;
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("TagManager_Error_Title"),
                LocalizedTextFormatter.Format("TagManager_Error_Message"), NotificationSeverity.Error);
            Reload();
            return null;
        }
    }

    /// <summary>Construye "N notas, M tarjetas y K tareas actualizadas" a partir del recuento por dominio
    /// — mismo constructor de mensaje por partes que <c>TrashViewModel.BuildEmptyPushMessage</c>, incluida
    /// la reutilización de la conjunción genérica <c>Trash_Notify_ListConjunction</c> ("y"/"and"), que no
    /// tiene nada de específico de la Papelera. No notifica nada si la operación no tocó ninguna entidad
    /// (la etiqueta ya no existía en ningún dominio cuando se confirmó).</summary>
    private void NotifySummary(LocalizedText title, TagChangeSummary summary)
    {
        if (summary.Total == 0) return;

        var parts = new List<LocalizedText>();
        if (summary.Notes > 0) parts.Add(LocalizedTextFormatter.PluralFormat("TagManager_Notify_NotesCount", summary.Notes, summary.Notes));
        if (summary.Kanban > 0) parts.Add(LocalizedTextFormatter.PluralFormat("TagManager_Notify_KanbanCount", summary.Kanban, summary.Kanban));
        if (summary.Polar > 0) parts.Add(LocalizedTextFormatter.PluralFormat("TagManager_Notify_PolarCount", summary.Polar, summary.Polar));

        var itemsList = parts.Count <= 1
            ? LocalizedTextFormatter.Join(string.Empty, parts.ToArray())
            : LocalizedTextFormatter.Join(" ",
                LocalizedTextFormatter.Join(", ", parts.Take(parts.Count - 1).ToArray()),
                LocalizedTextFormatter.Format("Trash_Notify_ListConjunction"), parts[^1]);

        _notificationService.PushLocalized(title, LocalizedTextFormatter.Format("TagManager_Notify_Summary_Message", itemsList), NotificationSeverity.Success);
    }
}
