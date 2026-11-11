using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>La tarjeta "Cambios pendientes de revisión" de una respuesta (AIA-07): los cambios que el asistente
/// preparó en ese turno, cada uno con su diff y una casilla para aprobarlo. Envuelve la cola real
/// (<see cref="AiChangeReview"/>); aplicar o descartar lo decide <see cref="AssistantViewModel"/>.</summary>
public sealed partial class AiChangeReviewViewModel : ObservableObject
{
    public AiChangeReviewViewModel(AiChangeReview review)
    {
        Review = review;
        Refresh();
    }

    public AiChangeReview Review { get; }

    public ObservableCollection<AiReviewChangeItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>"3 cambios esperan tu revisión".</summary>
    public string Summary => Plural.Get("Assistant_Review_Summary", Items.Count, Items.Count);

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    /// <summary>"Aplicar 2 seleccionados".</summary>
    public string ApplyLabel => Plural.Get("Assistant_Review_Apply", SelectedCount, SelectedCount);

    /// <summary>"Marcar todos" mientras falte alguno; "Desmarcar todos" con todos marcados.</summary>
    public string ToggleAllLabel => LocalizationService.Instance[Items.All(i => i.IsSelected) ? "Assistant_Review_DeselectAll" : "Assistant_Review_ToggleAll"];

    /// <summary>Selecciona o quita todos de una vez.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var select = Items.Any(i => !i.IsSelected);
        foreach (var item in Items) item.IsSelected = select;
    }

    /// <summary>Vuelve a leer los cambios pendientes de la cola (al prepararse uno nuevo durante el turno).</summary>
    public void Refresh()
    {
        var pending = Review.Changes.Where(c => c.IsPending).ToList();
        foreach (var gone in Items.Where(i => !pending.Contains(i.Change)).ToList()) Items.Remove(gone);
        foreach (var change in pending.Where(c => Items.All(i => i.Change != c)))
        {
            var item = new AiReviewChangeItemViewModel(change);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AiReviewChangeItemViewModel.IsSelected)) RefreshSelection();
            };
            Items.Add(item);
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
        RefreshSelection();
    }

    /// <summary>Los cambios aprobados, en el orden en que se prepararon.</summary>
    public IReadOnlyCollection<AiReviewChange> Approved => Items.Where(i => i.IsSelected).Select(i => i.Change).ToHashSet();

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(ToggleAllLabel));
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ApplyLabel));
        OnPropertyChanged(nameof(ToggleAllLabel));
        foreach (var item in Items) item.RefreshLocalizedLabels();
    }
}

/// <summary>Un cambio de la revisión: qué nota, qué tipo de cambio, cuántas líneas entran y salen y, al desplegarlo,
/// el diff con unas líneas de contexto alrededor de cada cambio.</summary>
public sealed partial class AiReviewChangeItemViewModel : ObservableObject
{
    private const int DiffContextLines = 2;

    private List<DiffLine>? _diff;

    public AiReviewChangeItemViewModel(AiReviewChange change)
    {
        Change = change;
    }

    public AiReviewChange Change { get; }

    public string Title => Change.Title;

    /// <summary>Espacio y ruta: "Vault · Proyectos/Plan.md".</summary>
    public string Location => Change.WorkspaceName.Length > 0 ? $"{Change.WorkspaceName} · {Change.RelativePath}" : Change.RelativePath;

    /// <summary>Qué pidió el asistente, con el nombre de la herramienta ya traducido ("Editar nota").</summary>
    public string Action => LocalizationService.Instance["Assistant_Tool_" + Change.Activity.Tool];

    public bool IsCreation => Change.Kind == AiReviewChangeKind.Create;

    public MaterialIconKind Icon => IsCreation ? MaterialIconKind.NotePlusOutline : MaterialIconKind.NoteEditOutline;

    /// <summary>Se aprueba al aplicar. Nace marcado: lo normal es querer lo que se pidió.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTooltip))]
    private bool _isExpanded;

    public string ToggleTooltip => LocalizationService.Instance[IsExpanded ? "Assistant_Review_HideDiff" : "Assistant_Review_ShowDiff"];

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded && DiffLines.Count == 0)
            foreach (var line in TextDiffService.Excerpt(Diff, DiffContextLines)) DiffLines.Add(line);
    }

    /// <summary>Se rellena al desplegar: una nota larga no calcula su diff si nadie lo mira.</summary>
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    private List<DiffLine> Diff => _diff ??= TextDiffService.Compare(
        Change.OriginalContent.Replace("\r\n", "\n", StringComparison.Ordinal),
        Change.ProposedContent.Replace("\r\n", "\n", StringComparison.Ordinal));

    /// <summary>"+12 −3".</summary>
    public string Stats => $"+{Diff.Count(l => l.Kind == DiffLineKind.Added)} −{Diff.Count(l => l.Kind == DiffLineKind.Removed)}";

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Action));
        OnPropertyChanged(nameof(ToggleTooltip));
    }
}
