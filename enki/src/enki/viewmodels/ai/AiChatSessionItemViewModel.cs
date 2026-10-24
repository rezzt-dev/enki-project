using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Fila del historial de chats.</summary>
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

    [ObservableProperty]
    private bool _isActive;

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}
