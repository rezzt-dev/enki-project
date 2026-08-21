using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>One entry in the session selector shown on the Notes page — <see cref="Session"/> null
/// represents the main vault itself, always the first entry.</summary>
public sealed class SessionOptionViewModel(Session? session)
{
    public Session? Session { get; } = session;
    public string DisplayName => Session?.Name ?? LocalizationService.Instance["Sessions_VaultOptionLabel"];
    public bool IsVault => Session is null;
}
