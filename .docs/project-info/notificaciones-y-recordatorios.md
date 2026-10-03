# Notificaciones y recordatorios

Documento de referencia para quien retome esta funcionalidad. Describe el diseño de la barra de título
de Enki y el estado de cada punto que originalmente quedaba pendiente — todos implementados ahora,
salvo lo anotado al final en "Posibles próximos pasos".

## El punto de partida: son dos cosas distintas

El pedido explícito fue no mezclarlas, y el diseño las mantiene separadas de punta a punta —
modelo, servicio, viewmodel y panel propios para cada una:

| | 🔔 Campana — **Notificaciones** | 📥 Bandeja — **Recordatorios** |
|---|---|---|
| Qué es | Log de eventos de la propia app | Vista derivada de "qué vence pronto" |
| Dirección | *Push*: algo pasó (vault cambiado, error de sync…) | *Pull*: algo vence pronto |
| Origen del dato | `AppNotification`, persistido en `notifications.json` | `KanbanCard.DueDateUtc` (`kanban.json`) **+** `Reminder.NotifyAtUtc` (`reminders.json`) |
| Se "lee" | Sí, por ítem (`IsRead`) | No hay estado propio de lectura — sí hay descarte, ver abajo |
| Acción al hacer click | Marca como leída | Navega a Kanban (tarjeta) o a Recordatorios (recordatorio propio) y abre el detalle |

**Actualización:** la bandeja 📥 originalmente solo proyectaba tarjetas de Kanban (ver sección siguiente,
que describe ese diseño inicial). Como el icono está literalmente rotulado "Recordatorios"
(`Reminder_InboxTooltip`/`Reminder_InboxTitle`) y desde entonces se añadió una página "Recordatorios" con
recordatorios propios de verdad (`models/Reminder.cs`, independientes de Kanban), la bandeja ahora también
incluye esos recordatorios — ver `ReminderItemViewModel.SourceKind` y
`ReminderInboxViewModel.Reload`, que mezcla ambas fuentes por fecha antes de bucketizar. Un recordatorio
que se dispara sigue empujando además un aviso a la campana (comportamiento sin cambios, es el "ding"
puntual de que algo acaba de pasar), pero mientras siga activo y no completado también aparece listado en
la bandeja 📥 hasta que se completa o se descarta, igual que una tarjeta de Kanban vencida.

**Actualización (iconos y bandeja de entrada):** la barra de título tiene ahora cuatro botones de
contenido, en este orden fijo: **buscar · notificaciones · recordatorios · bandeja de entrada**. El panel
de recordatorios descrito arriba pasó de `InboxOutline` a `CalendarClockOutline`, el mismo icono que la
entrada "Recordatorios" del sidebar (`SidebarSectionCatalog`), para que el botón y la página a la que
lleva se reconozcan como la misma cosa; `InboxOutline` quedó libre y es ahora el icono del botón nuevo.

Ese botón nuevo, la **bandeja de entrada**, no es un panel más de lectura: es la captura rápida. Abre un
popup con el mismo `QuickCaptureViewModel` que la mini-ventana global (`Ctrl+Alt+Space`) y que la entrada
"Captura rápida" de la paleta de comandos — un solo viewmodel, un solo borrador, un solo destino
recordado (`VaultSettings.QuickCaptureDestination`), así que lo escrito en cualquiera de las tres puertas
sigue ahí al abrir otra. La idea acaba donde ya acababa: nota en `Inbox/` del vault o tarjeta de Kanban,
vía `QuickCaptureService`. Es un popup y no una ventana porque el gesto nace desde la ventana de Enki,
que ya está a la vista. Se oculta por separado con `AppSettings.InboxPanelEnabled` (Ajustes ›
Notificaciones), igual que la campana y los recordatorios.

## Modelo y persistencia

- **`models/AppNotification.cs`** — `Id`, `Title`, `Message`, `Severity` (`Info/Success/Warning/Error`),
  `CreatedUtc`, `IsRead`.
- **`services/NotificationService.cs`** — mismo patrón load/save por JSON que `SettingsService` y
  `KanbanService` (`%AppData%/Enki/notifications.json`). `Push(title, message, severity)` inserta al
  principio, recorta a las últimas 50 y dispara `NotificationAdded` para que la UI se refresque sin
  polling. `MarkAsRead` / `MarkAllAsRead`. También consulta `SettingsService` para silenciar por
  severidad (ver "Preferencias en Ajustes" más abajo) — una notificación silenciada no se persiste ni
  se emite el evento, así que no deja rastro en la campana.
- Los **ítems de la bandeja no tienen modelo propio** — cada uno es una proyección de solo lectura
  (`ReminderItemViewModel`, ver abajo) sobre una de dos fuentes reales: `KanbanService.Board`
  (`KanbanCard.DueDateUtc`) o `ReminderService.ActiveReminders` (`Reminder.NotifyAtUtc`, el modelo propio
  de la página Recordatorios, `models/Reminder.cs`). Evita un tercer almacén que se pueda desincronizar de
  cualquiera de los dos reales. Lo único persistido aparte de kanban/reminders es **qué ítems se
  descartaron** —
  `models/DismissedReminder.cs` + `services/DismissedReminderService.cs`
  (`%AppData%/Enki/dismissed-reminders.json`), keyed por `{ cardId, dueDateUtcSnapshot }` así una tarjeta
  reprogramada vuelve a aparecer en vez de quedar oculta para siempre.
- **Aplazamientos (T5)** — `models/SnoozedReminder.cs` + `services/SnoozedReminderService.cs`
  (`<vault>/.tasks/snoozed-reminders.json`), gemelo exacto del almacén de descartes y con el mismo tope
  de entradas: uno guarda "no me lo vuelvas a enseñar", el otro "vuélvemelo a enseñar a tal hora". Keyed
  por `{ sourceId, dueUtcSnapshot }` por el mismo motivo, y con un `Count` de aplazamientos consecutivos.

## ViewModels (`viewmodels/shell/`)

- **`NotificationItemViewModel`** / **`NotificationCenterViewModel`** — colección + `UnreadCount` +
  comandos `Toggle`, `Close`, `MarkAllRead`, `SelectItem` (marca leída al click). Al recibir
  `NotificationAdded` también dispara un toast del sistema vía `ToastService` si
  `AppSettings.ShowDesktopToasts` está activo.
- **`ReminderItemViewModel`** (con `enum ReminderBucket { Overdue, Today, Soon }` y
  `enum ReminderItemSourceKind { KanbanCard, Reminder }`) / **`ReminderInboxViewModel`** — recorre
  `_kanbanService.Board.Columns.SelectMany(...).Cards` **y** `_reminderService.ActiveReminders`, se queda
  con lo que vence dentro de `AppSettings.ReminderSoonWindowDays` (configurable en Ajustes, 7 días por
  defecto), descarta lo que está en `DismissedReminderService` (keyed por `SourceId`, común a ambas
  fuentes), mezcla y bucketiza ambas listas por fecha, y expone `OverdueCount` (solo vencidas — mismo
  criterio que `KanbanViewModel.OverdueCount`) como el número que se muestra en la insignia. `OpenCard`
  despacha por `SourceKind`: una tarjeta abre Kanban, un recordatorio propio abre la página Recordatorios
  (`RemindersViewModel.OpenReminderById`). Se recalcula al abrir el panel **y** en vivo, suscrito a
  `KanbanService.BoardChanged` y `ReminderService.RemindersChanged` (ver más abajo) — editar la fecha de
  una tarjeta desde Kanban, o crear/completar/borrar un recordatorio desde su página, con el panel abierto
  ya se refleja sin esperar al próximo `Toggle`. Comando `Dismiss` descarta un ítem individual sin tocar la
  tarjeta real.
- El bucketing distingue una fecha límite "de todo el día" (calendario solo, medianoche local) de una
  con hora específica: con hora, "vencida" se calcula contra el instante actual; sin hora, contra el
  día completo — ver `KanbanCard.IsPastDue`/`FormatDueLabel`, compartido con
  `KanbanCardViewModel.IsOverdue`/`DueDateLabel` para que tablero y bandeja usen siempre el mismo criterio.
- **`MainWindowViewModel`** instancia `NotificationCenter`/`ReminderInbox` siempre (no detrás de
  `IsVaultReady`, porque tanto el tablero kanban como el log de notificaciones son independientes de qué
  vault esté abierto) y le pasa dos callbacks de navegación: `OpenKanbanCard` → `Navigate(AppPage.Kanban)` +
  `KanbanViewModel.OpenCardById` para ítems de tipo `KanbanCard`, y `OpenReminder` →
  `Navigate(AppPage.Reminders)` + `RemindersViewModel.OpenReminderById` para ítems de tipo `Reminder`.
  También expone `NotificationsPanelEnabled`/`RemindersPanelEnabled` (ver Ajustes) para ocultar cada botón
  por separado sin afectar al otro.
- Disparadores de notificación conectados: cambio de vault (`MainWindowViewModel.OnVaultChanged`), vault
  guardado que ya no existe al iniciar (`MainWindowViewModel`, ctor), papelera vaciada y tarjeta
  restaurada (`TrashViewModel.ConfirmEmpty`/`RestoreTask`), sesión nueva creada
  (`SessionsViewModel.ConfirmCreateSession`). Cada uno sigue el mismo patrón: una línea
  `_notificationService.Push(...)` desde el viewmodel que ya tiene la referencia al servicio.

## Vista (`views/shell/MainWindow.axaml` + `styles/Styles.axaml`)

- Los botones viven en el mismo `StackPanel` flotante que ya dibujaba minimizar/maximizar/cerrar,
  pero no dependen de `UseCustomTitleBar` — ese flag solo oculta minimizar/maximizar/cerrar cuando el SO
  dibuja sus propios botones. Campana, recordatorios y bandeja de entrada tienen cada uno su toggle
  independiente en Ajustes
  (`NotificationsPanelEnabled`/`RemindersPanelEnabled`/`InboxPanelEnabled`).
- Cada botón es un `Button.titlebar-btn` con un `Border.titlebar-badge` superpuesto — cuadrado, no
  redondo, visible solo cuando el contador es > 0 (`CountToBoolConverter`).
- El panel emergente reutiliza el patrón `Popup` + `Border.dropdown-popover` que ya usaba el selector de
  fecha del editor de tarjetas kanban (`Border.calendar-popover`).
- Cada ítem de la bandeja de recordatorios tiene un botón "✕" anidado (mismo patrón que los botones
  editar/eliminar dentro de `Button.kanban-card`) que dispara `ReminderInboxViewModel.DismissCommand`
  sin abrir la tarjeta.

## Preferencias en Ajustes (`viewmodels/settings/`, sección "Notificaciones")

Nueva sección en `SettingsViewModel`/`SettingsView.axaml`, mismo patrón de las demás
(`SettingsSectionViewModel` + `partial void On<Prop>Changed` que escribe a `AppSettings` y guarda):

- **Paneles**: mostrar/ocultar campana, recordatorios y bandeja de entrada por separado;
  activar/desactivar el aviso emergente (toast) del sistema.
- **Interruptor global**: `NotificationsEnabled` está activo de forma predeterminada. Al apagarlo,
  `NotificationService` descarta todos los avisos nuevos antes de guardarlos o emitir eventos; por
  tanto tampoco aparecen toasts ni suenan avisos. El historial anterior permanece disponible y las
  preferencias de categoría, toast y sonido se conservan para cuando se vuelva a activar.
- **Silenciar por categoría**: un toggle por severidad (`Info/Success/Warning/Error`) — una categoría
  silenciada ni se guarda ni aparece en la campana (ver `NotificationService.Push`/`IsMuted`).
- **Recordatorios**: slider para la ventana de "próxima a vencer" (`ReminderSoonWindowDays`, 1–30 días).

## Toast nativo (`services/ToastService.cs`)

Envoltorio sobre `Avalonia.Controls.Notifications.WindowNotificationManager`, adjuntado de forma
perezosa a `App.MainWindowRef` (mismo punto de entrada que ya usa `SettingsViewModel.ChangeVaultFolder`
para llegar a un `TopLevel` desde un viewmodel). Se dispara desde
`NotificationCenterViewModel.OnNotificationAdded` cuando `ShowDesktopToasts` está activo, así un evento
urgente no depende de que el usuario mire la campana.

## Recordatorios con hora (`viewmodels/kanban/KanbanCardEditorViewModel.cs`)

El calendario del editor de tarjetas sigue eligiendo solo el día; un `ToggleSwitch` "Agregar hora"
opcional (visible una vez elegida la fecha) suma `DueHour`/`DueMinute` encima de ese día
(`CombinedDueDate`) en vez de reemplazarlo — apagarlo vuelve a dejar la fecha a medianoche local
("todo el día", el comportamiento de siempre). `KanbanCard.DueDateUtc` ya soportaba hora (`DateTime?`);
lo que faltaba era la UI, y el criterio de "vencida" en `KanbanCard.IsPastDue` para no tratar una
tarjeta con hora futura de hoy como si ya hubiera vencido.

## Aplazar recordatorios (T5)

"Aplazar" está en la fila de la bandeja de recordatorios (icono `AlarmSnooze`, junto al de descartar) y en
la fila del centro de notificaciones de los avisos nacidos de un vencimiento. Ofrece dos duraciones rápidas
configurables (10 min y 1 h de fábrica), "Esta tarde" (hoy a las 18:00), "Mañana" (mañana a las 09:00) y
"Personalizado…" (minutos a mano, de 1 minuto a 7 días).

**Aplazar no es reprogramar.** Un aplazamiento nunca toca la entidad de origen: ni la fecha límite de una
`KanbanCard`, ni el `NotifyAtUtc` de un `Reminder`, ni la serie de uno recurrente. Se registra aparte en
`SnoozedReminderService` y se resuelve contra la entidad real cada vez que hace falta. El documento de la
funcionalidad proponía reescribir `NotifyAtUtc` para los recordatorios propios y usar el almacén solo para
tarjetas; se descartó porque su propio criterio de aceptación —"aplazar una ocurrencia de un recordatorio
recurrente no deberá alterar las ocurrencias futuras de la serie"— lo contradice: cuando un recordatorio
recurrente se dispara, `ReminderService.Fire` ya ha avanzado `NotifyAtUtc` a la siguiente ocurrencia, así
que reescribirlo *sería* mover la serie. Un solo motor para los dos orígenes es además el que deja el
aplazamiento como algo puramente local, que es lo que decidió §9 del documento de funcionalidades.

**Piezas:**

- `services/SnoozeSchedule.cs` — lógica pura (sin estado, sin disco, sin UI, como `DoNotDisturbSchedule`):
  traduce una `SnoozeOption` al instante UTC concreto. Las duraciones relativas son aritmética de UTC; las
  opciones con hora de pared ("esta tarde", "mañana") se calculan en hora local y se convierten al final,
  por el mismo motivo que `PolarTaskService.CalculateNextDueDate` — "mañana a las 09:00" sigue siendo las
  09:00 de reloj aunque entre medias haya una transición de horario de verano. La hora local inexistente
  del salto de primavera se desplaza en pasos de 15 minutos en vez de lanzar.
- `DoNotDisturbSchedule.NextInstantOutside` — si el instante elegido cae en la franja "no molestar", el
  aviso se difiere al final de la franja en vez de tragárselo.
- `services/SnoozedReminderService.cs` — persistencia, `PopDue(nowUtc)` (devuelve y retira los vencidos,
  mismo contrato que `ReminderService.PopDueReminders`) y `PruneWhere` para soltar los huérfanos.
- `MainWindowViewModel.CheckDueSnoozes` — en el mismo ciclo de 30 s del temporizador que ya existía, ni un
  segundo temporizador ni un segundo motor de avisos. Antes de reavisar revalida cada aplazamiento contra
  los almacenes reales: origen borrado, completado o con la fecha cambiada ⇒ el aplazamiento se descarta.
- `AppNotification.SnoozeTarget` — a qué vencimiento apunta un aviso, para que el centro de notificaciones
  pueda ofrecer "Aplazar" sin adivinar el origen a partir del texto. Null en todo lo demás: un fallo de
  sincronización no es algo que se pueda posponer.

En la bandeja, un ítem aplazado no desaparece: sale del recuento de vencidos (cubo `ReminderBucket.Snoozed`),
se ordena al final, se atenúa y muestra "Aplazado hasta 15:40" y, a partir del segundo, el contador de
aplazamientos. No hay tope de aplazamientos consecutivos (§9), pero el contador los hace visibles.

## Pruebas (`tests/enki.Tests/`)

Proyecto xUnit nuevo, referenciando `enki.csproj` vía `ProjectReference`. Cubre los dos puntos con
lógica no trivial que el propio diseño original señalaba como los más valiosos para testear:

- **`ViewModels/ReminderItemViewModelTests.cs`** — bucketing (`Overdue/Today/Soon`), incluyendo el caso
  de fecha sin hora (comparación de todo el día) contra fecha con hora (comparación de instante).
- **`Services/NotificationServiceTests.cs`** — `Push` inserta al principio, recorta a 50, persiste entre
  instancias y dispara `NotificationAdded`.
- **`Services/SnoozeScheduleTests.cs`** — la aritmética de "Aplazar" con zona horaria inyectada
  (Europe/Madrid, para que el resultado no dependa de la máquina): duraciones, "esta tarde" antes y después
  de la hora, rodar al día siguiente, DST de primavera, hora local inexistente, franja "no molestar" y
  valores fuera de rango.
- **`Services/SnoozedReminderServiceTests.cs`** — aplazar, renovar contando el repetido, `PopDue`,
  cancelar, podar huérfanos, persistir entre instancias y el tope de 100 entradas.
- **`ViewModels/ReminderInboxViewModelTests.cs`** — que aplazar no toque la tarjeta ni la serie de un
  recordatorio recurrente, el valor rápido configurado, el personalizado, el contador, cancelar, que
  descartar tire el aplazamiento pendiente, un solo menú abierto a la vez y el orden de la lista.

`NotificationService`/`SettingsService` aceptan un parámetro opcional `storageDirectory` (por defecto
`null` → `%AppData%/Enki`, igual que antes) para que los tests apunten a una carpeta temporal en vez de
tocar los ficheros reales del desarrollador. Correr `dotnet test enki/tests/enki.Tests/enki.Tests.csproj`.

## Cómo extender: agregar un nuevo punto de notificación

Desde cualquier viewmodel que ya reciba (o pueda recibir) `NotificationService` por constructor —
mismo patrón que `SettingsService`/`KanbanService` se pasan hoy:

```csharp
_notificationService.Push(
    title: "Papelera vaciada",
    message: "Se eliminaron 4 notas permanentemente.",
    severity: NotificationSeverity.Warning);
```

`Push` persiste y dispara `NotificationAdded` (salvo que la severidad esté silenciada en Ajustes), así
que cualquier panel abierto — y el toast, si está activo — se refresca solo, sin tocar
`MainWindowViewModel`.

## Posibles próximos pasos

Ninguno bloqueante; ideas para seguir puliendo si hace falta más adelante:

- **Silenciar por origen**, no solo por severidad — p. ej. "no avisarme de sesiones nuevas" sin apagar
  también los errores de vault, que hoy comparten la misma severidad `Success`/`Error`.
- Notificaciones del **SO nativo** (más allá del toast in-app de `WindowNotificationManager`) si se
  justifica para eventos realmente urgentes con la app en segundo plano.

## Listas de actividad: búsqueda y estados

Las páginas de Notificaciones y Recordatorios separan la cabecera, las acciones globales y los filtros.
Reutilizan la tipografía, los segmentos, los botones y los colores del tema; las filas tienen separadores
finos y un fondo sutil para elementos sin leer, sin desplazamiento al pasar el puntero.

- **Notificaciones:** búsqueda por título o mensaje, filtros Todos / Sin leer y orden de más reciente a
  más antiguo. Abrir una notificación la marca como leída; su detalle sigue abierto aunque deje de
  cumplir el filtro Sin leer.
- **Recordatorios:** búsqueda por título o descripción y filtros Todos / Pendientes / Vencidos /
  Completados. Los pendientes se ordenan por fecha de aviso y los completados quedan al final. El botón
  de cada fila permite completar o volver a pendiente sin abrir el detalle. El estado completado se
  identifica expresamente y no se presenta como programado.
- El contador muestra resultados visibles frente al total. Una búsqueda sin coincidencias ofrece
  restablecer los filtros y se distingue de una colección vacía. Las acciones de marcar todos como
  leídos y limpiar siguen afectando a la colección completa, independientemente del filtro; limpiar
  conserva su confirmación.
- El formulario de creación dispone de más ancho y los detalles permiten desplazar el contenido
  manteniendo las acciones en el pie. Las etiquetas nuevas están traducidas a todos los idiomas.

`VisibleItems` es una proyección de los mismos objetos de `Items`, no otro almacén. Los filtros y la
búsqueda se conservan al recargar. Recordatorios escucha `ReminderService.RemindersChanged` para
actualizar la lista y el detalle cuando otra pantalla modifica las entidades.

Validación automatizada: `ActivityListViewModelTests` cubre búsqueda, orden y transiciones de estado;
`ActivityViewsUiTests` comprueba los enlaces de comandos y filas con títulos largos a 720 px de ancho.
