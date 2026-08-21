# Changelog

Todos los cambios notables de Enki se documentan en este fichero. El formato sigue a grandes rasgos
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/), y el versionado sigue
[SemVer](https://semver.org/lang/es/) (`MAJOR.MINOR.PATCH`) mientras la app se mantenga en `0.x` — es
decir, cualquier cambio puede romper compatibilidad entre versiones menores hasta llegar a `1.0.0`.

## [Sin publicar]

Cierra la Fase 10 (opcional) del roadmap de corrección de deficiencias (`docs/analisis-proyecto/roadmap.md`)
— deuda técnica menor y mejoras de producto derivadas de la auditoría, cajón de sastre deliberado:

- Recurrencia local nativa para Recordatorios (independiente de Polar): un recordatorio recurrente se
  reprograma solo a su siguiente ocurrencia en vez de tener que recrearse a mano cada vez.
- "Deshacer" al descartar un recordatorio de la bandeja del shell, con una ventana de unos segundos antes de
  que el descarte se vuelva permanente.
- Los descartes de recordatorios (`dismissed-reminders.json`) se purgan cuando la tarjeta asociada se borra
  para siempre, en vez de quedar huérfanos.
- El WebView del grafo de sesiones se dispone explícitamente al salir de la pestaña — evitaba un motor de
  navegador nativo huérfano por cada ciclo de entra/sale.
- Cancelación de una búsqueda global en vuelo cuando el usuario sigue tecleando, en vez de arriesgarse a que
  una búsqueda lenta pise con resultados obsoletos a una más reciente.
- Intervalo y retención del backup automático (Ajustes › General) ganan control propio en la UI — antes
  solo el interruptor de activación era visible.
- `AnalysisLevel` del analizador de .NET subido a `latest-recommended` en el proyecto principal, con los
  ~116 diagnósticos resultantes corregidos caso a caso (mayormente `CultureInfo` explícito en formateo de
  texto de UI vs. datos internos).
- Icono de aplicación real (`.ico`/`.icns`/PNG), sustituyendo el icono genérico del SO.
- `README.en.md` — versión en inglés, dado que la app ya soporta 6 idiomas de UI.
- Decisión documentada (sin implementar): mantener la composición manual de servicios del shell en vez de
  introducir un contenedor de DI — ver `docs/analisis-proyecto/decision-contenedor-di.md`.

Cierra también la Fase 9 (opcional) — robustez de sincronización Polar y notificaciones, ninguno de sus
hallazgos compromete la integridad de los datos:

- Reconexión automática de `PolarRealtimeService` tras una caída del socket (reutiliza el backoff del
  temporizador de sincronización en vez de un temporizador paralelo propio).
- Los tres botones de "Sync now" (Recordatorios, Gestión de tareas, Ajustes › Cuenta) distinguen un sync
  completado de uno descartado porque otro ya estaba en curso.
- Reintento periódico (cada hora) del aviso de fallo de sincronización mientras el problema persista, no
  solo en la transición inicial.
- Aviso al usuario cuando una edición local pierde un conflicto de sincronización (Last-Write-Wins) frente a
  una versión remota más reciente.

## [0.1.0] — 2026-08-21

Primera versión con número real (`enki/Directory.Build.props`, antes generaba `1.0.0.0` por defecto sin
que nadie lo hubiera decidido) y con este propio changelog. No es un "1.0" porque la app todavía no ha
tenido una release pública ni un empaquetado real para usuarios finales — ver
`docs/analisis-proyecto/roadmap.md`, Fase 8, para el resto de higiene de release pendiente
(instalador/paquete distribuible).

Cierra las Fases 0–8 del roadmap de corrección de deficiencias
(`docs/analisis-proyecto/roadmap.md`), sobre una auditoría completa del código existente:

- **Fase 0** — Red de seguridad mínima: logging a fichero, captura de excepciones no observadas.
- **Fase 1** — Persistencia atómica del contenido de usuario (notas, skills, sesiones, ajustes) y backup
  automático opcional.
- **Fase 2** — Recordatorios: corrección de notificación perdida tras reprogramar y de deriva horaria en
  tareas recurrentes de Polar.
- **Fase 3** — Borrado seguro: papelera y confirmaciones consistentes en Kanban/Notas/Sesiones.
- **Fase 4** — Seguridad de credenciales y aislamiento de cuentas Polar (tokens redactados en
  exportaciones/backups, filas `Dirty` aisladas por cuenta).
- **Fase 5** — Integridad de la red de enlaces (wikilinks): reescritura al renombrar, resolución
  unificada, distinción visual de enlaces rotos/ambiguos.
- **Fase 6** — Rendimiento del editor y del vault: debounce de backlinks/outline/stats, caché de
  metadatos de notas, tamaño de vault asíncrono.
- **Fase 7** — Accesibilidad: `AutomationProperties.Name` en las 17 vistas, vía alternativa a
  drag&drop para mover tarjetas Kanban, atajos de teclado.
- **Fase 8** — Higiene de documentación y operaciones de release: enlaces de documentación reparados,
  LICENSE, CI (`dotnet build`/`dotnet test` en cada push/PR), analizadores de warnings-como-errores,
  metadatos de build (`Company`/`Copyright`/`Version`), este changelog, y guía de empaquetado para
  usuarios no técnicos.

Detalle completo de cada punto (fichero:línea, tests añadidos, criterio de aceptación) en
`docs/analisis-proyecto/roadmap.md`.
