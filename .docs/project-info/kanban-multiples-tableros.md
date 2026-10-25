# Kanban: múltiples tableros por vault

Implementación de la propuesta de [múltiples tableros](../kanban-multiples-tableros.md).
Fecha: 30 de septiembre de 2026.

## Uso

En **Ajustes → Kanban → Múltiples tableros** se activa la organización por tableros. Está
apagada por defecto y no necesita una cuenta Polar. La gestión de tareas de Polar continúa
como un modo separado, con sus propias entidades y sincronización.

El selector muestra el tablero actual, permite buscar y crear tableros, y marca el predeterminado.
Las acciones de cada fila permiten renombrar, elegir el predeterminado y eliminar. El nombre se
recorta y admite entre 1 y 80 caracteres; no puede repetirse ignorando
mayúsculas. Un tablero nuevo tiene las tres columnas base y ninguna tarjeta de ejemplo.

Solo se eliminan tableros vacíos: las tarjetas archivadas y la papelera también cuentan como
contenido. El último tablero no se elimina. Si se elimina el predeterminado, el primero restante
pasa a serlo dentro de la misma escritura.

Para desactivar la función hay que conservar un único tablero. El interruptor no fusiona,
elimina ni oculta tareas. Una restauración que combine varios tableros con el ajuste apagado
activa el modo múltiple y lo anuncia en Notificaciones.

**Mover a otro tablero…** está en el detalle y en el menú de tarjeta. El formulario exige una
columna destino; preselecciona el mismo rol base cuando existe, sin comparar nombres de columnas
personalizadas. Al terminar se permanece en el origen y aparece **Abrir tablero destino**.

El nombre del tablero abre el selector; así la cabecera muestra una sola vez el destino actual.
El selector funciona con búsqueda, flechas, Enter y Escape; al cerrarse devuelve el foco a su
botón. La lista tiene altura máxima y `ListBox` virtualizado, distingue el tablero seleccionado
del predeterminado y muestra un estado sin resultados. Crear y gestionar tableros están dentro
del selector. Los nombres largos se eliden y conservan su tooltip. El selector se ajusta al ancho
disponible y las herramientas pueden pasar de fila con poco ancho. Se reutilizan los estilos,
colores y tipografías del proyecto.

## Estado y navegación

- El último tablero abierto se recuerda por vault en `KanbanLastSelectedBoardId`.
- `KanbanMultipleBoardsEnabled` también es una preferencia del vault, no de instalación.
- El predeterminado pertenece únicamente al almacén Kanban.
- Búsqueda, filtros, orden, archivo y desplazamiento se recuerdan por tablero durante la sesión.
- Cambiar de tablero cancela el arrastre y cierra menús y diálogos transitorios.
- Un formulario modificado ofrece Guardar, Descartar o Cancelar antes de navegar; un fallo al
  guardar conserva el formulario y la selección. Esto también se aplica a enlaces globales.
- Al cambiar de vault se eliminan filtros y borradores del anterior, incluida captura rápida.

La selección vive en `KanbanViewModel`. `KanbanService` expone `Boards`, `DefaultBoard`,
`AllColumns`, `AllTrashedCards` y consultas a las entidades reales. Se retiró la API ambigua
`Board`; seleccionar no dispara `BoardChanged` ni modifica lo que ven otros módulos.

## Persistencia y recuperación

Se conserva un solo `<vault>/.tasks/kanban.json`, ahora con este contenedor:

```json
{
  "SchemaVersion": 1,
  "DefaultBoardId": "identificador-estable",
  "Boards": [
    { "Id": "identificador-estable", "Name": "Trabajo", "Columns": [], "TrashedCards": [] }
  ]
}
```

Las tarjetas no almacenan una segunda referencia de pertenencia ni se copian entre módulos:
la ubicación en `Boards → Columns → Cards` determina su propietario. Tableros, columnas y tarjetas
se validan con identificadores únicos en el vault.

La primera carga de un archivo antiguo guarda sus bytes originales en
`kanban.json.pre-multiboard.bak`. Si ya hay una copia diferente, conserva ambas con un sufijo único.
Solo después ejecuta las migraciones heredadas y escribe el contenedor con `JsonFileStore`.
Lectura, respaldo y conversión comparten el bloqueo local; la escritura es atómica. Reabrir un
archivo versionado no repite migraciones ni cambia identidades.

Si no se puede crear la copia, el archivo es inválido o la versión es desconocida, se conserva
el original y se bloquea su escritura. El aviso de recuperación aparece en Notificaciones.
Para recuperarlo, cerrar Enki, restaurar una copia válida de `kanban.json` y volver a abrir el vault.
Las copias ZIP incluyen el almacén y el respaldo de migración; la restauración valida el Kanban
antes de confirmar la carpeta restaurada.

**Volver a una versión anterior de Enki requiere restaurar el archivo previo a la migración.**
El escritor antiguo no comprende el contenedor nuevo. El bloqueo y la escritura atómica siguen
siendo locales: no se incorpora una fusión distribuida para sincronizadores externos.

Las operaciones de tablero, movimiento, edición de tarjeta y purga revierten los cambios en
memoria si falla la escritura; no publican éxito antes de persistir. La restauración de tarjetas
vuelve a su tablero propietario y revierte también un fallo de guardado.

## Reglas de movimiento

Mover conserva la misma instancia e identidad de tarjeta, nota vinculada, etiquetas, creación,
vencimiento, duración y subtareas. Tanto el arrastre como el editor y el formulario entre tableros
usan las reglas de `KanbanService`:

- Entre roles equivalentes no se reinician subtareas ni fechas de completado.
- Entrar en Hecho sella el completado y completa las subtareas.
- Salir de Hecho limpia el completado; entrar en Pendiente desmarca subtareas.
- Una archivada continúa archivada al ir a Hecho; al salir se desarchiva, con aviso en el formulario.
- Un movimiento valida destino y posición antes de mutar, y guarda una sola vez.

## Integraciones

Calendario, búsqueda global, recordatorios, estadísticas, etiquetas, tareas vinculadas a notas
y sesiones consultan todos los tableros. El Dashboard agrega los estados por rol y muestra el
total del vault sin archivadas. Búsqueda, capas de calendario y desgloses de columnas distinguen
**Tablero · Columna**. Los colores de calendario conservan la clave `kanban:{columnId}`.

Abrir una tarea por identificador selecciona su tablero y el modo Kanban, incluidas archivadas.
La creación desde Calendario elige tablero y columna explícitos. Captura rápida recuerda ambos
destinos por vault; si desaparecen, presenta el predeterminado al volver a abrirse. Si desaparecen
mientras se escribe, rechaza el envío y conserva el borrador.

La papelera agrega todos los tableros y conserva la procedencia. Restaurar una columna eliminada
usa una columna del mismo tablero. Purga de 30 días y borrados permanentes recorren todas las
papeleras y mantienen `CardPermanentlyDeleted`.

PNG/PDF usan el tablero abierto, con su nombre en el título y el nombre de archivo. La paleta
ofrece crear y cambiar tablero cuando el modo múltiple está activo.

## Validación

Resultado de la verificación de esta implementación:

- `dotnet test enki/tests/enki.Tests/enki.Tests.csproj`: 2.180 pruebas correctas.
- Pruebas UI de Kanban en esta revisión: 31 pruebas correctas, incluidos anchos lógicos de
  480/600/900 y el estado sin resultados del selector.
- `dotnet build enki/src/enki/enki.csproj -c Release --no-restore`: sin errores ni advertencias.
- `git diff --check`: sin errores de espacios.


Pruebas de servicios y viewmodels: migración con archivo y papelera, respaldo fallido, JSON
inválido, versión futura, IDs duplicados, nombres, predeterminado, borrado protegido, equivalencia
de roles, reversión de escrituras, filtros por tablero, borradores, navegación a archivadas,
preferencias incoherentes, destinos de captura, estadísticas, calendario y restauración.

Pruebas Avalonia: selector y formulario reales, foco, teclado, Escape, nombres de 80 caracteres,
anchos lógicos de 600/900 y ambas orientaciones; además de las regresiones existentes de Kanban,
Calendario, Papelera, Ajustes y exportación.

Medición local orientativa en Debug, 100 tarjetas por tablero; incluye calentamiento del runtime:

| Tableros | Tarjetas | Carga | Selección | Resolver una tarjeta por tablero |
| --- | --- | --- | --- | --- |
| 1 | 100 | 25,80 ms | 2,27 ms | 1,38 ms |
| 20 | 2.000 | 28,16 ms | 0,16 ms | 0,48 ms |
| 100 | 10.000 | 137,64 ms | 4,83 ms | 9,39 ms |

La prueba verifica que solo se construyen los viewmodels de las 100 tarjetas del tablero visible.
Estos números son observaciones de este entorno, no límites de uso ni garantías de rendimiento.

Antes de un release candidate sigue siendo necesaria la comprobación manual con lector de
pantalla real, zoom del sistema, temas, traducciones extensas y un sincronizador de vault real,
junto con [la lista de QA del proyecto](qa-manual-release-candidate.md). Las pruebas headless no
sustituyen esa comprobación.

### Distribución de la cabecera y barra de herramientas

El nombre del tablero es también el selector cuando hay varios tableros. Comparte fila con
los modos, exportación y «Nueva columna»; el resumen aparece debajo, sin duplicar el selector.
El desplegable conserva búsqueda, navegación con teclado y gestión de cada tablero.

La barra agrupa búsqueda y filtros a la izquierda, y archivo y ordenación a la derecha.
Alta prioridad y vencidas muestran icono y contador, con tooltip y nombre accesible.
En Ajustes › Kanban se puede ocultar cada uno de los cuatro botones por separado. Las
preferencias se guardan y actualizan la vista al instante; ocultar un botón conserva el
filtro o la ordenación actuales. «Limpiar» permite retirar los filtros activos.
