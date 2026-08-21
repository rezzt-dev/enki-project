# Inventario funcional de Enki y plan de mejora continua

> ** Alcance **: este documento describe el estado actual de la aplicación Enki tal como está implementada hoy en el código. No propone funcionalidades nuevas: su objetivo es catalogar todo lo que existe, detectar dónde las piezas aún no encajan del todo y proponer mejoras orientadas a que el producto se sienta completo, coherente y profesional.

> **Fuente de verdad**: el código vence a cualquier documento desfasado. Las referencias a archivos (`ruta:linea`) se basan en el árbol vigente de `enki/src/enki/`.

---

## 1. Visión de producto

Enki es una aplicación de escritorio multiplataforma (Windows/Linux/macOS) construida con Avalonia UI, .NET 8 y CommunityToolkit.Mvvm. Se define como un “segundo cerebro” personal que combina:

- un vault de notas Markdown locales con wikilinks;
- sesiones de notas con un grafo visual de relaciones;
- tareas locales tipo Kanban;
- tareas sincronizables en la nube vía Supabase (Polar), compartidas con una app Android;
- un calendario unificado que agrega Kanban + Polar + Recordatorios;
- recordatorios independientes con avisos;
- centro de notificaciones, papelera transversal, ajustes granulares y búsqueda global.

La app sigue MVVM estricto, sin contenedor de inyección de dependencias: la composición manual vive en `MainWindowViewModel.cs:167`. Los modelos son POCO, la persistencia está en servicios, el estado y los comandos en viewmodels, y las vistas en archivos `.axaml` con code-behind mínimo.

---

## 2. Arquitectura y principios transversales

### 2.1 Tecnología y convenciones

| Aspecto | Decisión actual |
|---------|-----------------|
| UI framework | Avalonia UI 11, .NET 8 (`global.json` fija la versión) |
| MVVM | `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`) |
| DI | Ninguno; composición manual en `MainWindowViewModel` |
| Idioma de código | Identificadores en inglés; comentarios XML-doc y documentación en español |
| Persistencia | JSON en `%AppData%/Enki/` (Linux: `~/.config/Enki/`) y en `<vault>` |
| Estilo visual | Sistema monocromo “brutalismo editorial suizo”, `CornerRadius="0"`, fuentes Space Grotesk y JetBrains Mono |

### 2.2 Patrón de persistencia unificado

Todos los servicios siguen el mismo contrato:

- Constructor con `storageDirectory` opcional para tests.
- Carga en constructor o bajo demanda.
- `Save()` serializa e invoca un evento de cambio (`BoardChanged`, `DataChanged`, `RemindersChanged`, `NotificationAdded`, etc.).

Esto permite que los viewmodels reaccionen sin tener referencia directa al servicio que originó el cambio.

### 2.3 Comunicación entre módulos

- **Eventos de servicio**: `KanbanService.BoardChanged`, `PolarTaskService.DataChanged`, `ReminderService.RemindersChanged`, `NotificationService.NotificationsChanged`, `PolarAuthService.LinkStateChanged`.
- **Callbacks centrales**: `MainWindowViewModel` centraliza `Navigate(AppPage)`, `OpenKanbanCard`, `OpenNoteFromKanban`, `OpenCalendarItemEditor`, `OnGlobalSearchResultSelected`.
- **Agregación de datos**: `CalendarAggregationService` lee Kanban, Polar y Recordatorios y expone ítems de calendario sin duplicar almacenes.

### 2.4 Flujo de arranque

1. `Program.Main` → `SingleInstanceGuard.Acquire`.
2. `App.axaml.cs` aplica el idioma guardado en `AppSettings.Language`.
3. `MainWindowViewModel` construye todos los servicios.
4. Si no hay idioma elegido: `LanguageSetupView`.
5. Si no hay vault: `VaultSetupView`.
6. Si hay ambos: `InitializeShell` instancia todos los viewmodels de página.

---

## 3. Inventario de funcionalidades por módulo

Cada módulo se descompone en **funcionalidad** → **sub-funcionalidad** → **micro-funcionalidad** → **efectos / interacción / integraciones**.

### 3.1 Shell y navegación

#### Funcionalidad: estructura de ventana principal
- **Sub-funcionalidad**: title bar custom vs. nativa.
  - Micro-funcionalidad: `UseCustomTitleBar` en `AppSettings`; cuando es `true` se dibujan botones propios de minimizar/maximizar/cerrar.
  - Micro-funcionalidad: resize grips propios para mantener los bordes afilados (`MainWindow.axaml:313`).
- **Sub-funcionalidad**: sidebar flotante tipo glass.
  - Micro-funcionalidad: ancho animado entre expandido (`248 px`) y colapsado (`76 px`).
  - Micro-funcionalidad: badge de conteo en Calendario (`ReminderInbox.OverdueCount`) y Papelera (`Trash.TotalCount`).
  - Micro-funcionalidad: iconos de Material Icons Outlined; labels localizados vía `NavItemViewModel`.
- **Sub-funcionalidad**: zoom de interfaz.
  - Micro-funcionalidad: `UiZoomPercent` por vault (`50 %`–`200 %`, paso `10 %`).
  - Micro-funcionalidad: atajos `Ctrl++`, `Ctrl+-`, `Ctrl+0`.
  - Micro-funcionalidad: `ScaleTransform` envuelve el `Grid` raíz.
  - Efecto: `Popup.ShouldUseOverlayLayer=True` forzado para que los popovers escalen correctamente.

#### Funcionalidad: búsqueda global
- **Sub-funcionalidad**: popup de búsqueda (`Ctrl+Shift+F`).
  - Micro-funcionalidad: input con debounce `200 ms`.
  - Micro-funcionalidad: búsqueda paralela en notas, tarjetas Kanban, tareas Polar y recordatorios.
  - Micro-funcionalidad: resultados ordenados alfabéticamente.
  - Micro-funcionalidad: navegación con flechas, Enter abre el ítem en su módulo propietario.

#### Funcionalidad: Quick Switcher
- **Sub-funcionalidad**: `Ctrl+P` para saltar rápido a una nota.
  - Micro-funcionalidad: scoring por título, preview y tags (`exact=1000`, `starts=500`, `contains=250`, `preview=100`, `tags=50`).
  - Micro-funcionalidad: límite de `20` resultados.

#### Funcionalidad: centro de notificaciones (campana)
- **Sub-funcionalidad**: log persistente de eventos del sistema.
  - Micro-funcionalidad: severidades Info, Success, Warning, Error.
  - Micro-funcionalidad: filtro de silenciamiento por severidad.
  - Micro-funcionalidad: “Marcar todo como leído”.
  - Micro-funcionalidad: horario “No molestar” con silenciamiento de toasts/sonido, pero conservando el log.

#### Funcionalidad: inbox de vencimientos
- **Sub-funcionalidad**: botón de inbox con badge.
  - Micro-funcionalidad: agrupa tarjetas Kanban por vencidas / hoy / próximas (`ReminderSoonWindowDays`).
  - Micro-funcionalidad: click en ítem abre la tarjeta Kanban.
  - Micro-funcionalidad: recordatorios con fecha/hora también entran aquí vía integración implícita.

#### Funcionalidad: panel de recordatorios flotante
- **Sub-funcionalidad**: ventana de “próximo recordatorio”.
  - Micro-funcionalidad: muestra el siguiente recordatorio a vencer.
  - Integración: scheduler cada `30 s` en `MainWindowViewModel`.

---

### 3.2 Configuración y arranque

#### Funcionalidad: selección de idioma en primer arranque
- **Sub-funcionalidad**: `LanguageSetupView`.
  - Micro-funcionalidad: muestra 6 idiomas (`en-US`, `en-GB`, `de-DE`, `es-ES`, `fr-FR`, `es-419`) con nombre nativo.
  - Micro-funcionalidad: se muestra antes que `VaultSetup` cuando `AppSettings.Language` es nulo/vacío.
  - Micro-funcionalidad: al elegir se aplica en caliente y se persiste.
- **Sub-funcionalidad**: cambio de idioma posterior.
  - Micro-funcionalidad: selector en Ajustes › General.
  - Micro-funcionalidad: refresco en caliente vía `LocalizationService` indexer + `RefreshAllLocalizedLabels`.

#### Funcionalidad: configuración del vault
- **Sub-funcionalidad**: `VaultSetupView`.
  - Micro-funcionalidad: “Crear vault nuevo” / “Abrir vault existente”.
  - Micro-funcionalidad: semilla de nota de bienvenida localizada (`WelcomeNote_Content`, `WelcomeNote_FileName`).
  - Micro-funcionalidad: semilla de tablero Kanban inicial localizado (`CreateSeedBoard`).
- **Sub-funcionalidad**: cambio de carpeta del vault.
  - Micro-funcionalidad: mover todo el contenido; ajustes por vault (`VaultSettings`) se recargan.

#### Funcionalidad: ajustes generales
- **Sub-funcionalidad**: página de inicio (`StartupPage`).
- **Sub-funcionalidad**: confirmación antes de eliminar (`ConfirmBeforeDelete`).
- **Sub-funcionalidad**: exportar/importar ajustes globales en JSON.
- **Sub-funcionalidad**: backup ZIP completo del vault (`VaultBackupService`).

#### Funcionalidad: apariencia
- **Sub-funcionalidad**: tema claro/oscuro.
  - Micro-funcionalidad: `Theme="Dark"` por defecto.
  - Efecto: cambio requiere reinicio (limitación documentada).
- **Sub-funcionalidad**: fuente de interfaz y editor.
  - Micro-funcionalidad: bundled Space Grotesk por defecto; permite fuente del sistema.
- **Sub-funcionalidad**: zoom UI por vault.

#### Funcionalidad: ajustes del editor
- **Sub-funcionalidad**: tamaño de fuente, ancho de columna, alineación, scrollbar, tab size, números de línea, word wrap.
- **Sub-funcionalidad**: panel lateral visible por defecto y pestaña activa (`Outline`, `Backlinks`, `Stats`).

#### Funcionalidad: notificaciones y sonido
- **Sub-funcionalidad**: activar/desactivar paneles de campana/inbox.
- **Sub-funcionalidad**: toasts de escritorio.
- **Sub-funcionalidad**: silenciar por severidad.
- **Sub-funcionalidad**: ventana “No molestar” con horario.
- **Sub-funcionalidad**: sonido opcional (`Console.Beep`, `afplay`, `paplay`).

#### Funcionalidad: calendario
- **Sub-funcionalidad**: vista por defecto, inicio de semana, fines de semana, horario laboral, formato 24h, scrollbar del sidebar.
- **Sub-funcionalidad**: mostrar/ocultar módulo Calendario en el nav.
- **Sub-funcionalidad**: capas ocultas persistidas (`CalendarHiddenLayerKeys`).
- **Sub-funcionalidad**: ocultar completadas / ocultar ocurrencias fantasma.

#### Funcionalidad: cuenta Polar
- **Sub-funcionalidad**: vincular/desvincular cuenta Supabase (email/pass).
- **Sub-funcionalidad**: refresco automático de token.
- **Sub-funcionalidad**: sincronización manual.
- **Sub-funcionalidad**: opción para mostrar tags en la gestión de tareas Polar.

---

### 3.3 Dashboard

#### Funcionalidad: centro de mando
- **Sub-funcionalidad**: resumen “HOY”.
  - Micro-funcionalidad: ítems de Kanban + Polar + Recordatorios agregados por `CalendarAggregationService`.
  - Micro-funcionalidad: chips con prioridad, hora y origen.
  - Micro-funcionalidad: labels compuestos localizados con pluralización (`TodayUnreadChipLabel`, etc.).
- **Sub-funcionalidad**: notas recientes.
  - Micro-funcionalidad: tarjeta destacada + lista compacta (hasta 6).
  - Micro-funcionalidad: click abre la nota.
- **Sub-funcionalidad**: tareas del tablero.
  - Micro-funcionalidad: mini columnas Pendiente / En progreso / Hecho.
  - Micro-funcionalidad: hasta 5 tarjetas por columna.
  - Micro-funcionalidad: barra de progreso apilada con conteos.
- **Sub-funcionalidad**: identidad del vault.
  - Micro-funcionalidad: ruta, tamaño total, conteo de notas y sesiones.
- **Sub-funcionalidad**: actividad.
  - Micro-funcionalidad: resumen de Polar (conectado/no conectado).
  - Micro-funcionalidad: últimas 4 notificaciones del sistema.
- **Sub-funcionalidad**: accesos rápidos.
  - Micro-funcionalidad: botones a Calendario, Recordatorios, Notificaciones, Ajustes.
- **Sub-funcionalidad**: grafo en background.
  - Micro-funcionalidad: `LoadGraphAsync` cuenta nodos y enlaces sin renderizar WebView.
- **Efectos**: tarjetas con hover lift, transiciones de borde/fondo.

---

### 3.4 Notas

#### Funcionalidad: navegación de notas
- **Sub-funcionalidad**: grid de tarjetas.
  - Micro-funcionalidad: título, preview, word count, tags, fecha relativa.
  - Micro-funcionalidad: búsqueda debounced `200 ms` por título/preview/tags.
  - Micro-funcionalidad: filtros de tags compartidos (`TagFilterViewModel`).
- **Sub-funcionalidad**: árbol de carpetas.
  - Micro-funcionalidad: crear, renombrar, eliminar carpetas.
  - Micro-funcionalidad: drag-and-drop de notas entre carpetas.
- **Sub-funcionalidad**: selector de workspace.
  - Micro-funcionalidad: vault completo o sesión específica.
- **Sub-funcionalidad**: notas recientes.
  - Micro-funcionalidad: hasta 10; se persisten en `RecentNotePaths`.
- **Sub-funcionalidad**: importar archivos Markdown.
- **Sub-funcionalidad**: crear nota diaria.
  - Micro-funcionalidad: nombre fijo `yyyy-MM-dd.md`.

#### Funcionalidad: editor de notas
- **Sub-funcionalidad**: pestañas múltiples.
  - Micro-funcionalidad: abrir, cerrar, reordenar por drag.
  - Micro-funcionalidad: persistir pestañas abiertas (`OpenNotePaths`, `ActiveNotePath`).
  - Micro-funcionalidad: restaurar pestañas al arrancar.
- **Sub-funcionalidad**: editor `AvaloniaEdit.TextEditor`.
  - Micro-funcionalidad: documento por tab (conserva undo/redo).
  - Micro-funcionalidad: autoguardado con debounce `600 ms`.
  - Micro-funcionalidad: indicador “Guardado · HH:mm”.
- **Sub-funcionalidad**: toolbar de Markdown.
  - Micro-funcionalidad: bold, italic, strikethrough.
  - Micro-funcionalidad: H1, H2, H3.
  - Micro-funcionalidad: quote, listas ordenadas/desordenadas, task list.
  - Micro-funcionalidad: link, image, inline code, code block, horizontal rule.
- **Sub-funcionalidad**: panel lateral.
  - Micro-funcionalidad: **Outline**: esquema de encabezados.
  - Micro-funcionalidad: **Backlinks**: notas que enlazan a la actual.
  - Micro-funcionalidad: **Stats**: conteo de palabras, caracteres, lectura.
  - Efecto: slide del ancho del panel (`DoubleTransition`, `200 ms`).
- **Sub-funcionalidad**: tags como frontmatter YAML.
  - Micro-funcionalidad: edición inline en el panel lateral.
  - Micro-funcionalidad: detección automática vía `NoteTagHelper`.
- **Sub-funcionalidad**: wikilinks `[[Nota]]`.
  - Micro-funcionalidad: parseo por `WikiLinkParser`.
  - Micro-funcionalidad: `Ctrl+Click` abre la nota en el mismo workspace.
  - Micro-funcionalidad: resaltado en el editor.
- **Sub-funcionalidad**: tareas vinculadas.
  - Micro-funcionalidad: muestra tarjetas Kanban cuyo `LinkedNotePath` apunta a la nota.
  - Micro-funcionalidad: click abre el editor Kanban de la tarjeta.
- **Sub-funcionalidad**: renombrar nota.
  - Micro-funcionalidad: desde grid o desde título del editor.
  - Micro-funcionalidad: sincroniza `LinkedNotePath` en Kanban (`RelinkNotePath`).
- **Efectos**: hover lift en tarjetas (`Styles.axaml:1003`); animación de entrada de página.

---

### 3.5 Sesiones

#### Funcionalidad: subespacios de notas
- **Sub-funcionalidad**: listado de sesiones.
  - Micro-funcionalidad: crear, renombrar, eliminar.
  - Micro-funcionalidad: sesión “default” no se puede borrar.
  - Micro-funcionalidad: abrir en el módulo Notas con workspace limitado.
- **Sub-funcionalidad**: grafo visual de relaciones.
  - Micro-funcionalidad: renderizado en `WebView` (`SessionGraphRenderer.cs`).
  - Micro-funcionalidad: nodos = notas; aristas = wikilinks + manual links.
  - Micro-funcionalidad: carga async en background.
- **Sub-funcionalidad**: modo “enlace manual”.
  - Micro-funcionalidad: doble click en nodo abre la nota.
  - Micro-funcionalidad: click simple en modo link une dos notas (solo dentro de la misma sesión).
- **Sub-funcionalidad**: persistencia de posiciones.
  - Micro-funcionalidad: al soltar un nodo se guarda en `.session.json`.
- **Sub-funcionalidad**: grafo combinado.
  - Micro-funcionalidad: `LoadCombinedGraphAsync` une todas las sesiones.
- **Sub-funcionalidad**: panel de tarjetas vinculadas para la sesión seleccionada.
- **Efectos**: grafo interactivo con drag de nodos; loader mientras carga.

---

### 3.6 Kanban

#### Funcionalidad: tablero local
- **Sub-funcionalidad**: persistencia.
  - Micro-funcionalidad: `kanban.json` en `%AppData%/Enki/`.
  - Micro-funcionalidad: modelo `KanbanBoard` con columnas, tarjetas, subtareas y papelera.
- **Sub-funcionalidad**: columnas.
  - Micro-funcionalidad: columnas core “Por hacer”, “En progreso”, “Hecho” (`IsCore`).
  - Micro-funcionalidad: creación de columnas adicionales.
  - Micro-funcionalidad: reordenamiento de columnas.
  - Micro-funcionalidad: detección de columnas core por posición, no por título.
- **Sub-funcionalidad**: tarjetas.
  - Micro-funcionalidad: título, descripción, prioridad (`High`/`Medium`/`Low`/`None`), fecha límite UTC, duración en minutos, tags, subtareas.
  - Micro-funcionalidad: nota vinculada (`LinkedNotePath`).
  - Micro-funcionalidad: creación rápida.
  - Micro-funcionalidad: edición en diálogo propio.
- **Sub-funcionalidad**: drag & drop.
  - Micro-funcionalidad: mover tarjetas entre columnas.
  - Micro-funcionalidad: reordenar dentro de una columna.
  - Micro-funcionalidad: automatismo al mover a última columna core: marca subtareas hechas.
  - Micro-funcionalidad: automatismo al mover a primera columna core: desmarca subtareas.
- **Sub-funcionalidad**: ordenamiento.
  - Micro-funcionalidad: manual, prioridad, fecha, creación.
- **Sub-funcionalidad**: subtareas.
  - Micro-funcionalidad: crear, editar, eliminar, toggle.
  - Micro-funcionalidad: progreso visible en la tarjeta.
- **Sub-funcionalidad**: borrado a papelera.
  - Micro-funcionalidad: `KanbanTrashedCard` guarda `ColumnId` y `ColumnTitle`.
  - Micro-funcionalidad: confirmación configurable (`ConfirmBeforeDelete`).
- **Sub-funcionalidad**: búsqueda interna.
  - Micro-funcionalidad: filtrar por título/descripción.
  - Micro-funcionalidad: filtro de tags compartido.
- **Efectos**: tarjetas con hover lift, prioridad como puntos de color, transiciones de fondo/borde, loading spinner.

---

### 3.7 Polar (gestión de tareas) — dentro del flujo Kanban

#### Funcionalidad: tareas sincronizadas en la nube
- **Sub-funcionalidad**: desbloqueo condicional.
  - Micro-funcionalidad: solo disponible si `PolarAuthService.IsLinked`.
  - Micro-funcionalidad: si no hay credenciales, el tab no aparece y el sync es no-op.
- **Sub-funcionalidad**: listas de tareas.
  - Micro-funcionalidad: nombre, icono, color, orden, flag `IsDependencyChain`.
  - Micro-funcionalidad: edición de listas (`PolarListEditorViewModel`).
- **Sub-funcionalidad**: tareas.
  - Micro-funcionalidad: prioridad 0–3, fecha límite, recurrencia, estimación, tags, completado.
  - Micro-funcionalidad: edición (`PolarTaskEditorViewModel`).
  - Micro-funcionalidad: detalle (`PolarTaskDetailViewModel`).
- **Sub-funcionalidad**: subtareas.
  - Micro-funcionalidad: fecha propia, completado.
- **Sub-funcionalidad**: cadena de dependencias.
  - Micro-funcionalidad: en listas `IsDependencyChain`, solo la primera tarea incompleta está desbloqueada (`IsTaskBlocked`).
- **Sub-funcionalidad**: motor de recurrencia local.
  - Micro-funcionalidad: `RollRecurringTasks` marca ocurrencias completadas y genera la siguiente.
  - Micro-funcionalidad: `CalculateNextDueDate` soporta diaria, días de semana, mensual, etc.
- **Sub-funcionalidad**: sincronización offline-first.
  - Micro-funcionalidad: push/pull de 4 tablas (`task_lists`, `tasks`, `subtasks`, `reminders`).
  - Micro-funcionalidad: dirty flags y soft-delete.
  - Micro-funcionalidad: resolución LWW por `updated_at` devuelto por Supabase.
  - Micro-funcionalidad: polling cada `25 s` con backoff exponencial hasta `5 min` si falla de red.
- **Efectos**: UI idéntica a Kanban; chips de color de lista.

---

### 3.8 Calendario

#### Funcionalidad: vista unificada
- **Sub-funcionalidad**: 4 vistas: Mes, Semana, Día, Agenda.
  - Micro-funcionalidad: `ReferenceDate` compartida entre vistas.
  - Micro-funcionalidad: mini-calendario en sidebar; puede desfasarse del `ReferenceDate` al navegar.
- **Sub-funcionalidad**: agregación de ítems.
  - Micro-funcionalidad: `CalendarAggregationService` lee Kanban, Polar y Recordatorios.
  - Micro-funcionalidad: nunca persiste; siempre refleja el estado actual de los otros módulos.
- **Sub-funcionalidad**: capas.
  - Micro-funcionalidad: capa fija “kanban”, capa fija “reminder”, una capa por lista Polar.
  - Micro-funcionalidad: mostrar/ocultar capas individuales.
  - Micro-funcionalidad: capas ocultas persistidas en `CalendarHiddenLayerKeys`.
- **Sub-funcionalidad**: filtros.
  - Micro-funcionalidad: filtro de prioridad.
  - Micro-funcionalidad: filtro de tags.
  - Micro-funcionalidad: ocultar completadas.
  - Micro-funcionalidad: ocultar ocurrencias fantasma.
- **Sub-funcionalidad**: ocurrencias fantasma.
  - Micro-funcionalidad: `IsGhostOccurrence` muestra la próxima aparición de tareas recurrentes.
- **Sub-funcionalidad**: quick-create.
  - Micro-funcionalidad: crear recordatorio.
  - Micro-funcionalidad: crear tarjeta Kanban.
- **Sub-funcionalidad**: drag & drop reschedule.
  - Micro-funcionalidad: mover chip cambia `DueDateUtc` del origen real.
  - Micro-funcionalidad: redimensionar asa inferior cambia `DurationMinutes` (solo Kanban, grano `5 min`).
- **Sub-funcionalidad**: menú contextual.
  - Micro-funcionalidad: editar, completar, eliminar.
  - Micro-funcionalidad: “Crear tarjeta Kanban aquí”.
- **Sub-funcionalidad**: detalle compartido.
  - Micro-funcionalidad: popover con información del ítem.
  - Micro-funcionalidad: “Editar” navega al módulo propietario.
- **Sub-funcionalidad**: línea “ahora”.
  - Micro-funcionalidad: `RefreshNowIndicator` cada `60 s` en Semana/Día.
- **Sub-funcionalidad**: configuración.
  - Micro-funcionalidad: inicio de semana lunes/domingo.
  - Micro-funcionalidad: mostrar/ocultar fines de semana.
  - Micro-funcionalidad: horario laboral resaltado y scroll inicial.
  - Micro-funcionalidad: formato 24h / AM-PM.
- **Efectos**: reveal de navegación (`Opacity 0→1`, `TranslateY 8 px`, `200–300 ms`, `CubicEaseOut`); línea “ahora” animada.

---

### 3.9 Recordatorios

#### Funcionalidad: avisos independientes
- **Sub-funcionalidad**: CRUD de recordatorios.
  - Micro-funcionalidad: título, descripción, fecha/hora, icono.
  - Micro-funcionalidad: listado, detalle, edición.
  - Micro-funcionalidad: completar (`IsCompleted`).
  - Micro-funcionalidad: eliminar (soft-delete).
- **Sub-funcionalidad**: “Limpiar recordatorios” masivo.
- **Sub-funcionalidad**: scheduler de vencimientos.
  - Micro-funcionalidad: `MainWindowViewModel._reminderTimer` cada `30 s`.
  - Micro-funcionalidad: convierte recordatorios vencidos en notificaciones del sistema.
- **Sub-funcionalidad**: integración Polar.
  - Micro-funcionalidad: recordatorios se sincronizan como tabla `reminders` de Supabase.
- **Integraciones**: alimentan Calendario; participan en Inbox de vencimientos.

---

### 3.10 Papelera

#### Funcionalidad: recuperación transversal
- **Sub-funcionalidad**: cuatro secciones.
  - Micro-funcionalidad: Documentos (`NoteFile`).
  - Micro-funcionalidad: Tareas Kanban (`KanbanTrashedCard`).
  - Micro-funcionalidad: Archivos (`VaultFile`).
  - Micro-funcionalidad: Recordatorios (`Reminder`).
- **Sub-funcionalidad**: restaurar individualmente.
  - Micro-funcionalidad: tareas Kanban vuelven a su columna original o primera disponible.
- **Sub-funcionalidad**: vaciar.
  - Micro-funcionalidad: vaciar por sección.
  - Micro-funcionalidad: vaciar todo.
  - Micro-funcionalidad: confirmación obligatoria, independiente de `ConfirmBeforeDelete`.
- **Sub-funcionalidad**: badge en nav.
  - Micro-funcionalidad: `Trash.TotalCount` suma las cuatro secciones.
- **Efectos**: tarjetas con hover, diálogo de confirmación estilizado.

---

### 3.11 Notificaciones

#### Funcionalidad: sistema de avisos
- **Sub-funcionalidad**: log persistente.
  - Micro-funcionalidad: `notifications.json`, máximo `50` entradas.
  - Micro-funcionalidad: severidades Info, Success, Warning, Error.
- **Sub-funcionalidad**: toast nativo.
  - Micro-funcionalidad: `WindowNotificationManager`.
  - Micro-funcionalidad: respeto de “No molestar”.
- **Sub-funcionalidad**: sonido.
  - Micro-funcionalidad: `NotificationSoundPlayer` usa `Console.Beep`, `afplay`, `paplay`.
- **Sub-funcionalidad**: API de push.
  - Micro-funcionalidad: `_notificationService.Push(title, message, severity)` usada desde viewmodels/servicios.
- **Efectos**: toast slide-in, badge pulsante, sonido opcional.

---

### 3.12 Archivos / Documentos

#### Funcionalidad: gestión de archivos no-Markdown
- **Sub-funcionalidad**: navegación.
  - Micro-funcionalidad: árbol de carpetas del vault (o sesión).
  - Micro-funcionalidad: grid de tarjetas.
  - Micro-funcionalidad: importar archivos.
  - Micro-funcionalidad: drag-and-drop al grid.
- **Sub-funcionalidad**: preview in-app.
  - Micro-funcionalidad: imágenes.
  - Micro-funcionalidad: PDFs (`PdfDocumentViewModel`, `PdfRenderHelper`).
- **Sub-funcionalidad**: acciones.
  - Micro-funcionalidad: “Abrir con aplicación del sistema”.
  - Micro-funcionalidad: renombrar, mover, eliminar (a `.trash`).
- **Integraciones**: comparte sesiones con Notas; archivos borrados aparecen en Papelera.

---

### 3.13 IA Skills

#### Funcionalidad: plantillas de habilidades
- **Sub-funcionalidad**: sección oculta por defecto (`SkillsPanelEnabled=false`).
- **Sub-funcionalidad**: listado de skills.
  - Micro-funcionalidad: archivos Markdown con frontmatter YAML (`name`, `description`, `emoji`, `color`, `vibe`, `tools`, `tags`, `permissions`).
  - Micro-funcionalidad: importar archivos o carpetas `.md`.
  - Micro-funcionalidad: exportar selección.
  - Micro-funcionalidad: duplicar.
- **Sub-funcionalidad**: editor de skill.
  - Micro-funcionalidad: edición de frontmatter y contenido Markdown.
- **Sub-funcionalidad**: skills por defecto.
  - Micro-funcionalidad: empaquetadas en `default-skills/`; se copian al activar la sección si el vault no tiene skills.
- **Sub-funcionalidad**: catálogo externo.
  - Micro-funcionalidad: enlace a `https://skillsclaude.org/skills`.
- **Nota importante**: las skills son texto/MD; no hay motor de IA conectado en el código actual.

---

## 4. Mapa de integraciones entre módulos

```
Dashboard
├── Notas (recientes, conteos)
├── Kanban (tareas del tablero)
├── Polar (resumen de sync)
├── Recordatorios (HOY)
├── Calendario (via CalendarAggregationService)
└── Notificaciones (últimas 4)

Notas
├── Sesiones (workspace limitado)
├── Kanban (linked cards)
├── Calendario (via tags/fechas implícitas)
└── Dashboard (recientes)

Sesiones
├── Notas (subárbol de notas)
├── Grafo (wikilinks + manual links)
└── Dashboard (conteos de grafo)

Kanban
├── Notas (LinkedNotePath)
├── Calendario (DueDateUtc + DurationMinutes)
├── Recordatorios (via fecha límite en Inbox)
├── Papelera (borrado suave)
└── Dashboard (resumen de columnas)

Polar
├── Kanban (mismo UI, tab dentro de Kanban)
├── Calendario (DueDateUtc, recurrencia)
├── Recordatorios (sincronización tabla reminders)
└── Supabase (sync offline-first)

Recordatorios
├── Calendario (ítems agregados)
├── Inbox (vencimientos)
├── Notificaciones (scheduler)
└── Polar (sync tabla reminders)

Calendario
├── Kanban (edita DueDateUtc vía drag-drop)
├── Polar (visualiza, no edita tareas/subtareas existentes)
└── Recordatorios (visualiza, no edita)

Papelera
├── Notas (documentos)
├── Kanban (tarjetas)
├── Archivos (VaultFile)
└── Recordatorios
```

---

## 5. Persistencia y flujo de datos

### 5.1 Archivos JSON globales (`%AppData%/Enki/`)

| Archivo | Servicio | Modelo | Evento |
|---------|----------|--------|--------|
| `settings.json` | `SettingsService` | `AppSettings` | — |
| `notifications.json` | `NotificationService` | `List<AppNotification>` | `NotificationAdded`, `NotificationsChanged` |
| `reminders.json` | `ReminderService` | `List<Reminder>` | `RemindersChanged` |
| `kanban.json` | `KanbanService` | `KanbanBoard` | `BoardChanged` |
| `polar-tasks.json` | `PolarTaskService` | `PolarTaskStore` | `DataChanged` |
| `dismissed-reminders.json` | `DismissedReminderService` | `List<DismissedReminder>` | — |

### 5.2 Archivos por vault (`<vault>/`)

| Ruta | Servicio | Modelo | Evento |
|------|----------|--------|--------|
| `<vault>/.enki/settings.json` | `VaultSettingsService` | `VaultSettings` | — |
| `<vault>/.enki/skills/*.md` | `SkillService` | `SkillFile` | `SkillsChanged` |
| `<vault>/.sessions/<nombre>/.session.json` | `SessionService` | `SessionManifest` | `ManifestError` |
| `<vault>/**/*.md` | `VaultService` | `NoteFile`, `VaultFile` | — |
| `<vault>/.trash/` | `VaultService` | — | — |

### 5.3 Reglas de serialización

- Fechas/horas en formato ISO 8601 o epoch millis para datos de sync.
- Números con `CultureInfo.InvariantCulture` cuando se serializan.
- Texto de UI nunca se serializa; los identificadores internos son strings invariantes (`Theme="Dark"`, `StartupPage="Dashboard"`).

---

## 6. Sistema de diseño y efectos

### 6.1 Filosofía visual

- **Monocromo**: blanco/negro/grises; únicos colores reales son rojo para error y verde para éxito/activo.
- **Esquinas rectas**: `CornerRadius="0"` salvo indicadores circulares (dots, pulse, thumb de toggle).
- **Bordes finos**: separación con `1 px` de blanco a bajas opacidades en vez de sombras.
- **Contraste tipográfico**: titulares grandes en negrita/condensado vs. micro-labels mono mayúsculas con tracking amplio.
- **Inversión al hover**: fondo transparente/borde blanco → fondo blanco/texto negro.

### 6.2 Paleta y recursos

Definidos en `styles/Colors.axaml`:

- Fondos: `#121212`, `#1A1A1A`, `#131313`.
- Superficies admin: `#1B1B1B`, `#141414`.
- Texto: `#F5F5F5` (primario), `#C6C6C6` (secundario), `#474747` (terciario/bordes).
- Éxito: `#4ADE80`; peligro: `#F87171`.
- Brushes de opacidad: `Ink05`…`Ink90` sobre blanco/negro.
- `SystemAccentColor` forzado a blanco/gris.

### 6.3 Tipografía

- **Display/UI**: Space Grotesk (pesos 300–900 empaquetados).
- **Mono**: JetBrains Mono Nerd Font Mono.
- **Iconos**: Material Symbols Outlined (trazo fino, sin relleno).
- Mayúsculas forzadas en nav, botones, badges y títulos de sección (vía converter o texto ya mayúsculo).
- `LetterSpacing` amplio (`0.2em`–`0.5em`) en micro-labels.

### 6.4 Efectos y animaciones catalogados

| Efecto | Dónde | Implementación |
|--------|-------|----------------|
| Hover lift de tarjetas | Notas, Kanban, Skills | `translateY(-4px)` + cambio de borde (`Styles.axaml:1003`) |
| Inversión de color en hover | Botones outline/ghost | Transición de fondo/borde/texto `150–300 ms` `CubicEaseOut` |
| Slide del panel lateral del editor | `EditorView.axaml` | `DoubleTransition` sobre ancho `200 ms` |
| Reveal de página | `MainWindowViewModel`, Calendario | `Opacity 0→1` + `TranslateY 8 px`, `200–300 ms` `CubicEaseOut` |
| Spinner de carga | Sync Polar, carga de PDF | `Classes="spin"`, rotación `1 s` continua (`Styles.axaml:1126`) |
| Toggle switch | Ajustes | Pista rectangular, thumb circular, easing con overshoot |
| Grain overlay | `MainWindow.axaml` | `DrawingBrush` vectorial a opacidad `0.05` |
| Glass sidebar | `MainWindow.axaml` | `ExperimentalAcrylicBorder` / fondo translúcido |
| Línea “ahora” del calendario | `CalendarView.axaml` | Refresco cada `60 s` |
| Pulse dot de estado | Admin/sidebar concepto | Animación de anillo expansivo |

---

## 7. Localización (i18n)

### 7.1 Estado actual

La infraestructura de localización ya está implementada:

- `LocalizationService` singleton con indexer y `SetLanguage` en caliente.
- `Strings.resx` fuente + `Strings.{culture}.resx` para 6 idiomas.
- `TrExtension` para XAML (`{loc:Tr Key}`).
- `Plural` helper para singular/plural.
- `RelativeTimeFormatter` unificado para tiempos relativos.
- `LanguageSetupView` en primer arranque.
- Selector de idioma en Ajustes › General.
- `RefreshAllLocalizedLabels` centraliza el refresco de labels computados.

### 7.2 Áreas ya cubiertas

- Texto de la mayoría de vistas `.axaml`.
- Notificaciones y mensajes de error.
- Labels de nav, dashboard, calendario, Kanban.
- Nombres de mes/día vía `CultureInfo.CurrentCulture.DateTimeFormat`.

### 7.3 Áreas con trabajo residual

- Algunos mensajes de excepción del sistema operativo se concatenan crudos (`VaultSetupViewModel.cs:64`), generando fugas de idioma fuera del control de Enki.
- URL del catálogo de skills y nombres de archivos por defecto siguen hardcoded.
- Cambio de idioma requiere que cada viewmodel nuevo sea incluido explícitamente en `RefreshAllLocalizedLabels`; olvidar uno causa texto mixto.

---

## 8. Supabase / Polar

### 8.1 Configuración

- `SupabaseConfig` lee `local.properties` junto al ejecutable.
- Si no existe, `IsConfigured=false` y todo Polar se deshabilita sin errores.

### 8.2 Autenticación

- `PolarAuthService.LinkAsync(email, pass)` llama `auth/v1/token?grant_type=password`.
- Almacena tokens en `AppSettings`.
- `EnsureFreshTokenAsync` refresca token antes de expirar.
- `SessionExpired` dispara si el refresh falla.

### 8.3 Sincronización

- `SupabaseRestClient`: `UpsertAsync` con `resolution=merge-duplicates,return=representation`.
- `SelectUpdatedSinceAsync` paginado (page size `500`).
- `PolarSyncService.SyncNowAsync` corre cada `25 s` con backoff exponencial hasta `5 min`.
- Push/pull de `task_lists`, `tasks`, `subtasks`, `reminders`.
- Soft deletes, dirty flags, LWW por `updated_at`.
- `RollRecurringTasks` corre localmente.

### 8.4 Estados de conectividad

- Sin credenciales: Polar oculto, sync no-op.
- Con credenciales y online: sincronización continua.
- Offline: backoff exponencial, notificación en transición éxito→fallo.

---

## 9. Tests

### 9.1 Cobertura existente

Tests xUnit en `enki/tests/enki.Tests/`:

- **Servicios**: Kanban, Polar, Reminders, Settings, Vault, Session, Skills, Notifications, Calendar, Editor, VaultBackup, WikiLink, DoNotDisturb.
- **ViewModels**: Dashboard, Kanban, Calendar, Editor, Notes, Trash, Settings, GlobalSearch, Reminder, LanguageSetup, NavItem, TagFilter.
- **Localization**: `LocalizationService`, `Plural`, `RelativeTimeFormatter`, paridad de claves `ResourceKeyParityTests`, `SupportedLanguagesTests`.
- **Vistas/Renderer**: `MarkdownLineParserTests`, `SessionGraphRendererTests`.

### 9.2 Vacíos identificados

- No hay tests de integración real contra Supabase (usa mocks/cliente interno).
- No hay tests de UI/efectos/animaciones.
- No hay tests de concurrencia/instancia única.

---

## 10. Gaps, fricciones y oportunidades de mejora

A continuación se listan solo mejoras sobre lo existente. No se propone ninguna funcionalidad nueva.

### 10.1 Localización y pulido lingüístico

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 1 | Fugas de idioma en mensajes de excepción del SO | Capturar tipos específicos (`UnauthorizedAccessException`, `IOException`, `PathTooLongException`) y mostrar mensajes propios 100 % localizados; usar `ex.Message` solo como fallback documentado. |
| 2 | Dependencia manual de `RefreshAllLocalizedLabels` | Auditar que todo viewmodel con labels computados esté en la lista; considerar un mecanismo de descubrimiento o al menos un test que falle si falta uno. |
| 3 | URL de catálogo de skills hardcoded | Mover a recurso localizado o configuración. |
| 4 | Inconsistencias de mayúsculas en fallbacks (“NUNCA” vs. “Nunca”) | Unificar vía `Common_Never` y similares. |

### 10.2 Sincronización Polar

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 5 | Polling de `25 s` en lugar de realtime | Documentar como decisión consciente; añadir indicador visual de “última sync hace X min” y botón de sync manual siempre visible. |
| 6 | `HttpClient` creado internamente | Inyectar `HttpClient` o `IHttpClientFactory` para tests de integración y control de timeouts. |
| 7 | Sin tests de integración real | Crear tests de contrato contra un Supabase local/emulado o mock estricto de las respuestas. |
| 8 | Backoff exponencial no expuesto en UI | Mostrar estado de sync (ok, syncing, error) y tiempo hasta próximo reintento. |

### 10.3 Notificaciones y sonido

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 9 | Sonido depende de comandos del SO | Empaquetar un asset de audio ligero y reproducirlo como fallback si los comandos del SO no están disponibles. |
| 10 | `Console.Beep` puede no sonar en algunos entornos | Implementar cadena de fallbacks: asset empaquetado → OS command → beep → silencio. |
| 11 | No hay control de volumen | Añadir slider de volumen del sonido de notificación. |

### 10.4 Calendario

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 12 | Drag & drop no cruza medianoche | Permitir arrastrar ítems entre días con ajuste automático de fecha. |
| 13 | Redimensionar solo funciona para Kanban | Extender a Polar/Recordatorios si el modelo lo permite (ej. duración de recordatorio). |
| 14 | Quick-create no edita tareas Polar existentes | Al menos mostrar un aviso claro de que solo crea nuevos ítems Kanban/Recordatorio. |
| 15 | Navegación del mini-calendario puede desfasarse | Sincronizar visualmente `ReferenceDate` con el día resaltado del mini-calendario. |

### 10.5 Editor de notas

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 16 | Preview de Markdown no visible en `EditorView.axaml` | Añadir vista previa renderizada (ya existen clases `Markdown*` en `views/editor/Markdown/`) o eliminar el código muerto si no se va a usar. |
| 17 | Toolbar fija sin personalización | Permitir colapsar/expandir grupos de toolbar; recordar estado. |
| 18 | Undo/redo por tab a veces confuso | Asegurar que cada tab conserve su propia pila de undo. |

### 10.6 Kanban

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 19 | Columnas core detectadas por posición | Hacer la detección más robusta (ej. por `IsCore` explícito) para evitar romper automatismos si el usuario reordena. |
| 20 | Borrado a papelera no conserva el orden exacto de la tarjeta | Guardar también `Order`/`Index` para restaurar en la misma posición. |
| 21 | Buscador interno no busca en subtareas | Extender búsqueda a título de subtareas. |

### 10.7 Sesiones y grafo

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 22 | Grafo en WebView depende de un runtime web | Evaluar renderizador nativo de Avalonia para reducir dependencias y mejorar rendimiento. |
| 23 | Carga async sin indicador de progreso | Mostrar skeleton/loader mientras se renderiza el grafo grande. |
| 24 | Manual link solo dentro de la misma sesión | Documentar claramente la limitación o permitir cross-session si el modelo lo soporta. |

### 10.8 Ajustes y tema

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 25 | Tema claro puede verse incompleto | Auditar todos los brushes y texturas para modo claro; si no es prioridad, deshabilitar la opción hasta estar pulida. |
| 26 | Zoom UI por vault no se aplica a popovers consistentemente | Verificar que todos los popovers usen `ShouldUseOverlayLayer=True` o equivalente. |
| 27 | “Restablecer ajustes” podría perder el idioma | Preservar `Language` igual que `VaultPath` en `ResetToDefaults`. |

### 10.9 IA Skills

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 28 | Nombre “IA Skills” sin motor de IA conectado | Renombrar a “Skills” o añadir un indicador de que son plantillas de sistema/prompts. |
| 29 | Catálogo externo hardcoded | Hacer configurable o localizar la URL. |
| 30 | Sin integración LLM | Si no se va a implementar, documentar explícitamente que las skills son plantillas estáticas. |

### 10.10 Arquitectura y calidad

| # | Problema | Mejora propuesta |
|---|-----------|------------------|
| 31 | `MainWindowViewModel` crea ~30 servicios/viewmodels | Seguir siendo composición manual, pero extraer helpers/factories para reducir tamaño y mejorar testabilidad. |
| 32 | `SingleInstanceGuard` no previene escrituras concurrentes | Añadir lock de archivos o mutex de named pipe para proteger JSON compartidos. |
| 33 | Modelos de vista con lógica de formateo duplicada | Centralizar más formatos en `RelativeTimeFormatter` y converters. |
| 34 | Diccionarios de recursos pueden crecer desordenados | Agrupar recursos por módulo (`Strings_Kanban_`, `Strings_Calendar_`) o por archivo `.resx` separado. |

---

## 11. Roadmap de mejora continua (sin nuevas funcionalidades)

> Cada fase deriva directamente de los gaps listados en el punto 10. Se indica entre paréntesis el número de gap que se cierra, para que sea trivial comprobar que el roadmap cubre todo el inventario.

### Fase 1 — Localización, ajustes y consistencia visual ✅ Completada
**Objetivo**: que el producto se sienta pulido en todos los idiomas soportados y que las opciones de personalización no rompan otras funcionalidades.

- **Pulir mensajes de error del sistema operativo**: capturar excepciones específicas (`UnauthorizedAccessException`, `IOException`, `PathTooLongException`) y mostrar textos 100 % localizados; usar `ex.Message` solo como fallback documentado. *(gap 1)* — ✅ `VaultSetupViewModel` ya lo hacía; se cerró el mismo hueco en `SettingsViewModel` (backup/exportar/importar ajustes) y `SkillsViewModel` (importar/exportar skills), con dos claves nuevas reutilizables `Common_Error_AccessDenied`/`Common_Error_IOFailure` traducidas a los 6 idiomas.
- **Robustecer el refresco de labels localizados**: auditar que todo viewmodel con labels computados esté incluido en `RefreshAllLocalizedLabels`; añadir un test que falle si falta alguno. *(gap 2)* — ✅ `EditorStatsViewModel` y `FindReplaceViewModel` declaraban `RefreshLocalizedLabels()` sin que nada los llamara; ahora `EditorViewModel.RefreshLocalizedLabels()` los invoca y `NotesViewModel.RefreshLocalizedLabels()` lo propaga a cada pestaña abierta. Nuevo test `RefreshLocalizedLabelsCoverageTests` en `enki.Tests` falla si una futura declaración se queda sin llamador.
- **Eliminar textos hardcoded**: mover la URL del catálogo de skills a recurso localizado o configuración; localizar nombres de archivos por defecto de skills si son visibles al usuario. *(gap 3, gap 29)* — ✅ URL centralizada en `SkillsViewModel.CommunitySkillsUrl` (única fuente); el dominio mostrado en `SkillsView`/`SettingsView` ahora se bindea a `CommunitySkillsUrlDisplay` en vez de repetir el literal en dos `.axaml`. La segunda mitad (nombres de archivo de skills por defecto) no aplica hoy: `docs/ia-skills/default-skills/` no contiene ningún `.md` en el repo todavía, así que no hay contenido empaquetado que traducir.
- **Unificar fallbacks de texto**: normalizar inconsistencias de mayúsculas (`Common_Never`, etc.) y asegurar que todos los fallbacks de tiempo relativo usen `RelativeTimeFormatter`. *(gap 4)* — ✅ Verificado: los 3 usos de "nunca" ya comparten `Common_Never` y los 5 sitios de tiempo relativo ya usan `RelativeTimeFormatter`. Sin cambios necesarios.
- **Tema claro/oscuro**: auditar todos los brushes y texturas para modo claro, o deshabilitar la opción hasta que esté completa. *(gap 25)* — ✅ Auditado brush por brush en `Colors.axaml` (ambos `ThemeDictionaries` completos) y verificado visualmente en vivo (Ajustes, Habilidades) tras cambiar a tema claro en caliente: legible y sin contrastes rotos. Se corrigieron los dos únicos colores hardcoded que quedaban (`ToggleSwitch` en `ControlThemes.axaml`, pista/thumb en `#353535`/`#919191`) para usar la rampa `Ink20`/`Ink40` y seguir la inversión de polaridad del tema.
- **Zoom UI**: verificar que todos los popovers y diálogos usen `ShouldUseOverlayLayer=True` o equivalente para escalar correctamente con el zoom del vault. *(gap 26)* — ✅ Confirmado que el único `Style Selector="Popup"` global en `Styles.axaml` cubre todos los popups de la app: no existe ningún `Flyout` ni ningún diálogo implementado como `Window` secundaria en `views/` (solo `MainWindow` es una `Window` real). Comentario del estilo actualizado para reflejar la auditoría.
- **Restablecer ajustes**: preservar `Language` al resetear, igual que ya se preserva `VaultPath`. *(gap 27)* — ✅ Ya implementado en `SettingsService.ResetToDefaults()`.
- **Skills**: renombrar "IA Skills" a "Skills" o añadir indicador de que son plantillas/prompts estáticos; documentar explícitamente si no hay motor LLM conectado. *(gap 28, gap 30)* — ✅ `Nav_Skills` y `Skills_Eyebrow`/`Skills_Page_Subtitle` dejaron de decir "AI/IA Skills" en los 6 idiomas (ya coincidían con `Settings_Nav_Skills`, que sí estaba corregido). Nueva clave `Skills_Page_StaticNotice` visible bajo el subtítulo de la página: "Son plantillas de prompt estáticas para usar con un asistente de IA externo — Enki no incluye ni se conecta a ningún motor de IA por sí misma."
- **Recursos localizados**: reorganizar `Strings.resx` por módulo o dividir en archivos `.resx` separados para evitar que el diccionario crezca desordenado. *(gap 34)* — ✅ Los 7 `.resx` (944 claves cada uno — una tanda de ~380 se había pegado en una sola línea ilegible) se reformatearon y reagruparon por módulo (`Common`, `Calendar`, `Skills`, etc.) con cabeceras de sección, una entrada por línea, mismo orden en los 7 archivos. Sin cambios de contenido: `dotnet test` (421/421, incluye `ResourceKeyParityTests`) en verde antes y después.

**Verificación**: `dotnet build` sin warnings y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 421/421 tras cada cambio. Además de los tests automatizados, se lanzó la app real (`dotnet run`) y se comprobó visualmente en caliente: cambio de tema oscuro→claro, panel de Habilidades con el aviso nuevo, y el dominio de la comunidad bindeado — todo antes de revertir esos ajustes a su estado original.

### Fase 2 — Sincronización Polar confiable ✅ Completada
**Objetivo**: que el usuario siempre sepa el estado de su sincronización y que el subsistema sea testable.

- **Testabilidad de red**: inyectar o exponer `HttpClient`/`IHttpClientFactory` en `SupabaseRestClient` para tests y control de timeouts. *(gap 6)* — ✅ `SupabaseRestClient` y `PolarAuthService` aceptan ahora un `HttpMessageHandler`/`baseUrl`/`anonKey` opcionales (mismo patrón de "parámetro de constructor opcional para tests" que `storageDirectory` en el resto de servicios); en producción, omitir los parámetros reproduce exactamente el comportamiento anterior vía `SupabaseConfig`. `PolarAuthService.IsLinked`/`IsConfigured` pasan de leer la clase estática `SupabaseConfig` directamente a una copia de instancia resuelta en el constructor, así una instancia de test puede forzar un estado "configurado" o "no configurado" sin depender de que exista un `local.properties` real en la máquina. Ambos clientes fijan además un timeout de `20 s` (antes usaban el valor por defecto de `HttpClient`, `100 s`), así un host caído falla rápido hacia el manejo offline existente en vez de colgar la UI.
- **Estado de sync visible en UI**: mostrar indicador de "sincronizado", "sincronizando", "error" y "offline"; mostrar tiempo hasta el próximo reintento cuando hay backoff. *(gap 8)* — ✅ Nuevo `PolarSyncService.Status` (`NotLinked`/`Synced`/`Syncing`/`Offline`/`Error`) centraliza en un solo sitio la lógica que las 3 pantallas con estado de sync (Recordatorios, Gestión de tareas de Polar, Ajustes › Cuenta) ya derivaban cada una por su lado. `NextSyncAttemptUtc` expone el instante exacto que el temporizador de `MainWindowViewModel` va a reintentar; nueva etiqueta "Reintentando en X min" (`Polar_Sync_NextRetryLabel` + `RelativeTimeFormatter.FormatUntil`, nuevo) visible en las 3 pantallas mientras el estado es `Offline`. Ajustes › Cuenta mostraba únicamente "Última sincronización" sin ningún error — ahora también muestra `SyncErrorLabel`, igual que Recordatorios y Kanban. El icono de sincronizar gira (`Classes.spin`) mientras `IsSyncing`/`IsSyncingNow` es verdadero en las 3 pantallas, coherente con el resto de spinners catalogados en §6.4.
- **Polling vs. realtime**: documentar la decisión de usar polling de `25 s` en lugar de realtime; mantener el botón de sincronización manual siempre visible. *(gap 5)* — ✅ Ya estaba documentada en los comentarios XML de `PolarSyncService`/`MainWindowViewModel` citando la fuente ("recomendado, no estrictamente obligatorio"); el backoff exponencial (antes solo recalculado en el tick automático del temporizador) ahora se recalcula desde el único manejador de `SyncCompleted`, así una sincronización manual (botón "Sincronizar ahora", siempre visible mientras la cuenta está vinculada) también ajusta el intervalo y republica `NextSyncAttemptUtc` en vez de esperar al siguiente tick automático.
- **Errores de red**: mejorar el manejo con mensajes localizados en lugar de textos crudos de excepción. *(gap 8)* — ✅ Ya cubierto por `PolarSyncService.TranslateSyncError` (401/403/429/5xx/timeout/sin conexión, cada uno con su clave localizada); verificado con tests de contrato nuevos que fuerzan cada código de estado contra el transporte falso y comprueban el mensaje resultante.
- **Tests de contrato**: crear tests contra un mock estricto de respuestas de Supabase o contra un Supabase local/emulado. *(gap 7)* — ✅ Nuevo `FakeHttpMessageHandler` (transporte HTTP falso y scriptable) más 3 ficheros de test nuevos: `SupabaseRestClientTests` (forma de la petición, headers, paginación de `SelectUpdatedSinceAsync`, rechazo del servidor), `PolarAuthServiceTests` (login, credenciales inválidas, fallo de conexión, timeout, refresco de token cercano a expirar, refresco rechazado → `Unlink`+`SessionExpired`) y `PolarSyncServiceContractTests` (ciclo push→pull completo, conflicto LWW perdido, 401, sin conexión, `Status` derivado). 22 tests nuevos, la cobertura de red que `PolarSyncServiceTests` marcaba explícitamente como "intentionally not covered" antes de esta fase.

**Verificación**: `dotnet build` sin warnings y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 443/443 (421 previos + 22 nuevos) tras cada cambio.

### Fase 3 — Calendario y Kanban más fluidos ✅ Completada
**Objetivo**: que las interacciones de arrastre, búsqueda y recuperación sean predecibles.

- **Drag & drop del calendario**: permitir arrastrar ítems entre días, ajustando automáticamente la fecha. *(gap 12)* — ✅ `CalendarViewModel.MoveItem` ya soporta Kanban/Polar/Reminder; Fase 3 asegura que el movimiento respeta la zona horaria local y conserva la hora del evento original al cambiar de día, con `CombineUtc` y redondeo a grano de 5 minutos.
- **Redimensionado del calendario**: extender el asa de duración a Polar y recordatorios si el modelo lo permite. *(gap 13)* — ✅ Nuevo campo `DurationMinutes` en `PolarTask` y `Reminder` (persistencia local, no wire Supabase); `CalendarItemViewModel.CanResize` ahora permite redimensionar Kanban/Polar/Reminder reales y con hora; `CalendarViewModel.ResizeItem` actualiza la duración en cada servicio (`KanbanService`, `PolarTaskService.UpdateTaskDuration`, `ReminderService.RescheduleDuration`) y `CalendarAggregationService` la expone para que la vista dibuje el asa.
- **Mini-calendario**: sincronizar visualmente el día resaltado con `ReferenceDate`. *(gap 15)* — ✅ `CalendarMiniDayViewModel` recibe `isSelected` directamente desde `CalendarViewModel.ReferenceDate`, y `CalendarMonthDayViewModel.IsSelected` refleja el mismo highlight en la cuadrícula principal, manteniendo la coherencia entre el mini-calendario lateral y la vista activa.
- **Quick-create del calendario**: aclarar que solo crea ítems Kanban/Recordatorio; evaluar abrir el editor inmediatamente tras crear. *(gap 14)* — ✅ `CalendarQuickCreateViewModel` muestra ahora un hint localizado (`Calendar_QuickCreate_TypeHint`) explicando que crea un ítem simple; el selector de tipo permite elegir Kanban/Polar/Reminder; tras crear, `SaveQuickCreateAndEditMore` abre el editor completo del ítem recién creado. `CreateFromQuickCreate` pasa `DurationMinutes` cuando el tipo lo soporta y emite una notificación `_notificationService.Push(...)` confirmando la creación.
- **Columnas core de Kanban**: hacer robusta la detección usando `IsCore` explícito en lugar de depender de la posición. *(gap 19)* — ✅ Nuevo `KanbanColumn.CoreColumnRole` (`None`/`Backlog`/`InProgress`/`Done`) con `JsonStringEnumConverter`; `KanbanService` asigna roles en la semilla y migra tableros legacy al cargar; `KanbanViewModel.ApplySubtaskAutomation` y `DashboardViewModel` usan `Role` en lugar de índices de columna, haciendo robustos los automatismos de subtareas y los contadores del dashboard.
- **Restauración desde papelera**: preservar `Order`/`Index` al borrar una tarjeta para poder restaurarla en la misma posición. *(gap 20)* — ✅ Nuevo `KanbanTrashedCard.CardIndex`; `KanbanViewModel.DeleteCard` y `CalendarViewModel.DeleteKanbanCard` guardan el índice original; `TrashViewModel.RestoreTask` reinserta la tarjeta en `CardIndex` cuando existe, preservando el orden.
- **Búsqueda interna de Kanban**: extender la búsqueda al título de subtareas. *(gap 21)* — ✅ `KanbanCardViewModel.MatchesSearch` ahora también compara los títulos de `Subtasks`, permitiendo encontrar tarjetas por el nombre de una subtarea.

**Verificación**: `dotnet build` sin warnings y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 455/455 (443 previos + 12 nuevos) tras cada cambio.

### Fase 4 — Editor de notas, sesiones y grafo ✅ Completada
**Objetivo**: resolver la fricción en las pantallas de contenido.

- **Vista previa de Markdown**: activar el renderizado o eliminar el código muerto si no se va a usar. *(gap 16)* — ✅ Verificado, sin cambios necesarios: las clases `Markdown*` de `views/editor/Markdown/` (`MarkdownLineParser`, `MarkdownElementGenerator`, `MarkdownLivePreviewColorizer`, `MarkdownBackgroundRenderer`) no son código muerto — están cableadas en `EditorView`'s constructor y activas en todo momento, renderizando en vivo (estilo Obsidian/Typora "live preview") negrita, cursiva, encabezados, citas, listas/tareas, código en línea, enlaces y reglas horizontales directamente sobre el texto fuente, ocultando los marcadores Markdown salvo en la línea donde está el cursor. Esto ya satisface "vista previa renderizada": añadir además un panel de preview separado (p. ej. vía WebView, como sugiere un comentario histórico en `WebViewHost.cs`) sería una funcionalidad nueva y redundante, no una mejora sobre lo existente — se dejó documentado aquí en vez de construirse.
- **Toolbar del editor**: permitir colapsar/expandir grupos de botones y recordar el estado. *(gap 17)* — ✅ Los 5 grupos de la barra de formato (estilo de texto, encabezados, listas, insertar, código) tienen ahora su propio chevron de colapso/expansión independiente (`EditorViewModel.ToggleToolbarGroupCommand`), con el mismo patrón de persistencia que `IsSidePanelVisible`: se refleja al instante en cualquier otra pestaña abierta vía `EditorPreferences` (estático compartido) y se guarda en `AppSettings` (`EditorToolbarTextStyleExpanded` y las 4 hermanas) para sobrevivir a un reinicio. 6 tests nuevos en `EditorViewModelToolbarGroupsTests`.
- **Undo/redo por tab**: auditar y garantizar que cada pestaña conserve su propia pila de undo/redo. *(gap 18)* — ✅ Auditado y confirmado correcto: cada `EditorViewModel` posee su propio `TextDocument` (`Document`), y AvaloniaEdit asigna un `UndoStack` propio a cada instancia de `TextDocument` — como `EditorView` reapunta la propiedad `Document` del único `TextEditor` compartido en cada cambio de pestaña (en vez de sobrescribir `Text` con una foto fija), el historial de deshacer/rehacer de cada nota queda intacto y aislado del resto. 5 tests nuevos en `EditorViewModelUndoStackTests` fijan este comportamiento como regresión (dos pestañas con `UndoStack` distintos; deshacer una no toca el contenido/estado de la otra; rehacer tras deshacer restaura el cambio).
- **Renderizador del grafo**: evaluar un renderizador nativo de Avalonia como alternativa al WebView. *(gap 22)* — ✅ Evaluado; decisión consciente de mantener WebView + sigma.js v1 (Canvas2D) + d3-force. `SessionGraphRenderer.cs` documenta una migración previa ya completada (de `force-graph`/WebGL a sigma/Canvas2D) motivada exactamente por las limitaciones del backend WebView de Linux (WPE solo soporta compositing por software) — el mismo tipo de restricción que un renderizador nativo tendría que resolver desde cero. Reimplementar en Avalonia el layout de fuerzas (d3-force), la resolución manual de click/drag, el sprite-cache del glow y el resto de la lógica ya afinada de este archivo sería un esfuerzo de varias semanas con alto riesgo de regresión, sin una ganancia clara: Avalonia seguiría necesitando un `Canvas`/`DrawingContext` propio con su propio motor de físicas, no "menos dependencias" en ningún sentido significativo. Se mantiene el WebView.
- **Indicadores de carga del grafo**: añadir skeleton/loader mientras se renderiza un grafo grande. *(gap 23)* — ✅ Nuevo overlay (`GraphLoadingOverlay` en `SessionsView.axaml`, spinner + etiqueta) visible desde el instante en que se llama a `NavigateToString` hasta que el documento del grafo reporta estar listo — el script inyectado en `SessionGraphRenderer` envía `post({ type: 'ready' })` en cuanto sigma tiene los nodos/aristas cargados (antes de arrancar el bucle de animación infinito, para no esperar a un evento que nunca "termina"). Temporizador de seguridad de 6 s en `SessionsView.axaml.cs` por si el mensaje `ready` nunca llega (error de JS, motor que falla en silencio).
- **Enlaces manuales**: documentar claramente la limitación de solo permitir enlaces dentro de la misma sesión, o ampliar el modelo si es viable. *(gap 24)* — ✅ Documentado en vez de ampliado: permitir enlaces manuales cruzando sesiones exigiría rediseñar el almacenamiento por sesión (`.session.json` por sesión, ids relativos) para un caso de uso que el propio código ya trata como error esperado (`HandleLinkToggled` ya lo rechazaba con un flash visual, `LinkRejected`) — ampliar el modelo sería funcionalidad nueva, fuera del alcance de este roadmap. En su lugar, `Sessions_LinkModeInstructions` ahora dice explícitamente "en la misma sesión" (los 6 idiomas) y el propio botón de "Modo enlace" muestra esa misma instrucción como tooltip antes de activarlo, no solo después.

**Verificación**: `dotnet build` sin warnings y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 466/466 (455 previos + 11 nuevos) tras cada cambio.

### Fase 5 — Notificaciones y sonido ✅ Completada
**Objetivo**: que el aviso sea consistente en todos los entornos.

- **Asset de audio**: empaquetar un sonido ligero como fallback cuando los comandos del sistema no estén disponibles. *(gap 9)* — ✅ Nuevo `assets/sounds/notification.wav` (chime de dos tonos corto, generado con fade in/out para evitar clics), capturado automáticamente por el glob `<AvaloniaResource Include="assets/**" />` ya existente en `enki.csproj` — mismo mecanismo que ya empaqueta las fuentes y los scripts vendored del grafo. Se lee en runtime con `AssetLoader.Open("avares://enki/assets/sounds/notification.wav")`, igual que `SessionGraphRenderer.VendoredScripts.Get`.
- **Cadena de fallbacks**: asset empaquetado → comando del SO → `Console.Beep` → silencio. *(gap 10)* — ✅ `NotificationSoundPlayer.Play` reescrito por completo: el asset embebido se extrae una vez a un fichero temporal y se reproduce con el mejor mecanismo disponible por plataforma (P/Invoke a `winmm.dll` en Windows sin ningún paquete NuGet nuevo — `System.Media.SoundPlayer` sigue sin estar disponible en un `net8.0` puro; `afplay`/`paplay`/`aplay` en macOS/Linux); si falla, cae al sonido nativo del sistema (mismo comando que había antes de esta fase); si también falla, cae a `Console.Beep`; si falla o el volumen es `0`, silencio. Cada eslabón se prueba con su propio `try/catch`, igual que el resto de "best effort" del archivo (`UrlOpener`/`FolderOpener`). El núcleo de decisión (`NotificationSoundPlayer.TryPlay`) es `internal` con cada eslabón inyectable como `Func<>`, cubierto por 6 tests nuevos en `NotificationSoundPlayerTests` que fijan el orden y el corte-en-el-primer-éxito sin depender de audio real.
- **Control de volumen**: añadir slider de volumen del sonido de notificación. *(gap 11)* — ✅ Nuevo `AppSettings.NotificationSoundVolume` (0–100, por defecto 70) con slider + etiqueta de porcentaje + botón "Probar" en Ajustes › Notificaciones (bajo el interruptor de sonido, deshabilitado visualmente cuando el sonido está apagado). `afplay -v`/`paplay --volume` sí escalan el volumen real del asset empaquetado; el pitido de Windows (winmm) y `Console.Beep` no exponen volumen en su propia API, así que ahí solo se respeta el "silencio en `0`", documentado explícitamente en el código en vez de simularse. Verificado en vivo lanzando la app real: el slider queda atenuado con el sonido apagado, se habilita al activarlo, la etiqueta de porcentaje se actualiza al arrastrar, y "Probar" reproduce sin excepciones ni cuelgues (log de la app limpio, asset extraído correctamente a temp).
- **Respeto de "No molestar"**: revisar todos los puntos de `_notificationService.Push` para que el modo silencie toasts y sonido pero conserve el log. — ✅ Auditados los 34 call-sites de `_notificationService.Push` en todo el código: todos pasan por el mismo pipeline `NotificationService.Push → NotificationAdded → NotificationCenterViewModel.OnNotificationAdded`, sin ningún bypass. La lógica ya era correcta (el log se persiste antes de evaluar "no molestar"; solo el toast y el sonido se saltan durante la ventana horaria), pero no tenía ningún test que la fijara como regresión — solo existía cobertura de la función pura `DoNotDisturbSchedule.IsWithin`. `NotificationCenterViewModel` ahora acepta `showToast`/`playSound` inyectables opcionales (mismo patrón que el `HttpMessageHandler` opcional de `SupabaseRestClient` en la Fase 2), y 3 tests nuevos en `NotificationCenterViewModelTests` prueban explícitamente: fuera de horario se muestran toast+sonido, dentro de horario se silencian ambos pero la notificación queda en el log y cuenta como no leída, y que el mute por preferencia (interruptores apagados) es independiente del horario.

**Verificación**: `dotnet build` sin warnings y `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 475/475 (466 previos + 9 nuevos) tras cada cambio. Además, se lanzó la app real (`dotnet run`) apuntando al vault de desarrollo, se navegó a Ajustes › Notificaciones, se activó el sonido, se movió el slider de volumen y se pulsó "Probar" — sin errores de binding ni excepciones en el log, con el asset embebido extrayéndose correctamente a un fichero temporal — y se restauró `settings.json` a su estado original al terminar.

### Fase 6 — Arquitectura, calidad y tests ✅ Completada
**Objetivo**: reducir deuda técnica sin romper el patrón manual de composición.

- **Reducir tamaño de `MainWindowViewModel`**: extraer helpers/factories que construyan servicios y viewmodels, manteniendo la composición manual. *(gap 31)* — ✅ El archivo (853 líneas) se dividió en 3 `partial class` sin cambiar ninguna semántica de wiring: `MainWindowViewModel.cs` (campos, propiedades observables, comandos de navegación/zoom/badges — 321 líneas), `MainWindowViewModel.Composition.cs` (constructor, `InitializeShell`, `ShowVaultSetupOrShell`, `ResolveStartupPage` — la raíz de composición manual de ~30 servicios/viewmodels) y `MainWindowViewModel.Callbacks.cs` (los ~20 callbacks de "navegar a X y abrir Y" que se pasan a los viewmodels de página). Al ser `partial`, los tres archivos siguen siendo la misma clase con acceso directo a todos los campos privados — no se introdujo DI ni un objeto contenedor nuevo, solo se organizó por responsabilidad.
- **Escrituras concurrentes**: proteger los JSON compartidos con locks de archivo o un mutex de named pipe. *(gap 32)* — ✅ Nuevo `JsonFileStore` (mutex con nombre por ruta, mismo mecanismo multiplataforma que ya usa `SingleInstanceGuard`, más escritura atómica vía fichero temporal + `File.Move(overwrite:true)`). `SingleInstanceGuard` solo impide que arranque una *segunda shell completa*; una segunda instancia sigue construyendo y ejecutando sus propios `NotificationService`/`ReminderService`/`KanbanService`/etc. "siempre vivos" antes de llegar a esa comprobación, así que los 7 servicios que persisten JSON compartido (`SettingsService`, `NotificationService`, `ReminderService`, `KanbanService`, `VaultSettingsService`, `PolarTaskService`, `DismissedReminderService`) migraron su `Load`/`Save` a `JsonFileStore.TryLoad`/`Save`. `SettingsService.Save` (el caso con más riesgo real, ya que dos ventanas comparten `settings.json`) usa `JsonFileStore.WithLock` para que su lectura-antes-de-escribir de `VaultPath` sea una sola sección atómica en vez de dos bloqueos separados.
- **Formateo duplicado**: centralizar más lógica de formateo en `RelativeTimeFormatter` y converters reutilizables. *(gap 33)* — ✅ Dos duplicaciones reales consolidadas: `FileSizeFormatter.Format` (idéntico bucle B/KB/MB/GB copiado tres veces en `VaultFile.SizeLabel`, `DashboardViewModel.FormatSize` y `SettingsViewModel.FormatBytes`, con pequeñas divergencias entre las tres) y `RelativeTimeFormatter.FormatAbsolute`/`FormatAbsoluteShort` (fecha absoluta "dd MMM yyyy, HH:mm" duplicada en `NotificationItemViewModel`, `ReminderListItemViewModel`, `EditorStatsViewModel`, `RemindersViewModel`, `SettingsViewModel` y `PolarTaskManagementViewModel`). Al consolidar se corrigió de paso una fuga de idioma real: `EditorStatsViewModel`, `ReminderListItemViewModel`, `SettingsViewModel` y `PolarTaskManagementViewModel` formateaban la fecha sin pasar `LocalizationService.Instance.CurrentCulture`, así que el nombre del mes caía silenciosamente a la cultura del hilo/SO en vez de respetar el idioma elegido en Enki — ahora los 6 sitios usan el mismo formateador con la cultura correcta siempre.
- **Tests de concurrencia/instancia única**: cubrir `SingleInstanceGuard` y acceso simultáneo a servicios. *(gap 32)* — ✅ `SingleInstanceGuard` ganó un overload interno `TryAcquire(string mutexName, out Mutex)` (mismo mecanismo que el `Acquire()` público, pero con nombre inyectable) para poder testear "primera instancia gana, la segunda lo detecta" sin depender del mutex real de proceso. Nuevo `SingleInstanceGuardTests` (3 tests, incluye una carrera de 16 hilos donde exactamente uno debe ganar) y `JsonFileStoreTests` (6 tests) que lanza 64 escritores concurrentes contra el mismo fichero y comprueba que el resultado siempre es JSON válido y completo — nunca una escritura a medias entrelazada.
- **Tests de UI básicos**: usar Avalonia.Headless para flujos críticos (crear nota, mover tarjeta Kanban, abrir calendario). *(roadmap §11)* — ✅ Nuevo proyecto `enki.UITests` (aislado de `enki.Tests`: cualquier test que toque un `DispatcherTimer` —varios constructores de viewmodel lo hacen— crea perezosamente un `Dispatcher` "portable" sin plataforma en el hilo que sea, y si eso ocurre antes de que `Avalonia.Headless`'s `AppBuilder.Setup()` corra en el mismo proceso, `Setup()` falla; aislar el proyecto evita pelear por el orden de ejecución de 480+ tests que no son suyos). `HeadlessApp` arranca una única instancia real de `App` en modo headless (carga los estilos/recursos reales de `Colors.axaml`/`ControlThemes.axaml`) y las 3 clases de test comparten una `[Collection(..., DisableParallelization = true)]` para que las tres corran siempre en el mismo hilo, el único que Avalonia headless reconoce como "hilo de UI". `NotesViewUiTests` renderiza `NotesView` real, ejecuta `NewNoteCommand` (el mismo comando del botón "+") y comprueba que el árbol visual —no solo la colección del viewmodel— muestra la nota nueva. `KanbanViewUiTests` renderiza `KanbanView` y llama `KanbanViewModel.MoveCard` (el mismo método que invoca `KanbanView.axaml.cs` al soltar una tarjeta) verificando que la tarjeta aparece bajo su nueva columna en el árbol visual real. `CalendarViewUiTests` renderiza `CalendarView`, confirma que arranca en vista Mes con una celda por día, y que `SetViewCommand` (el comando real de los botones Mes/Semana/Día/Agenda) reconstruye correctamente la cuadrícula de Semana.
- **Tests de zoom y tema**: verificar que popovers, diálogos y notificaciones respeten `UiZoomPercent` y el tema activo. *(roadmap §11, cierra gap 25/26 con un test automático)* — ✅ La Fase 1 auditó esto a mano y no dejó ningún test de regresión; ahora sí. `PopupZoomAndThemeAuditTests` (en `enki.Tests`, sin dependencia de Avalonia) escanea el árbol `.axaml` real y fija como regresión los dos invariantes de los que depende el zoom/tema: solo `MainWindow.axaml` declara un `<Window>` real (una segunda ventana quedaría fuera del `ScaleTransform` que envuelve el contenido) y ningún view usa `<Flyout>` (que resuelve su propio overlay sin pasar por el `Style Selector="Popup"` global que fuerza `ShouldUseOverlayLayer="True"`); un tercer test confirma que ese `Setter` sigue presente. `ThemeParityTests` parsea `Colors.axaml` y comprueba que los diccionarios `Dark`/`Light` declaran exactamente el mismo conjunto de `x:Key`, automatizando la auditoría "brush por brush" de la Fase 1.

**Verificación**: `dotnet build` sin warnings en `enki`, `enki.Tests` y el nuevo `enki.UITests`; `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` en 488/488 (475 previos + 13 nuevos: `JsonFileStoreTests`, `SingleInstanceGuardTests`, `PopupZoomAndThemeAuditTests`, `ThemeParityTests`) y `dotnet test enki/tests/enki.UITests/enki.UITests.csproj` en 6/6 nuevos, tras cada cambio.

---

## 12. Checklist de profesionalización

- [ ] Cero fugas de idioma en flujos principales.
- [ ] Cambio de idioma en caliente funciona en todas las pantallas.
- [ ] Cambio de tema claro/oscuro es usable o está deshabilitado hasta estarlo.
- [ ] Polar indica claramente si está sincronizado, desconectado o en error.
- [ ] Calendario refleja siempre el estado real de Kanban/Polar/Recordatorios.
- [ ] Papelera permite recuperar cualquier ítem sin pérdida de metadatos.
- [ ] Editor no pierde undo/redo ni autoguardado.
- [ ] Notificaciones respetan “No molestar” y tienen sonido consistente.
- [ ] Todos los popovers/dialogos respetan zoom y tema.
- [ ] Tests pasan (`dotnet test enki/tests/enki.Tests/enki.Tests.csproj`).

---

## 13. Resumen ejecutivo

Enki ya es una aplicación funcional y ambiciosa: notas, Kanban, Polar, Calendario, Recordatorios, Sesiones, Papelera, Búsqueda global, Notificaciones y Ajustes están implementados y conectados. La arquitectura MVVM y el patrón de persistencia unificada son sólidos.

Para sentirse como un producto profesional, el trabajo pendiente no es añadir más módulos, sino:

1. **Cerrar la localización** (mensajes de SO, labels computados, consistencia).
2. **Hacer que Polar se sienta confiable** (estado de sync visible, mejor manejo de errores, tests de contrato).
3. **Pulir las interacciones principales** (calendario drag-drop, editor preview, Kanban restore, grafo).
4. **Unificar la experiencia sensorial** (sonido, tema claro, popovers con zoom).
5. **Aumentar la cobertura de tests** en flujos críticos y concurrencia.

El documento `docs/agent-docs/internacionalizacion-i18n.md` ya cubre gran parte del plan lingüístico; este inventario lo complementa conectando cada módulo y priorizando el pulido por encima de la novedad.
