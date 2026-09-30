# Kanban: múltiples tableros por vault

Estado: propuesta de producto y análisis técnico; pendiente de implementación.
Fecha: 30 de septiembre de 2026.

## 1. Objetivo y recomendación

Permitir que el usuario organice sus tarjetas en tantos tableros temáticos como necesite dentro de
un mismo vault: Trabajo, Estudios, Personal o proyectos concretos. Cada tablero tiene sus propias
columnas, tarjetas y tareas archivadas. No se establece un límite artificial de tableros.

La capacidad se activa desde **Ajustes → Kanban → Múltiples tableros**, desactivada por defecto.
Conservar la experiencia actual para quien solo necesita un tablero es un requisito del producto.

La recomendación es añadir un selector de tablero con búsqueda dentro del modo Kanban y mantener
Gestión de tareas de Polar como otro modo de la sección. Calendario, búsqueda y demás vistas globales
consultarán todos los tableros del vault, independientemente del que esté abierto.

Las decisiones de este documento son propuestas, no comportamiento ya implementado ni decisiones
confirmadas por el usuario. La más relevante para validar es cómo desactivar el modo múltiple (§4).

## 2. Lo que existe realmente

| Área | Situación comprobada en el código | Consecuencia |
| --- | --- | --- |
| Modelo | `KanbanBoard` tiene nombre, columnas y papelera; carece de identificador | Añadir identidad estable para cada tablero |
| Servicio | `KanbanService.Board` representa un solo tablero | Cambiar el almacenamiento y revisar sus consumidores |
| Vista | `KanbanViewModel` alterna `Board` y `TaskManagement` | Separar la selección del modo de la selección del tablero |
| Polar | Su modo depende de `PolarAuthService.IsLinked` y usa entidades y servicios propios | Los tableros locales no necesitan una cuenta Polar |
| Persistencia | La composición crea `KanbanService` con `VaultService`; `VaultDataStorage` resuelve `<vault>/.tasks/kanban.json` | Mantener datos separados por vault |
| Ajustes | `SettingsService` guarda las preferencias en `<vault>/.enki/settings.json`, salvo claves de instalación | El interruptor debe ser una preferencia del vault |
| Integraciones | Numerosos consumidores recorren `Board.Columns` | Sustituir esas lecturas por consultas explícitas según su alcance |
| Archivo | Las tarjetas archivadas siguen en su columna con `ArchivedAtUtc` | Conservar esa representación y las reglas de cada consumidor |

Las convenciones generales de `AGENTS.md` describen AppData, pero estos servicios ya tienen
persistencia vinculada al vault. Aquí manda el código actual. Se conserva el constructor con
`storageDirectory` para pruebas y el comportamiento existente antes de configurar un vault.

Polar no es un segundo tablero Kanban: tiene listas y tareas propias. Esta propuesta amplía
exclusivamente los tableros de `KanbanCard`; no copia tareas Polar ni cambia su contrato de sincronización.

## 3. Alcance de la primera versión

- Activar y desactivar la capacidad por vault.
- Crear, seleccionar, renombrar y eliminar tableros vacíos.
- Elegir un tablero predeterminado y recordar el último abierto.
- Mover una tarjeta existente a otro tablero y columna conservando su identidad.
- Mantener columnas, filtros, ordenación y archivado dentro del tablero seleccionado.
- Actualizar todas las integraciones existentes para que ninguna tarea desaparezca de ellas.
- Migrar los datos actuales sin perder identificadores, relaciones ni papelera.

Se posponen duplicación de tableros, plantillas configurables, tableros compartidos, sincronización
con Polar, archivado de tableros y borrado de tableros con contenido. También se pospone una vista
«Todos los tableros»: combinar columnas con flujos diferentes requiere otro diseño de producto.
Estas ampliaciones no son necesarias para resolver la organización temática.

## 4. Activación y desactivación

### Activar

Texto propuesto del ajuste: **Múltiples tableros**.
Descripción: «Organiza tus tareas en tableros independientes dentro de este vault».

Activar conserva el tablero actual, su nombre y todos sus datos. Aparecen el selector y las acciones
de gestión. El tablero existente pasa a ser el predeterminado; no se crean tableros ni tarjetas de
ejemplo adicionales. La actualización se aplica sin reiniciar.

### Desactivar: propuesta recomendada

Con un único tablero se desactiva inmediatamente y desaparecen los controles de gestión múltiple.
El tablero restante sigue siendo el tablero normal, aunque no sea el original.

Con varios tableros, el interruptor permanece activado y se explica:
«Para volver al modo de un solo tablero, conserva uno y mueve a él las tareas que quieras mantener».
Se ofrece **Gestionar tableros** para acceder a los existentes. No se mueve ni se elimina contenido
como efecto secundario del interruptor.

El coste de esta opción es una desactivación menos inmediata después de usar varios tableros.
A cambio, el modo simple significa realmente un solo tablero y no quedan tareas ocultas con avisos,
resultados de búsqueda o citas del calendario que llevan a un tablero inaccesible.

Alternativas consideradas:

| Alternativa | Problema |
| --- | --- |
| Ocultar los tableros adicionales | Hay que resolver accesos desde Calendario, búsqueda y recordatorios a contenido oculto |
| Fusionarlos automáticamente | No hay correspondencia fiable entre columnas personalizadas; puede alterar estados e historial |
| Desactivar solo la creación de nuevos tableros | Es segura, pero el ajuste ya no activa/desactiva una experiencia de tablero único |

Si se exige desactivar en cualquier momento conservando todos los tableros, la tercera opción es la
alternativa preferible, con otro nombre y descripción que expresen exactamente esa limitación.

El estado incoherente «modo simple + varios tableros», por restauración de ajustes o modificación
externa, debe recuperarse mostrando el modo múltiple y notificándolo; nunca ocultando contenido.

## 5. Interfaz e interacción

### Jerarquía de navegación

Mantener la sección **Kanban** en la navegación principal. En modo múltiple, el segmento actual pasa
a **Tableros / Gestión de tareas**; el segundo conserva su disponibilidad ligada a Polar. En modo
simple se conserva la denominación y presentación actuales.

Esquema orientativo de la cabecera con el modo múltiple activo:

```text
KANBAN                            [ TABLEROS ] [ GESTIÓN DE TAREAS ]

[ Trabajo                              ▾ ]  [ ⋯ ]  [ Exportar ] [ + Columna ]

[ Buscar en este tablero… ]   [ Prioridad ]   [ Etiquetas ]   [ Orden ] [ Archivo ]
───────────────────────────────────────────────────────────────────────────────
  PENDIENTE                    EN CURSO                      HECHO
  tarjetas                     tarjetas                      tarjetas
```

El selector hace visible el nombre actual; su lista ofrece búsqueda, marca del tablero seleccionado,
indicación de predeterminado y **Crear tablero**. «Gestionar tableros» abre una lista compacta con las
mismas acciones por tablero, sin convertirla en una tercera página obligatoria para navegar.

No añadir una pestaña por tablero: con muchos nombres se agota el ancho. Tampoco añadir una barra
lateral permanente que reduzca el espacio horizontal del Kanban. La lista del selector tiene altura
limitada, desplazamiento y preparación para virtualización si el volumen lo requiere.

El menú `⋯` del tablero contiene Renombrar, Usar como predeterminado y Eliminar tablero. Crear
permanece visible en el selector, no solo en un menú contextual. Eliminar explica por qué no está
disponible si el tablero contiene tarjetas o es el último.

### Crear y cambiar de tablero

Crear pide únicamente un nombre. Se propone recortar espacios exteriores, exigir entre 1 y 80
caracteres y evitar nombres duplicados sin distinguir mayúsculas dentro del vault. Estos límites son
decisiones nuevas de la propuesta, no restricciones del modelo actual.

El tablero nace con las tres columnas base por rol y sin tarjetas de ejemplo. Se selecciona al
guardar y muestra «Este tablero todavía no tiene tareas», con una acción para crear la primera.
La primera creación no cambia el predeterminado.

Al cambiar de tablero se reconstruyen solo sus columnas y tarjetas. Búsqueda, filtros, orden y
posición de desplazamiento se recuerdan por tablero durante la sesión; inicialmente solo la
selección del tablero se persiste. Los menús y diálogos cerrados no se reabren por recordar el estado.

Un formulario con cambios pendientes debe resolverse con Guardar, Descartar o Cancelar antes de
cambiar, también si el cambio procede de un enlace global. Cancelar mantiene el tablero y el borrador.
Un arrastre en curso se cancela antes de cambiar; no puede terminar en columnas de otro tablero.

### Mover tarjetas

Añadir **Mover a otro tablero…** al detalle y al menú de tarjeta. Un formulario muestra tablero y
columna destino; cambiar el tablero actualiza las columnas válidas. Cancelar no modifica el modelo.
Tras mover, se permanece en el origen y se ofrece un acceso al destino en el flujo de confirmación.
La operación debe poder hacerse íntegramente con teclado.

Preseleccionar una columna con el mismo `CoreColumnRole` cuando exista; para columnas personalizadas,
exigir selección explícita, sin deducir equivalencia por nombre. Mover conserva `Id`, nota vinculada,
etiquetas, vencimiento, duración, creación y subtareas. Las transiciones de rol aplican las reglas
actuales de completado y automatización. De Hecho a Hecho se conserva `CompletedAtUtc`; de Hecho a
otra situación se limpia. Un cambio de tablero entre roles equivalentes no reinicia las subtareas.

Una tarjeta archivada movida a Hecho mantiene su archivo; al moverla a otra situación se desarchiva
y se informa en el formulario. Esto permite reorganizar el histórico sin borrarlo ni duplicarlo.

### Diseño, accesibilidad y espacios reducidos

Reutilizar `Border.dropdown-popover`, segmentos, botones y recursos del proyecto: esquinas rectas,
bordes de 1 px, Space Grotesk y JetBrains Mono según su función. No introducir colores por tablero
como nuevo sistema visual. Conservar los temas y preferencias tipográficas existentes.

En ventanas estrechas o con zoom alto, separar navegación, selector y herramientas en filas;
las acciones secundarias pueden pasar a un menú, pero nombre y selector deben seguir visibles.
Nombres largos llevan elipsis y acceso al texto completo, sin ensanchar las columnas.

El selector debe soportar teclado, búsqueda, Escape y retorno del foco al botón que lo abrió.
Los controles tendrán nombres accesibles, selección anunciada y foco visible. Todos los textos,
errores y plurales se incorporan a los recursos de los idiomas disponibles.

## 6. Modelo y persistencia propuestos

Mantener **un solo archivo** `<vault>/.tasks/kanban.json`. Un contenedor `KanbanStore` incorpora
`SchemaVersion`, `DefaultBoardId` y `Boards`; cada `KanbanBoard` gana un `Id` estable. La lista conserva
el orden de creación para esta primera versión. Columnas y tarjetas permanecen anidadas como ahora.

No añadir copias de tarjetas, un almacén por módulo ni referencias redundantes de pertenencia en
cada tarjeta: su ubicación en el modelo determina el tablero. Mantener identificadores únicos en el
vault para tableros, columnas y tarjetas permite seguir resolviendo enlaces existentes por tarjeta.

`KanbanMultipleBoardsEnabled` y `KanbanLastSelectedBoardId` se proponen en `AppSettings`, junto a las
preferencias Kanban existentes, fuera de `InstallationKeys`. Su persistencia seguirá siendo por vault.
Para captura rápida, añadir `QuickCaptureKanbanBoardId` a `VaultSettings`, junto al destino de columna
que ya existe. El tablero predeterminado pertenece al almacén Kanban, no a dos servicios distintos.

La selección inválida cae al predeterminado y después al primer tablero válido. Siempre debe existir
al menos un tablero y un predeterminado válido. Ninguna consulta global depende de la selección.

### Evolución de KanbanService

Exponer la colección real y operaciones concretas como `CreateBoard`, `RenameBoard`, `DeleteEmptyBoard`,
`FindCardLocation` y `MoveCardToBoard`. La búsqueda devuelve referencias al tablero, columna y tarjeta
existentes; no crea otra entidad persistida. Mantener composición manual y MVVM estricto.

La selección de interfaz pertenece al viewmodel y a su preferencia. Evitar transformar silenciosamente
`KanbanService.Board` en «el tablero que se está viendo»: haría que las lecturas globales cambiasen al
navegar. Revisar todos sus usos y retirar esa API ambigua al completar la conversión.

Las reglas compartidas para mover tarjetas deben centralizarse en `KanbanService`, adaptando tanto
el movimiento actual entre columnas como el nuevo entre tableros. El movimiento entre tableros
valida origen y destino antes de mutar y guarda el almacén completo una sola vez.

Conservar `Save()` y `BoardChanged` como aviso de cambios de datos del conjunto para no introducir
un mecanismo paralelo. Cambiar únicamente la selección no dispara ese evento. Ante fallo de guardado,
revertir la operación en memoria y comunicar el error por el sistema de notificaciones existente;
no presentar éxito ni refrescar consumidores con un estado no persistido.

### Migración y recuperación

1. Distinguir explícitamente el formato antiguo de tablero único del nuevo formato versionado.
2. Respaldar el archivo original antes de convertirlo. Si el respaldo falla, no ejecutar la conversión.
3. Envolver el tablero existente con un nuevo identificador estable; preservar nombres, identificadores
   de columnas/tarjetas, orden, archivo, papelera y todos los campos existentes.
4. Aplicar las migraciones heredadas de columnas y roles sin ejecutarlas de nuevo sobre datos ya migrados.
5. Validar invariantes y guardar mediante `JsonFileStore`, manteniendo escritura atómica y bloqueo.
6. Garantizar idempotencia: reabrir no crea tableros adicionales ni cambia identificadores.

La evolución de formato se ejecuta al cargar, aunque el modo múltiple esté desactivado: el interruptor
controla la experiencia, no dos modelos de persistencia diferentes. Documentar que volver a una versión
antigua de Enki requiere restaurar la copia previa; no prometer compatibilidad hacia atrás del escritor.

Un formato futuro desconocido o datos corruptos no deben interpretarse como instalación nueva y
sobrescribirse con un tablero vacío. Conservar el archivo y notificar un fallo recuperable. Las copias
y la restauración del vault deben incluir y verificar el nuevo formato. El bloqueo local y la escritura
atómica no resuelven conflictos entre dispositivos que sincronicen el vault; esta propuesta no añade
un protocolo de fusión distribuida ni cambia las garantías actuales.

## 7. Impacto fuera del tablero

| Consumidor | Comportamiento necesario |
| --- | --- |
| `CalendarAggregationService` | Agregar todos los tableros, incluidas archivadas según el comportamiento actual |
| `CalendarViewModel` | Resolver edición, borrado, subtareas y cambios de fecha contra el propietario real; creación con tablero y columna explícitos |
| `CalendarLayerColorService` | Recorrer todas las columnas; conservar claves `kanban:{columnId}` y colores existentes; mostrar tablero junto a columna |
| Búsqueda global | Buscar en todos y mostrar «Tablero · Columna»; abrir el destino correcto |
| `ReminderInboxViewModel` | Agregar pendientes de todos los tableros y mantener descartes/aplazamientos por `cardId` |
| `DashboardViewModel` | Mostrar total del vault, excluyendo archivadas como hoy; rotular el alcance y agrupar estados por rol |
| Estadísticas e informes | Mantener histórico global; distinguir «Tablero · Columna» en desgloses de columnas personalizadas |
| `EditorViewModel` y `SessionsViewModel` | Encontrar tareas vinculadas en todos los tableros, sin depender del que esté seleccionado |
| `RelinkNotePath` | Actualizar vínculos de notas en todos los tableros y sus papeleras |
| `TagManagerService` | Contar y renombrar etiquetas en todas las tarjetas del ámbito que ya maneja |
| Captura rápida | Seleccionar y recordar tablero y columna por vault; mostrar el destino final antes de guardar |
| `TrashViewModel` | Agregar papelera de todos, mostrar origen y restaurar en el tablero propietario |
| Exportación PNG/PDF | Exportar el tablero actual e incluir su nombre en título y nombre de archivo |
| Paleta y atajos | Crear tarjeta/columna en el tablero actual; ofrecer crear/cambiar tablero solo cuando corresponda |
| Cambio de vault | Cerrar estado transitorio, recargar almacén/preferencias y descartar selecciones del vault anterior |

Conservar inicialmente una capa Kanban en el calendario. Los selectores de columnas deben distinguir
el tablero. Un filtro adicional por tablero puede llegar después, sin cambiar los identificadores de
las citas ni perder las preferencias de color existentes.

Los accesos por `cardId` de `MainWindowViewModel.Callbacks` y `OpenCardById`/`OpenCardEditorById`
primero resuelven el propietario, seleccionan el modo Board y ese tablero, y luego abren el detalle
o editor. También deben funcionar con tarjetas archivadas, sin desarchivarlas por abrirlas.

Si desaparece el destino recordado de captura rápida, mostrar el predeterminado y su columna base
como nuevo destino antes del envío. No escribir silenciosamente en otro tablero por una preferencia
obsoleta. Si cambia el destino durante el envío, conservar el borrador y pedir una nueva selección.

## 8. Eliminación y papelera

La primera versión permite eliminar exclusivamente tableros sin tarjetas activas, archivadas ni
tarjetas en papelera. Tener columnas vacías no impide eliminarlos. El último tablero no se elimina.
Si se elimina el predeterminado, seleccionar su sustituto antes de guardar la operación.

No crear una papelera de tableros en esta fase. Las tarjetas eliminadas permanecen en
`KanbanBoard.TrashedCards`; su pertenencia permite restaurarlas en el tablero correcto. Si la columna
original desapareció, resolver una columna válida dentro de ese tablero con el criterio existente,
nunca dentro del seleccionado por casualidad. Conservar la retención actual de 30 días y aplicar
purga, vaciado y `CardPermanentlyDeleted` a todas las papeleras.

Para consolidar varios tableros y desactivar el modo múltiple, mover las tarjetas que se conservan;
las que estén en papelera deben restaurarse y moverse o eliminarse definitivamente mediante el flujo
existente. Eliminar tableros o apagar el ajuste nunca vacía la papelera automáticamente.

## 9. Desarrollo y criterios de aceptación

### Fase 1: almacenamiento e invariantes

Introducir `KanbanStore`, identidad de tablero, migración, consultas de ubicación y operaciones
compartidas. Mantener la experiencia simple mientras se adapta el resto; no publicar una versión
que permita crear varios tableros antes de completar las integraciones.

### Fase 2: integraciones y navegación

Actualizar todos los consumidores de `KanbanService.Board`, creación externa, restauración y
resolución de enlaces. Un cambio del tablero visible no altera estadísticas, calendario ni avisos.

### Fase 3: ajustes e interfaz

Implementar el interruptor, selector, creación, renombrado, predeterminado, traslado y eliminación
vacía. Incorporar búsqueda del ajuste, recarga al cambiar de vault, traducciones y accesibilidad.

### Fase 4: validación

Ampliar los tests xUnit de servicios y viewmodels, y los tests de interfaz existentes:

- Migración de un tablero real con columnas personalizadas, vínculos, archivadas y papelera; segunda
  apertura idempotente; ausencia de archivo, archivo corrupto, versión desconocida y fallo de escritura.
- Unicidad de IDs, validación de nombres, persistencia del predeterminado y recuperación de selección.
- Activar sin duplicar datos; desactivar con un tablero; impedir desactivar con varios; recuperar
  incoherencias entre preferencias y almacén sin ocultar contenido.
- Mover conserva `cardId`, notas, fechas y referencias de avisos; automatización y archivo coherentes;
  fallo de guardado conserva el origen y no deja duplicados.
- Todas las vistas globales incluyen tareas de un tablero distinto del abierto. Abrirlas selecciona
  su propietario; mutarlas o borrarlas modifica solo la entidad correcta.
- Restaurar y purgar tarjetas de un tablero no seleccionado; impedir eliminar tableros con histórico
  o papelera; conservar notificaciones de borrado permanente.
- Captura rápida y creación desde calendario muestran y respetan su destino aunque cambie la vista Kanban.
- Cambiar de vault no arrastra datos ni filtros; borradores y arrastres no sobreviven hacia otro destino.
- Selector con teclado y lector de pantalla, nombres largos, traducciones extensas, zoom alto y ambas
  orientaciones; exportación con el tablero y nombre correctos.

Ejecutar `dotnet test enki/tests/enki.Tests/enki.Tests.csproj` y las pruebas UI pertinentes durante la
implementación. Medir carga, selección y búsquedas con volúmenes crecientes, por ejemplo 1, 20 y 100
tableros y miles de tarjetas; son escenarios de prueba, no una promesa de rendimiento ni límites de uso.
Evitar construir todos los controles visuales al abrir un único tablero.

El esfuerzo es transversal: añadir el selector es una parte pequeña; la mayor parte está en migración,
integraciones y conservación de la identidad de las tareas. No estimar fechas sin desglosar esas tareas.

## 10. Referencias

- [Sistema visual](sistema-diseno-web.md).
- [Organización de Ajustes](ajustes-docs/06-organizacion-y-navegacion.md).
- [Tareas archivadas](kanban-tareas-archivadas.md).
- [Arquitectura del calendario](calendar-feature/11-arquitectura-tecnica.md).
- [Notificaciones y recordatorios](notificaciones-y-recordatorios.md).
- [Roadmap existente](analisis-proyecto/roadmap.md).

El análisis se ha contrastado con los modelos, servicios, viewmodels y vistas citados. No modifica
el estado del roadmap existente ni presenta la propuesta como una funcionalidad terminada.
