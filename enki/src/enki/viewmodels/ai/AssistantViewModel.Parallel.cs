using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Composición manual de conversaciones vivas. Cada viewmodel conserva compositor, revisión,
/// cancelación, servidor MCP registrado y mensajes propios. La sección y el panel enseñan la misma selección.</summary>
public sealed class AssistantChatWorkspace
{
    internal List<AssistantViewModel> Chats { get; } = [];
    internal HashSet<AiChatSession> RemovedSessions { get; } = [];
    public AssistantViewModel? Current { get; internal set; }
    public event Action<AssistantViewModel>? ChatSelected;

    internal void Select(AssistantViewModel chat)
    {
        Current = chat;
        ChatSelected?.Invoke(chat);
    }
}

public sealed partial class AssistantViewModel
{
    private readonly Func<AssistantViewModel> _createChat;
    private readonly Action _releaseSubscriptions;
    private bool _isRetired;
    public AssistantChatWorkspace Workspace { get; }

    private AssistantViewModel SwitchChat(AiChatSession? session)
    {
        SaveDraft();
        var target = session is null ? null
            : Workspace.Chats.FirstOrDefault(chat => chat._activeSession == session);
        if (target is null)
        {
            target = _createChat();
            target._restoredLastChat = true;
            if (session is not null)
            {
                // Una revisión de un chat visitado antes de abrir turnos en paralelo puede seguir en otro
                // viewmodel ocioso. Se traslada por mensaje antes de reconstruir sus burbujas.
                foreach (var peer in Workspace.Chats.Where(chat => chat != target))
                foreach (var message in session.Messages)
                    if (peer._reviews.Remove(message, out var review)) target._reviews[message] = review;
                target.OpenSession(session);
            }
            else target.StartNewChat();
        }
        target.RebuildSessionList();
        target.IsPanelHistoryOpen = false;
        Workspace.Select(target);
        if (session is null) target.ComposerFocusRequested?.Invoke();
        RefreshActivityState();
        return target;
    }
}
