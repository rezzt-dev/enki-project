# AUDITORIA DE SEGURIDAD Y FALLOS CRITICOS

> revision completa del codigo de `enki/src/enki/` buscando vulnerabilidades, fallos de seguridad,
> validaciones basicas ausentes, bugs latentes y cualquier cosa que pueda matar el proyecto una vez
> publicado. no es un roadmap de producto: aqui solo hay problemas y como cerrarlos.
>
> se aplica antes de publicar una version candidata, y cada vez que se toque el editor Markdown, el
> cifrado de sesiones, la persistencia o la superficie de red (Polar/Supabase).

**Fecha del analisis:** 2026-09-20 · rama `alfa-version` (`dee3860`)
**Alcance:** 563 archivos `.cs`, ~96.700 lineas, mas empaquetado (`enki/packaging/`) y CI
(`.github/workflows/`).

**Linea base verificada en el momento del analisis:**

- `dotnet build enki/src/enki/enki.csproj` — correcto, **sin warnings**.
- `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` — **1699 pruebas, 0 fallos** (64 s).

Es decir: nada de lo que sigue lo detecta el compilador ni la suite actual. Todos los hallazgos son
problemas de diseño, de validacion de entrada o de casos que ningun test cubre todavia.

## ESTADO

Los cuatro hallazgos bloqueantes (**SEC-01**, **SEC-02**, **BUG-01**, **BUG-02**) estan **corregidos**, con
pruebas de regresion propias: `ExternalLinkPolicyTests`, `RecurrenceRolloverTests`,
`SettingsNormalizationTests` y los casos reescritos de `SessionsViewModelEncryptionTests`.

Los cinco hallazgos **Medio** de seguridad (**SEC-03**, **SEC-04**, **SEC-05**, **SEC-06**, **SEC-07**)
estan **corregidos** tambien, con 101 pruebas nuevas en `PrivateTempFileTests`,
`SessionManifestValidationTests`, `RemoteImagePolicyTests` y `MarkdownRemoteImageDownloadTests`, mas 6 en
`SessionsProtectHintUiTests` (`enki.UITests`). Tras los arreglos: build sin warnings y **1857 pruebas, 0
fallos** en `enki.Tests` (linea base: 1756), y 254 en `enki.UITests`. El detalle de cada uno esta en su
ficha, bajo "Corregido".

**Lo que queda abierto de estos cinco** (y esta anotado en su ficha):

- **SEC-06** — no se ha anadido AAD al AES-GCM. Es el endurecimiento que impediria intercambiar el
  contenido cifrado de dos notas o de dos sesiones, pero exige cambiar el formato (version nueva,
  migracion y compatibilidad hacia atras) y un fallo ahi significa perdida irrecuperable de datos
  cifrados de usuarios reales. No se toca sin decision explicita.
- **SEC-07** — siguen abiertas dos decisiones de producto: si `EditorPreferences.RemoteImages` deberia
  pasar a estar **apagado por defecto** con un aviso en linea y un boton de "cargar" (el patron de los
  clientes de correo), y si `http://` deberia dejar de aceptarse como destino inicial. Ninguna de las dos
  se ha tomado aqui: la primera cambia el comportamiento por defecto del editor y la segunda rompe notas
  existentes legitimas.
- **SEC-03/SEC-04** — el comportamiento en **Windows y macOS no esta verificado**: todo el desarrollo y
  las pruebas se han hecho en Linux. Compila y esta razonado por plataforma, nada mas.

El resto de hallazgos (BUG-03 en adelante, SEC-08 en adelante) sigue abierto y mantiene el orden de trabajo
del final del documento.

---

## CLASIFICACION DE SEVERIDAD

| Nivel | Criterio |
|---|---|
| **Critico** | Puede ejecutar codigo ajeno, exponer datos cifrados o dejar la app permanentemente inutilizable. |
| **Alto** | Rompe una promesa explicita del producto (cifrado, "no se pierde nada") o cuelga/tumba la app en un caso alcanzable. |
| **Medio** | Fuga de informacion acotada, denegacion de servicio local, o perdida de datos en un caso poco comun. |
| **Bajo** | Robustez, higiene o endurecimiento defensivo; no hay un escenario de dano directo. |

---

## INDICE DE HALLAZGOS

| ID | Severidad | Titulo | Archivo principal |
|---|---|---|---|
| SEC-01 | ✅ **Critico** (corregido) | Ctrl+clic en un enlace Markdown ejecuta cualquier cosa via el shell | `views/editor/EditorView.axaml.cs:1140` |
| SEC-02 | ✅ **Alto** (corregido) | Contraseña minima de 4 caracteres en sesiones cifradas | `viewmodels/sessions/SessionsViewModel.cs:161` |
| BUG-01 | ✅ **Alto** (corregido) | Dos bucles de recurrencia sin tope: cuelgue permanente | `services/polar/PolarTaskService.cs:425` |
| BUG-02 | ✅ **Alto** (corregido) | Preferencias sin validacion de rango al cargar: app inusable sin salida | `viewmodels/shell/MainWindowViewModel.Composition.cs:133` |
| SEC-03 | ✅ Medio (corregido) | Temporal de nombre fijo y predecible en `/tmp` | `services/NotificationSoundPlayer.cs:105` |
| SEC-04 | ✅ Medio (corregido) | Ventana TOCTOU al restringir permisos del temporal descifrado | `services/VaultService.cs:377` |
| SEC-05 | ✅ Medio (corregido) | Los metadatos de una sesion protegida viven en claro | `models/SessionManifest.cs` |
| SEC-06 | ✅ Medio (corregido) | El manifiesto de cifrado se consume sin validar | `services/SessionEncryptionService.cs:200` |
| SEC-07 | ✅ Medio (corregido) | Imagenes remotas activadas por defecto: fuga de IP y SSRF local | `views/editor/Markdown/MarkdownImageCache.cs:243` |
| BUG-03 | Medio | Sin tope de pixeles al decodificar: bomba de imagen | `views/editor/Markdown/MarkdownImageCache.cs:279` |
| BUG-04 | Medio | `VaultNoteCache` crece sin limite | `services/VaultNoteCache.cs` |
| BUG-05 | Medio | La cache de imagenes no se purga al bloquear una sesion | `views/editor/Markdown/MarkdownImageCache.cs` |
| BUG-06 | Medio | Ningun `Regex` del proyecto tiene timeout (ReDoS) | `views/editor/Markdown/MarkdownHtmlParser.cs:87` |
| BUG-07 | Medio | `JsonFileStore` espera el mutex sin timeout | `services/JsonFileStore.cs:87` |
| BUG-08 | Medio | Tres `SettingsService` independientes en el arranque | `App.axaml.cs:41,51` |
| BUG-09 | Medio | El backup borra el destino antes de saber si puede escribirlo | `services/VaultBackupService.cs:43` |
| BUG-10 | Medio | No existe "restaurar copia de seguridad" en la app | `viewmodels/settings/SettingsViewModel.DangerZone.cs` |
| SEC-08 | Bajo | `SanitizeFileName` acepta `..`, `.` y nombres ocultos/reservados | `services/VaultService.cs:1547` |
| SEC-09 | Bajo | La exportacion HTML incrusta imagenes de fuera del vault | `services/NoteExportRenderer.cs:474` |
| SEC-10 | Bajo | Permisos por defecto de estado y logs en Linux/macOS | `services/JsonFileStore.cs:66` |
| BUG-11 | Bajo | Adjuntar imagen lee el archivo entero sin limite de tamaño | `services/NoteImageService.cs:54` |
| BUG-12 | Bajo | Un `.enki/settings.json` corrupto se vacia de forma silenciosa | `services/SettingsService.cs` (`ReadVaultObject`) |
| BUG-13 | Bajo | 36 `catch` vacios: fallos que no dejan rastro | (transversal) |
| BUG-14 | Bajo | Escritura "atomica" sin `fsync` | `services/JsonFileStore.cs:66`, `services/VaultService.cs:1081` |
| BUG-15 | Bajo | El recorrido del vault sigue enlaces simbolicos | `services/VaultBackupService.cs:50` |
| OPS-01 | Informativo | Consecuencias de excluir `local.properties` del paquete | `enki/packaging/` |

---

## CRITICO

### SEC-01 · Ctrl+clic en un enlace Markdown ejecuta cualquier cosa via el shell

**Donde:** [`EditorView.axaml.cs:1140`](../enki/src/enki/views/editor/EditorView.axaml.cs#L1140) y
[`UrlOpener.cs:10`](../enki/src/enki/services/UrlOpener.cs#L10).

```csharp
private static void OpenExternalUrl(string url)
{
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
```

El destino sale tal cual de `MarkdownLineParser` — es decir, del texto de la nota — y llega a
`ShellExecute` **sin ninguna lista blanca de esquemas**. El exportador a HTML
([`NoteExportRenderer.cs`](../enki/src/enki/services/NoteExportRenderer.cs), `SafeSchemes`) si valida el
esquema; el editor, que es donde el clic realmente ejecuta algo, no.

**Escenario concreto.** El usuario abre una nota que no escribio el (vault compartido por Syncthing/Drive,
plantilla descargada, nota recibida en un `.zip`, carpeta de un repo clonado) y hace Ctrl+clic sobre un
enlace cuyo rotulo dice `Documentacion`:

- Windows: `[Documentacion](\\192.0.2.10\share\factura.pdf.exe)` o `[Documentacion](C:\Users\...\payload.bat)` → se ejecuta.
- Windows: esquemas de handler (`ms-msdt:`, `search-ms:`, `ms-officecmd:`) → cadenas de explotacion conocidas.
- Linux: `[Documentacion](./payload.desktop)` con `xdg-open` → lanzador ejecutado.
- macOS: `[Documentacion](/Users/.../algo.app)` → aplicacion abierta.

El rotulo del enlace es texto libre, asi que el usuario no ve a donde apunta en ningun momento.

**Impacto.** Ejecucion de codigo arbitrario con un solo gesto que la propia app enseña como normal
("Ctrl+clic abre el enlace, como en Obsidian"). Es el unico hallazgo del informe que justifica por si solo
bloquear la publicacion.

**Como cerrarlo.**

1. Reutilizar la lista blanca que ya existe en `NoteExportRenderer.SafeSchemes` (`http`, `https`, `mailto`)
   y extraerla a un unico sitio compartido por exportacion y editor.
2. Para todo lo demas (rutas locales, `file:`, esquemas desconocidos): no abrir directo. O se ignora, o se
   pide confirmacion mostrando **el destino real completo** antes de abrir.
3. Si se decide permitir rutas locales, resolverlas y exigir que caigan dentro del vault, y aun asi
   rechazar extensiones ejecutables por plataforma (`.exe .bat .cmd .ps1 .scr .msi .com .desktop .app .command .sh`).
4. Mostrar el destino en un tooltip al pasar el raton sobre el enlace, para que Ctrl+clic nunca sea a ciegas.
5. Aplicar lo mismo a `UrlOpener.Open` (hoy solo recibe una URL constante, pero es una utilidad publica que
   invita a pasarle cualquier cosa mañana).

**Corregido.** Nuevo [`ExternalLinkPolicy`](../enki/src/enki/services/ExternalLinkPolicy.cs): lista blanca
de esquemas (`http`/`https`/`mailto`) mas, para una ruta local, contencion dentro del vault, rechazo por
extension ejecutable (lista de las tres plataformas a la vez, porque un vault se sincroniza entre equipos)
y comprobacion de existencia. Las rutas UNC (`\\host\share`) y protocol-relative (`//host/...`) se
rechazan explicitamente. El Ctrl+clic del editor pasa ahora por ahi
([`EditorView.axaml.cs`](../enki/src/enki/views/editor/EditorView.axaml.cs), `OpenLinkTarget`) y lo que se
rechaza se explica al usuario con un motivo concreto —esquema, fuera del vault, ejecutable, enlace roto—
via `EditorViewModel.ReportBlockedLink`, en los 7 idiomas; un ancla `#seccion` se ignora sin ruido.
`NoteExportRenderer` ya no duplica su propia lista blanca: la toma de la politica, asi que exportacion y
editor comparten un solo vocabulario. `UrlOpener.Open` tambien filtra. 31 pruebas nuevas en
[`ExternalLinkPolicyTests`](../enki/tests/enki.Tests/Services/ExternalLinkPolicyTests.cs).

---

## ALTO

### SEC-02 · Contraseña minima de 4 caracteres en sesiones cifradas

**Donde:** [`SessionsViewModel.cs:161`](../enki/src/enki/viewmodels/sessions/SessionsViewModel.cs#L161)
(`MinPasswordLength = 4`).

La criptografia en si esta bien hecha: AES-GCM, DEK envuelta por ranura, PBKDF2-SHA256 con 210.000
iteraciones, codigos de recuperacion con `RandomNumberGenerator`
([`SessionEncryptionService.cs`](../enki/src/enki/services/SessionEncryptionService.cs)). El problema es
que todo eso cuelga de una contraseña que la app acepta con 4 caracteres.

**Escenario concreto.** Un atacante con acceso al disco (portatil robado, backup en un USB, carpeta
sincronizada en la nube, el `.zip` de "Crear copia de seguridad" que incluye `.session.json` entero) copia
el manifiesto y ataca offline. Un espacio de 4 caracteres alfanumericos en minuscula son ~1,7 millones de
candidatos; con 210k iteraciones de PBKDF2 en GPU eso se agota en minutos. El contenido queda descifrado
aunque el usuario no vuelva a tocar la maquina.

**Impacto.** La funcion mas sensible del producto ("sesion protegida con contraseña") puede quedar
vulnerada con un esfuerzo trivial, sin que el usuario tenga ninguna señal de que su contraseña no era
suficiente.

**Como cerrarlo.**

1. Subir el minimo a **12 caracteres** (o 8 con requisito de mezcla) — es una contraseña que se teclea una
   vez por sesion de trabajo, no cada minuto.
2. Indicador de fortaleza en el dialogo de creacion, con la advertencia explicita de que no hay forma de
   recuperar el contenido si se pierde la contraseña y los 5 codigos.
3. Migrar a Argon2id cuando sea posible; PBKDF2 es lo que queda de serie en .NET, pero es el KDF mas debil
   frente a GPU de los que se pueden usar hoy.
4. Los vaults existentes con contraseña corta: avisar al abrir ("esta sesion usa una contraseña por debajo
   del minimo actual, cambiala") en vez de migrarlos en silencio.

**Corregido (puntos 1 y 2).** `MinPasswordLength` pasa de 4 a **12**, con el razonamiento del ataque
offline escrito en el propio codigo para que no vuelva a bajarse "porque es una app local". Se aplica al
crear una sesion y al cambiar la contraseña con un codigo de recuperacion. Como dejar el boton "Crear"
deshabilitado sin decir por que convierte un requisito razonable en un formulario que no responde, el
dialogo muestra ahora un aviso en cuanto lo escrito se queda corto (`IsNewSessionPasswordTooShort` +
`Sessions_CreateDialog_PasswordTooShort`, traducido). **Las sesiones ya creadas con contraseña corta
siguen abriendose** — el minimo solo gobierna la eleccion de una contraseña nueva, y hay un test que lo
fija. Sigue sin haber reglas de composicion, y los puntos 3 (Argon2id) y 4 (aviso a vaults heredados)
quedan pendientes.

### BUG-01 · Dos bucles de recurrencia sin tope: cuelgue permanente

**Donde:** [`PolarTaskService.cs:425`](../enki/src/enki/services/polar/PolarTaskService.cs#L425) y
[`ReminderService.cs:329`](../enki/src/enki/services/ReminderService.cs#L329).

```csharp
// PolarTaskService.RollRecurringTasks
while (nextDue <= now) { nextDue = CalculateNextDueDate(nextDue, task.Recurrence); }

// ReminderService.Fire
do { next = PolarTaskService.CalculateNextDueDate(next, reminder.Recurrence); } while (next <= nowUtc);
```

`CalculateNextDueDate` ([`PolarTaskService.cs:456`](../enki/src/enki/services/polar/PolarTaskService.cs#L456))
termina en `_ => local`: **devuelve la misma fecha** para cualquier valor de `PolarRecurrence` que no este
en el `switch`. Los dos bucles de arriba giran entonces para siempre.

**Escenario concreto.** `System.Text.Json` deserializa un entero fuera de rango a un `enum` sin quejarse.
Basta con que `polar-tasks.json` o `reminders.json` contenga `"Recurrence": 7` — por corrupcion de disco,
por una edicion manual, por un downgrade de version que introdujo un valor nuevo y luego se volvio atras —
para que la app entre en bucle infinito al cargar o al sincronizar. Y como ocurre en el arranque y en cada
tick del temporizador, el usuario no puede recuperarse desde la interfaz: hay que editar el JSON a mano.

Lo revelador es que **el patron correcto ya existe en el proyecto**:
[`CalendarAggregationService.cs:21`](../enki/src/enki/services/CalendarAggregationService.cs#L21) declara
un "hard safety cap on chained `CalculateNextDueDate` calls". Los otros dos sitios no lo aplicaron.

**Como cerrarlo.**

1. Sustituir `_ => local` por `_ => throw new ArgumentOutOfRangeException(...)` o, mejor para un dato que
   viene de disco, por `_ => local.AddDays(1)` mas un aviso en el log.
2. Aplicar el mismo tope de iteraciones de `CalendarAggregationService` en los dos bucles.
3. Sanear los enums al deserializar: cualquier valor fuera de `Enum.IsDefined` se degrada a `None`, igual
   que ya hace `PolarWireDtos.RecurrenceFromWire` con lo que llega de Supabase
   ([`PolarWireDtos.cs:41`](../enki/src/enki/services/polar/PolarWireDtos.cs#L41)).

**Corregido.** Los dos bucles pasan por `PolarTaskService.AdvanceUntilAfter`, que acota a 10.000 saltos
(~27 años en recurrencia diaria) y corta en cuanto el candidato no avanza — que es exactamente lo que
delata una recurrencia desconocida — o desborda `DateTime`. `RollRecurringTasks` deja entonces la tarea
como estaba y `ReminderService.Fire` degrada el recordatorio a "una sola vez", ambos sin colgar nada.
Ademas `PolarTaskService.NormalizeRecurrence` repara el valor al cargar `polar-tasks.json` y
`reminders.json`, para que ningun `switch` del resto de la app llegue a verlo. 9 pruebas nuevas en
[`RecurrenceRolloverTests`](../enki/tests/enki.Tests/Services/RecurrenceRolloverTests.cs) — las de valor
fuera de rango son, literalmente, las que antes no terminaban.

### BUG-02 · Preferencias sin validacion de rango al cargar: app inusable sin salida

**Donde:** [`MainWindowViewModel.Composition.cs:133`](../enki/src/enki/viewmodels/shell/MainWindowViewModel.Composition.cs#L133),
[`SettingsViewModel.cs:236`](../enki/src/enki/viewmodels/settings/SettingsViewModel.cs#L236),
[`SettingsService.ImportPreferences`](../enki/src/enki/services/SettingsService.cs).

```csharp
_uiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0;
```

Los comandos de la UI si acotan (`ZoomIn`/`ZoomOut` usan `Math.Clamp`), pero **la carga desde disco no
acota nada**. `UiZoomPercent = 0` deja toda la interfaz a escala 0 (ventana en blanco, sin ningun control
con el que volver a Ajustes). `EditorFontSize = 0` o negativo hace que Avalonia rechace la fuente y tumbe
el editor. `CalendarWorkingHoursStart = 99` rompe la vista de calendario.

**Escenario concreto.** Tres vias reales, ninguna exotica: (a) "Importar ajustes" con un JSON editado o
corrupto — `ImportPreferences` aplica cualquier valor cuya clave exista, sin validar; (b) corrupcion
parcial del `.enki/settings.json` del vault; (c) un vault compartido entre dos versiones distintas de Enki.

**Impacto.** La aplicacion arranca inutilizable y **no hay forma de recuperarla desde la propia app**: el
"Restablecer ajustes" esta dentro de la UI que ya no se ve. Para un producto publicado, esto es un ticket
de soporte con instrucciones de "edita este JSON a mano".

**Como cerrarlo.**

1. Un unico punto de saneado al cargar `AppSettings`/`VaultSettings`: cada campo numerico con su rango
   valido, y fuera de rango → valor por defecto (con aviso en el log).
2. Aplicar ese mismo saneado en `ImportPreferences`, que hoy es la entrada menos confiable de todas.
3. Un argumento de arranque `--safe-mode` (o `--reset-settings`) que ignore las preferencias del vault y
   entre con los valores por defecto. Es la valvula de escape que hoy no existe.

**Corregido (puntos 1 y 2).** Nuevo `AppSettings.Normalize()`, con el mismo papel que el
`VaultSettings.Normalize()` que ya existia, llamado en los tres puntos donde se deserializa: carga, cambio
de vault e importacion. Acota cuerpo de letra, ancho de columna, tabulacion, ventana de recordatorios,
volumen, backup automatico y franja horaria laboral (incluida la pareja cruzada), distinguiendo un 0 que
es `default(T)` de un archivo antiguo —vuelve al valor por defecto— de un valor imposible —se acota al
extremo del rango—. `VaultSettings.Normalize()` acota ademas `UiZoomPercent` a 50–200, que era el unico
ajuste capaz de dejar la app sin ninguna salida. 14 pruebas nuevas en
[`SettingsNormalizationTests`](../enki/tests/enki.Tests/Services/SettingsNormalizationTests.cs). El punto 3
(modo seguro de arranque) queda pendiente: es la red de seguridad para un valor que se escape del saneado.

---

## MEDIO

### SEC-03 · Temporal de nombre fijo y predecible en `/tmp`

**Donde:** [`NotificationSoundPlayer.cs:105`](../enki/src/enki/services/NotificationSoundPlayer.cs#L105).

```csharp
var path = Path.Combine(Path.GetTempPath(), "enki-notification-sound.wav");
using (var file = File.Create(path)) stream.CopyTo(file);
```

Nombre fijo, directorio compartido (CWE-377/CWE-59). En una maquina Linux/macOS con `/tmp` compartido,
otro usuario local puede crear de antemano `/tmp/enki-notification-sound.wav` como enlace simbolico a
cualquier archivo escribible por el usuario de Enki (`~/.bashrc`, `~/.config/autostart/...`): `File.Create`
sigue el enlace y **trunca y sobreescribe el objetivo**. Tambien permite el camino inverso: sustituir el
`.wav` por otro contenido que la app reproducira.

Contrasta con
[`VaultService.ResolveReadablePath`](../enki/src/enki/services/VaultService.cs#L377), que si usa `Guid`
para el nombre. Ese es el patron a copiar.

**Como cerrarlo.** Nombre con `Guid`, o mejor una carpeta propia por usuario
(`Path.Combine(Path.GetTempPath(), $"enki-{uid}")` creada con modo `0700`), y `FileMode.CreateNew` con
reintento en vez de `File.Create` sobre una ruta que puede existir.

**Corregido.** SEC-03 y SEC-04 eran el mismo problema visto desde dos sitios —escribir un temporal con
garantias en un directorio compartido—, asi que se resuelven una sola vez, en
[`PrivateTempFile`](../enki/src/enki/services/PrivateTempFile.cs), y no con dos parches distintos. Aplica
**las dos** defensas que el informe planteaba como alternativas, porque cada una tapa lo que la otra deja
pasar: un directorio propio por usuario dentro del temporal del sistema, creado con modo `0700` en la
propia llamada que lo crea y **comprobado en cada uso** (existe, no es un enlace simbolico, y su modo es
exactamente el esperado); y, dentro, `FileMode.CreateNew` mas `FileStreamOptions.UnixCreateMode` (0600), de
modo que ningun archivo se escribe nunca sobre una ruta que pueda existir ya.

El WAV empaquetado
([`NotificationSoundPlayer.EnsureAssetExtracted`](../enki/src/enki/services/NotificationSoundPlayer.cs))
conserva su nombre fijo, pero ahora **dentro** de ese directorio `0700`, donde ninguna otra cuenta puede
poner ni leer nada; se escribe con el patron temporal + `File.Move` que `JsonFileStore` ya usa en el resto
del proyecto, asi que tampoco se trunca ni se ve a medias. **No se limpia al salir, a proposito**: es
siempre el mismo archivo, de unos pocos KB, sin nada del usuario dentro, y borrarlo solo obligaria a volver
a extraerlo en cada arranque; ademas, un gancho de apagado propio para el reproductor de sonido seria un
segundo mecanismo de apagado en paralelo al que ya existe. La ruta de winmm (`SND_FILENAME`) no cambia:
sigue recibiendo una ruta de archivo real.

Si el directorio privado no es utilizable en esa maquina, `EnsureAssetExtracted` lanza y el try/catch por
eslabon de `TryPlay` degrada al sonido del sistema — exactamente para lo que esa cadena existe. 7 pruebas
nuevas en [`PrivateTempFileTests`](../enki/tests/enki.Tests/Services/PrivateTempFileTests.cs).
**Pendiente / no verificado:** el comportamiento en Windows y macOS solo esta razonado y compilado, no
probado (todo el trabajo se ha hecho en Linux).

### SEC-04 · Ventana TOCTOU al restringir permisos del temporal descifrado

**Donde:** [`VaultService.cs:377`](../enki/src/enki/services/VaultService.cs#L377).

```csharp
File.WriteAllBytes(tempPath, plaintext);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
```

La intencion esta bien (y esta documentada), pero entre la escritura y el `chmod` el archivo existe con la
mascara heredada — normalmente `0644`. Es una ventana corta, pero el contenido es exactamente el que el
usuario cifro a proposito.

**Como cerrarlo.** Crear el archivo ya con los permisos correctos:
`new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.None)`
precedido de `File.Create` + `SetUnixFileMode` **antes** de escribir el contenido, o usar
`UnixFileMode` en el constructor de `FileStreamOptions` (disponible en .NET 8).

**Corregido.** `ResolveReadablePath` ya no escribe nada por su cuenta: delega en `PrivateTempFile.Write`
(ver SEC-03), de modo que el archivo **nace con modo 0600 antes del primer byte de contenido**, en la misma
llamada al sistema que lo crea. No hay dos operaciones, asi que no hay ventana entre ellas. Si la escritura
falla a mitad, el archivo se borra antes de propagar el error: un temporal descifrado incompleto es justo
lo que no debe quedarse en disco.

El contrato de `ResolveReadablePath` no se toca — mismo prefijo `enki-dec-`, misma extension, misma
limpieza via `TryCleanupDecryptedTempFile`/`CleanupStaleDecryptedTempFiles` y las mismas llamadas desde
`Program.Main` y el cierre de `MainWindow`—, pero el barrido mira ahora **dos** sitios: el directorio
privado, donde viven los temporales desde este cambio, y la raiz del temporal del sistema, donde los dejaba
cualquier version anterior. Sin eso, actualizar dejaria las copias en claro de la version vieja tiradas ahi
para siempre.

Un detalle que el informe daba por bueno y no lo era: en .NET 8, el *setter* de
`FileStreamOptions.UnixCreateMode` esta anotado `[UnsupportedOSPlatform("windows")]`, y este proyecto
compila CA1416 como error — asignarlo sin condicionar por plataforma no compila. Lo mismo pasa con la
sobrecarga `Directory.CreateDirectory(string, UnixFileMode)`. Son las dos unicas condiciones por plataforma
que hay en `PrivateTempFile`, y en Windows no se pierde nada: el temporal ya esta dentro del perfil del
usuario. Cubierto por `PrivateTempFileTests` (modo `0600` del archivo, `0700` del directorio, borrado del
parcial y barrido de los dos sitios).

### SEC-05 · Los metadatos de una sesion protegida viven en claro

**Donde:** [`SessionManifest.cs`](../enki/src/enki/models/SessionManifest.cs) y el propio arbol de archivos.

Se cifra el **contenido** de las notas y sus adjuntos. No se cifran:

- los nombres de archivo de las notas (siguen siendo `.md` normales en `.sessions/<sesion>/`),
- el nombre de la sesion, su fecha, su numero de notas,
- `SessionManifestNode`, que guarda la ruta relativa de cada nota — o sea, el indice completo,
- los enlaces manuales entre notas (la estructura del grafo).

Es una decision de diseño defendible y esta razonada en el codigo (la barra lateral necesita mostrar la
sesion bloqueada). El problema es que **el usuario no lo sabe**: la UI dice "sesion protegida con
contraseña" y cualquiera asume que eso cubre los titulos.

**Como cerrarlo.** No hace falta cambiar el diseño. Hace falta decirlo: una linea en el dialogo de crear
sesion protegida ("se cifra el contenido de las notas; los titulos y la estructura siguen siendo visibles")
y la misma nota en la documentacion publica. Si en algun momento se quiere cerrar de verdad, hay que cifrar
tambien los nombres de archivo, y eso es un cambio de formato con migracion.

**Corregido.** El diseño no se toca: no se cifra ningun nombre ni cambia el formato. Lo que cambia es que
ahora se dice. `Sessions_CreateDialog_ProtectHint` pasa de "las notas de la sesion se cifran en disco" —que
invita justo a la suposicion equivocada— a decir las dos mitades: **se cifra el contenido de las notas y
sus adjuntos**, y **los nombres de archivo, la estructura de la sesion y sus fechas siguen legibles en
disco**. El aviso de los 5 codigos de recuperacion se conserva en la misma linea. Traducido a los 7
idiomas. El `TextBlock` del dialogo
([`SessionsView.axaml`](../enki/src/enki/views/sessions/SessionsView.axaml)) ya tenia
`TextWrapping="Wrap"` dentro de un `Border.modal-card` de ancho maximo 380, y
[`SessionsProtectHintUiTests`](../enki/tests/enki.UITests/SessionsProtectHintUiTests.cs) lo mide sobre el
XAML real en los 6 idiomas: el aviso ocupa 4 lineas (~70 px) en los 322 px utiles de la tarjeta y el
dialogo entero se queda en 516–530 px de alto segun el idioma, por debajo del tope de 640 que cabe en una
pantalla de portatil pequena.

Se dice "en disco" y no "en la app" a proposito: con la sesion bloqueada, la interfaz **si** oculta nombres
y contenido (`Files_LockedSession_Message`, `Notes_LockedSession_Message`,
`Sessions_Graph_AccessibleScope`), y esas cadenas son correctas tal y como estan. Lo que queda al
descubierto es el arbol de archivos de quien tenga acceso a la carpeta: una copia de seguridad, una carpeta
sincronizada, un equipo compartido. Se ha repasado el resto de los textos de sesiones en `Strings.resx`
buscando una promesa mas amplia de la real y no la hay.

La misma aclaracion esta ahora en la documentacion que describe la funcion:
`.docs/project-info/obsidian-docs/features-document.md` §1.6 (con el detalle de que si entra en el cifrado
el historial de versiones de la nota) e `informacion-general.md`. No se ha creado ningun documento nuevo.

### SEC-06 · El manifiesto de cifrado se consume sin validar

**Donde:** [`SessionEncryptionService.cs:200`](../enki/src/enki/services/SessionEncryptionService.cs#L200).

```csharp
var salt = Convert.FromBase64String(slot.SaltBase64);
var wrapped = Convert.FromBase64String(slot.WrappedDekBase64);
var wrappingKey = DeriveWrappingKey(secret, salt, slot.Iterations);
```

Dos problemas, ambos con datos que vienen de un archivo del disco:

1. **`FormatException` no capturada.** Si el base64 esta corrupto, `Convert.FromBase64String` lanza. El
   `catch` solo cubre `CryptographicException`, asi que la excepcion sale de `TryUnlockWithPassword` —
   documentado como *"Returns false on a wrong password — never throws for that case"* — y de
   `SessionService.Unlock`, documentado igual. Con un manifiesto dañado, intentar desbloquear tumba la app
   en vez de decir "no se pudo leer esta sesion".
2. **`Iterations` sin cota.** Sale del JSON. Un manifiesto con `"Iterations": 2000000000` congela el hilo
   que intente desbloquear durante horas. Tampoco se valida `FormatVersion`, pese a que su propio
   comentario dice que existe para poder rechazar formatos desconocidos.

**Como cerrarlo.** Capturar `FormatException`/`ArgumentException` junto a `CryptographicException` en
`TryUnwrapSlot`; acotar `Iterations` a un rango razonable (p. ej. 100.000–1.000.000) y rechazar el
manifiesto fuera de el; comprobar `FormatVersion == 1` antes de intentar nada.

Como endurecimiento adicional: hoy el AES-GCM no lleva **AAD**, asi que nada ata un bloque cifrado a su
archivo ni a su sesion. Alguien con acceso al disco puede intercambiar el contenido cifrado de dos notas o
de dos sesiones y la app lo aceptara como valido. Añadir la ruta relativa como AAD lo cierra.

**Corregido (todo menos el AAD).**
[`SessionEncryptionService.TryUnwrapSlot`](../enki/src/enki/services/SessionEncryptionService.cs) valida la
ranura entera **antes de derivar nada**: base64 decodificable (la `FormatException` se traduce a un fallo
propio en vez de escaparse), sal de al menos 16 bytes y DEK envuelta de al menos 60 (nonce + clave + tag,
que es lo que `WrapWithSecret` produce), e `Iterations` dentro de rango. `FormatVersion` se comprueba
ahora, por fin, en las dos puertas de entrada.

**Los limites, y por que esos.** El techo es 10.000.000: PBKDF2-SHA256 va del orden de 2–3 millones de
iteraciones por segundo y nucleo en un equipo de escritorio actual, asi que ese techo son unos pocos
segundos —lo peor que puede costar un valor disparatado que aun asi este dentro del rango—, frente a las
horas de los 2.000.000.000 del informe. El suelo es 1.000, mucho mas bajo que los 100.000 que sugeria el
informe, y es deliberado: **no es una politica de seguridad, es un detector de basura**. Toda version de
Enki ha escrito siempre `DefaultIterations` (210.000), asi que cualquier suelo por debajo de esa cifra deja
pasar el 100 % de los manifiestos reales; ponerlo en "lo que hoy se considera seguro" solo serviria para
dejar a alguien fuera de sus propios datos cifrados, sin ninguna via de recuperacion, por una decision que
tomo el programa y no el. 1.000 esta muy por encima de los valores que delatan corrupcion (0, negativos) y
muy por debajo de cualquier configuracion real.

**Contraseña incorrecta y manifiesto ilegible dejan de ser la misma respuesta.** Lo segundo lanza
[`SessionManifestCorruptException`](../enki/src/enki/models/SessionManifestCorruptException.cs) —tipo
propio, al lado de `SessionLockedException` y por el mismo motivo—, que `SessionService.Unlock` y
`ResetPasswordWithRecoveryCode` dejan pasar y `SessionsViewModel.ConfirmUnlock`/`ConfirmPasswordReset`
convierten en `Sessions_Unlock_CorruptManifest` (traducido a los 7 idiomas), con el motivo tecnico concreto
en el log. Sin esto, a alguien con una sesion dañada se le decia "contraseña incorrecta" y reintentaba
indefinidamente algo que nunca iba a funcionar.

**La ruta de recuperacion no se rompe por una ranura mala.** `TryUnlockWithRecoveryCode` salta la ranura
ilegible y sigue probando las otras: las 5 son envoltorios independientes de la misma DEK, y una dañada no
puede costarle al usuario los otros cuatro codigos. Solo si ninguna abrio nada **y** alguna era ilegible se
reporta la corrupcion.

19 pruebas nuevas en
[`SessionManifestValidationTests`](../enki/tests/enki.Tests/Services/SessionManifestValidationTests.cs).
Comprobado revirtiendo el arreglo: la de `Iterations` enorme no falla sin la cota, **se cuelga** (la suite
filtrada se agota a los 120 s), y las de base64/`FormatVersion`/`Iterations` fuera de rango fallan.

**Pendiente: el AAD.** Se deja fuera a proposito. Atar cada bloque cifrado a su ruta exigiria una version
de formato nueva, su migracion y la compatibilidad hacia atras, y un fallo ahi no es un error recuperable:
es perdida irreversible de datos cifrados de usuarios reales. No entra sin una decision explicita que lo
pida.

### SEC-07 · Imagenes remotas activadas por defecto: fuga de IP y SSRF local

**Donde:** [`MarkdownImageCache.cs:243`](../enki/src/enki/views/editor/Markdown/MarkdownImageCache.cs#L243),
preferencia en [`EditorPreferences.cs:165`](../enki/src/enki/services/EditorPreferences.cs#L165)
(`_remoteImages = true`).

La compensacion esta razonada en el codigo y hay interruptor en Ajustes, asi que esto es mas una
recomendacion que un defecto. Pero conviene tener presente el alcance real:

- **Pixel de seguimiento.** `![](https://…/x.png)` en una nota recibida revela IP, momento exacto de
  lectura y cada relectura. Para una app cuyo argumento de venta es "tus notas en tu disco, sin nube", es
  una fuga a terceros que el usuario no espera al abrir una nota.
- **SSRF local.** No hay restriccion de destino: `http://127.0.0.1:8080/...`, `http://192.168.1.1/...` o
  `http://169.254.169.254/...` se solicitan igual. En escritorio el impacto es limitado (no se ve la
  respuesta, solo si cargo o no), pero permite sondear la red local desde una nota ajena.
- Se permite `http://` en claro ademas de `https://`.
- `HttpClient` sigue redirecciones sin limitarlas a los mismos esquemas.

**Como cerrarlo.** Como minimo: rechazar destinos en rangos privados/loopback/link-local, exigir `https`
(o avisar al degradar), y evaluar poner la preferencia en **apagado por defecto** con un aviso en linea
("esta nota tiene imagenes remotas — cargarlas avisara a su servidor") y un boton de "cargar". Ese patron
(el de los clientes de correo) es el que encaja con la promesa del producto.

**Corregido (la parte tecnica).** Nuevo
[`RemoteImagePolicy`](../enki/src/enki/services/RemoteImagePolicy.cs):

- **Destino.** Se rechazan loopback, rangos privados, link-local (incluido el `169.254.169.254` de los
  metadatos de nube), CGNAT, multicast, documentacion y reservados, en IPv4 y en IPv6, y tambien las formas
  en que una IPv4 puede venir disfrazada dentro de una IPv6 (`::ffff:127.0.0.1`, 6to4, NAT64) — el hueco
  clasico de un filtro escrito solo para IPv4. La comprobacion **no se hace sobre la cadena de la URL**,
  que no es una defensa, sino en `SocketsHttpHandler.ConnectCallback`: se resuelve el nombre, se descartan
  las direcciones no publicas y se conecta **a esas mismas direcciones ya validadas**, de modo que no hay
  hueco entre lo validado y lo usado (DNS rebinding) y un nombre como `localhost` o un dominio publico que
  apunte dentro no son casos especiales, sino el mismo caso.
- **Redirecciones.** `AllowAutoRedirect = false` y bucle propio, con tope de 3 saltos. Cada salto abre su
  propia conexion, asi que pasa por el mismo filtro de destino; y se rechaza explicitamente el paso de
  `https` a `http`. Se hace a mano en vez de dejarselo al runtime precisamente para poder probarlo.
- **Tamaño.** `MaxRemoteBytes` (16 MB) se conserva, pero ahora se corta **leyendo por streaming**: el
  cuerpo deja de leerse en cuanto pasa del tope. Antes, `ReadAsByteArrayAsync` se traia el contenido entero
  a memoria y solo despues se miraba cuanto ocupaba, que es justo lo que el tope existia para evitar.
- **Tiempo.** 20 s de presupuesto total para toda la descarga, redirecciones incluidas — no 20 s por salto.

Un destino bloqueado se pinta como imagen rota (`MarkdownImageState.Failed`) y se anota con
`LogService.Warning`, igual que una imagen que falta: el `try/catch` de `MarkdownImageCache.Start` ya hacia
exactamente eso, asi que no sube ninguna excepcion. La cache, el presupuesto de memoria y `ForgetFailed`
quedan intactos. La costura para probar sin red es un handler inyectable
(`MarkdownImageCache.UseHttpHandlerForTests`), el mismo patron de `httpHandler` opcional que
`PolarAuthService`/`SupabaseRestClient` ya usaban.

75 pruebas nuevas entre
[`RemoteImagePolicyTests`](../enki/tests/enki.Tests/Services/RemoteImagePolicyTests.cs) (tabla de casos de
`IsPubliclyRoutable`, con los bordes de cada rango, y las redirecciones) y
[`MarkdownRemoteImageDownloadTests`](../enki/tests/enki.Tests/Services/MarkdownRemoteImageDownloadTests.cs)
(descarga con transporte falso). Ningun test toca la red.

**Pendiente: dos decisiones de producto que no se han tomado aqui.**

1. `EditorPreferences.RemoteImages` **sigue activo por defecto**, y `AppSettings.EditorRemoteImages` no se
   ha tocado. Cambiarlo a apagado por defecto con un aviso en linea y un boton de "cargar" —el patron de
   los clientes de correo— es lo que de verdad cierra el pixel de seguimiento, que el filtro de destino
   **no** aborda: un servidor publico que solo quiere ver la IP del lector sigue funcionando. Es un cambio
   de comportamiento por defecto del editor y le corresponde a quien decide el producto.
2. `http://` **se sigue aceptando** como destino inicial. Bloquearlo romperia notas existentes legitimas;
   lo que si se bloquea es *degradar* a `http` a mitad de una descarga que empezo en `https`.

### BUG-03 · Sin tope de pixeles al decodificar: bomba de imagen

**Donde:** [`MarkdownImageCache.cs:279`](../enki/src/enki/views/editor/Markdown/MarkdownImageCache.cs#L279).

Hay tope de **bytes** descargados (`MaxRemoteBytes = 16 MB`) y presupuesto de cache
(`MemoryBudgetBytes = 96 MB`), pero no hay tope de **pixeles antes de decodificar**:

```csharp
var bitmap = natural.Width > 0 && natural.Width <= decodeWidth
    ? new Bitmap(stream)                                   // decodificacion completa
    : Bitmap.DecodeToWidth(stream, decodeWidth, ...);
```

Una imagen de, por ejemplo, 100 × 20.000.000 px entra por la primera rama (ancho natural pequeño) y se
decodifica entera: 2.000 millones de pixeles × 4 bytes. Un PNG asi comprime a unos pocos KB. El
presupuesto de 96 MB solo se consulta **despues** de decodificar, cuando ya se reservo la memoria.

**Escenario concreto.** Abrir una nota ajena con un `![](bomba.png)`, o con un `data:` incrustado, o con
una URL remota. Resultado: `OutOfMemoryException` y la app se cae, o el sistema entra en swap.

**Como cerrarlo.** `ReadNaturalSize` ya lee la cabecera sin decodificar. Basta con rechazar ahi mismo
cualquier imagen cuyo `Width * Height` supere un limite (p. ej. 50 millones de pixeles) y pintar el hueco
de "imagen rota" que ya existe.

### BUG-04 · `VaultNoteCache` crece sin limite

**Donde:** [`VaultNoteCache.cs`](../enki/src/enki/services/VaultNoteCache.cs).

El diccionario guarda `(NoteFile, contenido completo)` por ruta y **nunca desaloja**: no hay presupuesto de
memoria, ni LRU, ni tope de entradas. Solo se quita algo al invalidar explicitamente (borrado, renombrado,
bloqueo de sesion).

`NoteSearchService.Search` recorre `GetAllNotesAcrossVaultWithContent()`, o sea que **una sola busqueda
global carga el vault entero en memoria y lo deja ahi para siempre**. Un vault de 5.000 notas de 50 KB son
250 MB residentes tras la primera busqueda; uno con notas grandes (registros, transcripciones, tablas
pegadas) escala peor.

Llama la atencion porque `MarkdownImageCache` — al lado — si tiene presupuesto y desalojo LRU. Es el mismo
patron aplicado a medias.

**Como cerrarlo.** Copiar el mecanismo de `MarkdownImageCache`: presupuesto en bytes, `LastUsedUtc` por
entrada y `Trim()` al insertar.

### BUG-05 · La cache de imagenes no se purga al bloquear una sesion

**Donde:** [`MarkdownImageCache.cs`](../enki/src/enki/views/editor/Markdown/MarkdownImageCache.cs) frente a
[`VaultNoteCache.InvalidatePrefix`](../enki/src/enki/services/VaultNoteCache.cs#L79).

`VaultNoteCache.InvalidatePrefix` existe precisamente para esto y su comentario lo dice: *"keeping
decrypted content resident in memory after the user deliberately locked the session would defeat the point
of locking it"*. `SessionService.Lock` lo llama. Pero `MarkdownImageCache` es una cache **estatica de
proceso** y nadie la purga: los bitmaps ya descifrados de los adjuntos de esa sesion siguen en memoria
(hasta 96 MB) despues de bloquearla.

**Como cerrarlo.** Añadir `MarkdownImageCache.InvalidatePrefix(rootPath)` — la clave de cache ya empieza
por `NoteDirectory`, asi que el filtrado es directo — y llamarlo desde `SessionService.Lock` junto al
existente. Añadir tambien un `Clear()` en el apagado, al lado de `SessionEncryptionService.LockAll()`
([`MainWindowViewModel.cs:505`](../enki/src/enki/viewmodels/shell/MainWindowViewModel.cs#L505)).

### BUG-06 · Ningun `Regex` del proyecto tiene timeout (ReDoS)

**Donde:** transversal. El caso mas expuesto es
[`MarkdownHtmlParser.cs:87`](../enki/src/enki/views/editor/Markdown/MarkdownHtmlParser.cs#L87):

```
<\s*(?<close>/)?\s*(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:\s+[a-zA-Z_:][-a-zA-Z0-9_:.]*(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(?<self>/)?\s*>
```

El grupo `attrs` es un cuantificador anidado sobre alternativas — el patron clasico de retroceso
catastrofico. Se ejecuta **por linea en cada repintado del editor**, sobre texto que el usuario (o quien
escribiera la nota) controla por completo.

Ninguno de los `Regex` del proyecto define `matchTimeout`.

**Escenario concreto.** Una linea larga tipo `<div aaaa aaaa aaaa aaaa …` sin el `>` final puede disparar
un retroceso exponencial: la interfaz se congela sin excepcion, sin log y sin forma de cancelar, porque
ocurre dentro del layout del editor.

**Como cerrarlo.** Pasar `TimeSpan.FromMilliseconds(100)` como `matchTimeout` a todos los `Regex`
construidos sobre contenido de usuario, capturar `RegexMatchTimeoutException` y degradar a "esta linea se
pinta como texto plano". Alternativa mas solida para `TagRegex`: reescribirlo con un escaner lineal a mano
(la gramatica es pequeña y ya es una lista blanca).

### BUG-07 · `JsonFileStore` espera el mutex sin timeout

**Donde:** [`JsonFileStore.cs:87`](../enki/src/enki/services/JsonFileStore.cs#L87).

```csharp
var mutex = new Mutex(initiallyOwned: false, $"Enki-File-{hash}");
try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
```

`WaitOne()` sin timeout espera **indefinidamente**. Se llama desde el constructor de `SettingsService`, o
sea en el arranque y en el hilo de UI. Si el mutex queda retenido por cualquier motivo — un proceso Enki
zombi, un cuelgue durante una escritura, o en Linux otro proceso local que cree un mutex con ese mismo
nombre — la aplicacion se queda colgada en el arranque sin ningun mensaje.

**Como cerrarlo.** `WaitOne(TimeSpan.FromSeconds(5))` y, al agotarse, decidir explicitamente: continuar sin
lock registrando un aviso (lectura) o fallar con un error visible (escritura). Nunca esperar para siempre
en el hilo de UI.

### BUG-08 · Tres `SettingsService` independientes en el arranque

**Donde:** [`App.axaml.cs:41`](../enki/src/enki/App.axaml.cs#L41),
[`App.axaml.cs:51`](../enki/src/enki/App.axaml.cs#L51) y
[`MainWindowViewModel.Composition.cs:23`](../enki/src/enki/viewmodels/shell/MainWindowViewModel.Composition.cs#L23).

`ApplyStoredLanguage()` y `ApplyStoredThemeVariant()` construyen cada uno su propio `SettingsService` solo
para leer un campo. Y el constructor de `SettingsService` no es una lectura: hace `Directory.CreateDirectory`,
`Load()`, crea el almacen seguro de credenciales y llama a `SwitchVault(...)`, que termina en `Save()`.

Consecuencias reales:

- **Tres ciclos completos de escritura de `settings.json` en cada arranque**, cada uno tomando el mutex de
  `JsonFileStore`, mas la logica de reconciliacion de `VaultPath` entre instancias que se pisan entre si.
- **Hasta tres accesos al llavero** si la cuenta esta vinculada. En Linux con el llavero bloqueado eso son
  tres dialogos de desbloqueo seguidos.
- Ninguna de las dos llamadas de `App.axaml.cs` esta en `try/catch`: una `IOException` (disco lleno,
  permisos en `%AppData%`) durante `OnFrameworkInitializationCompleted` **tumba la app antes de que exista
  ninguna ventana**, sin dialogo. Solo queda la linea del log.
- Tres copias en memoria de `AppSettings` que divergen en cuanto algo cambia.

Ademas contradice lo que dice `AGENTS.md`: la composicion manual esta en `MainWindowViewModel`, y esto son
dos composiciones paralelas.

**Como cerrarlo.** Un unico `SettingsService` creado antes de `OnFrameworkInitializationCompleted` (o un
lector ligero de solo lectura, sin `Save()`, para los dos campos que se necesitan antes de la ventana) y
pasarlo al resto. Y envolver el arranque en un `try/catch` que enseñe un dialogo con la ruta del log en vez
de morir en silencio.

### BUG-09 · El backup borra el destino antes de saber si puede escribirlo

**Donde:** [`VaultBackupService.cs:43`](../enki/src/enki/services/VaultBackupService.cs#L43).

```csharp
if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);
using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
```

Si la creacion del zip falla a mitad (disco lleno — muy plausible al escribir un vault entero, USB
desconectado, permisos), el backup anterior **ya no existe** y el nuevo esta truncado. En una funcion cuyo
proposito entero es "no perder nada", es el peor momento posible para no tener red de seguridad.

**Como cerrarlo.** Escribir a `destino.tmp-<guid>` y hacer `File.Move(temp, destino, overwrite: true)` solo
al terminar con exito — exactamente el patron que `JsonFileStore.Save` y `VaultService.WriteFileAtomic` ya
usan en el resto del proyecto. Borrar el temporal en el `catch`.

### BUG-10 · No existe "restaurar copia de seguridad" en la app

**Donde:** [`SettingsViewModel.DangerZone.cs`](../enki/src/enki/viewmodels/settings/SettingsViewModel.DangerZone.cs).

Hay `CreateBackup`, backup automatico con rotacion, `ExportSettings` e `ImportSettings`. **No hay
restauracion.** El usuario que pierde su vault tiene un `.zip` y ninguna via guiada para volver: debe
descomprimirlo a mano, entender la estructura `vault/` + `app-data/` y saber donde va cada cosa.

Para un producto publicado, una funcion de copia de seguridad sin restauracion es media funcion — y la
mitad que falta es la que importa el dia malo. Tambien implica que el camino de restauracion **nunca se ha
probado**, que es la forma clasica de descubrir que los backups no servian.

**Como cerrarlo.** Un comando "Restaurar copia de seguridad" que: valide el zip, avise de que reemplaza el
vault activo, restaure a una carpeta nueva (nunca sobre la actual), y haga entradas con
`Path.GetFullPath` verificado contra la raiz de destino para evitar *zip slip* si el archivo fue
manipulado. Y añadir un test de ida y vuelta backup → restauracion.

---

## BAJO

### SEC-08 · `SanitizeFileName` acepta `..`, `.` y nombres ocultos/reservados

**Donde:** [`VaultService.cs:1547`](../enki/src/enki/services/VaultService.cs#L1547).

Filtra `Path.GetInvalidFileNameChars()`, lo que si bloquea el recorrido de rutas (`/` es invalido en todas
las plataformas y `\` tambien en Windows). Pero deja pasar:

- `..` y `.` como nombre completo: `Path.Combine(parent, "..")` apunta al directorio padre. Hoy el bucle de
  colisiones lo desvia a `".. 2"`, asi que no se escapa del vault — pero se apoya en un efecto lateral, no
  en una comprobacion.
- Nombres que empiezan por `.`: una carpeta llamada `.notas` se crea correctamente y luego **desaparece del
  arbol**, porque `BuildFolderChildren` ([`VaultService.cs:636`](../enki/src/enki/services/VaultService.cs#L636))
  salta todo lo que empieza por punto. El usuario ve como su carpeta se esfuma.
- Nombres reservados de Windows (`CON`, `PRN`, `AUX`, `NUL`, `COM1`…), puntos y espacios finales: excepcion
  de E/S en Windows donde en Linux funciona.
- Sin limite de longitud: >255 bytes falla en la mayoria de sistemas de archivos.

**Como cerrarlo.** Rechazar explicitamente `.`/`..`, quitar el punto inicial (o avisar de que la carpeta
quedara oculta), comprobar la lista de nombres reservados en Windows, recortar puntos/espacios finales y
acotar la longitud.

### SEC-09 · La exportacion HTML incrusta imagenes de fuera del vault

**Donde:** [`NoteExportRenderer.cs:474`](../enki/src/enki/services/NoteExportRenderer.cs#L474)
(`TryEmbedLocalImage`).

`Path.Combine(noteDirectory, url)` sin comprobar contencion. Una nota con
`![](../../../../home/usuario/Fotos/privado.png)` incrusta ese archivo como base64 en el `.html` exportado
que el usuario luego comparte. El filtro por extension limita el daño a imagenes, y el resto del exportador
esta bien hecho (lista blanca de esquemas, escapado sistematico, saneado de etiquetas HTML).

**Como cerrarlo.** Exigir que la ruta resuelta caiga dentro del vault; si no, dejar el `src` original en vez
de incrustar.

### SEC-10 · Permisos por defecto de estado y logs en Linux/macOS

**Donde:** [`JsonFileStore.cs:66`](../enki/src/enki/services/JsonFileStore.cs#L66),
[`LogService.cs`](../enki/src/enki/services/LogService.cs), backups en `%AppData%/Enki/backups/`.

Todo se escribe con la mascara heredada (normalmente `0644` / legible por cualquier usuario local). Los
tokens ya viven en el llavero — ese trabajo esta hecho — pero siguen ahi: rutas del vault, correo de la
cuenta Polar vinculada, logs con rutas y mensajes de excepcion, y sobre todo **los zips de backup
automatico, que contienen el vault entero**.

**Como cerrarlo.** `0700` en la carpeta `%AppData%/Enki` y `0600` en los archivos que crea, en el arranque.
En Windows el ACL heredado del perfil ya lo cubre.

### BUG-11 · Adjuntar imagen lee el archivo entero sin limite de tamaño

**Donde:** [`NoteImageService.cs:54`](../enki/src/enki/services/NoteImageService.cs#L54)
(`File.ReadAllBytes(fullSource)`).

Arrastrar un archivo de varios GB a una nota lo carga entero en memoria antes de copiarlo. Tampoco se
comprueba que el archivo sea realmente una imagen (solo cuenta la extension del origen).

Contrasta con `BrandLogoService`, que si tiene `MaxSourceBytes = 64 MB` y valida el formato leyendo la
cabecera con Skia. Ese es el patron a replicar.

**Como cerrarlo.** Tope de tamaño con aviso al usuario y copia por streaming (`File.Copy` o `CopyToAsync`)
en vez de `ReadAllBytes`.

### BUG-12 · Un `.enki/settings.json` corrupto se vacia de forma silenciosa

**Donde:** `SettingsService.ReadVaultObject`
([`SettingsService.cs`](../enki/src/enki/services/SettingsService.cs)).

Ante un JSON ilegible copia el original a `<ruta>.corrupt-<guid>` (bien) y escribe `{}` en su lugar. El
usuario pierde todas sus preferencias del vault y **no se entera**: no hay notificacion, aunque el proyecto
tiene un `NotificationService` pensado justo para esto y `SettingsService.LoadedFromCorruptData` ya existe
para el caso equivalente del archivo global.

**Como cerrarlo.** Marcar el caso con una bandera analoga a `LoadedFromCorruptData` y que el compositor
avise con `_notificationService.Push(...)`, mencionando la ruta de la copia de seguridad.

### BUG-13 · 36 `catch` vacios: fallos que no dejan rastro

Hay 36 bloques `catch` que no registran nada. Muchos estan justificados y comentados (limpieza best-effort,
"sin navegador asociado"). Otros no: `PolarRealtimeService.ConnectAsync` traga cualquier excepcion sin
distinguir un problema de red de un fallo de programacion, y `JsonFileStore.TryLoad` captura `catch { }`
generico, lo que incluye `OutOfMemoryException` y errores de programacion enmascarados como "el archivo no
existia".

**Como cerrarlo.** Acotar los `catch` a los tipos esperados y añadir `LogService.Debug/Warning` en los que
hoy son mudos. El coste es una linea por sitio y convierte un "a veces no sincroniza" irreproducible en un
log accionable.

### BUG-14 · Escritura "atomica" sin `fsync`

**Donde:** [`JsonFileStore.cs:66`](../enki/src/enki/services/JsonFileStore.cs#L66),
[`VaultService.cs:1081`](../enki/src/enki/services/VaultService.cs#L1081).

El patron temporal + `File.Move` protege contra lecturas a medias — que es lo que buscaba — pero no contra
un corte de corriente: sin `FileStream.Flush(flushToDisk: true)` antes del `Move`, un fallo de energia
puede dejar el archivo destino con contenido vacio o mezclado, porque el rename llega al disco antes que
los datos.

**Como cerrarlo.** Escribir con `FileStream` y `Flush(true)` antes del `Move` en los archivos criticos
(notas, `settings.json`, `.session.json`).

### BUG-15 · El recorrido del vault sigue enlaces simbolicos

**Donde:** [`VaultBackupService.cs:50`](../enki/src/enki/services/VaultBackupService.cs#L50) y el resto de
usos de `SearchOption.AllDirectories`.

Un enlace simbolico dentro del vault apuntando a `~` o a `/` hace que el backup intente empaquetar medio
sistema de archivos; un ciclo de enlaces lo deja recorriendo hasta fallar. Es un caso raro, pero lo crea el
propio usuario sin mala intencion (un enlace a una carpeta de referencias, por ejemplo).

**Como cerrarlo.** Pasar `EnumerationOptions` con el atributo `ReparsePoint` ignorado, o comprobar
`FileSystemInfo.LinkTarget` antes de descender.

### OPS-01 · Consecuencias de excluir `local.properties` del paquete

Esto **esta bien resuelto** y se anota solo para que no se pierda el contexto al publicar. Los tres scripts
de empaquetado excluyen `local.properties`
([`build-linux-package.sh:93`](../enki/packaging/linux/build-linux-package.sh#L93),
[`build-macos-package.sh:96`](../enki/packaging/macos/build-macos-package.sh#L96),
[`installer.iss:78`](../enki/packaging/windows/installer.iss#L78)), y el `.gitignore` lo cubre. Ninguna
credencial de Supabase viaja en los paquetes.

La consecuencia es que **en un build publicado `SupabaseConfig.IsConfigured` es siempre `false`** y toda la
superficie de Polar queda desactivada. Conviene comprobar a mano en el paquete final que Ajustes › Cuenta
lo explica con claridad ("sincronizacion no disponible en esta compilacion") en vez de enseñar un formulario
de login que nunca puede funcionar.

Y para el dia que se decida distribuir con credenciales: la `anon key` de Supabase es publica por diseño,
pero solo es segura si (a) hay **RLS activo en las cuatro tablas** con politicas por `auth.uid()`, y (b) el
**registro publico esta desactivado** en el proyecto Supabase. Sin esas dos cosas, publicar la clave es
regalar escritura en la base de datos.

---

## LO QUE YA ESTA BIEN RESUELTO

Conviene dejarlo escrito para no "arreglarlo" por error en una pasada futura:

- **Criptografia de sesiones.** Envelope encryption correcto: AES-GCM autenticado, DEK aleatoria, ranuras
  independientes por secreto, PBKDF2-SHA256 con 210k iteraciones, `CryptographicOperations.ZeroMemory` en
  todas las rutas, codigos de recuperacion con `RandomNumberGenerator` y alfabeto sin caracteres ambiguos.
  El unico eslabon debil es la longitud minima de la contraseña (SEC-02).
- **Almacen de credenciales.** DPAPI / Keychain / libsecret con migracion desde `settings.json` heredado,
  degradacion controlada cuando el llavero no esta disponible, y redaccion de tokens tanto en "Exportar
  ajustes" como en los zips de backup. La Fase 1.4 del roadmap esta genuinamente cerrada.
- **Exportacion a HTML.** Lista blanca de esquemas, escapado sistematico con `WebUtility.HtmlEncode`,
  saneado de etiquetas HTML por lista blanca con atributos descartados, `rel="noopener noreferrer"`. Es el
  componente mejor endurecido del proyecto — y el contraste exacto con SEC-01.
- **Historial de versiones.** Los snapshots de una nota en sesion protegida viven **dentro** de la sesion,
  asi que quedan cifrados por el mismo mecanismo. Con poda por retencion. Bien pensado.
- **Escrituras concurrentes.** Mutex con nombre por archivo + temporal + `File.Move`, con el razonamiento
  de por que un segundo proceso puede existir pese al `SingleInstanceGuard`.
- **Limpieza de temporales descifrados.** Barrido al arrancar y al cerrar, con prefijo propio para no tocar
  archivos ajenos.
- **Higiene de codigo.** 0 warnings de compilacion, 1699 tests verdes, sin `async void` fuera de handlers
  de UI, sin `.Result`/`.Wait()` bloqueando el hilo de UI, sin `Random` en contextos de seguridad.

---

## ORDEN DE TRABAJO SUGERIDO

**Antes de publicar (bloqueante): hecho.**

1. ~~SEC-01 — lista blanca de esquemas en el Ctrl+clic del editor.~~
2. ~~BUG-01 — tope en los dos bucles de recurrencia y saneado del enum.~~
3. ~~BUG-02 — validacion de rango al cargar preferencias~~ (+ modo seguro de arranque: pendiente).
4. ~~SEC-02 — subir el minimo de contraseña~~ (+ indicador de fortaleza: pendiente).

**Antes de publicar (muy recomendable):**

5. ~~SEC-06 — capturar `FormatException` y acotar `Iterations`.~~ (+ AAD: pendiente, y fuera del alcance
   sin una decision explicita — cambia el formato).
6. ~~SEC-03 / SEC-04 — temporales con nombre aleatorio y permisos desde la creacion.~~ (sin verificar en
   Windows/macOS).
7. ~~SEC-05 — decir en la UI que el cifrado cubre el contenido, no los titulos.~~
8. BUG-09 — backup a temporal y `Move` al final.
9. BUG-03 — tope de pixeles antes de decodificar.
10. BUG-08 — un unico `SettingsService` y `try/catch` en el arranque.

**Primera iteracion despues de publicar:**

11. BUG-10 — restaurar copia de seguridad (con su test de ida y vuelta).
12. BUG-06 — timeouts de regex.
13. BUG-04 / BUG-05 — presupuesto en `VaultNoteCache` y purga de imagenes al bloquear.
14. BUG-07 — timeout en el mutex de `JsonFileStore`.
15. ~~SEC-07 — politica de imagenes remotas.~~ (+ default apagado y `http://`: decisiones de producto
   pendientes).

**Deuda de robustez, sin prisa:** SEC-08, SEC-09, SEC-10, BUG-11, BUG-12, BUG-13, BUG-14, BUG-15.

---

## QUE NO CUBRE ESTE ANALISIS

- No se han ejecutado pruebas dinamicas: ni fuzzing del parser Markdown, ni ejecucion real de los
  escenarios de explotacion descritos. Los hallazgos salen de lectura de codigo, y los escenarios son
  razonados, no reproducidos.
- No se ha auditado la configuracion del proyecto Supabase (politicas RLS, triggers, registro publico), que
  vive fuera de este repositorio y es donde se decide si la `anon key` es segura de publicar.
- No se han revisado las dependencias de terceros (`Avalonia`, `AvaloniaEdit`, `SkiaSharp`, `YamlDotNet`,
  `Supabase.Realtime`, `PDFium`) contra avisos de seguridad conocidos. Merece una pasada de
  `dotnet list package --vulnerable` en CI antes de cada release.
- No se ha revisado `enki/tests/enki.UITests/` ni la landing web.
