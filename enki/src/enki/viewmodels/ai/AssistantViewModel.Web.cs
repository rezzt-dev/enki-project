using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Acceso a internet del chat: el botón del globo del compositor lo enciende o apaga para la
/// conversación abierta (<see cref="AiChatSession.WebAccess"/>) o para la que se va a empezar. Se aplica en
/// caliente: apagarlo durante una respuesta ya impide la búsqueda o la lectura siguiente, porque el servidor del
/// vault lo lee en cada llamada. Encenderlo vale para el mensaje siguiente.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Lo elegido para el chat que se va a empezar; <see langword="null"/> mientras el usuario no lo
    /// toque, para seguir a <see cref="AiSettings.WebAccessForNewChats"/> aunque cambie en Ajustes.</summary>
    private bool? _draftWebAccess;

    private bool DraftWebAccess => _draftWebAccess ?? _settingsService.Settings.Ai.WebAccessForNewChats;

    /// <summary>Solo con herramientas: en modo texto el modelo no puede llamar a nada, tampoco a la web.</summary>
    public bool CanToggleWebAccess => VaultToolsAvailable && !IsUnassignedChat;

    public bool WebAccessEnabled => CanToggleWebAccess && (_activeSession?.WebAccess ?? DraftWebAccess);

    public string WebAccessTooltip => LocalizationService.Instance[WebAccessEnabled
        ? "Assistant_Web_Toggle_OnTooltip"
        : "Assistant_Web_Toggle_OffTooltip"];

    [RelayCommand]
    private void ToggleWebAccess()
    {
        if (!CanToggleWebAccess) return;
        var enabled = !WebAccessEnabled;
        if (_activeSession is { } session)
        {
            session.WebAccess = enabled;
            TrySaveSession(session, touch: false);
        }
        else
        {
            _draftWebAccess = enabled;
        }
        RefreshWebAccessState();
        OnPropertyChanged(nameof(ScopeSummary));
    }

    /// <summary>Lo que el modelo puede abrir sin preguntar al empezar el turno: las direcciones que el usuario
    /// escribió en esta conversación y las páginas que ya se leyeron en ella. Durante el turno se suman los
    /// resultados de búsqueda y los enlaces de cada página leída.</summary>
    private static AiWebUrlSet KnownWebUrls(AiChatSession session)
    {
        var urls = new AiWebUrlSet();
        foreach (var message in session.Messages)
        {
            if (message.Role == AiChatRole.User) urls.AddFrom(message.Text);
            foreach (var activity in message.Activities)
                if (activity is { TargetKind: AiToolTargetKind.WebPage, State: AiToolActivityState.Succeeded, TargetId: { } url })
                    urls.Add(url);
        }
        return urls;
    }

    private void RefreshWebAccessState()
    {
        OnPropertyChanged(nameof(CanToggleWebAccess));
        OnPropertyChanged(nameof(WebAccessEnabled));
        OnPropertyChanged(nameof(WebAccessTooltip));
    }
}
