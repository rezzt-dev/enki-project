using System.Linq;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Uso del proveedor: los tokens de cada respuesta y el total de la conversación, cuando el proveedor
/// los informa (el CLI de Claude y el de Codex en su resultado final, la API de Gemini y Ollama en cada
/// respuesta). Son solo cifras, guardadas con el mensaje; el ajuste <see cref="AiSettings.ShowUsage"/> decide si
/// se enseñan, no si se guardan. El coste solo aparece cuando el turno se facturó a una clave de API propia y el
/// proveedor lo calcula: con una suscripción no hay un coste por respuesta que enseñar.</summary>
public sealed partial class AssistantViewModel
{
    public bool ShowUsage => _settingsService.Settings.Ai.ShowUsage;

    private AiTokenUsage? ChatUsage => _activeSession?.Messages.Aggregate((AiTokenUsage?)null,
        (total, message) => AiTokenUsage.Add(total, message.Usage));

    /// <summary>Hay consumo que enseñar del chat abierto.</summary>
    public bool HasChatUsage => ShowUsage && ChatUsage is { IsEmpty: false };

    /// <summary>"Total del chat: 12.340 tokens de entrada · 2.100 de salida".</summary>
    public string ChatUsageLabel => ChatUsage is { IsEmpty: false } usage ? FormatUsage(usage, chatTotal: true) : string.Empty;

    /// <summary>Apunta en la respuesta lo que consumió el turno. El coste se descarta si no lo paga una clave de
    /// API del usuario: el CLI de Claude lo estima también con suscripción, y enseñarlo confundiría.</summary>
    private void RecordUsage(AiChatMessageViewModel bubble, ClaudeTurnResult? result)
    {
        if (result?.Usage is not { IsEmpty: false } usage) return;
        bubble.Model.Usage = new AiTokenUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CachedInputTokens = usage.CachedInputTokens,
            CostUsd = UsesApiKey ? usage.CostUsd : null
        };
        bubble.RefreshUsage();
    }

    private void RefreshUsage()
    {
        OnPropertyChanged(nameof(ShowUsage));
        OnPropertyChanged(nameof(HasChatUsage));
        OnPropertyChanged(nameof(ChatUsageLabel));
    }

    internal static string FormatUsage(AiTokenUsage usage, bool chatTotal)
    {
        var text = LocalizationService.Instance;
        var culture = text.CurrentCulture;
        var key = (chatTotal ? "Assistant_Usage_Chat" : "Assistant_Usage_Message") + (usage.CostUsd is null ? string.Empty : "Cost");
        return string.Format(culture, text[key], usage.InputTokens.ToString("N0", culture), usage.OutputTokens.ToString("N0", culture),
            usage.CostUsd is { } cost ? cost.ToString("0.00##", culture) + " USD" : string.Empty);
    }
}
