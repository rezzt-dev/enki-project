using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>"Revertir esta respuesta": deshace lo que el asistente cambió en un turno. La decisión de qué se
/// puede deshacer y la garantía de no pisar lo que el usuario editó después viven en
/// <see cref="AiVaultToolbox.RevertActivities"/>; aquí está la confirmación, el aviso del resultado y el apunte
/// para que el modelo se entere en el turno siguiente de que esas acciones ya no están hechas.</summary>
public sealed partial class AssistantViewModel
{
    private readonly Func<IReadOnlyList<AiToolActivity>, AiRevertReport>? _revertActivities;

    /// <summary>Se puede revertir ahora: hay con qué y no hay una respuesta en curso, que podría estar tocando
    /// lo mismo.</summary>
    public bool CanRevertResponses => _revertActivities is not null && !IsSending;

    /// <summary>Respuesta que espera la confirmación para revertirse.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingRevertSummary))]
    private AiChatMessageViewModel? _pendingRevertMessage;

    /// <summary>"3 acciones se pueden revertir", para el diálogo de confirmación.</summary>
    public string PendingRevertSummary => PendingRevertMessage is { } message
        ? Plural.Get("Assistant_Undo_Confirm_Count", message.RevertibleCount, message.RevertibleCount)
        : string.Empty;

    [RelayCommand]
    private void RequestRevertMessage(AiChatMessageViewModel? message)
    {
        if (message is { CanRevert: true } && CanRevertResponses) PendingRevertMessage = message;
    }

    [RelayCommand]
    private void CancelRevertMessage() => PendingRevertMessage = null;

    [RelayCommand]
    private void ConfirmRevertMessage()
    {
        var message = PendingRevertMessage;
        PendingRevertMessage = null;
        if (message is not { CanRevert: true } || !CanRevertResponses || _activeSession is not { } session
            || !session.Messages.Contains(message.Model)) return;

        // Lo que el editor tenga sin guardar se vuelca antes: una nota que el usuario está cambiando ahora
        // mismo ya no coincide con lo que dejó el asistente, y no se toca.
        _flushPendingEdits();
        AiRevertReport report;
        try
        {
            report = _revertActivities!(message.Model.Activities);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException or SessionLockedException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Undo_Notify_Failed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
            return;
        }

        message.RefreshRevertState();
        // El detalle de cada acción (revertida, o por qué no) queda a la vista en la propia respuesta.
        message.IsActivitiesExpanded = true;
        TrySaveSession(session, touch: false);
        NotifyReverted(report);
    }

    private void NotifyReverted(AiRevertReport report)
    {
        var parts = new List<LocalizedText>();
        if (report.Reverted > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Undo_Notify_Reverted", report.Reverted, report.Reverted));
        if (report.Skipped > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Undo_Notify_Skipped", report.Skipped, report.Skipped));
        if (report.NotRevertible > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Undo_Notify_NotRevertible", report.NotRevertible, report.NotRevertible));
        if (parts.Count == 0) parts.Add(LocalizedTextFormatter.Format("Assistant_Undo_Notify_Nothing"));

        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Undo_Notify_Title"),
            LocalizedTextFormatter.Join(" ", [.. parts]),
            report.Skipped > 0 || report.Reverted == 0 ? NotificationSeverity.Warning : NotificationSeverity.Success);
    }

    /// <summary>Acciones revertidas de las que el modelo todavía no sabe nada: se le cuentan al empezar el turno
    /// siguiente (ver <see cref="AiPromptBuilder.WithRevertedActions"/>).</summary>
    private static List<AiToolActivity> UnannouncedReverts(AiChatSession session) =>
        session.Messages.SelectMany(message => message.Activities)
            .Where(activity => activity.Undo is { State: AiUndoState.Reverted, ModelInformed: false })
            .ToList();
}
