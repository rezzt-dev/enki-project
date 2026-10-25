using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Fila del historial de chats: título, fecha y, mientras dura, el estado de su respuesta.</summary>
public sealed partial class AiChatSessionItemViewModel(AiChatSession session) : ObservableObject
{
    public AiChatSession Session { get; } = session;
    public string Id => Session.Id;

    public string Title => string.IsNullOrWhiteSpace(Session.Title)
        ? LocalizationService.Instance["Assistant_Chat_Untitled"]
        : Session.Title;

    public string Subtitle
    {
        get
        {
            var when = RelativeTimeFormatter.Format(Session.UpdatedUtc);
            return string.IsNullOrWhiteSpace(Session.SkillName) ? when : $"{when} · {Session.SkillName}";
        }
    }

    /// <summary>Conversación de antes de separar el historial por vault: se puede leer, y para continuarla hay
    /// que asociarla al vault abierto.</summary>
    public bool IsUnassigned => Session.VaultId is null;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Esta conversación está generando una respuesta ahora mismo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(HasStatus))]
    private bool _isRunning;

    /// <summary>La respuesta en curso espera una decisión del usuario (un permiso o una confirmación).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(HasStatus))]
    private bool _needsDecision;

    public bool HasStatus => IsRunning || NeedsDecision;

    public string StatusLabel => NeedsDecision
        ? LocalizationService.Instance["Assistant_Activity_NeedsDecision"]
        : IsRunning ? LocalizationService.Instance["Assistant_Activity_Working"] : string.Empty;

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(StatusLabel));
    }
}
