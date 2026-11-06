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

    /// <summary>Evita ofrecer un desplegable vacío cuando la skill solo define nombre y descripción.</summary>
    public bool HasMetadata => HasTags || Model.Tools.Count > 0 || Model.Permissions.Count > 0
        || !string.IsNullOrWhiteSpace(Model.Vibe);

    /// <summary>Identidad estable de la skill dentro de la biblioteca. Desde que hay carpetas,
    /// <see cref="Id"/> (el nombre de archivo) ya no es único —dos temas pueden tener su propio
    /// <c>redactor.md</c>— así que todo lo que compare skills entre recargas (restaurar la selección,
    /// localizar la copia recién duplicada) usa la ruta completa.</summary>
    public string FilePath => Model.FilePath;

    /// <summary>Carpeta en la que vive la skill, relativa a la biblioteca y vacía en la raíz. La tarjeta
    /// la muestra solo cuando la rejilla está enseñando resultados de toda la biblioteca (búsqueda o
    /// filtro de etiquetas activos), donde sin ella no se sabría de qué tema viene cada resultado.</summary>
    public string RelativeFolderPath => Model.RelativeFolderPath;
    public bool HasFolder => !Model.IsAtRoot;

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
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(TagChips));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(RelativeFolderPath));
        OnPropertyChanged(nameof(HasFolder));
    }
}
