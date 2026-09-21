# Cambios externos en el vault

> **Alcance**: qué hace Enki cuando otro programa —Syncthing, Dropbox, otro editor, un script— escribe,
> renombra o borra una nota que el editor tiene abierta: cómo se detecta, cómo se distingue de una
> escritura propia, la política de recarga y conflicto, y el ajuste que lo gobierna.
>
> **Fuera de alcance**: la sincronización de notas entre dispositivos (Enki no la hace; el vault es una
> carpeta de Markdown y quien la sincroniza es el usuario), el historial local de versiones
> (`historial-local-de-versiones.md` — se apoya en él, no lo sustituye) y el backup completo del vault
> (`empaquetado-y-distribucion.md` / `VaultBackupService`).
>
> **Fuente de verdad**: `models/NoteDiskStamp.cs`, `models/VaultExternalChange.cs`,
> `models/ExternalModificationException.cs`, `services/NoteDiskStamps.cs`,
> `services/VaultChangeWatcher.cs`, `services/VaultService.ExternalChanges.cs`,
> `viewmodels/editor/EditorViewModel.ExternalChanges.cs`, `views/editor/EditorView.axaml` (diálogos de
> conflicto y borrado, aviso de la cabecera) y los puntos de integración en
> `viewmodels/editor/EditorViewModel.cs` (`Save`, `OnTextChanged`, `FlushPendingSave`, `Close`),
> `viewmodels/notes/NotesViewModel.cs` y `viewmodels/shell/MainWindowViewModel.Composition.cs`.

## 1. Por qué existe

El vault de Enki es una carpeta de Markdown en disco, y eso es justo lo que hace que casi todo el mundo lo
ponga dentro de Syncthing, Dropbox o iCloud, o lo abra alguna vez con otro editor. Antes de esta
funcionalidad, el editor no miraba el disco nunca: leía la nota al abrir la pestaña y, a partir de ahí, el
autoguardado (`DispatcherTimer`, 600 ms) escribía encima cada vez que había algo que escribir.

La consecuencia era una pérdida de datos silenciosa. Si la nota cambiaba fuera mientras estaba abierta —el
móvil sincronizando un párrafo escrito en el tren, otro editor abierto en la otra pantalla—, el siguiente
autoguardado la pisaba con el contenido que Enki tenía en memoria. El historial local
(`CaptureAutosaveSnapshotBeforeWrite`) capturaba el contenido previo antes de escribir, así que el dato
pisado era recuperable, pero solo si el usuario llegaba a enterarse de que había pasado algo. Normalmente
no.

Es el único punto de la app con impacto sobre la integridad de los datos del usuario, y por eso se resuelve
con dos mecanismos independientes en vez de con uno.

## 2. Las dos capas

| Capa | Quién | Qué garantiza | Cuándo falla |
| --- | --- | --- | --- |
| Vigilancia activa | `VaultChangeWatcher` (`FileSystemWatcher`) | Que el usuario se **entere en el momento** de un cambio ajeno sobre una nota abierta | Desbordamiento del búfer de eventos, unidad de red, `inotify` agotado, equipo suspendido |
| Comprobación previa a guardar | `VaultService.WriteContentChecked` | Que un guardado **nunca** pise un cambio ajeno | No falla: mira el disco en el mismo instante de escribir |

La segunda no depende de la primera. Se puede apagar la vigilancia entera desde Ajustes y la garantía de
"no pisar" sigue en pie; lo que se pierde es el aviso inmediato.

## 3. Detectar: la huella en disco

`NoteDiskStamp` (`models/NoteDiskStamp.cs`) es una foto comparable de una nota en disco: fecha de
modificación, tamaño y hash SHA-256 del **contenido del archivo**.

Tres campos y no solo la fecha porque:

- La granularidad de `LastWriteTimeUtc` no es la misma en todos los sistemas de archivos: FAT/exFAT
  redondean a 2 s, y NTFS y ext4 tampoco coinciden entre sí.
- Los sincronizadores reescriben archivos byte a byte idénticos solo para refrescar su marca de tiempo. Sin
  el hash, cada una de esas reescrituras abriría un diálogo de conflicto injustificado.
- Dos contenidos distintos pueden ocupar exactamente lo mismo. Sin el hash, ese cambio pasaría por igual.

`NoteDiskStamps.Check` explota esa jerarquía: primero compara fecha y tamaño, que `FileInfo` ya trae sin
abrir el archivo; solo si difieren lee y hashea. El camino habitual —nada ha cambiado— no toca el disco más
allá de un `stat`.

El hash es de los **bytes del archivo**, no del texto. Para una nota de una sesión cifrada eso significa
hashear el criptograma, que es exactamente lo que interesa: permite saber que el archivo cambió sin
descifrarlo.

### 3.1 Cómo se descartan las escrituras propias

Este es el punto que decide si la funcionalidad es utilizable o un generador de falsos positivos: el propio
autoguardado de Enki produce eventos del sistema de archivos, y cada uno de ellos tiene que reconocerse
como propio.

No se hace con ventanas de tiempo ni con listas de "escrituras recientes" —heurísticas que fallan justo
cuando el disco va lento, que es cuando más importa— sino por **comparación exacta**:

1. Cada nota vigilada lleva registrada su huella (`VaultChangeWatcher.Track`).
2. El editor adopta la huella nueva en cuanto termina de guardar (`AdoptDiskStamp` → `UpdateStamp`),
   de forma síncrona, en el mismo hilo.
3. Al evaluar un evento, el vigilante compara el disco contra esa huella.

Un autoguardado deja el disco exactamente igual que la huella que el editor acaba de registrar, así que el
evento que genere se descarta por igualdad, no por sospecha.

### 3.2 Qué se vigila

`FileSystemWatcher` sobre la raíz del vault con `IncludeSubdirectories`, filtro `*.md`, `NotifyFilter` de
`LastWrite | FileName | Size` y un búfer interno de 64 KB (el de 8 KB por defecto se desborda en cuanto un
sincronizador vuelca una carpeta entera).

Solo se **evalúan** las rutas registradas, que hoy son las pestañas abiertas del editor: son las únicas con
contenido en memoria que un cambio ajeno pueda pisar. Una nota que no está abierta no necesita vigilancia;
el siguiente escaneo del vault ya la relee sola, porque `VaultNoteCache` compara fechas de modificación.

Se ignoran, además, las rutas que nunca son una nota del usuario aunque acaben en `.md`: `.enki/` (historial
local), `.trash/`, cualquier `.history/`, los temporales de la escritura atómica (`*.tmp-<guid>`) y los
archivos `.sync-conflict` que dejan los sincronizadores.

### 3.3 Rebote

400 ms por ruta, reiniciados en cada evento. Un sincronizador no escribe un archivo de una vez: crea un
temporal, lo renombra, toca la fecha… y cada paso es un evento. Sin el rebote, una sola sincronización
produciría varios avisos del mismo cambio.

### 3.4 Planes B

- **El `FileSystemWatcher` no se puede crear** (unidad de red, límite de `inotify` agotado) o **falla en
  caliente** (`Error`, desbordamiento del búfer): se registra la incidencia en `LogService` y se degrada a
  sondeo de las notas registradas cada 5 s (`IsPolling`).
- **El archivo no se puede leer** porque otro proceso lo tiene abierto en exclusiva mientras lo escribe: se
  reintenta hasta tres veces con el mismo rebote. Pasado ese punto se deja estar, porque la comprobación
  previa a guardar volverá a mirar el disco de todos modos.
- **Sesión protegida y bloqueada**: no se lee ni se compara nada. La ruta queda aplazada y un repaso
  periódico la reintenta; al desbloquear la sesión, la revisión se retoma sola. Ese temporizador solo existe
  mientras haya algo aplazado.

## 4. No pisar: la comprobación previa a guardar

`VaultService.WriteContentChecked(note, content, baseline)` es lo que el editor llama en lugar de
`WriteContent`:

1. `EnsureVaultAvailable()` — un vault entero que ya no está (unidad desconectada, carpeta movida) no es
   "alguien borró esta nota", y confundirlos llevaría al usuario a un diálogo de conflicto por un problema
   que en realidad es de otro tipo. Sale como el `VaultUnavailableException` de siempre.
2. Si hay huella, `NoteDiskStamps.Check`. Cualquier resultado distinto de "sin cambios" aborta la escritura
   y lanza `ExternalModificationException` con `Modified` o `Deleted`.
3. Solo entonces escribe, y devuelve la huella de lo que acaba de quedar en disco.

`ExternalModificationException` es un tipo propio y **no** deriva de `IOException` a propósito: no es un
fallo de E/S, el disco funciona perfectamente y la operación se rechaza a conciencia. El
`catch (IOException …)` que ya envolvía cada guardado lo trataría como "no se pudo guardar, reintenta en el
siguiente tick", que es exactamente lo contrario de lo que hay que hacer (reintentar pisaría el cambio
externo en cuanto el disco cooperase).

Con `baseline` nulo se comporta igual que `WriteContent`: es el caso de quien nunca leyó el archivo y por
tanto no tiene nada con lo que comparar.

## 5. Decidir: la política del editor

`EditorViewModel.HandleExternalChange` es el punto único de entrada, venga el aviso del vigilante o de la
comprobación previa a guardar.

| Estado local | Cambio externo | Qué pasa |
| --- | --- | --- |
| Sin cambios sin guardar | Modificada | Recarga silenciosa conservando cursor y desplazamiento, más una notificación discreta. Si el ajuste de recarga automática está apagado, pregunta igual que el caso de abajo. |
| Con cambios sin guardar | Modificada | Se pausa el autoguardado **de esa nota** y se abre el diálogo de conflicto. |
| Cualquiera | Contenido idéntico al de pantalla | Nada que resolver: se adopta la huella nueva y se sigue. |
| Cualquiera | Borrada | Aviso, el buffer se conserva en pantalla, autoguardado en pausa y se ofrece "guardar como nota nueva". |
| Cualquiera | Renombrada / movida | La pestaña sigue a su archivo a la ruta nueva; si además cambió el contenido, se evalúa como cualquier otra modificación. Si el destino no resulta ser una nota del vault, se trata como un borrado. |

La recarga silenciosa conserva la posición del cursor acotada al tamaño del texto nuevo (que puede ser más
corto) y, con ella, el desplazamiento: `EditorView` lleva el cursor a la vista al colocarlo. Para que esa
posición sea la real en el momento de la recarga —y no la del último cambio de pestaña—, `EditorView`
escribe `LastCaretOffset` en vivo desde `OnCaretOrSelectionChanged`.

### 5.1 El diálogo de conflicto

Tres acciones, y ninguna destruye nada:

- **Conservar la mía** — guarda antes la versión de disco en el historial de la nota
  (`NoteVersionTrigger.ExternalConflict`) y escribe el buffer encima.
- **Cargar la de disco** — guarda antes el buffer del editor en el historial y adopta el contenido externo.
- **Ver diferencias** — compara línea a línea con `TextDiffService`, la misma plantilla visual que el
  comparador del historial de versiones. Lo marcado como añadido es lo que aporta la versión del editor; lo
  marcado como eliminado, lo que solo está en la de disco.

`Esc` (y el cierre del diálogo) equivale a **decidir luego**: cierra el diálogo pero **no** reanuda el
autoguardado. Para que esa pausa no sea un estado invisible, la cabecera del editor muestra mientras tanto
un aviso —visible también en la vista simplificada, a diferencia del resto de adornos— que vuelve a abrir
el diálogo.

### 5.2 Cerrar la pestaña con la decisión pendiente

Cerrar la pestaña (o la app) con un conflicto sin resolver no puede escribir el texto sobre la nota —para
eso está la pausa— pero tampoco puede tirarlo. `EditorViewModel.Close` guarda lo que hubiera sin guardar
como versión del historial con el motivo "conflicto externo", que es donde el usuario ya sabe buscarlo, y
lo avisa.

### 5.3 Sesiones protegidas

Una nota de una sesión bloqueada no se lee ni se compara. El vigilante aplaza la revisión y la retoma al
desbloquear. Si aun así llegase un aviso hasta el editor con la sesión ya bloqueada, se ignora en silencio:
el autoguardado de esa pestaña ya falla por su cuenta con el aviso de sesión bloqueada de siempre.

## 6. Refrescar el resto de la app

Al aceptar un cambio externo, `VaultService.ApplyExternalChange`:

1. Invalida la entrada de `VaultNoteCache` para esa ruta. Hace falta explícitamente: esa caché se refresca
   comparando fechas de modificación, y este es precisamente el caso en el que esa fecha puede no haber
   cambiado.
2. Marca como obsoleto el tamaño del vault.
3. Dispara `ExternalChangeApplied`.

`NotesViewModel` se suscribe a ese evento y recarga su rejilla y su árbol, sin que el editor tenga que
conocer esa pantalla — mismo desacople que `KanbanService.BoardChanged`. Dentro de la propia pestaña se
refrescan etiquetas, enlaces entrantes, estadísticas, destinos de wikilink e historial, el mismo conjunto
que ya refrescaba una restauración del historial.

## 7. Ajustes

Ajustes › Editor:

- **Vigilar cambios externos en el vault** (activado por defecto). Apagado, `VaultChangeWatcher` no mira
  nada; la comprobación previa a guardar sigue en pie.
- **Recargar automáticamente si no hay cambios locales** (activado por defecto, deshabilitado si el
  anterior está apagado). Apagado, el caso sin cambios locales también pregunta.

Ambos viven en `AppSettings` (globales, no por vault). `VaultChangeWatcher` lee el primero del propio
`SettingsService` reaccionando a `SettingsChanged`, en vez de recibir otro callback desde
`SettingsViewModel`: ya depende de él, y así encender o apagar la vigilancia no necesita cablear nada nuevo
en la composición del shell.

Apagar la vigilancia no olvida las notas registradas —son las pestañas que el usuario sigue teniendo
abiertas—, así que volver a encenderla retoma la vigilancia sin obligarlas a reabrirse.

## 8. Ciclo de vida

`VaultChangeWatcher` se construye en `MainWindowViewModel` (composición manual, sin contenedor de DI) y no
dentro de Notas, porque su ciclo de vida es el del **vault activo**, no el de una pantalla:

- `InitializeShell` lo arranca sobre el vault recién abierto, antes de construir `NotesViewModel`.
- `OnVaultChanged` vuelve a llamar a `Start`, que libera el vigilante anterior y olvida sus notas
  registradas (pertenecían al vault que se acaba de dejar) antes de crear el nuevo.
- `Shutdown`/`Dispose` lo libera. `MainWindowViewModel` es `IDisposable` por este motivo, y ambos caminos
  son idempotentes.

Cada pestaña se registra al abrirse, reapunta su registro en cada renombrado o movimiento hecho desde la
propia Enki, y se da de baja al cerrarse (`EditorViewModel.Close`, llamado desde los tres caminos de cierre
de `NotesViewModel`). Antes de borrar o renombrar una nota desde Enki, la pestaña se da de baja primero:
esas operaciones generan sus propios eventos y, sin esto, la pestaña se avisaría a sí misma de que
"alguien" tocó la nota desde fuera.

## 9. Hilos

`ExternalChangeDetected` se emite siempre desde un hilo del pool (el del rebote o el del sondeo), nunca
desde el de interfaz — misma convención que `VaultService.VaultSizeChanged`. `EditorViewModel` filtra por
ruta en ese hilo (barato, sin tocar nada enlazado) y marshala solo lo suyo con `Dispatcher.UIThread.Post`.

`ExternalChangeApplied`, en cambio, se emite en el hilo del que llama, que hoy siempre es el de interfaz
porque quien resuelve un cambio externo es el editor.

## 10. Pruebas

- `Services/NoteDiskStampsTests.cs` — los casos límite que obligan a que la huella tenga tres campos: un
  `touch` sin cambio de contenido (no es un cambio) y un cambio de contenido del mismo tamaño (sí lo es).
- `Services/VaultServiceExternalChangesTests.cs` — la comprobación previa a guardar: escribe cuando no hay
  cambios, aborta sin tocar el archivo cuando los hay, distingue el borrado del vault desconectado, y la
  relectura que se salta la caché.
- `Services/VaultChangeWatcherTests.cs` — sobre un vault temporal y un `FileSystemWatcher` real, con
  tiempos de milisegundos: escritura propia que no genera aviso, escritura ajena que sí, ráfaga de N
  eventos que produce uno solo, reescritura idéntica que no produce ninguno, borrado, renombrado, notas no
  registradas, ajuste apagado, cambio de vault, liberación y degradación a sondeo.
- `ViewModels/EditorViewModelExternalChangeTests.cs` — la tabla de política completa, las dos salidas del
  diálogo con su snapshot, "decidir luego", la red de seguridad del guardado y el borrado externo.
- `UITests/EditorExternalConflictUiTests.cs` — el diálogo montado sobre una ventana real y el `KeyBinding`
  de `Esc`, que vive dentro del popup y no en la vista.

Lo que no cubren las pruebas automáticas —un sincronizador de verdad y edición simultánea en otro
editor— está en `qa-manual-release-candidate.md`.
