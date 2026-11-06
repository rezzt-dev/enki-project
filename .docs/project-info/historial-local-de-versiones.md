# Historial local de versiones por nota

> **Alcance**: snapshots automáticos y manuales del contenido de una nota, dónde viven dentro del vault,
> comparar una versión con la actual, restaurarla y el ciclo de vida del historial frente a renombrar,
> mover, borrar/restaurar y una reescritura masiva de wikilinks.
>
> **Fuera de alcance**: el backup completo del vault en un `.zip` (`empaquetado-y-distribucion.md` /
> `VaultBackupService`) — complementario, no sustituido por esto — y el control de versiones externo
> (Git), que sigue siendo responsabilidad del usuario si quiere ese nivel de historial.
>
> **Fuente de verdad**: `services/VaultService.NoteHistory.cs`, `services/TextDiffService.cs`,
> `models/NoteVersion.cs`, `viewmodels/editor/EditorVersionHistoryViewModel.cs`,
> `viewmodels/editor/NoteVersionItemViewModel.cs`, `views/editor/EditorView.axaml` (pestaña "Historial"
> del panel lateral) y los puntos de integración en `services/VaultService.cs`
> (`WriteContent`, `RenameNote`, `MoveNote`, `DeleteNote`, `RestoreNote`, `PermanentlyDelete`,
> `RewriteWikilinksToRenamedNote`).

## 1. Por qué existe

Antes de esta funcionalidad, el único contenido de una nota era el que hubiera en su `.md` en ese
instante. El autoguardado del editor (`EditorViewModel`, debounce de 600 ms) hacía que un error de
edición, un `Ctrl+A`+pegar en el sitio equivocado o un find-and-replace mal dirigido se escribiera a disco
segundos después sin que hubiera ningún punto anterior al que volver. El historial local convierte cada
nota en su propia línea de tiempo de snapshots, guardados dentro del propio vault — sin servidor, sin
cuenta, sin nube — igual que el resto de Enki.

## 2. Dónde viven los snapshots

Cada nota tiene su propia carpeta oculta de historial, resuelta por
`VaultService.VersionsFolderFor` según dónde vive la nota:

- Una nota del vault raíz: `<vault>/.enki/history/<clave>/`.
- Una nota dentro de una sesión: `<vault>/.sessions/<sesión>/.history/<clave>/`.

Anidar la variante de sesión **dentro** de la propia carpeta de la sesión no es casual: si la sesión está
cifrada, cualquier ruta bajo su carpeta es cifrada automáticamente por
`VaultService.ResolveEncryptedSessionRoot` (que resuelve por prefijo de ruta, no por lista blanca). El
historial de una nota protegida queda protegido exactamente igual que la nota — nunca es el único sitio
donde su contenido quedaría en claro.

`<clave>` combina el nombre de archivo (para poder identificar la carpeta a simple vista) con un hash de
la ruta relativa a su raíz de cifrado, así que dos notas con el mismo título en carpetas distintas nunca
colisionan.

Ambas raíces (`.enki/` y `.history/`) empiezan por punto, así que todo recorrido de notas que ya excluye
carpetas ocultas (`IsUnderHiddenDirectory`, `BuildFolderChildren`, `GetFiles`) excluye el historial sin
necesitar ningún caso especial — ni aparece como nota, ni como archivo en la sección Archivos, ni en la
búsqueda.

Cada versión es un `.md` (el contenido plano de la nota en ese momento, cifrado si le corresponde) más una
entrada en `manifest.json` (fecha, disparador, tamaño, número de palabras) — ver `models/NoteVersion.cs`.

## 3. Cuándo se captura un snapshot

`VaultService.CaptureSnapshot` decide si una llamada produce una versión nueva o se descarta:

- **Autoguardado** (`NoteVersionTrigger.Autosave`): `WriteContent` snapshotea el contenido que va a
  sustituir *antes* de sobrescribirlo, pero solo si han pasado al menos
  `VaultService.MinimumAutosaveInterval` (5 minutos) desde la última versión de esa nota, y solo si el
  contenido realmente cambió respecto a la última versión guardada. Sin este doble filtro, el debounce de
  600 ms del editor produciría miles de versiones casi idénticas por sesión de escritura.
- **Restaurar** (`NoteVersionTrigger.PreRestore`): `VaultService.RestoreVersion` snapshotea el contenido
  *actual* antes de sobrescribirlo con la versión elegida — siempre, sin importar el throttle. Restaurar
  la versión equivocada nunca es un callejón sin salida: es, simplemente, otra entrada de la lista desde
  la que volver a restaurar.
- **Antes de una operación masiva** (`NoteVersionTrigger.PreBulkOperation`): `RenameNote` puede reescribir
  el wikilink `[[...]]` de cualquier otra nota del vault que apuntara al nombre antiguo — una cascada que
  toca contenido sin que el usuario revise cada cambio uno a uno. Cada nota afectada recibe un snapshot de
  su contenido justo antes de esa reescritura.

## 4. Comparar y restaurar

La pestaña "Historial" del panel lateral del editor (`EditorVersionHistoryViewModel`) lista las versiones
de la nota abierta, más recientes primero. Elegir una abre un diálogo que la compara línea a línea contra
el contenido *en pantalla* (no el guardado en disco, que pueden diferir si hay cambios sin autoguardar
todavía) usando `TextDiffService` — un diff LCS clásico sobre líneas, adecuado para prosa Markdown donde
un diff por carácter fragmentaría cada párrafo en cambios diminutos.

Restaurar pide confirmación explícita y dentro del propio diálogo — nunca sobrescribe sin preguntar. El
propio `VaultService.RestoreVersion` es quien crea el snapshot `PreRestore` antes de escribir el contenido
elegido, así que la protección no depende de que la UI recuerde llamarlo por separado.

## 5. Historial y ciclo de vida de la nota

El historial sigue a la nota, no al revés:

- **Renombrar/mover** (`RenameNote`, `MoveNote`): `MoveVersionHistory` traslada la carpeta de historial
  completa a la nueva ubicación, re-cifrando cada snapshot si el movimiento cruza una frontera de cifrado
  (exactamente igual que ya hace `MoveAttachmentsFolder` con las imágenes adjuntas).
- **Papelera** (`DeleteNote`/`RestoreNote`): la carpeta de historial *no* se traslada físicamente a la
  papelera plana — se queda donde ya vivía (bajo su sesión, si tenía una), porque esa ubicación es la que
  conserva su cifrado correcto. Un sidecar propio, `.trash/.history-origins.json`, recuerda de dónde venía
  cada nota trasladada para poder encontrar su historial de nuevo al restaurarla o purgarlo al eliminarla
  en firme (`PermanentlyDelete`). Es un mapa independiente del `.trash-origins.json` que ya usa
  `ResolveEncryptedSessionRoot`, porque ese solo se escribe para notas cifradas y el historial necesita
  encontrarse también para las que no lo son.

## 6. Retención

`VaultService.MaxVersionsPerNote` (50) limita cuántas versiones conserva una nota — al superarse, se
eliminan primero las más antiguas. Generoso para semanas de autoguardado periódico más cada snapshot de
restauración/operación masiva, sin dejar crecer una nota muy editada sin límite.
