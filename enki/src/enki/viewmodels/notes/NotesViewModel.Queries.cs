using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class NotesViewModel
{
    public ObservableCollection<NoteQuery> SavedQueries { get; } = [];
    private string? _queryVaultPath;
    private bool _applyingQuery;

    [ObservableProperty] private bool _isQueryPanelOpen;
    [ObservableProperty] private string _queryName = string.Empty;
    [ObservableProperty] private string _queryTag = string.Empty;
    [ObservableProperty] private string _queryPropertyKey = string.Empty;
    [ObservableProperty] private string _queryPropertyValue = string.Empty;
    [ObservableProperty] private string _queryModifiedFrom = string.Empty;
    [ObservableProperty] private string _queryModifiedTo = string.Empty;
    [ObservableProperty] private bool _queryIncludeSubfolders;
    [ObservableProperty] private NoteQuery? _selectedQuery;
    [ObservableProperty] private string _queryError = string.Empty;

    public bool HasQueryError => QueryError.Length > 0;
    partial void OnQueryTagChanged(string value) => QueryChanged();
    partial void OnQueryPropertyKeyChanged(string value) => QueryChanged();
    partial void OnQueryPropertyValueChanged(string value) => QueryChanged();
    partial void OnQueryModifiedFromChanged(string value) => QueryChanged();
    partial void OnQueryModifiedToChanged(string value) => QueryChanged();
    partial void OnQueryIncludeSubfoldersChanged(bool value) => QueryChanged();
    partial void OnQueryErrorChanged(string value) => OnPropertyChanged(nameof(HasQueryError));

    private void QueryChanged()
    {
        if (_applyingQuery) return;
        QueryError = NoteQueryService.TryValidate(CurrentQuery(), out var error) ? string.Empty : error;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnSelectedQueryChanged(NoteQuery? value)
    {
        if (value is null) return;
        _applyingQuery = true;
        QueryName = value.Name;
        SearchText = value.Text;
        QueryTag = value.Tag;
        QueryPropertyKey = value.PropertyKey;
        QueryPropertyValue = value.PropertyValue;
        QueryModifiedFrom = value.ModifiedFrom;
        QueryModifiedTo = value.ModifiedTo;
        QueryIncludeSubfolders = value.IncludeSubfolders;
        IsQueryPanelOpen = true;
        _applyingQuery = false;
        Reload();
        TagFilter.SelectTags(value.SelectedTags ?? []);
        QueryChanged();
    }

    private NoteQuery CurrentQuery() => new()
    {
        Name = QueryName.Trim(), Text = SearchText.Trim(), Tag = QueryTag.Trim(),
        SelectedTags = TagFilter.SelectedTags.ToList(),
        PropertyKey = QueryPropertyKey.Trim(), PropertyValue = QueryPropertyValue.Trim(),
        ModifiedFrom = QueryModifiedFrom.Trim(), ModifiedTo = QueryModifiedTo.Trim(),
        IncludeSubfolders = QueryIncludeSubfolders
    };

    private void EnsureQueryVault()
    {
        if (string.Equals(_queryVaultPath, _vaultService.VaultPath, VaultService.PathComparison)) return;
        _queryVaultPath = _vaultService.VaultPath;
        SavedQueries.Clear();
        TagFilter.Items.Clear();
        SelectedQuery = null;
        ClearQueryFields();
        if (_vaultSettingsService?.Settings.NoteQueries is not { } stored) return;
        foreach (var query in stored.Where(q => q is not null && !string.IsNullOrWhiteSpace(q.Name)))
            SavedQueries.Add(query.Clone());
    }

    private void ClearQueryFields()
    {
        _applyingQuery = true;
        QueryName = QueryTag = QueryPropertyKey = QueryPropertyValue = string.Empty;
        QueryModifiedFrom = QueryModifiedTo = string.Empty;
        QueryIncludeSubfolders = false;
        SearchText = string.Empty;
        _applyingQuery = false;
        QueryError = string.Empty;
    }

    [RelayCommand]
    private void ToggleQueryPanel() => IsQueryPanelOpen = !IsQueryPanelOpen;

    [RelayCommand]
    private void ClearQuery()
    {
        SelectedQuery = null;
        ClearQueryFields();
        TagFilter.ClearSelection();
        Reload();
    }

    [RelayCommand]
    private void SaveQuery()
    {
        if (_vaultSettingsService is null) return;
        var query = CurrentQuery();
        if (query.Name.Length == 0) { QueryError = LocalizationService.Instance["Notes_Query_NameRequired"]; return; }
        if (!NoteQueryService.TryValidate(query, out var error)) { QueryError = error; return; }
        var existing = SavedQueries.FirstOrDefault(q => q.Name.Equals(query.Name, StringComparison.OrdinalIgnoreCase));
        var next = SavedQueries.Where(q => q != existing).Select(q => q.Clone()).Append(query).ToList();
        if (!TryPersistQueries(next)) return;
        if (existing is not null) SavedQueries.Remove(existing);
        SavedQueries.Add(query);
        SelectedQuery = query;
    }

    [RelayCommand]
    private void DeleteQuery()
    {
        if (SelectedQuery is null) return;
        var next = SavedQueries.Where(q => q != SelectedQuery).Select(q => q.Clone()).ToList();
        if (!TryPersistQueries(next)) return;
        SavedQueries.Remove(SelectedQuery);
        SelectedQuery = null;
    }

    private bool TryPersistQueries(System.Collections.Generic.List<NoteQuery> queries)
    {
        if (_vaultSettingsService is null) return false;
        var previous = _vaultSettingsService.Settings.NoteQueries;
        try
        {
            _vaultSettingsService.Settings.NoteQueries = queries;
            _vaultSettingsService.Save();
            QueryError = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vaultSettingsService.Settings.NoteQueries = previous;
            QueryError = LocalizationService.Instance["Notes_Query_SaveFailed"];
            NotifyVaultWriteFailed(ex);
            return false;
        }
    }
}
