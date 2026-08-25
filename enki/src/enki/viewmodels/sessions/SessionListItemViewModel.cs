using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class SessionListItemViewModel : ObservableObject
{
    public Session Session { get; private set; }

    public string Name => Session.Name;
    public int NoteCount => Session.NoteCount;
    public string NoteCountLabel => Plural.Get("Sessions_NoteCount", NoteCount, NoteCount);
    public bool IsDefault => Session.IsDefault;
    public bool IsEncrypted => Session.IsEncrypted;

    /// <summary>A small swatch in the session's color — shown as a colored dot next to the session name
    /// so the user can visually map the sidebar list to the colored node clusters and card accent bars
    /// in the graph. Uses the same <c>Views.Sessions.NodeBrightness.ColorFor</c> logic that the renderer
    /// uses, so the dot always matches the graph exactly.</summary>
    public ISolidColorBrush SessionColorBrush
    {
        get
        {
            var rgba = global::enki.Views.Sessions.SessionGraphRenderer.NodeBrightness.ColorFor(Session.Id, Session.Color);
            var parts = rgba.Replace("rgba(", "").Replace(")", "").Split(',');
            var r = byte.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var g = byte.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var b = byte.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    /// <summary>Runtime-only: whether this session's DEK is currently unlocked. Meaningless (always
    /// treated as true) for a non-encrypted session — set by <c>SessionsViewModel.Reload</c> against
    /// <c>SessionService.IsUnlocked</c> on every refresh, not persisted here.</summary>
    [ObservableProperty]
    private bool _isUnlocked;

    /// <summary>Drives the padlock icon shown in the session row — split into two mutually-exclusive
    /// bools (rather than one XAML multi-binding) so the closed/open glyph and their "unlock"/"lock"
    /// click targets can each bind to a plain bool.</summary>
    public bool ShowLockedIndicator => IsEncrypted && !IsUnlocked;
    public bool ShowUnlockedIndicator => IsEncrypted && IsUnlocked;

    partial void OnIsUnlockedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLockedIndicator));
        OnPropertyChanged(nameof(ShowUnlockedIndicator));
    }

    public SessionListItemViewModel(Session session)
    {
        Session = session;
    }

    public void UpdateSession(Session session)
    {
        Session = session;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(NoteCount));
        OnPropertyChanged(nameof(NoteCountLabel));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsEncrypted));
        OnPropertyChanged(nameof(ShowLockedIndicator));
        OnPropertyChanged(nameof(ShowUnlockedIndicator));
    }
}
