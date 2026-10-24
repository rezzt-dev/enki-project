# Apertura y cierre de tareas Kanban

El editor de tarjetas permite establecer **Apertura** (día, opcional) y **Cierre**
(día y hora opcional). Se puede conservar una única fecha o ninguna. Si existen
ambas, el cierre debe ser igual o posterior a la apertura; el formulario muestra
el error y desactiva Guardar cuando el intervalo es inválido.

La apertura se persiste como `KanbanCard.StartDateUtc`. El cierre reutiliza
`DueDateUtc`, manteniendo las fechas de vencimiento de las tarjetas anteriores,
los recordatorios y el criterio de tarea vencida. Ambos campos se guardan en el
JSON del tablero; no existe un almacén adicional para el calendario.

El calendario muestra dos hitos de la misma tarjeta: **Apertura: título** y
**Cierre: título**, en sus respectivos días, en Mes, Semana, Día y Agenda.
Por ejemplo, una tarea del 22 al 29 de octubre de 2026 aparece en esos dos días;
no ocupa los días intermedios. Las tarjetas sin apertura conservan su presentación
anterior. La apertura no se marca como vencida: ese estado corresponde al cierre.

Editar o eliminar cualquiera de los hitos actúa sobre la tarjeta original.
Arrastrar un hito modifica exclusivamente su fecha; los movimientos que invierten
el intervalo se rechazan con la notificación habitual. La apertura siempre es un
día completo. La duración horaria existente sigue asociada al cierre.

Las tarjetas con cualquiera de las dos fechas quedan fuera de «Sin programar».
El tablero y el detalle muestran ambas fechas cuando existe apertura.

## Comprobación manual

1. Crear una tarjeta con apertura 22/10/2026 y cierre 29/10/2026; guardar y reabrir.
2. Ver ambos hitos en el calendario y abrir desde ellos la misma tarjeta.
3. Intentar guardar un cierre el 21/10/2026: comprobar el error del formulario.
4. Arrastrar apertura al 23: comprobar que cierre sigue el 29.
5. Arrastrar apertura después del cierre: comprobar que se conserva la fecha y se avisa.
6. Quitar apertura: comprobar que solo queda el vencimiento habitual.
