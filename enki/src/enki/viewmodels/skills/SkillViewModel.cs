using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Envoltorio observable para mostrar una <see cref="SkillFile"/> como tarjeta en
/// <see cref="SkillsViewModel"/>.</summary>
public sealed partial class SkillViewModel : ObservableObject
{
    /// <summary>Máximo de etiquetas mostradas directamente en la tarjeta antes de colapsar el resto
    /// en un chip "+N" — el panel de detalle sigue mostrando la lista completa sin este límite.</summary>
    private const int MaxPreviewTags = 3;

    public SkillFile Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string? Emoji => Model.Emoji;
    public bool IsDefault => Model.IsDefault;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasTags => Model.Tags.Count > 0;

    /// <summary>Etiquetas de la tarjeta ya recortadas para la rejilla — si sobran, la última entrada
    /// es un marcador "+N" en vez de un nombre de etiqueta real, para no desbordar la altura fija de
    /// la tarjeta. El panel de detalle usa <see cref="SkillFile.Tags"/> directamente, sin este límite.</summary>
    public IReadOnlyList<string> TagChips => Model.Tags.Count <= MaxPreviewTags
        ? Model.Tags
        : Model.Tags.Take(MaxPreviewTags).Append($"+{Model.Tags.Count - MaxPreviewTags}").ToList();

    /// <summary>Marcada mediante la casilla de la tarjeta para incluirla en la próxima exportación —
    /// independiente de <see cref="SkillsViewModel.SelectedSkill"/>, que solo controla la previsualización.</summary>
    [ObservableProperty]
    private bool _isCheckedForExport;

    /// <summary>Resalta la tarjeta mientras es <see cref="SkillsViewModel.SelectedSkill"/> — actualizada
    /// por <c>SkillsViewModel.OnSelectedSkillChanged</c>, mismo patrón que
    /// <c>SessionsViewModel.OnSelectedSessionChanged</c>.</summary>
    [ObservableProperty]
    private bool _isSelected;

    public SkillViewModel(SkillFile model) => Model = model;

    [RelayCommand]
    private void ToggleExport() => IsCheckedForExport = !IsCheckedForExport;

    /// <summary>Refleja en la tarjeta un cambio del modelo subyacente (p. ej. tras guardar en el editor,
    /// donde <see cref="SkillService.SaveSkill"/> actualiza <see cref="Model"/> in-place).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Emoji));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagChips));
    }
}
