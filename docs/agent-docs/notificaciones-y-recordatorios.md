# Notificaciones y recordatorios

Documento de referencia para quien retome esta funcionalidad. Describe el diseño de la barra de título
de Enki y el estado de cada punto que originalmente quedaba pendiente — todos implementados ahora,
salvo lo anotado al final en "Posibles próximos pasos".

## El punto de partida: son dos cosas distintas

El pedido explícito fue no mezclarlas, y el diseño las mantiene separadas de punta a punta —
modelo, servicio, viewmodel y panel propios para cada una:

| | 🔔 Campana — **Notificaciones** | 📥 Bandeja — **Recordatorios** |
|---|---|---|
| Qué es | Log de eventos de la propia app | Vista derivada de tareas con fecha límite |
| Dirección | *Push*: algo pasó (vault cambiado, error de sync…) | *Pull*: algo vence pronto |
| Origen del dato | `AppNotification`, persistido en `notifications.json` | `KanbanCard.DueDateUtc`, ya vive en `kanban.json` |
| Se "lee" | Sí, por ítem (`IsRead`) | No hay estado propio de lectura — sí hay descarte, ver abajo |
| Acción al hacer click | Marca como leída | Navega a Kanban y abre el detalle de la tarjeta |

## Modelo y persistencia

- **`models/AppNotification.cs`** — `Id`, `Title`, `Message`, `Severity` (`Info/Success/Warning/Error`),
  `CreatedUtc`, `IsRead`.
- **`services/NotificationService.cs`** — mismo patrón load/save por JSON que `SettingsService` y
  `KanbanService` (`%AppData%/Enki/notifications.json`). `Push(title, message, severity)` inserta al
  principio, recorta a las últimas 50 y dispara `NotificationAdded` para que la UI se refresque sin
  polling. `MarkAsRead` / `MarkAllAsRead`. También consulta `SettingsService` para silenciar por
  severidad (ver "Preferencias en Ajustes" más abajo) — una notificación silenciada no se persiste ni
  se emite el evento, así que no deja rastro en la campana.
- Los **recordatorios no tienen modelo propio** — son una proyección de solo lectura sobre
  `KanbanService.Board` (`ReminderItemViewModel`, ver abajo). Es la decisión correcta mientras la única
  fuente de "cosas pendientes" sea Kanban: evita un segundo almacén que se puede desincronizar del real.
  Lo único persistido aparte del propio kanban es **qué recordatorios se descartaron** —
  `models/DismissedReminder.cs` + `services/DismissedReminderService.cs`
  (`%AppData%/Enki/dismissed-reminders.json`), keyed por `{ cardId, dueDateUtcSnapshot }` así una tarjeta
  reprogramada vuelve a aparecer en vez de quedar oculta para siempre.

## ViewModels (`viewmodels/shell/`)

- **`NotificationItemViewModel`** / **`NotificationCenterViewModel`** — colección + `UnreadCount` +
  comandos `Toggle`, `Close`, `MarkAllRead`, `SelectItem` (marca leída al click). Al recibir
  `NotificationAdded` también dispara un toast del sistema vía `ToastService` si
  `AppSettings.ShowDesktopToasts` está activo.
- **`ReminderItemViewModel`** (con `enum ReminderBucket { Overdue, Today, Soon }`) /
  **`ReminderInboxViewModel`** — recorre `_kanbanService.Board.Columns.SelectMany(...).Cards`, se queda
  con las que vencen dentro de `AppSettings.ReminderSoonWindowDays` (configurable en Ajustes, 7 días por
  defecto), descarta las que están en `DismissedReminderService`, las bucketiza y expone `OverdueCount`
  (solo vencidas — mismo criterio que `KanbanViewModel.OverdueCount`) como el número que se muestra en
  la insignia. Se recalcula al abrir el panel **y** en vivo, suscrito a `KanbanService.BoardChanged`
  (ver más abajo) — editar la fecha de una tarjeta desde Kanban con el panel abierto ya se refleja sin
  esperar al próximo `Toggle`. Comando `Dismiss` descarta un recordatorio individual sin tocar la
  tarjeta real.
- El bucketing distingue una fecha límite "de todo el día" (calendario solo, medianoche local) de una
  con hora específica: con hora, "vencida" se calcula contra el instante actual; sin hora, contra el
  día completo — ver `KanbanCard.IsPastDue`/`FormatDueLabel`, compartido con
  `KanbanCardViewModel.IsOverdue`/`DueDateLabel` para que tablero y bandeja usen siempre el mismo criterio.
- **`MainWindowViewModel`** instancia `NotificationCenter`/`ReminderInbox` siempre (no detrás de
  `IsVaultReady`, porque tanto el tablero kanban como el log de notificaciones son independientes de qué
  vault esté abierto) y cablea `OpenCardFromReminder` → `Navigate(AppPage.Kanban)` +
  `KanbanViewModel.OpenCardById`. También expone `NotificationsPanelEnabled`/`RemindersPanelEnabled`
  (ver Ajustes) para ocultar cada botón por separado sin afectar al otro.
- Disparadores de notificación conectados: cambio de vault (`MainWindowViewModel.OnVaultChanged`), vault
  guardado que ya no existe al iniciar (`MainWindowViewModel`, ctor), papelera vaciada y tarjeta
  restaurada (`TrashViewModel.ConfirmEmpty`/`RestoreTask`), sesión nueva creada
  (`SessionsViewModel.ConfirmCreateSession`). Cada uno sigue el mismo patrón: una línea
  `_notificationService.Push(...)` desde el viewmodel que ya tiene la referencia al servicio.

## Vista (`views/shell/MainWindow.axaml` + `styles/Styles.axaml`)

- Los dos botones viven en el mismo `StackPanel` flotante que ya dibujaba minimizar/maximizar/cerrar,
  pero no dependen de `UseCustomTitleBar` — ese flag solo oculta minimizar/maximizar/cerrar cuando el SO
  dibuja sus propios botones. Campana y bandeja tienen su propio toggle independiente en Ajustes
  (`NotificationsPanelEnabled`/`RemindersPanelEnabled`).
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

- **Paneles**: mostrar/ocultar campana y bandeja por separado; activar/desactivar el aviso emergente
  (toast) del sistema.
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

## Pruebas (`tests/enki.Tests/`)

Proyecto xUnit nuevo, referenciando `enki.csproj` vía `ProjectReference`. Cubre los dos puntos con
lógica no trivial que el propio diseño original señalaba como los más valiosos para testear:

- **`ViewModels/ReminderItemViewModelTests.cs`** — bucketing (`Overdue/Today/Soon`), incluyendo el caso
  de fecha sin hora (comparación de todo el día) contra fecha con hora (comparación de instante).
- **`Services/NotificationServiceTests.cs`** — `Push` inserta al principio, recorta a 50, persiste entre
  instancias y dispara `NotificationAdded`.

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
- **Snooze con plazo elegible** (hoy el descarte es "hasta que cambie la fecha"; podría sumarse "ocúltalo
  N días" sin esperar a un cambio real de la tarjeta).
- Notificaciones del **SO nativo** (más allá del toast in-app de `WindowNotificationManager`) si se
  justifica para eventos realmente urgentes con la app en segundo plano.
