using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Revisión de cambios por lotes (AIA-07): con <see cref="AiSettings.ReviewChangesBeforeApplying"/>
/// encendido, cada turno lleva su cola (<see cref="AiChangeReview"/>) y la respuesta enseña la tarjeta con los
/// cambios preparados para aprobarlos o descartarlos juntos. Qué se aplica y la garantía de no pisar lo que cambió
/// mientras tanto viven en <see cref="AiVaultToolbox.ResolveReview"/>; aquí está el ciclo de vida de las colas, el
/// aviso del resultado y su persistencia en el historial.
///
/// <para>Una cola vive en memoria, nunca en el historial del chat: guarda el contenido de las notas, que puede ser
/// de una sesión. Se conserva al cambiar de chat; al cambiar de vault o cerrar Enki, lo pendiente se da por
/// descartado y así queda en el historial.</para></summary>
public sealed partial class AssistantViewModel
{
    private readonly Func<AiChangeReview, IReadOnlyCollection<AiReviewChange>, AiReviewReport>? _resolveReview;

    /// <summary>Colas con cambios pendientes, por respuesta (la del historial, no su burbuja: al reabrir el chat se
    /// vuelven a enganchar).</summary>
    private readonly Dictionary<AiChatMessage, AiChangeReviewViewModel> _reviews = [];

    /// <summary>Se puede aplicar o descartar: la respuesta terminó (podría seguir preparando cambios) y hay con qué.</summary>
    public bool CanResolveReview => _resolveReview is not null && !IsSending;

    /// <summary>Abre la cola del turno si el usuario quiere revisar los cambios antes de aplicarlos.</summary>
    private AiChangeReview? BeginReview(AiChatMessageViewModel bubble)
    {
        if (_resolveReview is null || !_settingsService.Settings.Ai.ReviewChangesBeforeApplying) return null;
        var review = new AiChangeReview();
        var card = new AiChangeReviewViewModel(review);
        review.Changed += () => _dispatch(card.Refresh);
        _reviews[bubble.Model] = card;
        bubble.Review = card;
        return review;
    }

    /// <summary>Al terminar el turno: una cola en la que no se preparó nada no deja tarjeta; si quedó algo y el usuario
    /// no está mirando, se le avisa de que hay cambios esperando.</summary>
    private void EndReview(AiChatMessageViewModel bubble)
    {
        if (bubble.Review is not { } card) return;
        if (!card.HasItems)
        {
            _reviews.Remove(bubble.Model);
            bubble.Review = null;
            return;
        }
        if (!_isVisible())
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Review_Notify_Pending_Title"),
                LocalizedTextFormatter.PluralFormat("Assistant_Review_Notify_Pending_Message", card.Items.Count, card.Items.Count),
                NotificationSeverity.Info);
    }

    [RelayCommand]
    private void ApplyReview(AiChatMessageViewModel? message) => ResolveReview(message, approve: true);

    [RelayCommand]
    private void DiscardReview(AiChatMessageViewModel? message) => ResolveReview(message, approve: false);

    private void ResolveReview(AiChatMessageViewModel? message, bool approve)
    {
        if (message?.Review is not { } card || !CanResolveReview || _activeSession is not { } session
            || !session.Messages.Contains(message.Model)) return;
        if (approve && !card.HasSelection) return;

        // Lo que el editor tenga sin guardar se vuelca antes: si el usuario está cambiando una de esas notas, el
        // cambio preparado ya no parte de lo que hay y no se aplica.
        _flushPendingEdits();
        AiReviewReport report;
        try
        {
            report = _resolveReview!(card.Review, approve ? card.Approved : []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException or SessionLockedException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Review_Notify_Failed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
            return;
        }

        _reviews.Remove(message.Model);
        message.Review = null;
        message.RefreshRevertState();
        // Lo que no se pudo aplicar dice por qué en su fila: se despliega para que se vea.
        if (report.NotApplied > 0) message.IsActivitiesExpanded = true;
        TrySaveSession(session, touch: false);
        NotifyReviewResolved(report);
    }

    private void NotifyReviewResolved(AiReviewReport report)
    {
        var parts = new List<LocalizedText>();
        if (report.Applied > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Review_Notify_Applied", report.Applied, report.Applied));
        if (report.NotApplied > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Review_Notify_NotApplied", report.NotApplied, report.NotApplied));
        if (report.Discarded > 0)
            parts.Add(LocalizedTextFormatter.PluralFormat("Assistant_Review_Notify_Discarded", report.Discarded, report.Discarded));
        if (parts.Count == 0) return;
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Review_Notify_Title"),
            LocalizedTextFormatter.Join(" ", [.. parts]),
            report.NotApplied > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success);
    }

    /// <summary>Al abrir un chat: las respuestas con una cola viva la recuperan; una acción que quedó pendiente sin
    /// cola (se cerró Enki o se cambió de vault antes de decidir) se da por descartada, que es lo que ocurrió.</summary>
    private void AttachReviews(AiChatSession session)
    {
        var changed = false;
        foreach (var message in Messages)
        {
            if (_reviews.TryGetValue(message.Model, out var card))
            {
                message.Review = card;
                continue;
            }
            var stale = message.Model.Activities.Where(a => a.State == AiToolActivityState.Pending).ToList();
            if (stale.Count == 0) continue;
            foreach (var activity in stale) activity.State = AiToolActivityState.Discarded;
            message.RefreshRevertState();
            changed = true;
        }
        if (changed) TrySaveSession(session, touch: false);
    }

    /// <summary>Al cambiar de vault: lo pendiente ya no se puede aplicar sobre el vault que estaba abierto.</summary>
    private void DiscardAllReviews()
    {
        foreach (var (message, card) in _reviews)
        {
            foreach (var change in card.Review.Changes.Where(c => c.IsPending))
            {
                change.State = AiReviewChangeState.Discarded;
                change.Activity.State = AiToolActivityState.Discarded;
            }
            if (_chats.Sessions.FirstOrDefault(s => s.Messages.Contains(message)) is { } owner) TrySaveSession(owner, touch: false);
        }
        _reviews.Clear();
        foreach (var message in Messages)
        {
            message.Review = null;
            message.RefreshRevertState();
        }
    }

    private void RefreshReviewState() => OnPropertyChanged(nameof(CanResolveReview));
}
