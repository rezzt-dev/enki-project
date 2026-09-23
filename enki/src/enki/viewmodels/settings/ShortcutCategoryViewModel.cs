using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>One grouping card in Ajustes › Atajos ("Navegación", "Documentos y editor"...) — propuesta 29,
/// docs/propuesta-nuevas-funcionalidades.md. Groups follow <c>Services.ShortcutDefinition.CategoryKey</c>,
/// built once in <see cref="SettingsViewModel"/>'s constructor and re-labeled (not rebuilt) on a language
/// change, same as <c>SettingsSectionViewModel</c>.</summary>
public sealed partial class ShortcutCategoryViewModel : ObservableObject
{
    private readonly string _categoryKey;

    [ObservableProperty]
    private string _label;

    public string SearchText => string.Join("\n", Items.Select(item => Label + " " + item.Label));

    public IReadOnlyList<ShortcutRowViewModel> Items { get; }

    public ShortcutCategoryViewModel(string categoryKey, IReadOnlyList<ShortcutRowViewModel> items)
    {
        _categoryKey = categoryKey;
        _label = LocalizationService.Instance[categoryKey];
        Items = items;
    }

    public void RefreshLabel()
    {
        Label = LocalizationService.Instance[_categoryKey];
        foreach (var item in Items) item.RefreshLabel();
        OnPropertyChanged(nameof(SearchText));
    }
}
