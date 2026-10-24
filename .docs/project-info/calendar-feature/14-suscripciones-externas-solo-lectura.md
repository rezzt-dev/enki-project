# Calendarios externos de solo lectura

En **Ajustes › Calendario › Calendarios externos** se puede añadir más de una URL iCalendar. Cada suscripción aparece como capa independiente en el calendario. Sus eventos se muestran en Mes, Semana, Día y Agenda, así como en el resumen de Hoy; se pueden buscar y ocultar por capa. Son de solo lectura: no se pueden mover, redimensionar, completar, editar ni borrar desde Enki. Quitar una suscripción elimina su enlace y caché locales, tras confirmación.

## Obtener un enlace

- **Google Calendar:** en la web, Ajustes › Ajustes de mis calendarios › elegir calendario › Integrar calendario › **Dirección secreta en formato iCal**. [Instrucciones de Google](https://support.google.com/calendar/answer/37648?hl=es). La dirección es privada y debe tratarse como una contraseña; una cuenta de trabajo o centro educativo puede no ofrecerla.
- **Apple iCloud:** compartir el calendario como **Calendario público** y copiar el enlace `webcal://`. Enki lo convierte a HTTPS para descargarlo. **Cualquiera con el enlace puede ver los eventos**; no equivale a una suscripción privada a una cuenta iCloud. [Instrucciones de Apple](https://support.apple.com/guide/calendar/share-calendars-icl32362/mac).

La función acepta enlaces HTTPS y `webcal://` convertibles a HTTPS. No inicia sesión en Google ni iCloud ni implementa CalDAV/OAuth. Para importar una copia editable de un archivo `.ics`, sigue existiendo **Ajustes › Importación externa**: esa operación crea recordatorios y no se actualiza posteriormente.

## Actualización y almacenamiento

- Descarga inicial al añadir la URL y al arrancar Enki. Actualización automática cada 15 minutos mientras la app está abierta; el botón **Actualizar** fuerza una lectura inmediata.
- La última respuesta válida queda en `external-calendars.json` en la carpeta de datos de la instalación (`AppDataDirectory.Resolve()`), junto con la URL, ETag y fecha de actualización. El archivo se escribe de forma atómica y con permisos privados. No se sincroniza con el vault ni se incluye en la exportación de ajustes.
- La descarga tiene un límite de 5 MB, tiempo máximo de 15 segundos, no sigue redirecciones y conserva los eventos anteriores ante fallos de red o formato. Un fallo automático se avisa una vez hasta que la fuente vuelva a funcionar; el botón manual muestra el fallo en Ajustes.
- El motor de iCalendar expande recurrencias, excepciones y zonas horarias dentro del rango visible. Los eventos de varios días se proyectan una vez por cada día del intervalo. Se aplica un límite de evaluación para evitar que una fuente defectuosa bloquee la vista.

## Comprobación manual

1. Añadir un calendario de prueba con un evento horario, uno de día completo, uno de varios días y una serie con una excepción; verificar las cuatro vistas y la capa lateral.
2. Intentar arrastrar, redimensionar o abrir acciones de modificación de esos eventos: no deben ofrecerse.
3. Desconectar la red, pulsar **Actualizar** y comprobar que se muestra un aviso sin perder los eventos previos.
4. Volver a conectar, modificar el calendario de origen y actualizar; comprobar que el cambio aparece sin duplicados.
5. Quitar la suscripción y reiniciar; los eventos y la URL deben haber desaparecido.
