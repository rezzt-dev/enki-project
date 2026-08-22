namespace enki.Services;

/// <summary>Stable ids for every remappable shortcut (propuesta 29, docs/propuesta-nuevas-funcionalidades.md)
/// — constants instead of inline string literals so the id used to look up a gesture in
/// <see cref="ShortcutRegistry"/> (e.g. in <c>EditorView.axaml.cs</c>'s <c>OnEditorKeyDown</c>) can't drift
/// from the id declared in <see cref="ShortcutCatalog"/> without a compile error.</summary>
public static class ShortcutIds
{
    public const string GlobalSearch = "shell.globalSearch";
    public const string QuickSwitcher = "shell.quickSwitcher";
    public const string CommandPalette = "shell.commandPalette";
    public const string QuickNewNote = "shell.quickNewNote";
    public const string NavigateDashboard = "shell.navigateDashboard";
    public const string NavigateNotes = "shell.navigateNotes";
    public const string NavigateKanban = "shell.navigateKanban";
    public const string NavigateCalendar = "shell.navigateCalendar";
    public const string NavigateFiles = "shell.navigateFiles";
    public const string NavigateSessions = "shell.navigateSessions";
    public const string NavigateTrash = "shell.navigateTrash";
    public const string NavigateSkills = "shell.navigateSkills";
    public const string NavigateStatistics = "shell.navigateStatistics";
    public const string OpenSettings = "shell.openSettings";
    public const string ZoomIn = "shell.zoomIn";
    public const string ZoomOut = "shell.zoomOut";
    public const string ZoomReset = "shell.zoomReset";

    public const string NotesCloseTab = "notes.closeTab";
    public const string NotesNextTab = "notes.nextTab";
    public const string NotesPreviousTab = "notes.previousTab";
    public const string NotesToggleZenMode = "notes.toggleZenMode";

    public const string EditorBold = "editor.bold";
    public const string EditorItalic = "editor.italic";
    public const string EditorInsertLink = "editor.insertLink";
    public const string EditorFind = "editor.find";
    public const string EditorFindReplace = "editor.findReplace";

    public const string RemindersNew = "reminders.new";

    public const string TrashEmpty = "trash.emptyTrash";
}
