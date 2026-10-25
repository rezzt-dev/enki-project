using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una fila del Gestor de etiquetas — envuelve un <see cref="TagManagerEntry"/> de solo lectura
/// con el único estado propio de la vista: si está marcada para una fusión múltiple. Se reconstruye por
/// completo en cada <see cref="TagManagerViewModel.Reload"/>, nunca se edita en el sitio: renombrar/fusionar/
/// eliminar siempre pasan por <c>Services.TagManagerService.ReplaceTag</c> sobre las entidades reales.</summary>
public sealed partial class TagManagerItemViewModel : ObservableObject
{
    public TagManagerEntry Entry { get; }

    public string Name => Entry.Name;
    public int NoteCount => Entry.NoteCount;
    public int KanbanCount => Entry.KanbanCount;
    public int PolarCount => Entry.PolarCount;
    public int TotalCount => Entry.TotalCount;

    /// <summary>Marcada por el usuario como origen de una fusión múltiple —
    /// <see cref="TagManagerViewModel.RequestMergeCommand"/> se habilita con dos o más marcadas.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"N notas · M kanban · K polar" — solo los dominios con al menos un uso aparecen, para que
    /// una etiqueta exclusiva de un módulo no muestre "0 notas · 0 kanban · 3 polar".</summary>
    public string BreakdownLabel
    {
        get
        {
            var parts = new List<string>();
            if (NoteCount > 0) parts.Add(Plural.Get("TagManager_Breakdown_Notes", NoteCount, NoteCount));
            if (KanbanCount > 0) parts.Add(Plural.Get("TagManager_Breakdown_Kanban", KanbanCount, KanbanCount));
            if (PolarCount > 0) parts.Add(Plural.Get("TagManager_Breakdown_Polar", PolarCount, PolarCount));
            return string.Join(" · ", parts);
        }
    }

    public TagManagerItemViewModel(TagManagerEntry entry) => Entry = entry;

    /// <summary>Refresca <see cref="BreakdownLabel"/> tras un cambio de idioma en caliente (i18n Fase 4) —
    /// los conteos no cambian, solo el texto "notas"/"kanban"/"polar" ya resuelto.</summary>
    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(BreakdownLabel));
}
