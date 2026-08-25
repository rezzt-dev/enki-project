# enki - diseno ux y tema visual

> guia del aspecto grafico, diseno y tema de enki. todos los valores (colores, fuentes, radios, animaciones) estan extraidos del codigo fuente real de la aplicacion (`enki/src/enki/styles/`), para que sirvan como referencia fiel del diseno.

---

## 1. filosofia de diseno

el diseno de enki se puede describir como **"brutalismo editorial suizo, monocromo, dark-first"**:

- **blanco y negro casi puro**, sin colores de marca. los unicos colores reales son **rojo** para error/peligro y **verde** para exito/estado activo. todo lo demas son grises y opacidades de blanco sobre negro.
- **cero border-radius**: botones, inputs, tarjetas, dialogos - todo con esquinas 100% rectas. lo unico circular son los indicadores de estado (puntos, thumb de toggles, badge pulsante).
- **bordes finos de 1px en vez de sombras**: la profundidad se sugiere con lineas de `1px` a bajas opacidades de blanco, no con sombras difuminadas.
- **contraste tipografico extremo**: titulares grandes y condensados en negrita contra micro-etiquetas monoespaciadas en mayusculas con tracking (espaciado entre letras) muy amplio.
- **inversion al hover**: los elementos interactivos invierten el color - fondo transparente/borde blanco → fondo blanco/texto negro (o viceversa). es el micro-patron de interaccion mas repetido.
- **textura de grano** (ruido) superpuesta a opacidad minima sobre el fondo, para que el negro plano no se vea "digital muerto".
- **vidrio esmerilado (glassmorphism)** solo en la barra lateral flotante.
- **movimiento contenido**: transiciones cortas (150-300ms) con easing suave; nada de animaciones llamativas.

---

## 2. paleta de colores

definida en `enki/src/enki/styles/colors.axaml`. hay dos variantes de tema: **dark** (por defecto) y **light** (misma logica, polaridad invertida).

### 2.1 tema oscuro (dark) - el tema por defecto

| token | codigo hex | uso |
|---|---|---|
| `AppBackgroundColor` | `#121212` | fondo general de la aplicacion |
| `AppBackgroundAltColor` | `#0D0D0D` | fondos alternos / fondo del editor |
| `SidebarBackgroundColor` | `#131313` | barra lateral (glass) |
| `SurfaceColor` | `#1A1A1A` | superficies elevadas (tarjetas) |
| `PanelBackgroundColor` | `#1B1B1B` | paneles, tarjetas, dialogos |
| `PanelBackgroundHoverColor` | `#1F1F1F` | hover de paneles |
| `InputBackgroundColor` | `#141414` | inputs y campos de formulario |
| `RowBackgroundColor` | `#131313` | filas de listas |
| `RowBackgroundHoverColor` | `#181818` | hover de filas |
| `TextPrimaryColor` | `#F5F5F5` | texto principal "casi blanco" |
| `TextSecondaryColor` | `#C6C6C6` | texto secundario, subtitulos, tags |
| `TextMutedColor` | `#474747` | texto terciario, placeholders, deshabilitado |
| `InkColor` | `#FFFFFF` | la "tinta": blanco puro (bordes, acentos, inversion) |
| `InkOnColor` | `#000000` | texto sobre la tinta (botones solidos blancos) |
| `AccentColor` | `#FFFFFF` | color de acento (forzado a blanco, nunca azul) |
| `DangerColor` | `#F87171` | errores, botones destructivos |
| `SuccessColor` | `#4ADE80` | exito, estado activo |
| `HighlightColor` | `#F5C542` | resaltados (busqueda en el editor) |
| `ScrollTrackColor` | `#121212` | pista del scrollbar |
| `ModalOverlayBrush` | `#000000` al 15% | superposicion de dialogos modales |

### 2.2 tema claro (light) - polaridad invertida

| token | codigo hex | uso |
|---|---|---|
| `AppBackgroundColor` | `#F5F5F5` | fondo general |
| `AppBackgroundAltColor` | `#EDEDED` | fondos alternos / editor |
| `SidebarBackgroundColor` | `#F0F0F0` | barra lateral |
| `SurfaceColor` | `#FAFAFA` | superficies elevadas |
| `PanelBackgroundColor` | `#EAEAEA` | paneles y tarjetas |
| `PanelBackgroundHoverColor` | `#E2E2E2` | hover de paneles |
| `InputBackgroundColor` | `#EFEFEF` | inputs |
| `RowBackgroundColor` | `#F0F0F0` | filas |
| `RowBackgroundHoverColor` | `#E6E6E6` | hover de filas |
| `TextPrimaryColor` | `#0A0A0A` | texto principal "casi negro" |
| `TextSecondaryColor` | `#39393B` | texto secundario |
| `TextMutedColor` | `#B8B8B8` | texto terciario, placeholders |
| `InkColor` | `#000000` | la tinta: negro puro |
| `InkOnColor` | `#FFFFFF` | texto sobre la tinta |
| `AccentColor` | `#000000` | acento (negro) |
| `DangerColor` | `#F87171` | errores (igual que en oscuro) |
| `SuccessColor` | `#4ADE80` | exito (igual que en oscuro) |
| `HighlightColor` | `#F5C542` | resaltados (igual que en oscuro) |

### 2.3 el verdadero sistema: rampa de opacidades de la tinta (`Ink05`...`Ink90`)

el 90% de los matices de la ui **no son colores nuevos**: son la tinta (`InkColor` = blanco en oscuro, negro en claro) a distintas opacidades sobre el fondo. brushes definidos:

| brush | opacidad | uso tipico |
|---|---|---|
| `Ink05` | 5% | separadores casi invisibles, hover de overlays |
| `Ink10` | 10% | bordes por defecto de tarjetas y grids |
| `Ink15` | 15% | bordes intermedios |
| `Ink20` | 20% | bordes de badges/tags, track de lineas |
| `Ink30` / `Ink40` | 30-40% | bordes en hover, texto muted |
| `Ink50` / `Ink60` | 50-60% | texto secundario, iconos inactivos |
| `Ink70` / `Ink80` | 70-80% | texto de cuerpo principal |
| `Ink90` | 90% | texto casi tinta pura (enfasis fuerte) |

### 2.4 reglas de color

- **nada de color de marca**: para destacar algo se usa blanco puro (o negro en claro), nunca un color. el `SystemAccentColor` de avalonia (azul por defecto) esta **forzado a blanco/negro** para que foco, seleccion y checked mantengan la identidad monocroma.
- **seleccion de texto invertida**: fondo = tinta, texto = `InkOnColor` (blanco/negro, segun tema).
- **elemento activo**: inversion total (`ActivePillBackgroundBrush` = tinta, `ActivePillForegroundBrush` = fondo).
- **temas personalizados**: el usuario puede sobrescribir cualquier token desde el editor de temas de la app.

---

## 3. tipografia

| rol | fuente | detalle |
|---|---|---|
| **interfaz / display** | **space grotesk** | fuente de todo: titulares, body, botones, nav |
| **monoespaciada** | **jetbrains mono nerd font mono** | micro-labels, numeros, contadores, codigo |
| **iconos** | **material symbols outlined** | trazo fino, sin relleno |

archivos de fuente empaquetados (`enki/src/enki/assets/fonts/`):

- `spacegrotesk-light.ttf`, `spacegrotesk-regular.ttf`, `spacegrotesk-medium.ttf`, `spacegrotesk-semibold.ttf`, `spacegrotesk-bold.ttf`
- `jetbrainsmononerdfontmono-regular.ttf`, `jetbrainsmononerdfontmono-medium.ttf`, `jetbrainsmononerdfontmono-bold.ttf`, `jetbrainsmononerdfontmono-italic.ttf`

### 3.1 escala y estilo tipografico

- **titulares grandes**: space grotesk en pesos altos (negrita/semibold), en **mayusculas** y con tracking ajustado (letras juntas).
- **micro-labels** (el elemento tipografico mas distintivo): jetbrains mono, 10-12px, **mayusculas**, peso 700, **letter-spacing muy amplio** (0.2em-0.5em) y opacidad baja (`Ink30`-`Ink40`). aparecen en nav, botones, badges y titulos de seccion.
- **body copy**: space grotesk regular, en minusculas/mixed-case, sin tracking.
- **mayusculas forzadas** en nav, botones, badges y titulos de seccion (via converter o texto ya en mayusculas).

---

## 4. formas, bordes y profundidad

### 4.1 esquinas rectas (`CornerRadius="0"`)

| elemento | radio |
|---|---|
| botones, inputs, selects, textarea | `0` |
| tarjetas, paneles, dialogos, toasts | `0` |
| badges / tags / chips | `0` |
| barra lateral, titulo de ventana | `0` |
| **puntos de estado, thumb de toggles, pulse dots** | circular (unica excepcion) |

### 4.2 bordes como sistema de separacion

- **bordes de 1px** (`Ink10` por defecto, `Ink30`-`Ink50` en hover) en lugar de sombras.
- **listas tipo rejilla**: el truco del "gap de 1px" - un contenedor con fondo gris y separacion de 1px entre items hace de linea divisoria sin declarar bordes individuales.
- **hover de tarjetas**: elevacion (`translateY(-4px)`) + cambio de borde.
- **profundidad**: solo la barra lateral glass y el overlay de modales usan translucidez/sombra suave.

### 4.3 texturas y efectos de superficie

- **grano de pelicula**: `GrainBrush` - ruido vectorial disperso (48x48px tileable) a opacidad `0.05`, superpuesto al fondo de la ventana principal.
- **barra lateral glass**: fondo translucido estilo vidrio esmerilado (acrylic), flotante, con ancho animado entre expandido (`248px`) y colapsado (`76px`).
- **scrollbar custom**: track con el color de fondo de la app, thumb a `Ink20` (blanco al 20% en oscuro).

---

## 5. movimiento e interaccion

| efecto | detalle |
|---|---|
| **reveal de pagina** | opacidad 0→1 + deslizamiento vertical 8px, 200-300ms, `CubicEaseOut` |
| **hover lift** | tarjetas se elevan `-4px` con transicion de borde/fondo 150-300ms |
| **inversion al hover** | botones outline/ghost: fondo transparente/borde tinta → fondo tinta/texto fondo |
| **slide de panel lateral del editor** | transicion de ancho 200ms |
| **spinner de carga** | rotacion continua 1s (sync polar, carga de pdf) |
| **toggle switch** | pista rectangular, thumb circular, easing con ligero overshoot |
| **linea "ahora" del calendario** | se refresca cada 60s en vistas semana/dia |
| **pulse dot** | anillo expansivo para estados activos (ej. sesion en vivo) |
| **badge pulsante** | notificaciones nuevas en la campana |

filosofia general: **movimiento contenido** - duraciones cortas, sin rebotes ni llamadas de atencion.

---

## 6. componentes visuales clave

- **barra lateral**: secciones con iconos material symbols outlined + labels en mayusculas con tracking ancho; item activo con inversion de color; badges de conteo (vencimientos, papelera).
- **title bar personalizada**: botones propios de minimizar/maximizar/cerrar con esquinas rectas (opcional, configurable).
- **tarjetas**: borde 1px `Ink10`, fondo `PanelBackgroundColor`, hover con elevacion y borde mas brillante.
- **botones**: tres variantes - solido (fondo tinta/texto `InkOnColor`), outline (borde tinta, invierte al hover) y ghost (solo texto). siempre en mayusculas, sin radio.
- **chips de prioridad**: puntos de color (high/medium/low/none) sobre las tarjetas.
- **badges/tags**: mono, mayusculas, tracking amplio, borde `Ink20`.
- **dialogos modales**: overlay negro al 15% + panel `PanelBackgroundColor` con borde, esquinas rectas, cubriendo toda la ventana.
- **toasts**: panel oscuro con acento lateral izquierdo (blanco = exito, rojo = error).
- **calendario**: rejillas finas de 1px, chips de items con borde, linea "ahora" animada, prioridad como punto de color.

---

## 7. accesibilidad visual

- **contraste**: texto primario `#F5F5F5` sobre `#121212` (muy alto), secundario `#C6C6C6` (alto), muted `#474747` para placeholders/deshabilitados.
- **nombres accesibles**: botones con iconos llevan `AutomationProperties.Name` + tooltip para lectores de pantalla.
- **reduced motion**: las animaciones estan pensadas para no interferir con usuarios sensibles al movimiento (transiciones cortas y contenidas).
- **zoom de interfaz**: 50%-200% configurable por vault, escala toda la ui incluidos popovers.

---

## 8. resumen rapido (cheat sheet)

- **estetica**: brutalismo editorial suizo, monocromo, dark-first.
- **esquinas**: rectas (radio 0) en todo, salvo indicadores circulares.
- **fondo oscuro**: `#121212` · **fondo claro**: `#F5F5F5`.
- **texto oscuro**: `#F5F5F5` (primario) / `#C6C6C6` (secundario) / `#474747` (muted).
- **tinta (blanco)**: `#FFFFFF` - bordes y acentos a opacidades 5%-90%.
- **error**: `#F87171` · **exito**: `#4ADE80` · **resaltado**: `#F5C542`.
- **fuente ui**: space grotesk · **fuente mono**: jetbrains mono · **iconos**: material symbols outlined.
- **micro-labels**: mono, mayusculas, 10-12px, tracking 0.2-0.5em, opacidad baja.
- **bordes**: 1px de tinta a baja opacidad, nunca sombras.
- **hover**: inversion de color o elevacion de 4px.
- **animaciones**: 150-300ms, `CubicEaseOut`.
