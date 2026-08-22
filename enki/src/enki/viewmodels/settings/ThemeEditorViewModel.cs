using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>In-app editor for a single <see cref="CustomTheme"/>. Lives inside Ajustes › Apariencia and
/// lets the user change every catalog color with a picker or hex field, preview changes live, save them,
/// duplicate the theme, export it as CSS or cancel and revert to the previously-active palette.</summary>
public sealed partial class ThemeEditorViewModel : ObservableObject
{
    private readonly CustomTheme _theme;
    private readonly CustomThemeService _customThemeService;
    private readonly NotificationService _notificationService;
    private readonly LocalizationService _localization;
    private readonly Action _onSaved;
    private readonly Action _onCancelled;
    private readonly Action<CustomTheme> _onDuplicateOpened;
    private readonly Dictionary<string, string> _originalColors;
    private readonly bool _wasActive;

    [ObservableProperty]
    private string _themeName = "";

    [ObservableProperty]
    private ObservableCollection<ThemeColorGroupViewModel> _groups = [];

    public ThemeEditorViewModel(
        CustomTheme theme,
        CustomThemeService customThemeService,
        NotificationService notificationService,
        LocalizationService localization,
        bool isActive,
        Action onSaved,
        Action onCancelled,
        Action<CustomTheme> onDuplicateOpened)
    {
        _theme = theme;
        _customThemeService = customThemeService;
        _notificationService = notificationService;
        _localization = localization;
        _onSaved = onSaved;
        _onCancelled = onCancelled;
        _onDuplicateOpened = onDuplicateOpened;
        _originalColors = new Dictionary<string, string>(theme.Colors, StringComparer.Ordinal);
        _wasActive = isActive;
        _themeName = theme.Name;

        BuildGroups();
    }

    private void BuildGroups()
    {
        var slots = ThemeCatalog.All
            .Select(d => new ThemeColorSlotViewModel(d, _theme.Colors.TryGetValue(d.ResourceKey, out var hex) ? hex : d.DefaultHex))
            .ToList();

        foreach (var slot in slots)
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeColorSlotViewModel.Hex))
                    _theme.Colors[slot.ResourceKey] = slot.Hex;
                else if (e.PropertyName == nameof(ThemeColorSlotViewModel.Color))
                    Preview();
            };

        Groups = new ObservableCollection<ThemeColorGroupViewModel>(
            slots
                .GroupBy(s => s.Category)
                .Select(g => new ThemeColorGroupViewModel(g.Key, [.. g])));
    }

    /// <summary>Rebuilds every slot label when the UI language changes, so the editor does not stay
    /// frozen in the previous language.</summary>
    public void RefreshLabels()
    {
        foreach (var group in Groups)
        {
            group.RefreshLabel();
            foreach (var slot in group.Slots) slot.RefreshLabel();
        }
    }

    private void Preview()
    {
        try
        {
            CustomThemeService.Preview(_theme.Colors);
        }
        catch (FormatException)
        {
            // Ignore preview of incomplete hex values while typing.
        }
    }

    [RelayCommand]
    private void Save()
    {
        _theme.Name = string.IsNullOrWhiteSpace(ThemeName) ? _theme.Name : ThemeName.Trim();
        _customThemeService.Save(_theme);
        _customThemeService.Activate(_theme.Id);
        _onSaved();
    }

    [RelayCommand]
    private void Cancel()
    {
        // Restore the original colors so the app does not stay in a half-previewed state.
        _theme.Colors.Clear();
        foreach (var kvp in _originalColors) _theme.Colors[kvp.Key] = kvp.Value;
        CustomThemeService.Preview(_theme.Colors);

        if (!_wasActive) _customThemeService.Deactivate();
        _onCancelled();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        foreach (var slot in Groups.SelectMany(g => g.Slots))
        {
            var defaultHex = ThemeCatalog.All.First(d => d.ResourceKey == slot.ResourceKey).DefaultHex;
            slot.SetHex(defaultHex);
        }
        Preview();
    }

    [RelayCommand]
    private async Task Export()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _localization["Settings_Appearance_Theme_ExportDialogTitle"],
            SuggestedFileName = $"{_theme.Name}.css",
            DefaultExtension = "css",
            FileTypeChoices = [new FilePickerFileType("CSS") { Patterns = ["*.css"] }]
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination)) return;

        try
        {
            _customThemeService.Export(_theme.Id, destination);
            _notificationService.Push(
                _localization["Notification_ThemeTemplateExported_Title"],
                string.Format(CultureInfo.CurrentCulture, _localization["Notification_ThemeTemplateExported_Message"], destination),
                NotificationSeverity.Success);
        }
        catch (UnauthorizedAccessException)
        {
            _notificationService.Push(
                _localization["Notification_ThemeTemplateExportFailed_Title"],
                _localization["Common_Error_AccessDenied"],
                NotificationSeverity.Error);
        }
        catch (IOException)
        {
            _notificationService.Push(
                _localization["Notification_ThemeTemplateExportFailed_Title"],
                _localization["Common_Error_IOFailure"],
                NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private void Duplicate()
    {
        var copy = _theme.Clone();
        copy.Name = $"{ThemeName} {_localization["Settings_Appearance_Theme_DuplicateSuffix"]}".Trim();
        _customThemeService.Save(copy);
        _onDuplicateOpened(copy);
    }
}

/// <summary>Groups <see cref="ThemeColorSlotViewModel"/> rows by category inside the editor.</summary>
public sealed partial class ThemeColorGroupViewModel : ObservableObject
{
    public string Category { get; }

    [ObservableProperty]
    private ObservableCollection<ThemeColorSlotViewModel> _slots;

    public ThemeColorGroupViewModel(string category, ObservableCollection<ThemeColorSlotViewModel> slots)
    {
        Category = category;
        _slots = slots;
    }

    public void RefreshLabel()
    {
        OnPropertyChanged(nameof(Category));
    }
}
