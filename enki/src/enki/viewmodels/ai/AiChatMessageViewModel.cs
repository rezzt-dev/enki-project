using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(IsThinking))]
    private bool _isStreaming;

    /// <summary>Hay una respuesta en camino pero aún no ha llegado ni una palabra ni ninguna acción.</summary>
    public bool IsThinking => IsStreaming && !HasText && Activities.Count == 0;

    // ===================== Acciones en el vault =====================

    /// <summary>Lo que el asistente hizo en el vault durante esta respuesta, en orden.</summary>
    public ObservableCollection<AiToolActivityViewModel> Activities { get; } = [];

    public bool HasActivities => Activities.Count > 0;

    public string ActivitiesLabel => Plural.Get("Assistant_Activity_Count", Activities.Count, Activities.Count);

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
            return;
        }
        if (!Model.Activities.Contains(activity)) Model.Activities.Add(activity);
        Activities.Add(new AiToolActivityViewModel(activity));
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
        foreach (var attachment in Attachments) attachment.RefreshLocalizedLabels();
        foreach (var activity in Activities) activity.RefreshLocalizedLabels();
        PendingApproval?.RefreshLocalizedLabels();
        PendingReadAccess?.RefreshLocalizedLabels();
    }
}
