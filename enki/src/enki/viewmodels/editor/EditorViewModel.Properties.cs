using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Services;
using enki.Localization;

namespace enki.ViewModels;

public sealed partial class EditorViewModel
{
    public ObservableCollection<NotePropertyItemViewModel> Properties { get; } = [];

    [ObservableProperty] private bool _isPropertiesOpen;
    [ObservableProperty] private string _propertyKeyInput = string.Empty;
    [ObservableProperty] private string _propertyValueInput = string.Empty;
    [ObservableProperty] private string _propertyError = string.Empty;

    public bool HasPropertyError => PropertyError.Length > 0;
    partial void OnPropertyErrorChanged(string value) => OnPropertyChanged(nameof(HasPropertyError));

    private void RefreshProperties()
    {
        Note.Properties = NotePropertyHelper.Extract(Document.Text);
        Properties.Clear();
        foreach (var pair in Note.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            Properties.Add(new NotePropertyItemViewModel(pair.Key, pair.Value));
    }

    [RelayCommand]
    private void ToggleProperties() => IsPropertiesOpen = !IsPropertiesOpen;

    [RelayCommand]
    private void EditProperty(NotePropertyItemViewModel item)
    {
        PropertyKeyInput = item.Key;
        PropertyValueInput = item.Value;
        PropertyError = string.Empty;
    }

    [RelayCommand]
    private void SaveProperty()
    {
        if (IsAutosavePaused) return;
        FlushPendingSave();
        if (IsDirty) return;
        try
        {
            var changed = NotePropertyHelper.Set(Document.Text, PropertyKeyInput, PropertyValueInput);
            if (changed == Document.Text) return;
            Document.Text = changed;
            Save(changed);
            if (!IsDirty)
            {
                PropertyKeyInput = PropertyValueInput = string.Empty;
                PropertyError = string.Empty;
                RefreshProperties();
            }
        }
        catch (ArgumentException)
        {
            PropertyError = LocalizationService.Instance["Editor_Properties_InvalidKey"];
        }
        catch (FormatException)
        {
            PropertyError = LocalizationService.Instance["Editor_Properties_InvalidYaml"];
        }
    }

    [RelayCommand]
    private void DeleteProperty(NotePropertyItemViewModel item)
    {
        if (IsAutosavePaused) return;
        FlushPendingSave();
        if (IsDirty) return;
        try
        {
            var changed = NotePropertyHelper.Set(Document.Text, item.Key, null);
            if (changed == Document.Text) return;
            Document.Text = changed;
            Save(changed);
            if (!IsDirty) RefreshProperties();
        }
        catch (ArgumentException)
        {
            PropertyError = LocalizationService.Instance["Editor_Properties_InvalidKey"];
        }
        catch (FormatException)
        {
            PropertyError = LocalizationService.Instance["Editor_Properties_InvalidYaml"];
        }
    }
}

public sealed record NotePropertyItemViewModel(string Key, string Value);
