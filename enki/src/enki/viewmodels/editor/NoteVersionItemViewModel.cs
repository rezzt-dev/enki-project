using enki.Models;

namespace enki.ViewModels;

/// <summary>Thin wrapper exposing a <see cref="NoteVersion"/> to the history panel's list and its
/// commands — plain properties are enough since the model itself already carries every localized label
/// (<see cref="NoteVersion.CreatedLabel"/>, <see cref="NoteVersion.TriggerLabel"/>,
/// <see cref="NoteVersion.SizeLabel"/>) and this list has no per-item state of its own to add.</summary>
public sealed class NoteVersionItemViewModel(NoteVersion version)
{
    public NoteVersion Version { get; } = version;

    public string Id => Version.Id;
    public string CreatedLabel => Version.CreatedLabel;
    public string TriggerLabel => Version.TriggerLabel;
    public string SizeLabel => Version.SizeLabel;
}
