# De alpha a beta — revisión y correcciones

Fecha: 22 de septiembre de 2026.

Esta pasada prioriza integridad de datos, guardado, historial, cierre de editores, copias de seguridad y
sincronización Polar. **No declara la aplicación lista para beta**: los resultados automáticos no
sustituyen la revisión visual, de accesibilidad y de integración en los sistemas de escritorio soportados.

Se ha aplicado la skill local [code-reviewer](agents-skills/code-reviewer/SKILL.md), contrastando las
convenciones de `AGENTS.md`, la documentación y el código. Se mantienen MVVM, composición manual,
las entidades existentes y `NotificationService`; no se añaden almacenes paralelos.

## Fallos críticos corregidos

### A2B-01 — Cambios externos invisibles cuando coinciden fecha y tamaño

- **Reproducción:** abrir una nota, escribir otros bytes del mismo tamaño y restaurar su fecha de
  modificación. `NoteDiskStamps.Check` respondía `Unchanged`, permitiendo sobrescribir la edición ajena.
- **Corrección:** comparar siempre el hash del contenido; los metadatos no autorizan un guardado.
  `ReadFromDisk` calcula texto y hash a partir de una misma lectura, también con notas cifradas.
- **Pruebas:** `NoteDiskStampsTests.Check_WhenContentChangesKeepingTimestampAndSize_ReportsModified` y
  `VaultServiceExternalChangesTests`. La prueba del mismo tamaño y fecha falló antes del arreglo.

### A2B-02 — Escrituras de notas que eludían el control de cambios externos

- **Reproducción:** modificar externamente una nota diaria abierta y combinar su plantilla; el texto
  externo desaparecía. Reescribir etiquetas o wikilinks podía partir de una caché anterior. Abrir una
  pestaña con texto cacheado y adoptar una huella nueva también legitimaba una sobreescritura posterior.
  Renombrar la nota renovaba su huella sin reconciliar el contenido del editor.
- **Corrección:** plantilla diaria, restauración de versiones y cambios masivos pasan por
  `WriteContentChecked`. Las operaciones masivas releen el contenido antes de transformarlo. Abrir una
  pestaña obtiene contenido y huella juntos; cambiar su ruta conserva la referencia previa. Editar
  etiquetas ya no marca el contenido como guardado antes de que la escritura tenga éxito.
- **Pruebas:** combinación diaria frente a edición externa; apertura con caché obsoleta; renombrado con
  borrador local y edición externa; reescritura de etiquetas y wikilinks conservando texto externo;
  conflicto al editar etiquetas en la cabecera.

### A2B-03 — Borradores perdidos al cerrar, cambiar de editor o bloquear una sesión

- **Reproducción:** desconectar el vault con texto sin autoguardar y cerrar su pestaña o la aplicación.
  El guardado fallaba, pero el cierre descartaba el editor. Si el conflicto se descubría durante el último
  guardado, tampoco se conservaba en el historial. Las skills tenían el mismo problema al cerrar o
  sustituir su editor, y el cambio de vault solo comprobaba borradores de notas.
- **Corrección:** `TryPrepareClose` exige que el texto esté guardado o respaldado en el historial. Si no
  puede persistirse, la pestaña y la aplicación siguen abiertas. Las sesiones protegidas conservan su
  clave y muestran su estado desbloqueado hasta poder guardar. Las skills no se descartan si su guardado
  falla; también impiden cambiar de vault. «Salir» desde la bandeja utiliza el cierre cancelable.
- **Pruebas:** cierre fallido seguido de reconexión y reintento; conflicto descubierto durante el cierre;
  copia idéntica ya existente; cierre de skills fallido; bloqueo cifrado rechazado por el editor.

### A2B-04 — Restauración y resolución de conflictos sin respaldo comprobado

- **Reproducción:** restaurar una versión antes de que se guarde el último borrador. Solo se conservaba
  la versión del disco, perdiendo el texto aún en pantalla. Los botones del diálogo de conflicto también
  continuaban cuando no se podía escribir el historial.
- **Corrección:** preservar tanto el contenido del disco como el borrador pendiente antes de restaurar;
  abortar si no existe una copia recuperable. Resolver un conflicto exige igualmente conservar la versión
  descartada. Un vault desaparecido no se recrea al intentar guardar el historial. Una lectura fallida
  de la versión que se va a restaurar no se transforma en una restauración vacía.
- **Pruebas:** restauración con borrador pendiente y con una nueva edición externa; historial no
  escribible al elegir cualquiera de las dos resoluciones. Se comprueba que ninguna versión se pierde.

### A2B-05 — Copias incompletas reemplazaban la protección de copias anteriores

- **Reproducción:** activar el backup automático con un archivo que no se puede incluir, o con el vault
  desconectado. Se anunciaba internamente éxito, avanzaba la fecha y se rotaban las copias antiguas,
  pudiendo eliminar el último respaldo completo.
- **Corrección:** el vault ausente cuenta como entrada fallida. Los backups parciales se identifican
  como `Enki-IncompleteBackup-*`, no participan en la rotación y no aplazan el siguiente intento.
  Únicamente una copia completa permite actualizar la fecha y rotar. Los resultados parciales y errores
  se notifican. Un identificador único evita colisiones entre ejecuciones durante el mismo segundo.
- **Corrección adicional:** el ZIP temporal nace con permisos privados en Unix, y se fuerza a disco
  antes del renombrado. Anteriormente solo se restringían los permisos del archivo terminado.
- **Pruebas:** copia incompleta por ajustes que no se pueden redactar, vault ausente, conservación de
  copias anteriores, fecha sin avanzar, permisos y fallo de commit. Los dos nuevos escenarios de backup
  fallaron antes del arreglo.

### A2B-06 — Cambios remotos omitidos entre consultas de sincronización

- **Reproducción:** una edición remota llega después de consultar su tabla y antes de acabar las otras
  consultas. Guardar como cursor la hora de finalización hace que esa edición quede anterior al cursor
  del siguiente ciclo, sin haberse recibido nunca.
- **Corrección:** guardar la hora de inicio menos un milisegundo, únicamente después de completar el
  ciclo. El solapamiento incluye el límite de la consulta `updated_at > cursor`.
- **Prueba:** `PolarSyncServiceTests.SyncNowAsync_CursorDoesNotSkipChangesArrivingDuringPull`, con
  transporte HTTP simulado, cuatro consultas y comprobación del cursor persistido. No usa cuentas reales.
- **Límite:** el protocolo sigue usando el reloj local; no se ha certificado la integración con relojes
  desajustados entre cliente y servidor ni se ha sustituido por un cursor transaccional del servidor.

## Otras correcciones

- «Conservar la mía» solo anuncia éxito cuando se pudo guardar; un tercer cambio externo vuelve a abrir
  el conflicto sin un aviso de éxito contradictorio.
- Abrir y exportar notas usa lecturas que propagan errores. Una nota ausente, ilegible o con cifrado
  dañado no se convierte en un editor vacío que después pueda sobrescribir el original. El fallo al
  abrir se notifica en todos los idiomas disponibles; se conserva la detección de BOM al decodificar.
- Se actualiza la documentación de cambios externos y backups para describir el comportamiento actual.

## Validación

- Base previa a los cambios: **2.109 tests unitarios/de viewmodels aprobados**.
- Se reprodujeron cuatro fallos nuevos antes de aplicar sus correcciones: huella con metadatos iguales,
  combinación de plantilla diaria, backup incompleto y backup con vault ausente.
- `dotnet test enki/tests/enki.Tests/enki.Tests.csproj --no-restore --verbosity minimal`: **2.128
  aprobados, 0 fallidos, 0 omitidos**; 19 casos adicionales respecto a la base.
- `dotnet build enki/src/enki/enki.csproj --configuration Release --verbosity minimal`: **compilación
  correcta, 0 advertencias y 0 errores**.
- `dotnet test enki/tests/enki.UITests/enki.UITests.csproj --verbosity minimal`: **262 aprobados,
  0 fallidos, 0 omitidos**, con Avalonia Headless.
- `git diff --check`: correcto.
- El primer intento con `--no-restore` falló por artefactos de restauración anteriores que omitían las
  herramientas de diagnóstico de Debug. `dotnet test` restauró las dependencias y resolvió la compilación;
  no fue necesario modificar paquetes ni `Program.cs`.

## Criterio para continuar hacia beta

La arquitectura existente y sus tests permiten añadir regresiones sin duplicar la lógica de producto.
No hay decisiones de producto pendientes para estas correcciones. La valoración de esta revisión es
**continuar la validación antes de publicar**, no certificar que toda la aplicación carece de errores.

- Ejecutar [QA manual](project-info/qa-manual-release-candidate.md) sobre un build Release: accesibilidad,
  teclado, escalado, idiomas, temas, edición Markdown, diagramas y exportaciones.
- Probar cierre normal y desde bandeja, desconexión y reconexión del vault, conflictos al cerrar y sesiones
  cifradas con permisos de disco reales en Linux, Windows y macOS.
- Probar un sincronizador de archivos real y Polar Realtime con cuenta de prueba, incluidos cortes de red.
- La comprobación previa y el reemplazo del archivo no constituyen una transacción con procesos externos:
  sigue existiendo una ventana de concurrencia entre ambas operaciones. No debe describirse como una
  garantía absoluta de exclusión mutua frente a cualquier editor o sincronizador.
- Las copias parciales se conservan fuera de la rotación de completas; un fallo persistente requiere
  revisar esos archivos y la notificación correspondiente.

Los cambios documentales que ya estaban en el árbol de trabajo al comenzar esta revisión se han respetado.
