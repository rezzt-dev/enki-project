using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public enum PolarListEditorMode
{
    Create,
    Edit
}

/// <summary>Draft state backing the "nueva lista"/"editar lista" popup — same create+edit-share-one-form
/// pattern as <see cref="KanbanCardEditorViewModel"/>. Color is intentionally not editable here (cosmetic,
/// explicitly low-priority per docs/supabase-sync/00 — new lists just get the same default Polar itself
/// uses, and editing an existing list preserves whatever color it already had).</summary>
public sealed partial class PolarListEditorViewModel : ObservableObject
{
    public PolarListEditorMode Mode { get; }
    public bool IsCreateMode => Mode == PolarListEditorMode.Create;
    public PolarTaskList? SourceList { get; }

    public ObservableCollection<PolarIconOptionViewModel> IconOptions { get; } = [];

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _selectedIcon = "ic_list";

    [ObservableProperty]
    private bool _isDependencyChain;

    public string HeadingEyebrow => IsCreateMode
        ? LocalizationService.Instance["Polar_ListEditor_NewEyebrow"]
        : LocalizationService.Instance["Polar_ListEditor_EditEyebrow"];
    public string HeadingTitle => IsCreateMode
        ? LocalizationService.Instance["Polar_ListEditor_CreateTitle"]
        : LocalizationService.Instance["Polar_ListEditor_EditTitle"];
    public string ConfirmLabel => IsCreateMode
        ? LocalizationService.Instance["Polar_Editor_ConfirmCreate"]
        : LocalizationService.Instance["Polar_Editor_ConfirmSave"];
    public bool CanSave => !string.IsNullOrWhiteSpace(Title);

    /// <summary>Mismo motivo que en <see cref="PolarTaskEditorViewModel.RefreshLocalizedLabels"/>: el
    /// overlay abierto no se reconstruye en <c>Reload()</c>, se refresca en el sitio.</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(HeadingEyebrow));
        OnPropertyChanged(nameof(HeadingTitle));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    private PolarListEditorViewModel(PolarListEditorMode mode, PolarTaskList? sourceList)
    {
        Mode = mode;
        SourceList = sourceList;

        foreach (var key in PolarIconMap.Keys)
            IconOptions.Add(new PolarIconOptionViewModel(key, key == SelectedIcon));
    }

    public static PolarListEditorViewModel ForCreate() => new(PolarListEditorMode.Create, null);

    public static PolarListEditorViewModel ForEdit(PolarTaskList list)
    {
        var editor = new PolarListEditorViewModel(PolarListEditorMode.Edit, list)
        {
            Title = list.Title,
            SelectedIcon = list.Icon,
            IsDependencyChain = list.IsDependencyChain
        };
        foreach (var option in editor.IconOptions) option.IsSelected = option.Key == list.Icon;
        return editor;
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanSave));

    [RelayCommand]
    private void SelectIcon(string icon)
    {
        SelectedIcon = icon;
        foreach (var option in IconOptions) option.IsSelected = option.Key == icon;
    }
}
