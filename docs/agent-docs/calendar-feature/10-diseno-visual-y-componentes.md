# 10 — Diseño visual y componentes

## Contexto rápido

Este documento traduce `docs/sistema-diseno-web.md` (el sistema de diseño completo de Enki: brutalismo
editorial monocromo, cero radio de esquina, bordes de 1px, Space Grotesk/JetBrains Mono) a componentes
concretos del calendario. **Léase `docs/sistema-diseno-web.md` completo antes que este documento** — aquí
solo se especifica cómo aplicarlo a piezas nuevas, no se repiten los fundamentos.

## 1. Principio rector

El calendario tiene que verse como una pantalla más de Enki, no como un componente importado de otra
librería. Cero excepciones a: `CornerRadius="0"` (salvo indicadores circulares de "hoy"/estado), bordes
de opacidad de blanco en vez de sombras, tipografía Space Grotesk para todo salvo micro-labels/números en
JetBrains Mono, transición estándar de 300ms `CubicEaseOut`.

## 2. Celda de día (vista Mes)

- `Border` sin relleno propio, `BorderBrush="{DynamicResource Ink10}"` (o el token equivalente ya
  definido para bordes de 1px, ver estilos existentes) en los cuatro lados — la grilla completa se
  construye como una rejilla de líneas de 1px compartidas entre celdas, mismo truco que
  `docs/sistema-diseno-web.md` §3.2 documenta para las rejillas de la web ("border-l/border-t + cada
  celda border-r/border-b").
- Número de día: esquina superior, JetBrains Mono, tamaño pequeño, `White60`/`White70` normal,
  **blanco puro + fondo circular** cuando `IsToday == true` (único uso de radio no-cero en esta pieza,
  igual que el resto del sistema reserva los círculos para indicadores de estado).
- Día fuera del mes actual: número en `White30`/`White40`, chips de ese día no se generan (no hay items
  "fantasma de mes" — un día fuera de mes simplemente no muestra nada, es solo relleno de grilla).
- Chips: ver §4.

## 3. Grilla de horas (vistas Semana/Día)

- Línea de hora: texto pequeño JetBrains Mono, `White40`, alineado a la izquierda de cada franja.
- Separadores horizontales entre horas: `White05` (el más sutil de la escala, ver
  `docs/sistema-diseno-web.md` §1.2) — no debe competir visualmente con los propios chips.
- Rango de horario laboral (`CalendarWorkingHoursStart/End`, `09-configuracion-y-ajustes.md`): fondo
  ligeramente distinto (`White/[0.02]`–`White/[0.03]`, el mismo nivel que ya usa el "glow" sutil del
  admin en la web original) frente al resto de la grilla en negro plano.
- Línea de "ahora": 2px, color de acento de peligro (mismo rojo que el resto del sistema usa para
  error/vencido) cruzando horizontalmente la columna de "hoy", con un pequeño círculo relleno al inicio —
  el único elemento de color saturado permitido en toda la grilla, consistente con "nada de color de
  marca, solo blanco/negro + rojo de peligro + verde de estado" (`docs/sistema-diseno-web.md` §1.3).

## 4. Chip de `CalendarItem`

**Constantes de la paleta del calendario elegidas en Fase 1** (`CalendarItemViewModel.KanbanLayerColorHex`/
`ReminderLayerColorHex`): Kanban usa `#E5E5E5` (gris casi blanco, ya que la tarjeta no tiene color propio
hoy) y Recordatorios usa `#818CF8` (índigo suave) para diferenciarse tanto del gris de Kanban como del
rojo de peligro/verde de éxito ya reservados. El color "vencido" del borde (`EffectiveBorderColorHex`)
reutiliza el mismo `#F87171` que `DangerColor` en `styles/Colors.axaml`.

- Base: `Border` `CornerRadius="0"`, borde de 1px con el `LayerColor` de su origen
  (`03-glosario-y-modelo-de-datos.md` §5) a baja opacidad (`/20`–`/30`), fondo del mismo color a opacidad
  muy baja (`/05`–`/10`) — nunca un fondo sólido saturado, coherente con "nada de color de marca, todo en
  opacidades" del sistema de diseño.
- Texto: título en una línea, truncado con elipsis si no cabe; JetBrains Mono para la hora (si la tiene),
  Space Grotesk para el título — mismo contraste tipográfico mono/display que ya define el resto de la
  app.
- **Franja horaria en el chip posicionado de Semana/Día (addendum post-Fase 5)**: cuando
  `CalendarItemViewModel.HasDuration` es verdadero, la hora de inicio se dibuja arriba (misma posición que
  siempre) y la hora de fin se ancla al borde **inferior** del chip, junto al asa de redimensionado, en
  peso semibold para que se lea como el cierre del par "empieza / termina" en vez de una etiqueta más. El
  chip nunca muestra el rango combinado ("21:30–22:15") aquí — eso se reserva para Mes/Agenda/el popover
  de detalle (§5), donde no hay un alto propio del bloque que ya esté comunicando la duración.
- Estado completado (`IsCompleted == true`): texto tachado, opacidad general del chip reducida (`White50`
  en vez de `White80`).
- Estado vencido (`IsPastDue == true`, y no completado): borde en color de peligro en vez del `LayerColor`
  — el "vencido" siempre gana visualmente sobre el color de origen, porque es la señal más urgente.
- Estado fantasma (`IsGhostOccurrence == true`, ver `03-glosario-y-modelo-de-datos.md` §4): borde
  discontinuo (o, si Avalonia no soporta `StrokeDashArray` cómodo en el `Border` usado, opacidad
  reducida al 50% + un glifo pequeño de "repetición" al lado del título) y **sin** fondo relleno — debe
  leerse inequívocamente como "esto todavía no es real".
- Hover: `translateY` sutil + borde a blanco sólido, transición 300ms — mismo micro-patrón que
  `docs/sistema-diseno-web.md` §6.2 documenta para tarjetas de proyecto/tool cards.
- Chip de subtarea (`PolarSubtask`): mismo componente, con un indentado horizontal pequeño y un glifo de
  "hijo de" (línea/conector) para distinguirlo visualmente de una tarea de primer nivel sin necesidad de
  texto adicional.
- **Asa de redimensionado (addendum post-Fase 5, `Border.calendar-chip-resize-handle`)**: solo en el chip
  posicionado de Semana/Día (`CalendarTimedChipTemplate`) y solo cuando `CanResize` es verdadero (tarjeta
  Kanban real, con hora, no fantasma — ver `03-glosario-y-modelo-de-datos.md` §3). Una línea horizontal
  corta (`Ink30`) centrada en una franja de 7px en el borde inferior del chip, **visible siempre, no solo
  en hover** — a propósito, porque Enki no tiene ningún tour de onboarding aparte y el propio chip tiene
  que enseñar el gesto la primera vez que se ve. Al pasar el cursor por encima, la línea pasa a
  `InkBrush` (blanco sólido) y el cursor cambia a redimensionado vertical, mismo lenguaje de "hover =
  borde/acento a blanco sólido" que el resto de chips. Ver `05-interacciones-y-microinteracciones.md`
  §2b para el gesto completo.

## 5. Popovers de creación rápida y detalle

Reutilización literal, no reinterpretación, de los estilos ya definidos en `styles/Styles.axaml`:

- `Border.calendar-popover` para el selector de fecha (idéntico al que ya usa el editor de tarjetas
  Kanban) y para el popover de detalle de un `CalendarItem`.
- `Border.dropdown-popover` para el menú de filtros, el "+N más" de una celda con overflow, y el menú de
  capas si se implementa como popover en vez de panel fijo.
- `Button.dropdown-item` para cada fila seleccionable dentro de esos popovers.

No crear un tercer estilo de popover "propio del calendario" — si ninguno de los dos existentes encaja
para un caso concreto, es señal de que ese caso necesita resolverse con el patrón existente ajustado, no
con un componente visual nuevo.

## 6. Selector de vista (Mes/Semana/Día/Agenda)

Segmented control de cuatro botones, mismo lenguaje que el botón "outline que invierte al hover"
(`docs/sistema-diseno-web.md` §7.2, variante 2): borde blanco/transparente, texto mayúscula con tracking
ancho; el botón de la vista activa usa la variante "primario sólido" (fondo blanco, texto negro) en vez
del outline — mismo contraste que ya usa Enki para distinguir "opción activa" de "opción disponible" en
otros selectores de la app.

## 7. Panel de capas y panel de "no programadas"

Ambos siguen el patrón de lista con "gap de 1px como separador" (`docs/sistema-diseno-web.md` §3.2/§9.8):
contenedor con fondo `Muted15`/equivalente y `Spacing="1"`, cada fila con su propio fondo sólido — así el
fondo del contenedor asoma como línea divisoria sin declarar bordes individuales por fila.

## 8. Animación de entrada al cambiar de vista o de periodo

Al navegar entre meses/semanas/días (flechas de `04-vistas-y-navegacion.md` §9) o al cambiar de vista, la
grilla nueva entra con el mismo patrón de "reveal" que el resto de la app usa para paneles que aparecen:
`Opacity` 0→1 combinado con un `TranslateTransform.Y` pequeño (no los 24px de la web — en una vista de
escritorio que cambia de contenido con frecuencia, una distancia menor, 6–8px, evita que la animación se
sienta lenta en uso repetido), 200–300ms, `CubicEaseOut`. No añadir stagger por celda/chip individual —
correcto para revelar secciones de una landing page, pero en una grilla con hasta 42 celdas sería
ruidoso y ralentizaría la sensación de respuesta de la navegación.

## 9. Checklist de paridad visual específico del calendario

Además del checklist general de `docs/sistema-diseno-web.md` §10 (que sigue aplicando entero):

- [ ] Ningún chip usa un color sólido saturado de fondo — todos son opacidad baja sobre negro.
- [ ] El indicador de "hoy" es el único círculo relleno en la vista Mes; el punto de la línea de "ahora"
      es el único en Semana/Día.
- [ ] Los popovers de calendario reutilizan literalmente `calendar-popover`/`dropdown-popover`, sin
      estilos nuevos de fondo/borde/padding.
- [ ] El rojo de "vencido"/"ahora" es el mismo token de color de peligro que ya usa el resto de la app,
      no un rojo nuevo.
- [ ] Los chips fantasma son inequívocamente distinguibles de los reales sin necesidad de leer el texto.
