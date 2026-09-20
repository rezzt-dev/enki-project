# Ajustes › General — Zona de peligro: continuidad y protección de datos

> **Alcance**: este documento describe, tal como está implementado hoy, todo lo relacionado con
> proteger los datos del usuario desde Ajustes — tamaño y acceso al vault, copia de seguridad manual,
> restauración y copia automática, exportar/importar `settings.json`, restablecer ajustes por defecto, y el ajuste
> "preguntar antes de borrar" que gobierna las confirmaciones de borrado en el resto de la app. Todo
> vive en la tarjeta "Zona peligrosa" al final de **Ajustes › General**
> (`enki/src/enki/views/settings/SettingsView.axaml:175-261`), salvo "preguntar antes de borrar", cuyo
> efecto se dispara desde otros módulos.
>
> **Fuera de alcance**: el resto de Ajustes › General (carpeta del vault, idioma, página de inicio,
> scrollbar del Dashboard, registro de depuración) no está citado hacia este documento en el código y
> no se describe aquí.
>
> **Fuente de verdad**: el código vence a este documento. Verificado contra
> `enki/src/enki/viewmodels/settings/SettingsViewModel.cs`, `enki/src/enki/models/AppSettings.cs`,
> `enki/src/enki/services/SettingsService.cs`, `enki/src/enki/services/VaultBackupService.cs` y los seis
> `RequestDelete*` que consultan `ConfirmBeforeDelete`.

## §2 — Tamaño del vault y acceso a su carpeta

- **Tamaño del vault** (`VaultSizeLabel`, `SettingsViewModel.cs:101-106`): reutiliza
  `VaultService.GetVaultSizeBytes()` — el mismo cálculo que ya usa el Dashboard — formateado con
  `FileSizeFormatter.Format`. Se recalcula al construir la vista, cada vez que se cambia de carpeta
  (`ChangeVaultFolderCommand`) y de forma reactiva cuando `VaultService.VaultSizeChanged` se dispara
  desde el recálculo en segundo plano de `VaultService` (el evento llega en un hilo de fondo y se
  marshala al hilo de UI con `Dispatcher.UIThread.Post`, `SettingsViewModel.cs:427`).
- **Abrir carpeta del almacén** (`OpenVaultFolderCommand`): abre `VaultService.VaultPath` en el
  explorador de archivos del sistema vía `FolderOpener.Open`, mismo mecanismo que "Abrir carpeta de
  logs" en Diagnóstico.

Cambiar de carpeta de vault en sí (`ChangeVaultFolderCommand`, selector de carpeta + `SetVaultPath`) es
un ajuste de la tarjeta "Carpeta del almacén" más arriba en la misma página — no forma parte de la zona
de peligro ni está citado hacia este documento, pero conviene saber que también recalcula
`VaultSizeLabel` y dispara `_onVaultChanged()`.

## Copia de seguridad manual

`CreateBackupCommand` (`SettingsViewModel.cs:1066-1132`) abre un selector "Guardar como" con nombre
sugerido `Enki-Backup-{yyyy-MM-dd-HHmm}.zip` y delega en `VaultBackupService.CreateBackup(destino)`.

El zip resultante empaqueta dos cosas (`VaultBackupService.cs:40-75`):

- `vault/` — todo el contenido de la carpeta del vault activo, recursivo.
- `app-data/` — todos los `*.json` de nivel superior en `%AppData%/Enki` (`kanban.json`,
  `settings.json`, `notifications.json`, `reminders.json`, `polar-tasks.json`,
  `dismissed-reminders.json`, etc.).

**`settings.json` nunca se copia tal cual**: dentro del backup se reescribe con
`PolarAccessToken`/`PolarRefreshToken` puestos a `null` antes de comprimirlo
(`TryAddRedactedSettingsFile`, `VaultBackupService.cs:159-181`) — el resto de los `*.json` de
`app-data/` sí se copian sin modificar, porque no contienen credenciales. La misma redacción la aplica
`SettingsService.BuildRedactedExportJson()` para "Exportar ajustes" más abajo; son dos implementaciones
independientes del mismo criterio, no la misma llamada.

Un fichero individual bloqueado o ilegible **no aborta el resto del backup**: se omite y su ruta
relativa dentro del zip se añade a `VaultBackupResult.FailedEntries`. El comando reacciona así:

- `FailedEntries` vacío → notificación de éxito.
- `FailedEntries` no vacío → notificación de aviso ("copia de seguridad incompleta"), con el zip
  igualmente creado pero sin esos ficheros.
- `UnauthorizedAccessException`/`IOException` al escribir el propio zip → notificación de error con
  mensaje traducido.
- Cualquier otra excepción → notificación de error con `ex.Message` tal cual (sin traducir — limitación
  documentada, no un bug: el mensaje de una excepción de plataforma no traducida por tipo puede llegar
  en el idioma del sistema operativo en vez del idioma activo de Enki).

## Restaurar una copia

`RequestRestoreBackupCommand` pide el ZIP y una carpeta contenedora, propone dentro de ella
`Enki-Restored-{fecha}` y muestra una confirmación antes de tocar el disco. `RestoreBackup` valida que el
archivo solo contenga las raíces `vault/` y `app-data/`, rechaza enlaces simbólicos y comprueba con
`Path.GetFullPath` que cada entrada del vault quede bajo el temporal de extracción (*zip slip*). El
destino debe ser nuevo y no puede estar dentro del vault activo.

La extracción completa ocurre primero en un directorio temporal hermano; solo al terminar se renombra a
su nombre definitivo. Después la app cambia al vault restaurado y fusiona las preferencias redacted de
`app-data/settings.json` mediante `SettingsService`, sin recuperar credenciales ni la ruta antigua. El
vault anterior nunca se sobrescribe. Los demás JSON antiguos de `app-data/` no se extraen: desde la
migración a persistencia por vault sus copias vigentes ya viven dentro de `vault/.tasks` y `vault/.enki`.

## Copia de seguridad automática

Complemento opcional y apagado por defecto (`AutoBackupEnabled = false`) al backup manual, todo-o-nada,
de arriba: sin él, una edición destructiva entre dos backups manuales no tenía ninguna versión anterior
a la que volver.

- `AutoBackupIntervalDays` (por defecto `1`) y `AutoBackupRetentionCount` (por defecto `5`) —
  **ninguno de los dos tiene control en la UI**: no hay slider, campo numérico ni binding hacia ellos en
  `SettingsView.axaml` ni propiedad expuesta en `SettingsViewModel` más allá del propio `bool`
  `AutoBackupEnabled`. Son ajustables en el modelo (`AppSettings.cs:238-245`) y respetados por
  `VaultBackupService`, pero hoy están fijos a sus valores por defecto para cualquier usuario — la copia
  descriptiva del toggle ("una vez al día... conserva las últimas 5") es en realidad texto fijo, no una
  lectura en vivo de esos dos campos.
- Se comprueba con `VaultBackupService.RunScheduledBackupIfDue(settingsService)`
  (`VaultBackupService.cs:84-110`): no-op si `AutoBackupEnabled` está apagado o si todavía no han pasado
  `AutoBackupIntervalDays` desde `LastAutoBackupUtc`. El compositor
  (`MainWindowViewModel.Composition.cs:112-116`) lo invoca una vez al arrancar (por si Enki llevaba días
  cerrado) y luego cada 6 horas mediante un `DispatcherTimer`, para que el intervalo configurado se
  respete aunque la app permanezca abierta varios días seguidos.
- Los backups automáticos se escriben en `%AppData%/Enki/backups/Enki-AutoBackup-{yyyy-MM-dd-HHmmss}.zip`
  usando el mismo `CreateBackup` (misma redacción de tokens Polar) y luego se rota: se conservan los
  `AutoBackupRetentionCount` más recientes por fecha de creación y el resto se borra
  (`RotateOldBackups`, `VaultBackupService.cs:117-133`). Un backup antiguo bloqueado/no borrable se
  registra en el log y se salta, sin abortar la rotación de los demás.
- `RunScheduledBackupIfDue` nunca lanza — un `IOException`/`UnauthorizedAccessException` se registra en
  el log (`LogService.Error`) y el método devuelve `false`, pensado para llamarse desde un timer de
  fondo donde un fallo no debe tumbar nada más.

## §1 — Exportar / Importar ajustes

- **Exportar ajustes** (`ExportSettingsCommand`, `SettingsViewModel.cs:1142-1183`): guarda
  `settings.json` con nombre sugerido `enki-settings.json` vía
  `SettingsService.BuildRedactedExportJson()` — un round-trip JSON de `Settings` (no el mismo objeto en
  memoria) con `PolarAccessToken`/`PolarRefreshToken` puestos a `null` antes de serializar
  (`SettingsService.cs:123-129`), para que "Exportar ajustes" no se convierta en un vector de robo de
  sesión si el fichero exportado acaba en un backup, un USB o un ticket de soporte. La sesión en memoria
  de la app en ejecución no se toca. Antes de exportar se fuerza `_settingsService.Save()`, así que lo
  exportado siempre refleja el último estado guardado en disco.
  - Copia de la UI (`Settings_General_DangerZone_ExportHint`): "El fichero exportado no incluye tu
    sesión de Polar — tendrás que volver a iniciar sesión después de importarlo en otro equipo."
- **Importar ajustes** (`ImportSettingsCommand`, `SettingsViewModel.cs:1189-1227`): reemplaza
  `settings.json` **por copia de fichero cruda** (`File.Copy(origen, SettingsFilePath, overwrite: true)`)
  — no hay parseo, validación ni fusión de ningún tipo antes de sobrescribir. Cualquier `.json`
  seleccionado por el usuario (exportado por Enki o no) sustituye el fichero tal cual; si el contenido no
  deserializa como `AppSettings` la próxima vez que Enki arranque, `SettingsService.Load()` lo detecta
  como corrupto (`LoadedFromCorruptData = true`) y cae a valores por defecto — el fallo se descubre en el
  siguiente arranque, no en el momento de importar.
  - Como el vínculo con Polar nunca sobrevive a la redacción de "Exportar", reimportar el fichero
    exportado tampoco lo restaura — el usuario tiene que volver a iniciar sesión desde Ajustes › Cuenta
    (ver `03-cuenta-y-sincronizacion-polar.md`).
  - Los cambios importados requieren reiniciar Enki: no hay ningún mecanismo de recarga en caliente para
    las ~50 propiedades ya cargadas en `SettingsViewModel` (mismo motivo documentado para "Restablecer
    ajustes" más abajo).
- Ambos comandos comparten el mismo patrón de manejo de errores de tres niveles que "Crear copia de
  seguridad": `UnauthorizedAccessException`/`IOException` con mensaje traducido, cualquier otra
  excepción con `ex.Message` sin traducir.

## §3 — Preguntar antes de borrar (`ConfirmBeforeDelete`)

Toggle único (`AppSettings.ConfirmBeforeDelete`, por defecto `true`) consultado de forma independiente
por **seis** comandos `RequestDelete*`/`RequestDeleteColumn` en cuatro viewmodels distintos — cuando
está desactivado, cada uno salta directo al borrado en vez de abrir su diálogo de confirmación
(`PendingDelete`/`*PendingDelete`):

| Viewmodel | Comando | Qué borra |
|---|---|---|
| `KanbanViewModel.cs:404` | `RequestDeleteCard` | Tarjeta Kanban |
| `KanbanViewModel.cs:245` | `RequestDeleteColumn` | Columna Kanban (no-core) |
| `CalendarViewModel.cs:913` | `RequestDeleteItem` | Ítem del Calendario (no aplica a ocurrencias fantasma, filtradas antes por `IsGhostOccurrence`) |
| `PolarTaskManagementViewModel.cs:365` | `RequestDeleteList` | Lista de "Gestión de tareas" (Polar) |
| `PolarTaskManagementViewModel.cs:434` | `RequestDeleteTask` | Tarea de "Gestión de tareas" (Polar) |
| `SkillsViewModel.cs:323` | `RequestDeleteSkill` | Skill (excepto las marcadas `IsDefault`, que no se pueden borrar) |

> El comentario XML-doc de `ConfirmBeforeDelete` en `AppSettings.cs:150-153` describe solo cuatro
> destinos ("Kanban card, calendar item, Polar task, skill") — la lista real de seis sitios de arriba,
> verificada por `grep -rn ConfirmBeforeDelete`, incluye además la columna Kanban y separa lista de
> tarea Polar como dos comandos distintos. El comentario está desactualizado respecto al código; este
> documento refleja lo que el código hace hoy.

Todos comparten el patrón `_settingsService?.Settings.ConfirmBeforeDelete ?? true` — si no hay
`SettingsService` inyectado (tests), el comportamiento por defecto es pedir confirmación.

**No afecta al borrado permanente desde la Papelera**, que siempre confirma independientemente de este
ajuste — es un paso irreversible por diseño.

## Restablecer ajustes a valores por defecto

`RequestResetSettingsCommand` → `PendingResetSettings = true` abre un diálogo modal de confirmación
("Esto restablecerá todos los ajustes a sus valores predeterminados. No se eliminarán vuestras notas,
tareas ni datos de la bóveda."). `ConfirmResetSettingsCommand` ejecuta
`SettingsService.ResetToDefaults()` (`SettingsService.cs:107-113`):

- Reemplaza `Settings` por una instancia nueva de `AppSettings`, **conservando solo** `VaultPath` y
  `Language` — para no desvincular el vault activo ni forzar a re-elegir idioma como efecto colateral de
  "quiero valores por defecto".
- Todo lo demás vuelve a su valor por defecto: tema, fuentes, editor, notificaciones, calendario,
  vínculo con Polar (se pierde), pestañas abiertas guardadas, etc.
- Escribe a disco inmediatamente (`Save()`), pero **no refresca en caliente** ningún viewmodel ya
  cargado — no existe hoy mecanismo para eso. El llamador debe pedir al usuario que reinicie Enki.

## Persistencia y seguridad multi-ventana

`SettingsService.Save()` (`SettingsService.cs:65-78`) no es un simple "serializar y escribir": como dos
ventanas de Enki pueden compartir el mismo `settings.json` apuntando a vaults distintos, cada `Save()`
comprueba primero si `VaultPath` cambió en *esta* instancia; si no, relee `VaultPath` del disco antes de
escribir, para que un cambio de ajuste trivial en una ventana (tema, tamaño de fuente) no pise el vault
que otra ventana acaba de elegir. La lectura-antes-de-escribir ocurre dentro del mismo
`JsonFileStore.WithLock`, así que un `Save()` concurrente de la otra instancia no puede colarse entre
medias. Esto es relevante para "continuidad de datos" aunque no tenga UI propia: es la razón de que
tener dos ventanas de Enki abiertas contra vaults distintos no corrompa el vault de ninguna de las dos.

## Referencias cruzadas

- Ajustes › Cuenta (vínculo y sincronización con Polar): `03-cuenta-y-sincronizacion-polar.md`.
- Ajustes › Editor y "no molestar"/sonido: `04-editor-y-notificaciones.md`.
- Restaurar pestañas abiertas / atajos de teclado: `02-productividad-como-app-de-escritorio.md`.
