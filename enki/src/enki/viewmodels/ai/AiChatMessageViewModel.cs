using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una burbuja del chat. Envuelve el <see cref="AiChatMessage"/> real que se guarda en el historial
/// — no una copia —, así que lo que se escribe en <see cref="Text"/> durante el streaming es lo mismo que se
/// persiste al terminar el turno.</summary>
public sealed partial class AiChatMessageViewModel : ObservableObject
{
    public AiChatMessageViewModel(AiChatMessage model)
    {
        Model = model;
        _text = model.Text;
        Attachments = model.Attachments.Select(a => new AiAttachmentChipViewModel(a)).ToList();
        foreach (var activity in model.Activities) Activities.Add(new AiToolActivityViewModel(activity));
        Activities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasActivities));
            OnPropertyChanged(nameof(IsThinking));
            OnPropertyChanged(nameof(ActivitiesLabel));
            OnPropertyChanged(nameof(CanRevert));
        };
    }

    public AiChatMessage Model { get; }

    public bool IsUser => Model.Role == AiChatRole.User;
    public bool IsAssistant => Model.Role == AiChatRole.Assistant;

    public IReadOnlyList<AiAttachmentChipViewModel> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(CanAct))]
    private string _text;

    /// <summary>La respuesta está llegando todavía.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(IsThinking), nameof(CanRevert))]
    private bool _isStreaming;

    /// <summary>Hay una respuesta en camino pero aún no ha llegado ni una palabra ni ninguna acción.</summary>
    public bool IsThinking => IsStreaming && !HasText && Activities.Count == 0;

    // ===================== Acciones en el vault =====================

    /// <summary>Lo que el asistente hizo en el vault durante esta respuesta, en orden.</summary>
    public ObservableCollection<AiToolActivityViewModel> Activities { get; } = [];

    public bool HasActivities => Activities.Count > 0;

    public string ActivitiesLabel => Plural.Get("Assistant_Activity_Count", Activities.Count, Activities.Count);

    /// <summary>El detalle de acciones nace plegado en cada respuesta, también al reabrir el historial.
    /// Solo el usuario lo despliega; las actualizaciones del turno conservan su elección.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivitiesToggleTooltip))]
    private bool _isActivitiesExpanded;

    public string ActivitiesToggleTooltip => LocalizationService.Instance[IsActivitiesExpanded
        ? "Assistant_Activity_Collapse" : "Assistant_Activity_Expand"];

    [RelayCommand]
    private void ToggleActivities() => IsActivitiesExpanded = !IsActivitiesExpanded;

    /// <summary>Acción que espera la confirmación del usuario, si la hay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingApproval))]
    private AiApprovalViewModel? _pendingApproval;

    public bool HasPendingApproval => PendingApproval is not null;

    /// <summary>Petición de permiso para buscar y leer por su cuenta, si la respuesta la está esperando.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingReadAccess))]
    private AiReadAccessRequestViewModel? _pendingReadAccess;

    public bool HasPendingReadAccess => PendingReadAccess is not null;

    /// <summary>Añade o refresca la fila de una acción. El servidor del vault avisa con la misma instancia al
    /// empezar y al terminar, así que una segunda llamada solo actualiza la fila existente.</summary>
    public void ReportActivity(AiToolActivity activity)
    {
        var existing = Activities.FirstOrDefault(a => ReferenceEquals(a.Model, activity));
        if (existing is not null)
        {
            existing.Refresh();
            OnPropertyChanged(nameof(CanRevert));
            return;
        }
        if (!Model.Activities.Contains(activity)) Model.Activities.Add(activity);
        Activities.Add(new AiToolActivityViewModel(activity));
    }

    /// <summary>Cambios que esta respuesta preparó y esperan la revisión del usuario (AIA-07), si los hay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReview))]
    private AiChangeReviewViewModel? _review;

    public bool HasReview => Review is not null;

    // ===================== Revertir y consumo =====================

    /// <summary>Cuántas acciones de esta respuesta se pueden intentar deshacer todavía.</summary>
    public int RevertibleCount => Activities.Count(a => a.CanRevert);

    /// <summary>"Revertir esta respuesta" tiene algo que hacer: la respuesta terminó y cambió algo recuperable
    /// que aún no se ha revertido.</summary>
    public bool CanRevert => IsAssistant && !IsStreaming && RevertibleCount > 0;

    /// <summary>Tras revertir: cada fila enseña si se deshizo o por qué no.</summary>
    public void RefreshRevertState()
    {
        foreach (var activity in Activities) activity.Refresh();
        OnPropertyChanged(nameof(CanRevert));
    }

    public bool HasUsage => Model.Usage is { IsEmpty: false };

    /// <summary>"1.234 tokens de entrada · 567 de salida", con el coste si el proveedor lo dio.</summary>
    public string UsageLabel => Model.Usage is { IsEmpty: false } usage ? AssistantViewModel.FormatUsage(usage, chatTotal: false) : string.Empty;

    public void RefreshUsage()
    {
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageLabel));
    }

    [ObservableProperty]
    private bool _isInterrupted;

    /// <summary>Confirmación breve tras copiar la respuesta, en el propio botón.</summary>
    [ObservableProperty]
    private bool _isCopied;

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Las acciones (copiar, guardar como nota) solo tienen sentido sobre una respuesta terminada.</summary>
    public bool CanAct => IsAssistant && !IsStreaming && HasText;

    public string TimeLabel => RelativeTimeFormatter.FormatClockOrShortDate(Model.CreatedUtc);

    partial void OnTextChanged(string value)
    {
        Model.Text = value;
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnIsInterruptedChanged(bool value) => Model.IsInterrupted = value;

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(TimeLabel));
        OnPropertyChanged(nameof(ActivitiesLabel));
        OnPropertyChanged(nameof(ActivitiesToggleTooltip));
        OnPropertyChanged(nameof(UsageLabel));
        foreach (var attachment in Attachments) attachment.RefreshLocalizedLabels();
        foreach (var activity in Activities) activity.RefreshLocalizedLabels();
        PendingApproval?.RefreshLocalizedLabels();
        PendingReadAccess?.RefreshLocalizedLabels();
        Review?.RefreshLocalizedLabels();
    }
}
