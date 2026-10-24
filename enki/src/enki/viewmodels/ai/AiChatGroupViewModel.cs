using System.Collections.Generic;

namespace enki.ViewModels;

/// <summary>Grupo del historial: conversaciones del vault o de una misma sesión.</summary>
public sealed record AiChatGroupViewModel(string Title, IReadOnlyList<AiChatSessionItemViewModel> Chats);
