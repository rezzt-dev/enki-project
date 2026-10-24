using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Una fila de "lo que hizo el asistente" dentro de su respuesta. Envuelve la
/// <see cref="AiToolActivity"/> real que se guarda en el historial; <see cref="Refresh"/> vuelve a leerla
/// cada vez que el servidor del vault avisa de un cambio de estado.</summary>
public sealed partial class AiToolActivityViewModel(AiToolActivity model) : ObservableObject
{
    public AiToolActivity Model { get; } = model;

    /// <summary>Qué hizo, en el idioma vigente: <c>Assistant_Tool_create_note</c> → "Crear nota".</summary>
    public string Label => LocalizationService.Instance["Assistant_Tool_" + Model.Tool];

    public string Target => Model.Target;
    public bool HasTarget => Model.Target.Length > 0;

    public bool IsRunning => Model.State == AiToolActivityState.Running;
    public bool IsSucceeded => Model.State == AiToolActivityState.Succeeded;
    public bool IsFailed => Model.State == AiToolActivityState.Failed;
    public bool IsDenied => Model.State == AiToolActivityState.Denied;

    public string? Error => Model.Error;
    public bool HasError => !string.IsNullOrEmpty(Model.Error);

    /// <summary>Las notas y las tarjetas se pueden abrir desde la fila; el resto no tiene una pantalla propia
    /// a la que llevar.</summary>
    public bool CanOpen => IsSucceeded && !string.IsNullOrEmpty(Model.TargetId)
        && Model.TargetKind is AiToolTargetKind.Note or AiToolTargetKind.KanbanCard;

    public MaterialIconKind Icon => Model.TargetKind switch
    {
        AiToolTargetKind.Note => MaterialIconKind.NoteTextOutline,
        AiToolTargetKind.Folder => MaterialIconKind.FolderOutline,
        AiToolTargetKind.File => MaterialIconKind.FileImageOutline,
        AiToolTargetKind.Session => MaterialIconKind.SitemapOutline,
        AiToolTargetKind.KanbanBoard or AiToolTargetKind.KanbanCard => MaterialIconKind.ViewColumnOutline,
        AiToolTargetKind.PolarList or AiToolTargetKind.PolarTask => MaterialIconKind.CheckboxMarkedCircleOutline,
        AiToolTargetKind.Reminder => MaterialIconKind.CalendarClockOutline,
        AiToolTargetKind.Trash => MaterialIconKind.TrashCanOutline,
        _ => Model.Tool.StartsWith("search", System.StringComparison.Ordinal) ? MaterialIconKind.Magnify : MaterialIconKind.Creation
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDenied));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Icon));
    }

    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(Label));
}
