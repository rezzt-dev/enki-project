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

    /// <summary>De una página web se enseña la dirección completa al pasar el cursor; del resto, la etiqueta.</summary>
    public string TargetTooltip => Model.TargetKind == AiToolTargetKind.WebPage && Model.TargetId is { Length: > 0 } url ? url : Model.Target;

    public bool IsRunning => Model.State == AiToolActivityState.Running;
    public bool IsSucceeded => Model.State == AiToolActivityState.Succeeded;
    public bool IsFailed => Model.State == AiToolActivityState.Failed;
    public bool IsDenied => Model.State == AiToolActivityState.Denied;

    /// <summary>Preparado y a la espera de la revisión del usuario: todavía no se ha escrito nada.</summary>
    public bool IsPending => Model.State == AiToolActivityState.Pending;

    public bool IsDiscarded => Model.State == AiToolActivityState.Discarded;

    public string? Error => Model.Error;
    public bool HasError => !string.IsNullOrEmpty(Model.Error);

    /// <summary>Las notas, las tarjetas y las páginas web se pueden abrir desde la fila; el resto no tiene una
    /// pantalla propia a la que llevar.</summary>
    public bool CanOpen => IsSucceeded && !string.IsNullOrEmpty(Model.TargetId)
        && Model.TargetKind is AiToolTargetKind.Note or AiToolTargetKind.KanbanCard or AiToolTargetKind.WebPage;

    // ===================== Revertir =====================

    /// <summary>La acción cambió algo que todavía se puede intentar deshacer.</summary>
    public bool CanRevert => IsSucceeded && Model.Undo is { CanAttempt: true };

    public bool IsReverted => Model.Undo is { State: AiUndoState.Reverted };

    /// <summary>Se intentó revertir y se dejó como estaba.</summary>
    public bool IsRevertSkipped => Model.Undo is { State: AiUndoState.Skipped };

    public bool HasRevertNote => IsReverted || IsRevertSkipped || IsPending || IsDiscarded;

    /// <summary>"Revertida", o por qué no se revirtió; o, con la revisión de cambios, "Pendiente de revisión" o
    /// "Descartado". En el idioma vigente.</summary>
    public string RevertNote => Model.State switch
    {
        AiToolActivityState.Pending => LocalizationService.Instance["Assistant_Review_State_Pending"],
        AiToolActivityState.Discarded => LocalizationService.Instance["Assistant_Review_State_Discarded"],
        _ => Model.Undo switch
        {
            { State: AiUndoState.Reverted } => LocalizationService.Instance["Assistant_Undo_Reverted"],
            { State: AiUndoState.Skipped, Reason: { } reason } => LocalizationService.Instance["Assistant_Undo_Reason_" + reason],
            _ => string.Empty
        }
    };

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
        AiToolTargetKind.WebSearch => MaterialIconKind.SearchWeb,
        AiToolTargetKind.WebPage => MaterialIconKind.Web,
        AiToolTargetKind.Habit => MaterialIconKind.ClipboardCheckOutline,
        AiToolTargetKind.Section => MaterialIconKind.OpenInApp,
        AiToolTargetKind.Help => MaterialIconKind.HelpCircleOutline,
        AiToolTargetKind.Tag => MaterialIconKind.TagOutline,
        AiToolTargetKind.Insight => MaterialIconKind.ChartBoxOutline,
        _ => Model.Tool.StartsWith("search", System.StringComparison.Ordinal) ? MaterialIconKind.Magnify : MaterialIconKind.Creation
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(TargetTooltip));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDenied));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsDiscarded));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(IsReverted));
        OnPropertyChanged(nameof(IsRevertSkipped));
        OnPropertyChanged(nameof(HasRevertNote));
        OnPropertyChanged(nameof(RevertNote));
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(RevertNote));
    }
}
