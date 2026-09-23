using System.Linq;
using enki.Localization;
using enki.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string _searchText = string.Empty;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>El índice usa los mismos catálogos y entidades que las listas configurables.</summary>
    // Propiedades de instancia para los bindings y las notificaciones al cambiar de idioma.
#pragma warning disable CA1822
    public string ShortcutSearchText => string.Join("\n", ShortcutCatalog.All.Select(item =>
        LocalizationService.Instance[item.LabelKey] + " " + LocalizationService.Instance[item.CategoryKey]));

    public string SidebarSearchText => string.Join("\n", SidebarSectionCatalog.Meta.Values
        .Select(item => LocalizationService.Instance[item.LabelKey]));

#pragma warning restore CA1822

    public string DashboardSearchText => string.Join("\n", DashboardSections.Select(item => item.Title + " " + item.Description)
        .Concat(DashboardLayoutOptions.Select(item => item.Title + " " + item.Description)));

    public string ThemeSearchText => string.Join("\n", CustomThemes.Concat(BuiltInDarkThemes).Concat(BuiltInLightThemes)
        .Select(theme => theme.Name));

    private void RefreshSearchTexts()
    {
        OnPropertyChanged(nameof(ShortcutSearchText));
        OnPropertyChanged(nameof(SidebarSearchText));
        OnPropertyChanged(nameof(DashboardSearchText));
        OnPropertyChanged(nameof(ThemeSearchText));
        // Las plantillas no son texto traducible (son archivos del usuario), pero su desplegable sí
        // incluye la opción "sin plantilla", que cambia con el idioma.
        ReloadDailyNoteTemplates();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;
}
