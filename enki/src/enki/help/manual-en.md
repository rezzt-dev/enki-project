# Enki manual

User manual consulted by the built-in assistant. Each `##` section is a self-contained topic. Keyboard shortcuts are not
listed here: they are added on every search with the gesture the user has configured.

## Getting started, vault and language

Enki keeps everything in a vault: a folder on your computer that you choose on first launch, together with the language.
Notes are plain Markdown files inside that folder, so you can open, copy or sync them with other tools.

- Switch vault: use the recent vaults switcher in the main window, or Settings › Vault and files. Each vault remembers
  its own workspace.
- Change language: Settings › General. It applies immediately, without restarting.
- Open the vault folder in the system file manager: command palette › the command that opens the vault folder.
- Enki works offline. The network is only used by features you turn on: Polar, saving a web page, loading a remote
  image, checking for a new version or the AI assistant.

## Navigation: sidebar, global search, quick switcher and command palette

- The sidebar leads to every section: Dashboard, Calendar, Kanban, Notes, Files, Sessions, Trash, Habits, Skills,
  Assistant, Tags, Reminders, Notifications and Statistics. Settings sits at the bottom.
- Show, hide or reorder sections: Settings › Navigation. A section that is turned off disappears from the sidebar, the
  palette and the shortcuts, but its data is kept. Up to three shortcuts can be pinned next to Settings.
- Global search: finds notes, Kanban cards, Polar tasks and reminders from a single box.
- Quick switcher: jumps to a note by title, content or tags.
- Command palette: runs actions without walking through menus, such as creating a note, creating a note from a
  template, opening today's daily note, importing notes, toggling the light/dark theme, toggling focus mode or going to
  any section.
- Interface zoom: between 50% and 200%, with its shortcuts, with Ctrl and the mouse wheel or from the palette.

## Notes: creating, organising and tabs

- Create a note: the new note button in the Notes section, the new note command in the palette or its shortcut.
- Notes are organised in folders and subfolders; move them by drag and drop. Existing documents can be imported too.
- Several notes can be open in tabs; they are restored when Enki starts again.
- There is a full view to explore the vault and a simplified view centred on the document tree and the editor.
- Renaming a note updates the `[[wikilinks]]` that point to it and the Kanban cards linked to it.
- Deleting a note sends it to the Trash, where it can be restored for 30 days.

## Markdown editor

- The editor saves by itself (autosave) and supports undo and redo, find and replace, line numbers, line wrapping,
  reading width and alignment, configurable in Settings › Notes and editor.
- Formatting from the format bar or with shortcuts: headings, bold, italic, strikethrough, highlight, quotes, lists,
  tasks, links, images, code and separators.
- Type `/` in the editor to insert a command without remembering the Markdown syntax.
- Lists continue and renumber themselves; items can be nested and lines moved or duplicated.
- Pasting a web address over selected text turns it into a link.
- Next to the editor you can check the heading outline, backlinks, reading statistics, tags, history and linked tasks.
- Focus (zen) mode: hides the navigation and leaves only the content.

## Wikilinks, images, tables, formulas and embedded content

- `[[Note title]]` links to another note. Broken links are flagged, and links are kept when a note is renamed.
- `![[note]]` embeds a whole note, a section or a block, with a link to the original.
- Images: from a file, the clipboard, drag and drop, or with Markdown and Obsidian syntax.
- Tables: created and edited with the editor's tools, without typing every separator by hand.
- Callouts (note, tip, warning, danger) with Obsidian-compatible syntax.
- Code blocks are highlighted by language and copied with one action.
- LaTeX formulas and Mermaid diagrams are rendered locally, without sending the content anywhere.

## Note templates

- A template is a Markdown file in `.enki/templates`, inside the vault. Its YAML header may declare a name (`name`), a
  description (`description`) and a title pattern (`title`).
- Create a note from a template: in Notes, the from-template button, or the palette command that creates a note from a
  template.
- Create or edit templates: from the template picker itself.
- Available variables: title, date, time, day, month, year and identifier, with optional format and offsets (for
  example `{{date}}` or `{{title}}`).

## Daily note

- Open today's note: the daily note button in the Notes section or the palette command that opens today's daily note. If
  it does not exist, it is created.
- You can choose your own template for the daily note in Settings › Notes and editor; that template also decides the
  note's name.
- From the header of a daily note you can go to the previous day, the next day or back to today.
- If the daily note already exists, the merge-with-template button in its header adds only the sections it is missing,
  without touching what you wrote.
- The daily note consistency view shows ten weeks of daily notes and habits, without turning it into a score.

## Version history and external changes

- Every note has a local history: compare versions line by line and restore an earlier one. Restoring saves the current
  version first, so there is always a way back.
- Enki creates restore points while you work and before delicate operations.
- If another application (Syncthing, Dropbox, another editor) changes an open note, Enki notices: it reloads the note if
  you had no changes, or lets you choose between keeping your version, loading the one on disk or comparing them.

## Sessions and graph

- A session is a separate note space for a project, a course or a trip, with its own note tree.
- A session can be protected with a password and recovery codes: the content of its notes and attachments stays
  encrypted while it is locked.
- The graph shows the relationships between notes built from wikilinks and manual links; filter it by session, tag or
  connected group, and use it to find orphan notes.

## Files

- The Files section keeps images, PDFs, audio, video, code and other attachments inside the vault or a session.
- Browse them as an explorer with folders and cards or as a tree next to the preview.
- Search inside subfolders, filter by type and sort by name, date, size or format.
- Images, SVG, PDF and code or text files are previewed inside Enki; the rest opens with the system application.

## Kanban

- A board has columns (to do, in progress, done and any you add) and cards with a description, priority, due date,
  duration, tags, subtasks, web links and a linked note.
- Cards are moved and reordered by dragging; taking one to the done column completes it.
- The board can be searched, filtered and sorted by priority, date or creation.
- Completed cards can be archived: they leave the board but stay in the calendar, the statistics and the search. There
  is a dedicated view for archived cards.
- With multiple boards enabled, the board selector lets you create, rename and switch boards.
- The board can be exported to PNG or PDF from the palette.

## Polar: synced tasks

- Polar is optional. Link the account in Settings › Polar account to share lists, tasks and subtasks with the mobile
  app.
- You can keep working offline; changes sync when the network is back. The palette has a command to sync Polar now.
- Polar tasks are shown inside Kanban and the Calendar.

## Calendar

- It gathers Kanban cards, Polar tasks, reminders and subscribed external calendars in month, week, day and agenda
  views.
- Each source can be shown or hidden, and you can filter by priority or tags.
- Create reminders or cards from an empty slot, and drag an item to change its date.
- First day of the week, weekends, working hours and time format: Settings › Calendar.
- The current view can be exported to PNG or PDF.

## Reminders and notifications

- A reminder has a title, description, date, time and icon, and can repeat.
- The reminders inbox gathers what is overdue, what is due today and what is coming up.
- A reminder can be snoozed for a few minutes, until later, until tomorrow or until a specific moment without changing
  its original date.
- Desktop notifications, sound and do-not-disturb hours: Settings › Notifications.
- The notification centre keeps the notices and lets you filter them and mark them as read.

## Habits and statistics

- A habit can be daily, weekly or for specific days of the week. Mark it as done with one action in the Habits section.
- The consistency grid shows ten weeks, with no points or rewards.
- Statistics summarises by week, month or the whole history: notes created and edited, words, active days, completed
  tasks, habits and vault usage.

## Tags

- A note's tags live in its YAML header; cards and tasks have their own.
- The Tags section gathers the tags of notes, Kanban cards and Polar tasks: search, rename, merge or delete them without
  editing each item by hand.

## Trash

- Deleted notes, files, folders, sessions, cards, tasks, lists and reminders go to a shared trash.
- From the Trash you can restore any item or delete it permanently. What is permanently deleted cannot be recovered.
- Items are kept for 30 days.

## Quick capture and saving web pages

- Quick capture saves an idea as a note in `Inbox/` or as a Kanban card from a small window. Open it with its shortcut,
  from the palette or from the system tray.
- If you close it without sending, the draft is kept.
- Pasting a web address creates a clean note with the title, the original link and the readable content of the page.

## Export, import and backups

- A note can be exported to HTML, PNG or PDF (there are palette commands for it).
- The Kanban board and the calendar can be exported to PNG or PDF.
- Back up the vault to a ZIP file, and export or import settings: Settings › Backups.
- To sync the vault between computers with Syncthing, iCloud Drive or Google Drive, see the guide in Settings › Vault
  and files.

## Dashboard

- The Dashboard summarises the day: recent notes, task status, habits, activity, storage and quick links.
- Which blocks appear, in what order and how wide is configured in Settings › Dashboard.

## Appearance and personalisation

- Light theme, dark theme or your own colour themes: Settings › Appearance. The palette can toggle light/dark.
- Separate fonts for the interface, notes and code, and a custom sidebar logo: Settings › Appearance.
- Start page and other general options: Settings › General.

## Keyboard shortcuts: how to change them

- Every shortcut with a modifier can be changed in Settings › Keyboard shortcuts, and reset one by one or all at once.
- A few single context keys (inside Kanban, for example) are fixed and listed there only for reference.
- To know which shortcut an action has right now, see the "Keyboard shortcuts" sections of this manual: they reflect the
  user's current configuration.

## AI assistant

- Turn it on in Settings › Artificial intelligence, where you choose the provider: Claude, OpenAI (ChatGPT account
  through its CLI), Gemini (API key) or a local model with Ollama.
- Use it as its own section (Assistant) or as a side panel next to any other section; the title bar button and its
  shortcut open and close it.
- Each chat has a scope: the vault or a session. The scope selector in the header widens it, and the read permission
  decides whether the assistant may search and read on its own.
- The globe button in the message box turns on internet access for that chat.
- Notes and files can be attached to a message. Before sending, Enki shows what will be sent.
- What the assistant does in the vault is listed as actions in its reply. "Revert this reply" undoes what it changed,
  without touching what you edited afterwards.
- A reply can be copied, appended to the active note or saved as a new note.
- When the provider reports them, the tokens of each reply and the chat total are shown; hide them in Settings ›
  Artificial intelligence.
- Turning off "Allow changes to the vault" (Settings › Artificial intelligence) leaves the assistant read-only.
- "Review changes before applying them" (Settings › Artificial intelligence) keeps what the assistant changes in your
  notes under its reply, with the differences, so you apply what you tick or discard it all.
- The export button in the chat header saves the whole conversation as a new note.
- When you attach an image or a PDF, the message box says which provider will receive it and whether the model can see it.
- Ask it for a summary of your day or week, how you are doing this week, to rename or merge tags or to suggest links
  for a note; also from the command palette ("Assistant: …").
- "Local semantic search" (Settings › Artificial intelligence) builds an index with Ollama, on this computer, so the
  assistant finds notes by meaning. Rebuild or delete it from the same place.

## Skills

- A skill is a Markdown file with reusable instructions. The Skills section organises them in folders and lets you
  create, import, edit, duplicate and export them.
- "Use in assistant" starts a chat with that skill as its starting instructions.
- The section is hidden by default; turn it on in Settings › Navigation.

## Privacy and data

- Your content lives in the vault you choose; settings, in the app's local profile. Enki has no telemetry and does not
  send your notes to a service of its own.
- Protected sessions encrypt the content of their notes and attachments; file names and structure remain visible on
  disk.
- With the assistant, what you write and what you attach or let it look up is sent to the AI provider you chose. With
  Ollama everything stays on your computer.
