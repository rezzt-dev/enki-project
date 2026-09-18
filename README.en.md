<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/project-icons/dark-icon/enki-icon-light.png">
    <source media="(prefers-color-scheme: light)" srcset="docs/project-icons/light-icon/enki-icon-dark.png">
    <img alt="Enki" src="docs/project-icons/dark-icon/enki-icon-light.png" width="96">
  </picture>

  <h1>Enki</h1>
  <p><em>Your second brain, on your desktop.</em></p>

  <p>
    <a href="README.md">Leer en español</a>
  </p>
</div>

---

**Enki** is a desktop app built for one person: you. It is not a collaborative suite, a SaaS, or another tool that forces you to think like its product team. It is a private space to store ideas, tasks, projects, and reminders so that everything stays connected, within reach, and nothing gets lost.

It was born out of the frustration of having to open five different apps to know what to do today: notes in one place, tasks in another, the calendar in another, and reminders in yet another. Enki brings all of that into a single window without forcing everything together. Each thing keeps its place, and you decide how to connect them.

> The goal is not for you to use Enki all day. It is that, when you open it, you know exactly where what you need is.

---

## What can you do with Enki?

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>📝 Notes</h3>
      <p>A Markdown vault that lives on your disk. No lock-in, no proprietary formats. Create folders, drag notes around, tag them with <code>tags</code>, and link notes to each other using <code>[[wikilinks]]</code>.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🗂️ Kanban</h3>
      <p>A local task board with columns, priorities, due dates, and subtasks. Drag cards between columns, link them to a note, and recover deleted cards from the trash.</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>☁️ Polar</h3>
      <p>Cloud-synced tasks shared with the companion Android app. It is optional: if you do not link it, Enki works exactly the same with your local notes and Kanban.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🗓️ Calendar</h3>
      <p>A unified view of Kanban, Polar, and reminders. Drag a task to reschedule it, create reminders directly from an empty slot, and filter by what actually matters.</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔔 Reminders</h3>
      <p>Standalone alerts with date and time. They appear on the calendar, feed the due-date inbox, and sync with Polar if it is enabled.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🕸️ Sessions</h3>
      <p>Note subspaces with a visual relationship graph. Useful for isolated projects: a thesis, a trip, a product. Sessions limit what you see without deleting the rest.</p>
    </td>
  </tr>
</table>

In addition, Enki includes a **dashboard** that summarizes your day, **global search** (`Ctrl+Shift+F`), a **quick switcher** to jump to notes (`Ctrl+P`), a **notification center**, a **cross-cutting trash**, and granular settings so the interface adapts to you, not the other way around.

---

## Philosophy

- **Your data is yours.** Notes are plain Markdown files in a folder you choose. You can open them with any editor, back them up with any tool, and take them with you.
- **Connected, not cluttered.** Each module can stand alone, but it gains meaning when crossed with the others: a note linked to a Kanban card, a Polar task dragged on the calendar, a reminder that shows up in today's inbox.
- **No distractions.** Monochrome interface, clear typography, sharp corners, and restrained motion. No gamification, no constant notifications, no productivity pressure.
- **Works offline.** Polar is optional. Enki's core — notes, Kanban, reminders, calendar — works completely offline.

---

## Project status

> 🚧 **Actively under development.** This repository is private and for personal use for now. There is no published installer or third-party support.

Enki is already functional for daily use: notes, Kanban, calendar, reminders, search, and trash are operational. Polar sync requires Supabase credentials and is currently intended only for the author's environment.

If you are reading this, it is probably yourself a few months from now. Technical documentation lives in `docs/`, the change history in `CHANGELOG.md`, and code conventions in `AGENTS.md`.

---

## Getting started

If you have the development environment set up:

```bash
# Build
dotnet build enki/src/enki/enki.csproj

# Run
dotnet run --project enki/src/enki/enki.csproj

# Tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

The first time you launch Enki, it will ask you to choose a language and a vault folder. From then on, everything you create is stored there.

To sync with Polar, add your Supabase credentials to `enki/local.properties` (this file is git-ignored and never committed):

```properties
SUPABASE_URL=<project-url>
SUPABASE_ANON_KEY=<anon-key>
```

If your `.env` uses `SUPABASE_PUBLISHABLE_KEY`, copy that value to `SUPABASE_ANON_KEY`.
Do not copy `SUPABASE_SECRET_KEY`: the app only needs the publishable key.

Without that file, Enki still works perfectly in local mode.

---

## Documentation

The `docs/` folder collects the product analysis, improvement roadmap, and module specifications. Some useful entry points:

- [`docs/agent-docs/analisis-proyecto/analisis-deficiencias-proyecto.md`](docs/agent-docs/analisis-proyecto/analisis-deficiencias-proyecto.md) — audit of the current state and priorities.
- [`docs/agent-docs/analisis-proyecto/roadmap.md`](docs/agent-docs/analisis-proyecto/roadmap.md) — phased correction plan.
- [`docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md`](docs/agent-docs/inventario-funcionalidades-y-mejora-continua.md) — full feature inventory.
- [`docs/agent-docs/calendar-feature/01-contexto-y-vision-de-producto.md`](docs/agent-docs/calendar-feature/01-contexto-y-vision-de-producto.md) — why the Calendar exists.
- [`docs/agent-docs/notificaciones-y-recordatorios.md`](docs/agent-docs/notificaciones-y-recordatorios.md) — how reminders and notifications work.
- [`docs/agent-docs/sistema-diseno-web.md`](docs/agent-docs/sistema-diseno-web.md) — visual design system principles.

---

<div align="center">
  <p><sub>Built with patience for personal use.</sub></p>
  <p><a href="CHANGELOG.md">Changelog</a> · <a href="LICENSE">License</a></p>
</div>
