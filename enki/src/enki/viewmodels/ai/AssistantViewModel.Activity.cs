using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Estado del asistente visto desde fuera del chat: el indicador global de la barra de título, que
/// sigue a la vista mientras se navega por Enki, y el estado de cada fila del historial.</summary>
public enum AssistantActivityState
{
    Idle,
    /// <summary>Generando una respuesta o actuando en el vault.</summary>
    Working,
    /// <summary>La respuesta está parada esperando un permiso o una confirmación del usuario.</summary>
    NeedsDecision,
    /// <summary>Terminó una respuesta que el usuario todavía no ha visto.</summary>
    ResponseReady
}

/// <summary>Seguimiento de la respuesta en curso para el indicador global. No hay un aviso por fragmento ni por
/// herramienta: el indicador cambia de estado y solo una decisión pendiente, con el chat fuera de la vista, se
/// anuncia además con <c>NotificationService</c>.</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Conversación cuya respuesta está en curso (solo hay una a la vez).</summary>
    private AiChatSession? _runningSession;

    /// <summary>Terminó una respuesta con el asistente fuera de la vista y aún no se ha mirado.</summary>
    private bool _responseReady;

    /// <summary>Tarjetas de permiso o confirmación esperando al usuario en la respuesta en curso.</summary>
    private int _pendingDecisions;

    public AssistantActivityState ActivityState =>
        _pendingDecisions > 0 ? AssistantActivityState.NeedsDecision
        : IsSending ? AssistantActivityState.Working
        : _responseReady ? AssistantActivityState.ResponseReady
        : AssistantActivityState.Idle;

    public bool HasActivity => ActivityState != AssistantActivityState.Idle;
    public bool IsActivityWorking => ActivityState == AssistantActivityState.Working;
    public bool IsActivityNeedsDecision => ActivityState == AssistantActivityState.NeedsDecision;
    public bool IsActivityResponseReady => ActivityState == AssistantActivityState.ResponseReady;

    /// <summary>"Trabajando", "Necesita tu decisión" o "Respuesta lista", con el chat al que se refiere.</summary>
    public string ActivityLabel
    {
        get
        {
            var key = ActivityState switch
            {
                AssistantActivityState.Working => "Assistant_Activity_Working",
                AssistantActivityState.NeedsDecision => "Assistant_Activity_NeedsDecision",
                AssistantActivityState.ResponseReady => "Assistant_Activity_ResponseReady",
                _ => "Assistant_Activity_Idle"
            };
            return LocalizationService.Instance[key];
        }
    }

    /// <summary>Texto del botón del asistente en la barra de título: el estado y qué hacer con él.</summary>
    public string ActivityTooltip => HasActivity
        ? $"{ActivityLabel} · {ChatTitle}"
        : LocalizationService.Instance["Assistant_Panel_Toggle"];

    private void RefreshActivityState()
    {
        OnPropertyChanged(nameof(ActivityState));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsActivityWorking));
        OnPropertyChanged(nameof(IsActivityNeedsDecision));
        OnPropertyChanged(nameof(IsActivityResponseReady));
        OnPropertyChanged(nameof(ActivityLabel));
        OnPropertyChanged(nameof(ActivityTooltip));
        RefreshSessionActivity();
    }

    private void RefreshSessionActivity()
    {
        foreach (var item in Sessions)
        {
            item.IsRunning = item.Session == _runningSession;
            item.NeedsDecision = item.IsRunning && _pendingDecisions > 0;
        }
    }

    /// <summary>El usuario está viendo el chat (la sección o el panel acaban de enseñarse): una respuesta lista
    /// deja de estar pendiente de ver.</summary>
    public void MarkConversationSeen()
    {
        if (!_responseReady) return;
        _responseReady = false;
        RefreshActivityState();
    }

    // ===================== Panel lateral =====================

    /// <summary>Historial desplegado sobre la conversación en el panel lateral, donde no cabe al lado.</summary>
    [ObservableProperty]
    private bool _isPanelHistoryOpen;

    [RelayCommand]
    private void TogglePanelHistory() => IsPanelHistoryOpen = !IsPanelHistoryOpen;

    /// <summary>Desplazamiento del hilo de la superficie que se acaba de ocultar, para que la otra lo retome al
    /// cambiar entre sección y panel. <see langword="null"/> = pegado al final.</summary>
    public double? TranscriptScrollOffset { get; set; }

    private void RefreshActivityLocalizedLabels()
    {
        OnPropertyChanged(nameof(ActivityLabel));
        OnPropertyChanged(nameof(ActivityTooltip));
    }
}
