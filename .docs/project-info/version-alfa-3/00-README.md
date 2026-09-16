# Auditoría alfa-3 — índice

Auditoría técnica, de UX/UI y de experiencia de usuario del estado actual de Enki (rama `alfa-version`,
22 de agosto de 2026), realizada por un equipo de tres perspectivas (desarrollador senior full-stack,
experto UX/UI y usuario final) trabajando en paralelo sobre todo el árbol de código.

Estos dos documentos son autosuficientes: no hace falta el prompt que originó la auditoría ni ningún otro
contexto para entender qué se analizó, qué se encontró y qué hacer al respecto.

- **[01-analisis-detallado.md](01-analisis-detallado.md)** — inventario de lo analizado, metodología,
  y todos los hallazgos organizados por módulo y por perspectiva, con evidencia concreta
  (`archivo:línea`) para cada uno. Incluye una sección de "qué funciona bien" y otra de "hallazgos
  transversales" que aparecen repetidos en varios módulos.
- **[02-roadmap-implementacion.md](02-roadmap-implementacion.md)** — el mismo trabajo convertido en un
  plan de fases accionable, priorizado por impacto/esfuerzo, con dependencias explícitas entre tareas.

## Punto de partida importante

Este no es el primer análisis de Enki. `docs/agent-docs/analisis-proyecto/analisis-deficiencias-proyecto.md`
documenta una auditoría anterior, mucho más profunda en la base de código de esa fecha, cuyas fases de
corrección (`docs/agent-docs/analisis-proyecto/roadmap.md`) están **todas completadas** y confirmadas por
`CHANGELOG.md` (atomicidad de escritura, `Reminder.IsTriggered`, papelera/confirmaciones consistentes,
aislamiento de cuentas Polar, wikilinks, rendimiento del editor, accesibilidad, higiene de release, etc.).

Esta auditoría **no repite esos hallazgos ya resueltos** salvo para confirmar puntualmente (spot-check)
que la corrección sigue vigente. Todo el esfuerzo se concentra en encontrar problemas **nuevos**: código
añadido o modificado desde entonces (sesiones con contraseña, filtro de grupos del grafo, personalización
de la barra lateral, paleta de comandos, modo zen, exportación de notas, editor de temas con selector de
color) y en zonas del código que el análisis anterior cubrió con menos profundidad (Hábitos, Estadísticas,
Polar vs. Kanban, Ajustes, i18n).

## Alcance cubierto

Todo `enki/src/enki/` (110 viewmodels, 20 vistas `.axaml`, ~34 000 líneas de C# de producción, ~12 000
líneas de `.axaml`) y toda la suite de tests (`enki/tests/enki.Tests`, 742 casos `[Fact]`/`[Theory]`;
`enki/tests/enki.UITests`, 10 casos headless), dividido en cinco frentes de trabajo paralelos más una
pasada transversal de arquitectura/build/CI:

1. Sesiones, protección con contraseña (cifrado de sobre AES-GCM + PBKDF2), grafo de notas y filtro de
   grupos.
2. Notas, editor, Markdown en vivo, exportación, vault y wikilinks.
3. Kanban, Polar (gestión de tareas en la nube) y papelera.
4. Calendario, recordatorios, notificaciones, dashboard, hábitos y estadísticas.
5. Archivos/PDF, Skills, Ajustes, sistema de temas, shell/navegación, paleta de comandos e
   internacionalización.
6. Transversal: arquitectura de composición, build (`Directory.Build.props`, CI), deuda técnica general.

No se ha modificado ningún archivo de código durante esta auditoría.
