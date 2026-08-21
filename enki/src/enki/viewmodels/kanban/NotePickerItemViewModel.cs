using enki.Models;

namespace enki.ViewModels;

/// <summary>One row in the "vincular nota" picker inside <see cref="KanbanCardEditorViewModel"/> — wraps a
/// <see cref="NoteFile"/> plus which workspace it lives in (the vault root or a named session), since a
/// bare title alone doesn't tell notes with the same name in different sessions apart.</summary>
public sealed class NotePickerItemViewModel(NoteFile note, string workspaceLabel)
{
    public NoteFile Note { get; } = note;
    public string Title => Note.Title;
    public string WorkspaceLabel { get; } = workspaceLabel;
}
