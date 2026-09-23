<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".docs/resources/project-icons/dark-icon/enki-icon-light.png">
    <source media="(prefers-color-scheme: light)" srcset=".docs/resources/project-icons/light-icon/enki-icon-dark.png">
    <img alt="enki" src=".docs/resources/project-icons/dark-icon/enki-icon-light.png" width="96">
  </picture>

  <h1>ENKI</h1>
  <p><em>your second brain, on your desktop.</em></p>

  <p>
    <a href="README.md">leer en castellano</a>
  </p>
</div>

---

enki is a desktop application designed for one person: you. it brings notes, files, tasks, calendars, reminders, habits and workspaces together in one place, without forcing you to depend on an account or a permanent connection.

it grew out of a simple frustration: having to open several applications just to understand what matters today. enki keeps each thing in its proper place while letting you connect them whenever that is useful. a note can be linked to a task, a date can appear in the calendar and one tag can organise content from several modules without duplicating it.

> the goal is not for you to use enki all day. it is for you to know exactly where to find what you need whenever you open it.

---

## WHAT MAKES ENKI DIFFERENT

- **your data is yours.** notes are ordinary markdown files inside a folder you choose. you can open them in another editor, copy them, sync them or back them up with whichever tool you prefer.
- **local comes first.** notes, files, kanban, calendar, reminders, habits, statistics and sessions work without the internet. polar is an optional extension for anyone who wants to sync tasks.
- **connected without becoming cluttered.** each module can work on its own, but they all read the same notes, tasks, dates and tags when they need to work together.
- **your work is protected quietly.** autosave, version history, trash and external-change detection reduce the risk of losing content without interrupting your writing.
- **productivity is not a competition.** habits and statistics describe what you do without points, prizes or artificial pressure.
- **the workspace adapts to you.** you can change the layout, shortcuts, theme, fonts, zoom, dashboard and sidebar order.

---

## EVERYTHING YOU CAN DO

### CAPTURE WITHOUT BREAKING YOUR FLOW

- save an idea as a note in `Inbox/` or as a kanban card from a quick-capture window.
- open quick capture from the system tray, the command palette, an in-app shortcut or, where the platform supports it, a configurable global shortcut.
- close quick capture without worrying: the draft remains until you submit it successfully.
- paste a web address to create a clean note containing the title, original link and readable content from the page.
- switch between recent vaults without restarting the application and let every vault restore its own workspace.

### WRITE AND LOOK AFTER YOUR NOTES

- create markdown notes, folders and subfolders, import existing documents and move content with drag and drop.
- work with several notes in tabs that are restored automatically when enki starts again.
- use a full view to explore the vault or a simplified view focused on the document tree and editor.
- write with autosave, undo and redo, search and replace, line numbers, word wrap, reading width and configurable alignment.
- apply headings, bold, italics, strikethrough, quotes, lists, tasks, links, images, code and dividers from the formatting toolbar or with shortcuts.
- continue and renumber lists intelligently, nest items, move or duplicate lines and paste a url over selected text to turn it into a link.
- insert commands with `/` so you do not have to remember every piece of markdown syntax.
- consult the heading outline, backlinks, reading statistics, tags, history and linked tasks without leaving the editor.
- enable zen mode whenever you want to hide navigation and leave only the content.

### SEE MARKDOWN AS CONTENT, NOT NOISE

- see formatting directly while you write, with its syntax made available whenever you place the cursor over it.
- follow `[[wikilinks]]`, identify broken links and preserve references when a note is renamed.
- insert images from a file, the clipboard, drag and drop or markdown and obsidian link syntax.
- create tables without writing every separator by hand and edit rows and columns with the editor's own tools.
- display note, tip, warning and danger callouts with obsidian-compatible syntax.
- highlight code blocks according to their language and copy them with a single action.
- render latex formulae and mermaid diagrams locally, without relying on a cdn or sending content away from the computer.
- embed a complete note, section or block with `![[note]]` while retaining a direct link to the original content.
- export a note to html, png or pdf; long notes are divided into pages ready to save or share.

### REUSE STRUCTURES WITHOUT REPEATING WORK

- create notes from markdown templates stored inside the vault itself.
- use title, date, time, weekday, month, year and identifier variables, with optional formatting and offsets.
- choose a dedicated template for daily notes and let it define their filenames as well.
- move through previous and next days from a daily note's header, return to today or open any date from the consistency calendar.
- merge an existing daily note with its template to add only the sections that are still missing.
- review ten weeks of daily notes and habits in one restrained view, without turning consistency into a score.

### RECOVER WHAT CHANGED

- review each note's local history, compare an earlier version line by line and restore it whenever necessary.
- let enki create automatic restore points while you work and before delicate operations.
- keep editing the same vault with syncthing, dropbox or another editor: enki detects changes made outside the application.
- automatically reload a note with no local changes, or choose between keeping your version, loading the one on disk and comparing both when there is a conflict.
- preserve a copy of the discarded version before resolving a conflict so that no choice silently destroys content.
- recover notes, files, tasks, lists, sessions and reminders from shared trash before permanently removing them.

### EXPLORE KNOWLEDGE AND PROJECTS

- organise groups of notes into separate sessions so you can focus on a project, a course of study, a trip or any other specific context.
- protect a session with a password and recovery codes; the contents of its notes and attachments remain encrypted while the session is locked.
- explore relationships between notes in an interactive graph built from wikilinks and manual connections.
- view the entire vault, even when no sessions exist, and filter the graph by session, tag or connected group.
- discover orphan notes and connections across different sessions without changing or duplicating your files.

### STORE AND VIEW OTHER FILES

- import images, pdf documents, audio, video, code, text and other attachments into the vault or a session.
- browse files as an explorer with folders and cards or as a compact tree beside the preview.
- search within subfolders, filter by type and sort by name, date, size or format.
- preview images, svg files, pdf documents and code or text files without leaving enki.
- move through the pages of a pdf, adjust its zoom and browse thumbnails without loading the entire document at once.
- open unsupported formats with the system application, reveal their location, copy their path or import them directly into a folder.
- rename, move and delete files while keeping the open view up to date.

### PLAN LOCAL WORK WITH KANBAN

- organise cards into columns that you can create, reorder and adapt to your workflow.
- add a description, priority, due date, duration, tags, subtasks and a linked note.
- move and reorder cards with drag and drop, or let the board sort them by priority, date or creation time.
- search and filter the board without losing the context of its columns.
- archive completed tasks to clear the board without deleting them or removing them from the calendar, statistics or search.
- open archived tasks in their own view, filter them, sort them, edit them, restore them or move them to the trash.
- export the complete board to png or pdf even when all its columns do not fit on screen at once.

### SYNC TASKS WITH POLAR

- optionally link a polar account to share tasks with its android application through supabase.
- work with lists, tasks, subtasks, priorities, dates, estimates, recurrence, tags and dependency chains.
- continue working without a connection and sync changes when the network becomes available again.
- receive realtime updates and review sync status without turning the cloud into a requirement for using enki.
- keep session tokens in the operating system's secure storage and exclude them from settings exports and backups.

### SEE TIME IN ONE CALENDAR

- bring kanban cards, polar tasks and reminders together in month, week, day and agenda views.
- show or hide each source, filter by priority or tags and choose whether to display completed items or future occurrences.
- create reminders or cards directly from an empty calendar slot.
- drag a task to change its date and resize a kanban card from its calendar block to adjust its duration.
- configure the first day of the week, weekends, working hours and time format.
- export the current view to png or pdf with its days and items but without navigation controls.

### REMEMBER WITHOUT THE NOISE

- create independent reminders with a title, description, date, time and icon.
- review overdue items, what is planned for today and what is coming up from a shared inbox.
- receive in-app notices, desktop notifications and sound only when your preferences allow it.
- enable do not disturb hours: notices are still saved, but they do not interrupt you.
- snooze a reminder for a few minutes, until later, until tomorrow or until a custom time without changing its original date.
- review a persistent notification centre, filter what matters and mark everything as read when you are done.

### OBSERVE HABITS AND TRENDS

- create daily habits or habits for specific weekdays and mark them as done with one action.
- review a ten-week consistency grid without points, rewards or guilt-inducing messages.
- view statistics for a week, a month or all time to understand activity, rhythm, tasks, knowledge, habits and storage.
- see notes created and edited, words written, active days, completed tasks, priorities, subtask progress and vault usage.
- use the figures as descriptive context: enki does not turn them into compulsory targets.

### ORGANISE TAGS ACROSS ENKI

- see all active tags from notes, kanban cards and polar tasks in one view.
- search by name and check how often each tag appears in every module.
- rename, merge or remove tags without editing every item individually.
- preserve note history and the rest of each note's content while its tags are updated.

### FIND THINGS AND RUN ACTIONS QUICKLY

- open global search to find notes, kanban cards, polar tasks and reminders from one box.
- use the quick switcher to jump to a note by title, preview content or tags.
- open the command palette to navigate, create, import, sync, change the theme, control zoom or run actions without working through menus.
- customise shortcuts and restore any of them when a key combination no longer suits you.
- reorder sidebar sections and pin up to three entries beside settings.

### ADAPT THE SPACE TO THE WAY YOU WORK

- configure a dashboard with today's summary, recent notes, task status, habits, activity, storage and quick access.
- choose which sections appear, their order and how wide they are in the layout.
- switch between light, dark or custom colour themes edited inside the application.
- use a custom image as the sidebar logo and choose whether it appears square or circular.
- choose independent fonts for the interface, notes and code.
- adjust the overall zoom between `50 %` and `200 %`, expand or collapse the sidebar and choose which page opens at startup.
- change the interface language instantly between english (us), english (uk), deutsch, espanol (espana), espanol (latinoamerica) and francais.
- move through the main workflows with keyboard controls, accessible names and focus order prepared for assistive technologies.

### KEEP SKILLS AS REUSABLE KNOWLEDGE

- store skills in markdown with instructions, context, tools, tags and permissions described inside each file.
- create folders, import, edit, duplicate, move and export skills from a library with explorer or tree views.
- enable this section only if it is useful to you; it remains hidden by default.
- understand its current scope: enki organises skills as reusable content, but it does not yet include an ai engine that can execute them.

---

## HOW EVERYTHING CONNECTS

enki avoids creating parallel copies of the same information. the calendar displays dates that already live in kanban, polar and reminders; the dashboard summarises those same records; the tag manager changes the original tags; and the graph represents relationships that already exist between your notes.

an everyday workflow can begin with an idea written in quick capture, continue as a note inside a session, become a linked kanban card and end up scheduled on the calendar. once you complete it, the board, dashboard, search and statistics all see that change without asking you to maintain four versions of the same task.

---

## DATA, PRIVACY AND SECURITY

- primary content lives in the vault you choose, while supporting preferences are stored in the application's local profile.
- enki includes no telemetry and does not send your notes to its own service.
- the core works offline; the network is used only by explicit features such as polar, saving a web page, loading a remote image or checking for a new version.
- automatic update checks are disabled by default and, when enabled, only open the download page chosen by the user.
- remote images are downloaded with size, time and redirect limits, while private or local addresses are blocked.
- important writes are performed safely to avoid leaving incomplete files when an operation fails.
- protected sessions encrypt note contents and attachments; filenames, structure and dates remain visible on disk.
- you can create a zip backup of the vault and export or import settings whenever you need to move your environment.

---

## PROJECT STATUS

> under active development. the currently declared version is `0.2.0`, and the next set of changes remains in the changelog's `SIN PUBLICAR / UNRELEASED` section.

enki already covers a complete daily workflow and provides packaging for windows, linux and macos. it is still a young project: workflows that depend on the operating system, a real screen reader, live synchronisation or sharing the vault with other applications are reviewed manually before considering a release candidate.

the detailed history is available in [`CHANGELOG.md`](CHANGELOG.md). its unpublished section explains what already exists in the code but is not yet part of a closed release.

---

## GETTING STARTED FROM THE REPOSITORY

you need the .net sdk selected by the project. you can then build, run and verify the application with these commands:

```bash
# build
dotnet build enki/src/enki/enki.csproj

# run
dotnet run --project enki/src/enki/enki.csproj

# run the tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

on first launch, enki asks for the interface language and the folder that will become the vault. you can create a new one or open a folder that already contains your notes. from that point on, content is stored there and every local feature is available without configuring an account.

development builds use data kept separately from a normal installation, so a newly cloned copy starts clean and does not mix its settings with the version used for everyday work.

---

## ENABLE POLAR IF YOU WANT IT

polar appears as an account option only when the project knows the public supabase address and publishable key. for a development environment, create `enki/local.properties`, which git ignores:

```properties
SUPABASE_URL=<project-url>
SUPABASE_ANON_KEY=<publishable-key>
```

if your environment uses `SUPABASE_PUBLISHABLE_KEY`, copy that value into `SUPABASE_ANON_KEY`. do not use `SUPABASE_SECRET_KEY`: enki neither needs nor should receive a secret server key.

without this file, enki continues to work normally and retains all of its local features.

---

## PROJECT DOCUMENTATION

versioned documentation lives in [`.docs/project-info/`](.docs/project-info/). `docs/` is ignored by git and is not part of this repository. these are the main entry points:

- [feature inventory](.docs/project-info/inventario-funcionalidades-y-mejora-continua.md) — a detailed tour of existing features and how they relate to one another.
- [project analysis](.docs/project-info/analisis-proyecto/analisis-deficiencias-proyecto.md) — an audit of the product's condition and risks.
- [roadmap](.docs/project-info/analisis-proyecto/roadmap.md) — improvement phases and the status of each one.
- [markdown editor](.docs/project-info/editor-markdown.md) — writing and live-preview behaviour.
- [files](.docs/project-info/seccion-archivos.md) — attachment navigation, preview and organisation.
- [calendar](.docs/project-info/calendar-feature/00-README.md) — vision and scope of the unified view.
- [polar synchronisation](.docs/project-info/supabase-sync/00-README.md) — decisions and limits of optional synchronisation.
- [notifications and reminders](.docs/project-info/notificaciones-y-recordatorios.md) — notices, do not disturb hours and snoozing.
- [external changes](.docs/project-info/cambios-externos-en-el-vault.md) — protection from edits made by other applications.
- [design system](.docs/project-info/sistema-diseno-web.md) — visual and interaction principles.
- [release candidate qa](.docs/project-info/qa-manual-release-candidate.md) — manual checks required before publishing a release.

contribution conventions are documented in [`AGENTS.md`](AGENTS.md), commit rules in [`.docs/commit-guidelines.md`](.docs/commit-guidelines.md), and the pull request, merge and release format in [`.docs/pr-merge-release-guidelines.md`](.docs/pr-merge-release-guidelines.md).

---

<div align="center">
  <p><sub>built patiently for personal use.</sub></p>
  <p><a href="CHANGELOG.md">changelog</a> · <a href="LICENSE">license</a></p>
</div>
