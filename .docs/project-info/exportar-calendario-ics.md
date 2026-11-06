# EXPORTAR EL CALENDARIO A ICS

> Seguimiento de **MED-04**. Calendario › botón de exportar › «Calendario (ICS)». Acción manual y local: no
> hay servidor, suscripción ni cuenta.

## 1. QUÉ SE EXPORTA

`CalendarIcsExportService` toma los elementos de `CalendarAggregationService` y no escribe nada en ninguna
entidad:

- tarjetas Kanban con fecha de inicio o de cierre (dos eventos si tienen las dos, como en el calendario);
- tareas y subtareas de Polar con fecha;
- recordatorios.

**Quedan fuera:** lo completado (en otro calendario sería ruido), los eventos de calendarios externos
suscritos (ya viven en otro calendario) y las ocurrencias «fantasma» (se exporta la regla, no cada fecha).

## 2. CÓMO SE ESCRIBE

| caso | resultado |
| --- | --- |
| elemento con hora, sin repetición | `DTSTART` en UTC (`…Z`): el mismo instante en cualquier calendario |
| elemento con hora y duración | `DTEND` = inicio + duración; sin duración no hay `DTEND` (evento puntual, como en Enki) |
| día completo (medianoche local) | `DTSTART;VALUE=DATE` y `DTEND` al día siguiente |
| repetido | hora de pared en la zona del sistema con su `VTIMEZONE` y un `RRULE` |
| texto | escapado y plegado a 75 bytes por la serialización de `Ical.Net` |
| `UID` | estable (`kanban-<id>@enki`, `reminder-<id>@enki`, `-start` para la apertura): exportar otra vez actualiza en vez de duplicar |

Recurrencias (`PolarRecurrence` → `RRULE`): diaria `FREQ=DAILY`; semanal `FREQ=WEEKLY`; mensual
`FREQ=MONTHLY`; lunes y miércoles `FREQ=WEEKLY;BYDAY=MO,WE`; primer día del mes
`FREQ=MONTHLY;BYMONTHDAY=1`.

**Por qué la hora de pared y no UTC en los repetidos.** Enki repite sobre la hora local
(`PolarTaskService.CalculateNextDueDate`): «cada día a las 09:00» sigue siendo las 09:00 tras un cambio de
horario. En UTC el calendario destino lo movería una hora. Hay prueba con `Europe/Madrid` a través del cambio
de octubre. Sin identificador IANA de la zona (Windows sin equivalente) los repetidos caen a UTC.

## 3. DIFERENCIAS CONOCIDAS

- «Mensual» de Enki suma un mes a la fecha anterior (31 de enero → 28 de febrero → 28 de marzo);
  `FREQ=MONTHLY` omite los meses que no tienen ese día.
- Una tarea repetida se exporta con la fecha actual como inicio de la serie; las ocurrencias ya pasadas no se
  reconstruyen.
- No se exportan prioridad, etiquetas ni color de capa.

## 4. COMPROBADO

Con `Ical.Net` (análisis del archivo, ocurrencias a través del cambio de horario) y con el lector de
importación de Enki (`ReadGoogleCalendar`): mismas horas, duraciones y serie. **No** se ha importado en Google
Calendar, Thunderbird, Apple Calendar ni en un móvil.

Pruebas: `CalendarIcsExportServiceTests`.
