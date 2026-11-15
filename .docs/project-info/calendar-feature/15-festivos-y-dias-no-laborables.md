# Festivos y días no laborables

En **Ajustes › Calendario › Festivos y días especiales** se eligen los países cuyos festivos muestra el Calendario. Los fines de semana y los festivos nacionales de los países activos aparecen con un **fondo más claro** que el resto de días en Mes, Semana, Día y el mini-calendario; la Agenda lista los festivos bajo la cabecera del día, aunque ese día no tenga items. Los festivos regionales y los días especiales (Nochebuena, Día de la Madre…) solo se rotulan: no aclaran la casilla porque no son inhábiles para todo el mundo.

## Países disponibles

| Grupo | Países |
| --- | --- |
| Unión Europea | Los 27 estados miembros. |
| Latinoamérica | Argentina, Bolivia, Brasil, Chile, Colombia, Costa Rica, Cuba, Ecuador, El Salvador, Guatemala, Honduras, México, Nicaragua, Panamá, Paraguay, Perú, República Dominicana, Uruguay y Venezuela. |
| Otros países | Estados Unidos y Reino Unido (idiomas de la interfaz que no cubren los dos grupos anteriores; Alemania, Francia y España ya están en la UE). |

## Cómo funciona

- **Sin red.** Las reglas viven en el código (`services/holidays/`): fechas fijas, Pascua occidental y ortodoxa, n-ésimo día de la semana, solsticio de junio y los traslados que fija cada ley (Ley Emiliani en Colombia, feriados trasladables en Argentina y República Dominicana, Ley 19.668 en Chile, puentes cívicos de Ecuador, Día del Rey en Países Bajos). Ningún dato del usuario sale del equipo y el resultado es el mismo en cualquier máquina.
- **Días sustitutivos.** Reino Unido, Irlanda, Bulgaria y Letonia añaden el siguiente laborable libre cuando el festivo cae en fin de semana; Estados Unidos observa el viernes anterior o el lunes siguiente; Panamá y Bolivia trasladan el domingo al lunes. El sustitutivo se rotula como «(día sustitutivo)» y también aclara la casilla.
- **Cambios legales por año.** Cada regla puede limitarse a un intervalo de años (Juneteenth desde 2021, los festivos portugueses suprimidos de 2013 a 2015, Store bededag hasta 2023, Nochebuena festiva en Polonia desde 2025, San Francisco en Italia desde 2026…).
- **Nombres.** Si la interfaz está en el idioma del país se usa el nombre oficial («Natividad del Señor»); si no, el nombre común traducido de los recursos (`Holiday_Common_*`) y, para los festivos propios del país, el nombre en inglés. La descripción emergente muestra siempre el nombre oficial y el tipo de festivo. El nombre del país lo da ICU en el idioma activo.
- **Capas.** Cada país activo es una capa «Festivos · País» en la barra lateral: se oculta sin perder la selección de Ajustes y su color (editable como el de cualquier capa) marca la etiqueta del festivo. Con más de un país, la etiqueta lleva el código del país («ES · …») y la casilla indica «+N» si coinciden varios festivos.
- **Qué no son.** Los festivos no son `CalendarItemViewModel`: no se editan, mueven, completan ni borran, no ocupan hueco de chip, no aparecen en el resumen de «Hoy» ni en la exportación `.ics`.

## Ajustes

| Ajuste | Clave en `AppSettings` | Por defecto |
| --- | --- | --- |
| Países activos (en orden de activación) | `CalendarHolidayCountries` | ninguno |
| Mostrar festivos regionales y días especiales | `CalendarShowSecondaryHolidays` | activado |
| Aclarar los días no laborables | `CalendarShadeNonWorkingDays` | activado |

El botón «Añadir *país*» propone el país de la región del sistema cuando está en el catálogo. Los cambios se aplican al Calendario abierto sin recargar (`CalendarViewModel.ApplyHolidaySettings`). El enlace «Gestionar festivos» de la barra lateral del Calendario abre esta sección de Ajustes.

El fondo aclarado usa el recurso `CalendarNonWorkingDayBrush` (tinta al 7 %), entre `Ink05` (hover y horario laboral) e `Ink10` (costuras), para que un día inhábil no parezca ni resaltado ni fundido con las líneas de la rejilla. Al ser tinta, aclara en el tema oscuro y oscurece en el claro.

## Limitaciones

- No se incluyen los días que se fijan por decreto cada año: puentes turísticos, fiestas locales y autonómicas que cambian de un año a otro, ni vacaciones escolares.
- Los festivos regionales se marcan sin indicar la región concreta salvo en el nombre (p. ej. «Vendredi saint (Alsace-Moselle)»); no hay selección por comunidad o estado.
- Honduras agrupa por ley la «Semana Morazánica» en días que varían cada año; el catálogo usa las fechas conmemorativas (3, 12 y 21 de octubre).
- Las reglas de Pascua ortodoxa y del solsticio solo se calculan entre 1950 y 2099.
- Si un país cambia sus festivos, hay que actualizar `HolidayCatalog.*.cs` y la prueba correspondiente en `HolidayServiceTests`.

## Comprobación manual

1. En Ajustes › Calendario activar España y México. En Mes, diciembre: el 8 y el 25 aparecen aclarados y rotulados con «ES ·»; el 12 rotula «MX · Día de la Virgen de Guadalupe» sin aclarar; los sábados y domingos aparecen aclarados.
2. Ocultar la capa «Festivos · España» en la barra lateral: desaparecen sus rótulos y el 8 deja de estar aclarado; volver a activarla.
3. Desactivar «Mostrar festivos regionales y días especiales»: la Nochebuena deja de rotularse.
4. Desactivar «Aclarar los días no laborables»: ningún día aparece aclarado, pero los rótulos se mantienen.
5. Cambiar el idioma a inglés: los festivos comunes se traducen («Christmas Day») y la descripción emergente conserva el nombre oficial.
6. Pasar a Semana, Día y Agenda: la cabecera de columna rotula el festivo y la columna se aclara; la Agenda muestra los días festivos aunque no tengan items.
