using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;

namespace enki.ViewModels;

/// <summary>One row in a "pick a note from the vault" picker — wraps a <see cref="NoteFile"/> plus which
/// workspace it lives in (the vault root or a named session), since a bare title alone doesn't tell notes
/// with the same name in different sessions apart. Shared by <see cref="KanbanCardEditorViewModel"/>'s
/// "vincular nota" picker and <see cref="NoteLinkPickerViewModel"/>'s wikilink picker in the editor.</summary>
public sealed partial class NotePickerItemViewModel(NoteFile note, string workspaceLabel) : ObservableObject
{
    public NoteFile Note { get; } = note;
    public string Title => Note.Title;
    public string WorkspaceLabel { get; } = workspaceLabel;

    /// <summary>Drives the highlighted-row style during arrow-key navigation — only
    /// <see cref="NoteLinkPickerViewModel"/> uses this; the Kanban picker only ever selects via a mouse
    /// click, so it stays false there.</summary>
    [ObservableProperty]
    private bool _isSelected;
}
