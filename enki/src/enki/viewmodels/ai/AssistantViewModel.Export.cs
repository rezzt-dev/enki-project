using System;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>"Exportar conversación" (AIA-09): guarda el chat entero —mensajes, adjuntos, acciones en el vault y
/// respuestas interrumpidas— como una nota Markdown nueva en la raíz del espacio del chat, con propiedades de origen,
/// y la abre. Igual que "Guardar como nota", nunca escribe sobre una nota existente. El Markdown lo arma
/// <see cref="AiChatExporter"/> con los textos del idioma vigente.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Hay una conversación con mensajes y no está llegando una respuesta, que quedaría a medias.</summary>
    public bool CanExportConversation => _activeSession is { Messages.Count: > 0 } && !IsSending;

    [RelayCommand]
    private void ExportConversation()
    {
        if (!CanExportConversation || _activeSession is not { } session) return;
        var text = LocalizationService.Instance;
        var culture = text.CurrentCulture;
        var title = string.Format(culture, text["Assistant_Export_NoteTitle"],
            session.Title.Length > 0 ? session.Title : text["Assistant_Note_DefaultTitle"]);
        var labels = new AiChatExportLabels(
            text["Assistant_Export_User"],
            AssistantName,
            text["Assistant_Export_Attachments"],
            text["Assistant_Export_Actions"],
            text["Assistant_Message_Interrupted"],
            tool => text["Assistant_Tool_" + tool],
            ExportStateLabel);
        CreateNoteInChatSpace(title, AiChatExporter.ToMarkdown(session, title, labels, DateTime.UtcNow, culture),
            "Assistant_Notify_ChatExported");
    }

    /// <summary>Cómo terminó una acción, para la exportación: nada si se hizo y sigue hecha.</summary>
    private static string? ExportStateLabel(AiToolActivity activity)
    {
        var text = LocalizationService.Instance;
        if (activity.Undo is { State: AiUndoState.Reverted }) return text["Assistant_Undo_Reverted"];
        return activity.State switch
        {
            AiToolActivityState.Failed => text["Assistant_Export_State_Failed"],
            AiToolActivityState.Denied => text["Assistant_Export_State_Denied"],
            AiToolActivityState.Pending => text["Assistant_Review_State_Pending"],
            AiToolActivityState.Discarded => text["Assistant_Review_State_Discarded"],
            _ => null
        };
    }

    private void RefreshExportState()
    {
        OnPropertyChanged(nameof(CanExportConversation));
        ExportConversationCommand.NotifyCanExecuteChanged();
    }
}
