# QA manual de release candidate

> **Estado**: checklist activa. Cubre el punto 1.3 de
> [`roadmap-publicacion-y-mejora-continua.md`](../roadmap-publicacion-y-mejora-continua.md) — "qa manual
> documentada". No es un documento aspiracional: los cuatro bloques de verificación (lector de pantalla,
> wikilinks/enlaces rotos, reconexión de Polar Realtime, selector de recurrencia) son exactamente las
> cuatro notas **"Verificación manual pendiente"** que quedaron abiertas al cerrar las Fases 5, 7, 9 y 10
> de [`analisis-proyecto/roadmap.md`](analisis-proyecto/roadmap.md) — ninguna se ha ejecutado nunca contra
> la app corriendo de verdad, solo por tests headless e inspección de código. Este documento las convierte
> en un procedimiento repetible en vez de dejarlas dispersas en cuatro notas de cierre de fase distintas.

## 1. Cuándo se ejecuta

- Antes de etiquetar cualquier release candidate para `0.2.0` o equivalente (cualquier versión que se
  vaya a publicar fuera de este equipo de desarrollo).
- Antes de cerrar en producción cualquiera de las cuatro "Verificación manual pendiente" citadas arriba,
  si no se ha hecho ya.
- Recomendado también tras cualquier cambio no trivial en el editor Markdown, el store de Polar/Realtime,
  el motor de recurrencia o las vistas `.axaml` recién auditadas de accesibilidad — aunque el gate estricto
  de "obligatorio antes de versión publicable" son los dos casos anteriores.

## 2. Qué cubre esto y qué no

`enki/tests/enki.Tests/` ya prueba en xUnit toda la lógica no trivial: expansión de ocurrencias de
recurrencia, resolución de wikilinks (`Resolved`/`Broken`/`Ambiguous`), el contrato de
`PolarSyncOutcome`, `LocalEditsOverwritten`, etc. — ver
[`13-pruebas-y-checklist-de-calidad.md`](calendar-feature/13-pruebas-y-checklist-de-calidad.md) para el
criterio ya aplicado ("lógica en xUnit, UI a mano") en la funcionalidad de calendario.

Este documento cubre exactamente lo que esos tests **no pueden** cubrir, por diseño o por límite técnico:

- **Lector de pantalla real**: los tests headless verifican que `AutomationProperties.Name` está
  presente y tiene el valor esperado; no pueden verificar que Orca/NVDA/VoiceOver lo anuncia de forma útil,
  en el orden correcto, o que un control sigue siendo operable solo con teclado.
- **Render visual real**: los tests de `WikiLinkParserTests` verifican `ResolveTargetWithStatus`; no
  pueden verificar que `DangerBrush` se ve realmente distinto de `Accent` en el tema activo, en las seis
  fuentes tipográficas de las seis localizaciones, o con un tema personalizado importado.
- **Reconexión real de socket**: `PolarRealtimeService` envuelve `Supabase.Realtime`, que abre un
  WebSocket real sin transporte inyectable — no hay forma de simular una caída de socket en un test
  headless (mismo motivo por el que `PolarSyncServiceTests` deja fuera la orquestación HTTP completa de
  `SupabaseRestClient`). Solo una cuenta Polar real y una red real lo verifican.
- **Selector de recurrencia renderizado**: los tests verifican el enum, el formateo de texto
  (`PolarRecurrenceFormatterTests` si existe) y el `SelectRecurrenceCommand`; no pueden verificar que el
  grupo de `RadioButton` se ve, se agrupa y se lee bien en las tres pantallas donde aparece.

## 3. Cómo registrar una ejecución

Antes de empezar, anota versión candidata, plataforma(s) cubiertas y fecha. Al terminar, dos estados
válidos por punto: `OK` o `FALLO` (con una línea describiendo qué pasó y, si se abrió, el número de
issue/commit del fix). Ningún punto se marca `OK` "de memoria" sin haberlo ejecutado en esa pasada
concreta — si un punto no se pudo probar (por ejemplo, sin acceso a macOS), se marca `NO PROBADO` con el
motivo, nunca se asume aprobado por herencia de una pasada anterior.

```
Versión candidata: _______   Fecha: __________   Plataforma(s): __________   Tester: __________
```

---

## 4. Lector de pantalla real

Contexto: Fase 7 de `analisis-proyecto/roadmap.md` añadió `AutomationProperties.Name` a las 17 vistas que
existían entonces. Desde entonces se han sumado más — a día de hoy, **23 de las 30 vistas `.axaml`**
tienen al menos una `AutomationProperties`; **7 no tienen ninguna** y deberían revisarse primero por si ya
no aplica (algunas pueden ser vistas puramente de layout sin controles interactivos propios):
`LanguageSetupView`, `VaultSetupView`, `FilesView`, `ThemeEditorView`, `TagFilterView`, `NotesView`,
`SkillEditorView`.

Herramienta: **Orca** en Linux (`orca` desde terminal, o `Super+Alt+S` en la mayoría de escritorios GNOME),
**NVDA** en Windows, **VoiceOver** en macOS (`Cmd+F5`). Basta con verificar en una plataforma real por
pasada — no hace falta repetir las tres en cada release candidate, pero si Enki dice soportar las tres,
cada una debe haberse probado al menos una vez antes de la primera release estable.

### Flujos a recorrer (los mismos cuatro que quedaron pendientes en el cierre de la Fase 7)

- [ ] **Crear y abrir una nota**: el lector anuncia el nombre del botón "nueva nota" (no solo "botón"),
      anuncia el título de la nota al enfocar un elemento de la lista, y el editor no queda mudo al enfocar
      el área de texto (anuncia al menos "editor de texto" o el nombre de la pestaña activa).
- [ ] **Crear una tarjeta Kanban**: el lector anuncia el nombre de cada columna al navegar por tabulación,
      el botón "nueva tarjeta" tiene nombre accesible (no icono mudo), y el formulario de edición de tarjeta
      anuncia cada campo (título, descripción, fecha, recurrencia, columna destino) al enfocarlo.
- [ ] **Crear un recordatorio**: el selector de recurrencia (§7 de este documento) anuncia qué opción está
      marcada dentro del grupo, no solo "botón de radio" sin contexto; los campos de hora/minuto
      (`A11y_Reminders_NotifyHourField`/`NotifyMinuteField`) se anuncian con su propósito.
- [ ] **Navegar el menú de Ajustes**: cada sección se anuncia al enfocarla, cada `ToggleSwitch` anuncia su
      etiqueta asociada (no solo "activado"/"desactivado" sin decir de qué), y es posible llegar a cualquier
      control solo con teclado (Tab/Shift+Tab/flechas), sin necesitar el ratón en ningún punto del flujo.

### Verificaciones transversales

- [ ] El orden de tabulación en el tablero Kanban sigue siendo columna a columna, tarjeta a tarjeta dentro
      de cada columna — el orden que la Fase 7 verificó por inspección de código, ahora confirmado
      auditivamente.
- [ ] Ningún foco visual desaparece al navegar por teclado (indicador de foco siempre visible, no solo para
      quien usa ratón).
- [ ] Una notificación nueva (🔔) o un elemento de la bandeja de recordatorios (📥) se anuncia sin que el
      usuario tenga que ir a buscarlo manualmente (verificar si hay alguna región viva/`LiveSetting`; si no
      la hay, documentarlo aquí como hallazgo, no como fallo bloqueante de por sí).
- [ ] Los siete `.axaml` sin `AutomationProperties` (arriba) se revisan uno a uno: si tienen controles
      interactivos reales, es un hallazgo que pasa al roadmap; si no, se anota aquí que se revisó y no
      aplica.
- [ ] Los diálogos modales (confirmación de borrado, vaciar papelera, etc.) atrapan el foco dentro del
      diálogo y devuelven el foco al control que lo abrió al cerrarse.

---

## 5. Render visual de wikilinks y enlaces rotos

Contexto: Fase 5 distinguió tres estados de wikilink (`Resolved`/`Broken`/`Ambiguous`,
`WikiLinkParser.ResolveTargetWithStatus`) y hasta ahora solo se ha verificado por tests headless y lectura
de la plantilla `.axaml` — nunca visto renderizado. En el editor en vivo
(`MarkdownLivePreviewColorizer`), el criterio de estilo es:

| Estado | Color | Estilo de texto |
|---|---|---|
| `Resolved` | `Accent` (color de enlace normal) | subrayado |
| `Broken` | `Danger` | subrayado |
| `Ambiguous` | `Danger` (mismo color que `Broken`) | subrayado + **cursiva** (única diferencia visual entre los dos) |

### Editor — vista en vivo

- [ ] `[[Nota que existe]]` se ve en el color de acento normal, igual que un enlace Markdown `[texto](url)`.
- [ ] `[[Nota que no existe]]` se ve en `Danger` (rojo/color de alerta del tema activo), subrayado, **sin**
      cursiva.
- [ ] Dos notas con el mismo título (crear deliberadamente "Nueva nota" en dos carpetas distintas de la
      misma sesión) hacen que `[[Nueva nota]]` se vea en `Danger` **con** cursiva — visualmente distinguible
      de un enlace simplemente roto, no solo "ambos rojos e idénticos".
- [ ] El color no cambia a media escritura de forma intermitente: escribir dentro de un `[[...]]` ya
      existente no lo hace parpadear entre roto/resuelto antes de terminar la palabra (criterio ya
      documentado en el comentario de `WikiLinkNotesProvider`: null hace que todo se vea resuelto en vez de
      parpadear roto un frame).
- [ ] Cambiar de pestaña de editor reapunta el color correctamente al conjunto de notas de la pestaña activa
      (abrir dos notas de dos sesiones distintas, una con el link roto y otra con el mismo target resuelto,
      confirmar que cada pestaña colorea según su propio alcance).
- [ ] Repetir la comprobación de arriba con el tema oscuro, el tema claro, y al menos un tema personalizado
      importado (Ajustes › Apariencia › tema personalizado) — `Danger`/`Accent` deben seguir siendo
      distinguibles entre sí y del texto normal en los tres.
- [ ] Ctrl+click sobre un enlace resuelto navega a la nota; sobre uno roto o ambiguo, no navega y no lanza
      ningún error visible (confirmar el comportamiento exacto — silencioso vs. notificación — contra lo que
      documente `EditorViewModel.OpenWikiLink`).

### Kanban — icono de nota vinculada rota

- [ ] Una tarjeta con nota vinculada existente muestra el icono `NoteTextOutline` normal.
- [ ] Tras borrar o mover esa nota fuera del vault, la misma tarjeta cambia a `NoteAlertOutline` (icono de
      alerta) sin necesitar recargar la tarjeta manualmente.
- [ ] El tooltip del icono de alerta muestra el texto de "nota vinculada no encontrada" (mismo texto que el
      aviso al hacer clic fallido) — comprobar en al menos dos idiomas (p. ej. español y un idioma
      distinto) que el tooltip está traducido y no en blanco.
- [ ] El popover de detalle de tarjeta (`KanbanCardDetailViewModel`) refleja el mismo icono/tooltip que la
      tarjeta en el tablero, sin divergir.

### Backlinks y borrado

- [ ] El panel de backlinks de una nota lista exactamente las mismas notas que enlazan a ella que las que
      se colorean como `Resolved` en sus respectivos editores (misma resolución, sin divergencias entre
      Ctrl+click, backlinks y colorizador).
- [ ] Borrar (mover a papelera) una nota con backlinks muestra el aviso de "N notas quedarán con un enlace
      roto" antes de completar el borrado, con el número correcto.
- [ ] Tras ese borrado, reabrir las notas afectadas confirma que sus `[[...]]` ahora se ven en `Danger`
      (rotos de verdad, no solo advertidos).

### Exportación (alcance reducido, verificar que no rompe nada)

- [ ] Exportar una nota con un wikilink roto a HTML no lanza error; el wikilink se renderiza con la clase
      `.wikilink` genérica (subrayado punteado), sin distinguir roto/resuelto — esto es el comportamiento
      esperado (el documento exportado no tiene contexto de vault), no un hallazgo.

---

## 6. Reconexión real de Polar Realtime

Contexto: Fase 9 añadió reconexión automática a `PolarRealtimeService` cuando el socket se cae solo
(`SocketState.Close`/`Error` → `IsConnected = false` → el mismo tick de `_polarSyncTimer` que ya reintenta
sync con backoff exponencial llama de nuevo a `ConnectAsync()`). Nunca se ha probado contra una caída de
red real, solo por inspección de código contra la API de `Supabase.Realtime` 7.4.0. Requiere **una cuenta
Polar real vinculada** en dos sesiones (o dos dispositivos) si se quiere además verificar la propagación
del cambio, no solo la reconexión.

Recordatorio de temporización antes de cronometrar nada: intervalo base **25 s**, backoff exponencial hasta
un máximo de **5 min** mientras la app no consiga sincronizar; se resetea al intervalo base en cuanto un
intento de sync vuelve a alcanzar el servidor.

- [ ] Con la app abierta y Polar vinculado, confirmar en los logs (`LogService`, ruta de log local) que
      `PolarRealtimeService` conectó al arrancar/vincular.
- [ ] Cortar la red del equipo unos segundos (desactivar Wi-Fi, desenchufar el cable, o bloquear el host de
      Supabase por firewall/`hosts` un momento) y confirmar en el log que el socket pasa a cerrado/erróneo.
- [ ] Restaurar la red y confirmar, **sin reiniciar la app ni tocar nada manualmente**, que dentro de la
      siguiente ventana de backoff (empieza en 25 s) el socket vuelve a conectar solo.
- [ ] Mientras el socket está caído, editar una tarea/lista/recordatorio Polar desde **otro** dispositivo u
      otra sesión con la misma cuenta; confirmar que el cambio llega al primer dispositivo tras la
      reconexión (por el polling de respaldo si no por el propio realtime), sin necesitar un "Sync now"
      manual.
- [ ] Repetir la prueba con una caída de red más larga (varios minutos, para forzar que el backoff suba de
      25 s) y confirmar que igualmente reconecta sin intervención, solo que más tarde.
- [ ] Cerrar sesión de Polar (desvincular) con el socket conectado: confirmar que `Disconnect()` corta
      limpiamente (sin excepciones en el log) y que no queda ningún reintento de conexión en segundo plano
      tras desvincular.
- [ ] Volver a vincular la misma cuenta u otra distinta: confirmar que el socket conecta de nuevo sin
      arrastrar estado de la sesión anterior (mismo criterio que la limpieza de `polar-tasks.json` al
      desvincular).
- [ ] Provocar (o esperar) una renovación silenciosa del token de acceso (`EnsureFreshTokenAsync`) con el
      socket activo; confirmar por log que `RefreshAuthIfNeeded` reaplica el nuevo JWT sin reconectar el
      socket entero.
- [ ] Dejar la app en segundo plano con el portátil suspendido (sleep) 10+ minutos, reanudar, y confirmar
      que el socket (que probablemente se cayó durante la suspensión) reconecta solo sin más que el ciclo
      normal del temporizador — este es literalmente el escenario que motivó la Fase 9 ("un socket caído por
      inactividad, suspensión/reanudación, o un corte de red que no llega a desconectar del todo").

---

## 7. Selector de recurrencia

Contexto: Fase 10 añadió el selector segmentado de recurrencia al editor de Recordatorios (reutilizando el
mismo componente que ya existía en Kanban/Gestión de tareas Polar) — nunca visto renderizado en una
ejecución gráfica real. El selector aparece en tres sitios que deben verse y comportarse de forma idéntica:
editor de tarjeta Kanban, editor de tarea de Gestión de tareas (Polar), y editor de Recordatorios. Las seis
opciones son siempre las mismas: **Ninguna, Diaria, Semanal, Mensual, Lun+Mié, Día 1 de cada mes**.

### Render y agrupación

- [ ] En los tres editores, el grupo de `RadioButton` se ve como un selector segmentado coherente (mismo
      estilo visual que el resto del sistema de diseño), no como radios sueltos.
- [ ] Solo una opción puede estar marcada a la vez dentro de cada editor (el `GroupName` no se filtra entre
      grupos de distintos editores abiertos a la vez, p. ej. una tarjeta Kanban y un recordatorio en dos
      pestañas/ventanas simultáneas).
- [ ] El texto de cada opción no se corta ni se envuelve mal en ninguna de las seis localizaciones — probar
      al menos alemán (`Deutsch`, suele ser el texto más largo) y español, que son los dos extremos de
      longitud habituales en esta app.
- [ ] El chip/etiqueta de recurrencia visible en la tarjeta/fila/lista (`RecurrenceLabel`,
      `PolarRecurrenceFormatter.Format`) muestra el texto correcto para las seis opciones, incluyendo el
      caso cultural (`Lun+Mié` usando `GetAbbreviatedDayName` de la cultura activa — probar en francés o
      alemán para confirmar que no se queda en español ni en inglés por error de fallback).

### Interacción

- [ ] Cambiar de "Ninguna" a cualquier otra opción y guardar persiste la recurrencia (reabrir el
      editor confirma que quedó marcada la opción correcta, no "Ninguna" de nuevo).
- [ ] Cambiar de una recurrencia a otra distinta (p. ej. Semanal → Mensual) sin pasar por "Ninguna" en medio
      funciona igual de bien que pasar por "Ninguna" primero.
- [ ] El selector funciona igual con teclado (flechas para moverse entre opciones del grupo, Espacio/Enter
      para marcar) que con ratón — parte de la verificación de accesibilidad de §4, pero specifically para
      este control porque es un `RadioButton.GroupName`, no un `ComboBox`.

### Comportamiento real de la recurrencia (rollover)

- [ ] Completar una tarea/recordatorio **Diario** hace avanzar la fecha al día siguiente y la tarea
      reaparece como pendiente, visible en Kanban/Gestión de tareas/Recordatorios y en el Calendario si está
      abierto.
- [ ] Completar uno **Semanal** avanza 7 días exactos.
- [ ] Completar uno **Mensual** el día 31 de un mes con 30 o menos días del mes siguiente hace el ajuste de
      overflow esperado (cae en el último día del mes siguiente, no en un mes que no tiene ese día).
- [ ] Completar uno **Lun+Mié** en lunes avanza al miércoles de la misma semana; completado en miércoles
      avanza al lunes de la semana siguiente.
- [ ] Completar uno de **Día 1 de cada mes** avanza al día 1 del mes siguiente, sin importar en qué día del
      mes actual se completó.
- [ ] Un recordatorio recurrente vencido con la app cerrada varios días se reprograma a la ocurrencia
      correcta al reabrir la app (avanza en bucle, no se dispara una sola vez desfasado) — comprobar
      cambiando la fecha del sistema hacia atrás, creando el recordatorio, y adelantando la fecha antes de
      reabrir Enki, si se puede probar en una máquina de pruebas desechable.
- [ ] El rollover disparado desde el Calendario (completar una ocurrencia fantasma) produce exactamente el
      mismo resultado que completar la misma tarea desde su vista nativa (Kanban/Gestión de tareas) — mismo
      criterio que ya pide el punto correspondiente de
      [`13-pruebas-y-checklist-de-calidad.md`](calendar-feature/13-pruebas-y-checklist-de-calidad.md).

---

## 8. Otras verificaciones pendientes de la misma familia (opcional, no bloqueante para 0.2.0)

Detectadas en la misma auditoría (cierre de Fase 10) pero fuera del alcance explícito de este punto del
roadmap — se listan aquí para no perderlas, se ejecutan si hay tiempo en la misma pasada:

- [ ] La barra de "Deshacer" tras descartar un elemento de la bandeja de recordatorios se ve y funciona
      (aparece, cuenta atrás de 6 s, deshacer restaura el elemento).
- [ ] El icono de la aplicación se ve correctamente en la barra de tareas/ventana (Windows), el dock
      (macOS) y el selector de ventanas/`.desktop` (Linux) — especialmente el `.icns` de macOS, construido
      a mano sin `iconutil` real (ver [`empaquetado-y-distribucion.md`](empaquetado-y-distribucion.md)).

---

## 8.1 Notas diarias (propuesta 8)

Lo que los tests automáticos no cubren de [`notas-diarias.md`](notas-diarias.md): que los controles se vean
donde deben y que el calendario se lea bien en los dos temas y con el zoom aplicado.

- [ ] Con una nota diaria abierta, la cabecera del editor muestra día anterior / fecha / día siguiente, y
      el botón "ir a hoy" aparece **solo** cuando la nota no es la de hoy. En una nota normal no aparece
      nada de esto y la cabecera se ve como antes.
- [ ] `Ctrl+Alt+←` / `Ctrl+Alt+→` navegan con el foco dentro del texto de la nota (no solo con el foco en
      la cabecera), y no hacen nada con una nota normal abierta.
- [ ] Con una plantilla de nota diaria configurada, navegar a un día sin nota la crea con la plantilla
      aplicada y con el nombre que dicta su `title`.
- [ ] "Combinar con la plantilla" solo aparece cuando falta alguna sección; su tooltip enumera cuáles; tras
      pulsarlo, lo que estaba escrito sigue intacto y la pestaña "Historial" ofrece la versión anterior.
- [ ] El calendario de constancia se abre desde la vista completa, la vista simplificada y la paleta de
      comandos; se cierra al hacer clic fuera y al volver a pulsar su botón (no se reabre en el mismo
      gesto).
- [ ] Las celdas se distinguen bien en tema claro y en tema oscuro (rellena / hueca / apagada), el día de
      hoy se reconoce por su borde y el panel sigue legible con el zoom de interfaz al 150 %.
- [ ] Con el idioma cambiado en caliente, las iniciales de los días de la semana, el orden de las columnas
      y los textos del panel quedan en el idioma nuevo sin reiniciar.
- [ ] Con un lector de pantalla, cada celda anuncia su fecha y su estado, y las de días futuros se anuncian
      como no disponibles.

---

## 8.2 Cambios externos en el vault (T1)

Lo que los tests automáticos no pueden cubrir de
[`cambios-externos-en-el-vault.md`](cambios-externos-en-el-vault.md): un sincronizador de verdad, con sus
ráfagas, sus temporales y sus relojes, y dos editores escribiendo sobre la misma carpeta a la vez. Los
tests usan un `FileSystemWatcher` real pero un solo proceso y un vault local.

**Montaje**: el vault dentro de una carpeta sincronizada con Syncthing o Dropbox y, aparte, el mismo vault
abierto en otro editor de texto (VS Code, `nvim`, el editor del sistema).

- [ ] Con una nota abierta en Enki y **sin** tocarla, editarla y guardarla desde el otro editor: Enki la
      recarga sola en unos segundos, sin diálogo, el cursor se queda donde estaba y el desplazamiento no
      salta al principio. Aparece una notificación discreta ("nota actualizada desde fuera").
- [ ] Con una nota abierta y **con** texto sin guardar, editarla y guardarla desde el otro editor: aparece
      el diálogo de conflicto, el indicador de guardado deja de avanzar y el texto del editor sigue
      intacto. Esperar un minuto sin tocar nada: el archivo en disco **no** cambia (el autoguardado está
      realmente en pausa).
- [ ] En ese diálogo, "Ver diferencias" muestra las dos versiones línea a línea y se lee bien en tema claro
      y oscuro y con el zoom de interfaz al 150 %.
- [ ] "Conservar la mía": el archivo en disco pasa a tener el texto del editor y la pestaña "Historial"
      ofrece la versión de disco descartada con el motivo "conflicto externo".
- [ ] "Cargar la de disco": el editor pasa a mostrar el texto ajeno y el "Historial" ofrece el texto local
      descartado, también como "conflicto externo".
- [ ] `Esc` cierra el diálogo sin decidir: aparece el aviso de conflicto en la cabecera, el autoguardado
      sigue en pausa y pulsar el aviso vuelve a abrir el diálogo. Cerrar la pestaña en ese estado deja el
      texto sin guardar en el "Historial" de la nota y avisa de ello.
- [ ] Dejar que el sincronizador traiga varios cambios de la misma nota seguidos (editar desde el móvil o
      desde otro equipo varias veces): Enki muestra **un** aviso por cambio, no una ráfaga, y no aparece
      ningún diálogo por los archivos `.sync-conflict` que el sincronizador deje en la carpeta.
- [ ] Borrar la nota abierta desde el explorador de archivos: el texto sigue en pantalla, aparece el
      diálogo de borrado y "Guardar como nota nueva" la recrea con ese contenido.
- [ ] Renombrar la nota abierta desde el explorador de archivos: la pestaña sigue al archivo, el título de
      la cabecera cambia y el siguiente guardado escribe en la ruta nueva.
- [ ] Editar en Enki durante un par de minutos seguidos sin tocar nada fuera: **no** aparece ningún
      diálogo de conflicto (el autoguardado no se confunde con un cambio externo).
- [ ] Con una sesión protegida bloqueada y una nota suya que cambie por sincronización: no aparece ningún
      diálogo ni se lee nada de esa sesión; al desbloquearla, la revisión se retoma.
- [ ] Ajustes › Editor › "Vigilar cambios externos en el vault" apagado: no aparecen avisos, pero editar la
      nota desde fuera y después escribir en Enki sigue abriendo el conflicto en vez de pisar el cambio
      ajeno. Volver a encenderlo retoma la vigilancia sin cerrar y reabrir la pestaña.
- [ ] Con el vault en una unidad de red o en una carpeta muy grande, comprobar en el log
      (`LogService`) que, si la vigilancia degrada a sondeo, queda registrado — y que los cambios se
      siguen detectando, solo que más tarde.
- [ ] Cambiar de vault desde Ajustes con pestañas abiertas y después editar desde fuera una nota del vault
      **anterior**: no aparece ningún aviso (el vigilante ya no la vigila).

---

## 9. Criterio de salida antes de `0.2.0`

- Todos los puntos de las secciones 4 a 7 están en `OK` o `NO PROBADO` con motivo documentado — ningún
  `FALLO` sin resolver o sin decisión explícita de aplazarlo (con justificación) queda abierto.
- Cualquier `FALLO` encontrado se documenta como hallazgo nuevo (en `analisis-proyecto/roadmap.md` o como
  entrada de `changelog.md` si ya se corrige en la misma sesión) antes de cerrar esta pasada.
- Esta checklist se ejecuta al menos una vez completa contra un build de release (no contra `dotnet run`
  en modo debug) antes de la primera versión que se distribuya fuera del equipo de desarrollo.
