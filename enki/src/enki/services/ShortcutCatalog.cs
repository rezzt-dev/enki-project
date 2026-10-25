using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>Single source of truth for every remappable shortcut (propuesta 29,
/// docs/propuesta-nuevas-funcionalidades.md) — both the "Atajos" settings page and the code that actually
/// wires up <c>KeyBinding</c>s/key handlers in <c>MainWindow</c>, <c>NotesView</c>, <c>RemindersView</c>,
/// <c>TrashView</c> and <c>EditorView</c> read from this same list via <see cref="ShortcutRegistry"/>,
/// instead of each view hardcoding its own gesture literal like before. Deliberately scoped to shortcuts
/// declared as a modifier chord (Ctrl/Alt/Meta + key) — the handful of single-key, context-sensitive
/// shortcuts (Kanban's <c>N</c>/<c>Delete</c>/<c>Esc</c>, the PDF viewer's arrow-key page navigation) stay
/// fixed and are shown read-only in Ajustes › Atajos, since remapping a bare letter key risks colliding
/// with normal typing the moment focus lands somewhere unexpected.</summary>
public static class ShortcutCatalog
{
    public static readonly IReadOnlyList<ShortcutDefinition> All =
    [
        // ===================== Navegación (shell, MainWindow.axaml) =====================
        new(ShortcutIds.GlobalSearch, "Ctrl+Shift+F", "Settings_Navigation_Title", "Settings_Navigation_GlobalSearch"),
        new(ShortcutIds.QuickSwitcher, "Ctrl+P", "Settings_Navigation_Title", "Settings_Shortcuts_QuickSwitcher"),
        new(ShortcutIds.CommandPalette, "Ctrl+Shift+K", "Settings_Navigation_Title", "Settings_Shortcuts_CommandPalette"),
        new(ShortcutIds.QuickNewNote, "Ctrl+Shift+N", "Settings_Navigation_Title", "Settings_Navigation_QuickNote"),
        new(ShortcutIds.QuickCapture, "Ctrl+Alt+Space", "Settings_Navigation_Title", "Settings_Shortcuts_QuickCapture"),
        new(ShortcutIds.NavigateDashboard, "Ctrl+D1", "Settings_Navigation_Title", "Settings_Navigation_ToDashboard"),
        new(ShortcutIds.NavigateNotes, "Ctrl+D2", "Settings_Navigation_Title", "Settings_Navigation_ToNotes"),
        new(ShortcutIds.NavigateKanban, "Ctrl+D3", "Settings_Navigation_Title", "Settings_Navigation_ToKanban"),
        new(ShortcutIds.NavigateCalendar, "Ctrl+D4", "Settings_Navigation_Title", "Settings_Navigation_ToCalendar"),
        new(ShortcutIds.NavigateFiles, "Ctrl+D5", "Settings_Navigation_Title", "Settings_Navigation_ToFiles"),
        new(ShortcutIds.NavigateSessions, "Ctrl+D6", "Settings_Navigation_Title", "Settings_Navigation_ToSessions"),
        new(ShortcutIds.NavigateTrash, "Ctrl+D7", "Settings_Navigation_Title", "Settings_Navigation_ToTrash"),
        new(ShortcutIds.NavigateSkills, "Ctrl+D8", "Settings_Navigation_Title", "Settings_Navigation_ToSkills"),
        new(ShortcutIds.NavigateAssistant, "Ctrl+D9", "Settings_Navigation_Title", "Settings_Navigation_ToAssistant"),
        new(ShortcutIds.ToggleAssistantPanel, "Ctrl+Shift+D9", "Settings_Navigation_Title", "Settings_Navigation_ToggleAssistantPanel"),
        new(ShortcutIds.NavigateStatistics, "Ctrl+Shift+T", "Settings_Navigation_Title", "Settings_Navigation_ToStatistics"),
        new(ShortcutIds.OpenSettings, "Ctrl+OemComma", "Settings_Navigation_Title", "Settings_Navigation_OpenSettings"),

        // ===================== Zoom de vista (shell, MainWindow.axaml) =====================
        // Ctrl+Add/Ctrl+Subtract/Ctrl+NumPad0 (variantes de teclado numérico) y Ctrl+rueda del ratón se
        // mantienen como alias fijos junto al gesto remapeable — ver MainWindow.axaml.cs.
        new(ShortcutIds.ZoomIn, "Ctrl+OemPlus", "Settings_ViewZoom_Title", "Settings_Shortcuts_ZoomIn"),
        new(ShortcutIds.ZoomOut, "Ctrl+OemMinus", "Settings_ViewZoom_Title", "Settings_Shortcuts_ZoomOut"),
        new(ShortcutIds.ZoomReset, "Ctrl+D0", "Settings_ViewZoom_Title", "Settings_ViewZoom_Reset"),

        // ===================== Documentos y editor (NotesView.axaml, EditorView.axaml.cs) =====================
        new(ShortcutIds.NotesCloseTab, "Ctrl+W", "Settings_EditDocs_Title", "Settings_EditDocs_CloseTab"),
        new(ShortcutIds.NotesNextTab, "Ctrl+Tab", "Settings_EditDocs_Title", "Settings_Shortcuts_NextTab"),
        new(ShortcutIds.NotesPreviousTab, "Ctrl+Shift+Tab", "Settings_EditDocs_Title", "Settings_Shortcuts_PreviousTab"),
        new(ShortcutIds.NotesToggleZenMode, "Ctrl+Shift+Enter", "Settings_EditDocs_Title", "Settings_Shortcuts_ToggleZenMode"),
        // Nota diaria (propuesta 8): solo hacen algo con una nota diaria abierta, así que comparten
        // categoría con el resto de atajos del editor en vez de estrenar una propia.
        new(ShortcutIds.NotesDailyPreviousDay, "Ctrl+Alt+Left", "Settings_EditDocs_Title", "Settings_Shortcuts_DailyPreviousDay"),
        new(ShortcutIds.NotesDailyNextDay, "Ctrl+Alt+Right", "Settings_EditDocs_Title", "Settings_Shortcuts_DailyNextDay"),
        new(ShortcutIds.EditorBold, "Ctrl+B", "Settings_EditDocs_Title", "Settings_EditDocs_Bold"),
        new(ShortcutIds.EditorItalic, "Ctrl+I", "Settings_EditDocs_Title", "Settings_EditDocs_Italic"),
        new(ShortcutIds.EditorInsertLink, "Ctrl+K", "Settings_EditDocs_Title", "Settings_EditDocs_InsertLink"),
        new(ShortcutIds.EditorFind, "Ctrl+F", "Settings_EditDocs_Title", "Settings_EditDocs_SearchInNote"),
        new(ShortcutIds.EditorFindReplace, "Ctrl+H", "Settings_EditDocs_Title", "Settings_Shortcuts_FindReplace"),
        new(ShortcutIds.EditorStrikethrough, "Ctrl+Shift+X", "Settings_EditDocs_Title", "Settings_Shortcuts_Strikethrough"),
        new(ShortcutIds.EditorInlineCode, "Ctrl+Shift+C", "Settings_EditDocs_Title", "Settings_Shortcuts_InlineCode"),
        new(ShortcutIds.EditorHighlight, "Ctrl+Shift+M", "Settings_EditDocs_Title", "Settings_Shortcuts_Highlight"),
        new(ShortcutIds.EditorToggleQuote, "Ctrl+Shift+Q", "Settings_EditDocs_Title", "Settings_Shortcuts_ToggleQuote"),
        new(ShortcutIds.EditorToggleList, "Ctrl+Shift+L", "Settings_EditDocs_Title", "Settings_Shortcuts_ToggleList"),
        new(ShortcutIds.EditorToggleTask, "Ctrl+Enter", "Settings_EditDocs_Title", "Settings_Shortcuts_ToggleTask"),
        // Operaciones de línea, con los gestos que ya traen de serie los editores de código.
        new(ShortcutIds.EditorMoveLineUp, "Alt+Up", "Settings_EditDocs_Title", "Settings_Shortcuts_MoveLineUp"),
        new(ShortcutIds.EditorMoveLineDown, "Alt+Down", "Settings_EditDocs_Title", "Settings_Shortcuts_MoveLineDown"),
        new(ShortcutIds.EditorDuplicateLines, "Ctrl+Shift+D", "Settings_EditDocs_Title", "Settings_Shortcuts_DuplicateLines"),
        new(ShortcutIds.EditorDeleteLines, "Ctrl+Shift+Y", "Settings_EditDocs_Title", "Settings_Shortcuts_DeleteLines"),
        new(ShortcutIds.EditorSelectLine, "Ctrl+L", "Settings_EditDocs_Title", "Settings_Shortcuts_SelectLine"),

        // ===================== Recordatorios (RemindersView.axaml) =====================
        new(ShortcutIds.RemindersNew, "Ctrl+N", "Settings_Shortcuts_Category_Reminders", "Settings_Shortcuts_RemindersNew"),

        // ===================== Papelera (TrashView.axaml) =====================
        new(ShortcutIds.TrashEmpty, "Ctrl+Shift+E", "Settings_Shortcuts_Category_Trash", "Settings_Shortcuts_EmptyTrash")
    ];
}
