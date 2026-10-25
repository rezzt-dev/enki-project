using System.Collections.Generic;

namespace enki.ViewModels;

/// <summary>Grupo del historial: conversaciones del vault, de una misma sesión o, aparte, las antiguas que aún
/// no tienen vault asignado.</summary>
public sealed record AiChatGroupViewModel(string Title, IReadOnlyList<AiChatSessionItemViewModel> Chats, bool IsUnassigned = false);
