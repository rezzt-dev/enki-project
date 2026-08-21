# Sistema de diseño de rezzt.dev — guía de recreación

> Documento de referencia visual extraído directamente del código fuente de la web (`my-web`), pensado para **recrear el mismo lenguaje visual en una aplicación Avalonia**. No es un resumen de intenciones: cada valor (color, tamaño, spacing, transición) está copiado literalmente de los ficheros fuente citados entre paréntesis, para que se pueda portar 1:1 sin adivinar nada.

---

## 0. Filosofía del diseño

El sitio (portfolio + panel admin) sigue una estética que se puede describir como **"brutalismo editorial suizo, monocromo, en modo oscuro"**:

- **Blanco y negro casi puro**, sin colores de marca. El único color real es rojo para error/peligro y verde para un punto de "sistema activo". Todo lo demás son grises y opacidades de blanco sobre negro.
- **Cero border-radius.** Botones, inputs, tarjetas, imágenes: todo tiene esquinas 100% rectas. Lo único circular son puntos de estado y el logo. Esta es la señal de identidad más fuerte del sitio.
- **Bordes finos de 1px en vez de sombras.** La profundidad se sugiere con `border: 1px solid rgba(255,255,255,0.1)` y variantes, no con `box-shadow` (excepto la cabecera "glass" y la imagen de perfil, que sí llevan sombra suave).
- **Contraste tipográfico extremo:** titulares gigantes en `font-black` (900), mayúsculas, `tracking-tighter` (letras muy juntas) — contra micro-etiquetas en mayúsculas con `tracking` muy abierto (0.2em–0.5em) y tamaños de 9–12px. Ese choque de escalas es la firma tipográfica del sitio.
- **Todo en mayúsculas** para navegación, botones, badges, labels y titulares de sección (el body copy normal sí va en minúsculas/mixed-case).
- **Imágenes en escala de grises que recuperan color al hover** (`grayscale` → `grayscale-0`), como metáfora de "interacción revela contenido".
- **Inversión de color al hover** en botones y elementos interactivos: borde blanco/fondo transparente → fondo blanco/texto negro (o viceversa). Es el micro-patrón de interacción más repetido del sitio.
- **Textura de grano (ruido SVG)** superpuesta a muy baja opacidad (2–3%) en heroes y el shell del admin, para que el negro plano no se vea "digital muerto".
- **Vidrio esmerilado (glassmorphism)** solo en la cabecera flotante: `backdrop-filter: blur(10px)` + `background: rgba(255,255,255,0.05)`.
- **Animación de entrada por scroll** (GSAP): todo aparece con `opacity 0→1` y `y: 24px→0`, en stagger, una sola vez por sección.

---

## 1. Color

**Fuente:** `tailwind.config.js`, `src/styles/main.css`, `src/styles/projects-page.css`, `src/pages/admin-dashboard.js`, `src/pages/admin-editor/components/fields.js`.

### 1.1 Paleta base (tokens Tailwind)

| Token | Valor | Uso |
|---|---|---|
| `background-dark` | `#121212` | Fondo general del body / hero |
| `surface-dark` | `#1A1A1A` | Superficies elevadas (frame de foto, tarjetas) |
| `primary` (texto) | `#F5F5F5` | Texto base "casi blanco" |
| `accent` | `#FFFFFF` | Blanco puro — CTAs, bordes activos, texto invertido |
| Fondo alterno de sección | `#0D0D0D` | Secciones "zebra" (About, Stack, Shovels, Project overview alterna) |
| Admin — fondo shell | `#131313` | Fondo del panel admin |
| Admin — superficie sidebar/topbar | `#1B1B1B` (a veces `#1b1b1b`) | Sidebar, tarjetas de stats, filas hover |
| Admin — hover de superficie | `#1f1f1f` / `#181818` | Hover de stat cards / filas de tabla |
| Admin — fondo de inputs | `#141414` | Inputs de formulario del editor |
| Texto secundario | `#c6c6c6` | Subtítulos, meta info, tags |
| Texto terciario/disabled | `#474747` | Placeholders, texto deshabilitado, bordes por defecto |
| Selección de texto | fondo `#FFFFFF`, texto `#000000` | `selection:bg-white selection:text-black` en `<body>` |
| Peligro | `red-400` / `red-500` (Tailwind) | Errores, badge "eliminar", botón destructivo |
| Éxito/estado | `green-400` (Tailwind) | Punto pulsante "sesión activa" / "system status" |

### 1.2 El verdadero sistema de color: opacidades de blanco sobre negro

El 90% de los matices de la UI **no son colores nuevos**, son `rgba(255,255,255, X)` (clases Tailwind `white/5` … `white/90`) aplicadas sobre fondos negros/casi negros. Escala de opacidades usada, de más sutil a más fuerte:

- `white/5` — separadores casi invisibles, hover de overlays (`bg-white/[0.02]`, `bg-white/[0.03]`, `bg-white/[0.05]`)
- `white/10` — bordes por defecto de tarjetas y grids (`border-white/10`)
- `white/20` — bordes de badges/tags, track del timeline, línea de proceso
- `white/30`–`white/40` — bordes en hover, texto "muted" de labels
- `white/50`–`white/60` — texto secundario de cuerpo, iconos inactivos
- `white/70`–`white/80` — texto de cuerpo principal sobre negro
- `white/90` — texto casi blanco puro (`strong` en rich text)

Equivalente en el admin: la misma escala pero sobre `#474747` como base de borde (`#474747/15`, `/30`, `/40`) en vez de blanco puro, porque el admin es un poco más "gris" que el marketing site.

**Regla práctica para Avalonia:** define una única `Color Base="White"` y genera brushes por opacidad (`WhiteAlpha05`, `WhiteAlpha10`, `WhiteAlpha20`… `WhiteAlpha90`) en vez de colores nombrados. Es exactamente así como está construido el sistema en CSS.

### 1.3 Nada de color de marca

No hay azul, morado ni ningún "accent color" de producto. Cuando hace falta destacar algo (badge "Destacado", stat activo, borde de foco) se usa **blanco puro**, nunca un color. Esto es intencional y debe respetarse al portar a Avalonia: nada de `AccentColor` de Fluent por defecto (es azul) — hay que sobreescribirlo a blanco/negro puro.

---

## 2. Tipografía

**Fuente:** `index.html`, `tailwind.config.js`, `project/index.html`.

- **Display / UI font:** **Space Grotesk** (Google Fonts), pesos cargados: `300;400;500;600;700;800;900`. Es la fuente de *todo*: titulares, body, botones, nav.
- **Mono font:** **JetBrains Mono**, pesos `400;500;700`. Se usa para: micro-labels de sección (`.section-label`), año de proyecto, contadores, "system status", números de proceso. Solo se carga explícitamente vía `<link>` en `project/index.html`; en el resto de páginas se referencia por CSS (`font-family: 'JetBrains Mono', monospace`) confiando en el fallback si no está precargada — al recrear en Avalonia, simplemente **empaqueta y usa siempre JetBrains Mono**, no hace falta replicar esa inconsistencia.
- **Iconos:** **Material Symbols Outlined** (variable font, ejes `wght` 100–700 y `FILL` 0–1). Outline por defecto, no relleno.

### 2.1 Escala tipográfica

Tamaños custom definidos en `tailwind.config.js` (fluid con `clamp`, pensados para responsive web; en Avalonia usar los valores de escritorio grandes de cada rango):

| Token | `clamp()` | Line-height | Letter-spacing | Uso |
|---|---|---|---|---|
| `text-hero` | `2.75rem → 6rem` (44–96px) | 0.9 | `-0.04em` | Título hero de detalle de proyecto |
| `text-section` | `1.75rem → 3.5rem` (28–56px) | 1 | `-0.03em` | H2 de cada sección |
| `text-subheading` | `1.125rem → 1.5rem` (18–24px) | 1.2 | `-0.01em` | Subtítulos de paso de proceso |

Además, en el resto del sitio los titulares usan la escala estándar de Tailwind pero siempre con el mismo patrón: `font-black uppercase tracking-tighter leading-none/[0.85]/[0.9]`. Ejemplos reales:
- Hero home: `text-4xl sm:text-5xl md:text-6xl lg:text-7xl font-black tracking-tighter leading-[0.85] uppercase`
- H2 de sección (About/Projects/Journey/Stack/Shovels): `text-4xl md:text-5xl(/6xl) font-black uppercase tracking-tighter`
- Nombre "next project" en detalle: hasta `text-9xl`
- Contacto (nombre grande): `text-5xl … text-8xl font-black leading-none tracking-tighter uppercase`

### 2.2 Micro-labels (el "eyebrow" de cada sección)

Patrón repetido literalmente en cada sección (clase compartida `.section-label` en `main.css` + variantes inline):

```
text-white/40 font-bold text-xs uppercase tracking-[0.4em] mb-4
```

Mono, 10–12px, mayúsculas, `letter-spacing` **0.2em a 0.5em** (muy abierto), color `white/30`–`white/40`, peso 700. Aparece antes de cada `<h2>`, en badges, en meta-info de tarjetas, en el footer. **Esta combinación (texto minúsculo + tracking gigante + mayúsculas + opacidad baja) es el elemento tipográfico más distintivo del sitio y el más fácil de olvidar al portar el diseño.**

### 2.3 Reglas de tracking (letter-spacing) por contexto

| Contexto | Tailwind class | Valor |
|---|---|---|
| Titulares grandes | `tracking-tighter` | `-0.05em` |
| Nav links / botones | `tracking-widest` o `tracking-[0.2em]` | `0.1em` / `0.2em` |
| Labels de sección | `tracking-[0.3em]` a `tracking-[0.5em]` | `0.3–0.5em` |
| Body copy | sin tracking | `0` |

---

## 3. Espaciado, bordes y "sharp corners"

**Fuente:** `tailwind.config.js`

```js
borderRadius: {
  DEFAULT: "0rem",
  lg: "0rem",
  xl: "0rem",
  full: "9999px"
}
```

Es decir: **todo radio se fuerza a 0 excepto `rounded-full`**, reservado para: el punto del timeline, el thumb del toggle-switch, badges circulares y el punto de "estado activo". En Avalonia: `CornerRadius="0"` en botones, cards, inputs, bordes de sección; `CornerRadius="999"` o `Ellipse` solo para esos puntos.

### 3.1 Contenedores

- Ancho máximo típico de contenido: `max-w-[1100px]` (home) o `max-w-[1200px]` (secciones internas/about/stack/journey/detalle de proyecto). Admin usa `max-w-7xl`.
- Padding horizontal estándar: `px-4 md:px-6` en secciones de marketing; `px-5 sm:px-6 lg:px-8 xl:px-12` en detalle de proyecto; `px-4 sm:px-8 lg:px-12` en admin.
- Padding vertical de sección: `py-16 md:py-32` (secciones cortas) o `py-20 md:py-28 lg:py-36` (detalle de proyecto).

### 3.2 Bordes como sistema de separación

En vez de sombras/tarjetas con fondo distinto, se usan **rejillas de bordes de 1px**:
- Tarjetas sueltas: `border border-white/10`, hover `border-white/30` o `border-white/50`, con `transition-all`.
- Grids tipo "ficha de catálogo" (Stack): contenedor con `border-l border-t border-white/10` y cada celda con `border-r border-b border-white/10` → el resultado visual es una **rejilla completa de líneas de 1px**, sin duplicar bordes.
- Listas tipo tabla (admin project rows, stat cards): contenedor con `bg-[#474747]/15` (o `bg-white/10` en detalle) + `gap-[1px]` entre hijos + cada hijo con su propio color de fondo sólido → el gap de 1px hace de "línea divisoria" perfecta sin declarar `border-bottom` en cada fila. **Truco a replicar en Avalonia:** un `ItemsControl` con `Grid`/`StackPanel` de fondo gris oscuro y `Spacing="1"`, cada item con su propio `Background` sólido — el fondo del contenedor "asoma" como línea de 1px entre items.

### 3.3 Radios de esquina — resumen

| Elemento | Radio |
|---|---|
| Botones, inputs, selects, textarea | `0` |
| Tarjetas (proyecto, feature, stat, tool) | `0` |
| Imagen de perfil (frame) | `0` (cuadrada, no circular) |
| Badges/tags | `0` |
| Toast, modal, dropdown | `0` |
| Punto de timeline / pulse dot / thumb de toggle | `9999px` (circular) |

---

## 4. Iconografía

**Fuente:** `index.html`, uso repetido en todos los componentes.

- Librería única: **Material Symbols Outlined** (`<span class="material-symbols-outlined">nombre_icono</span>`), variable font con ejes de peso y relleno. Estilo **outline**, nunca relleno, salvo excepciones puntuales.
- Tamaños típicos: `text-sm`/`text-base` en botones y nav, `text-lg`–`text-4xl` en tarjetas y stats, hasta `text-5xl` en empty states.
- Peso reducido en iconos decorativos grandes: `font-light` (p. ej. iconos de contacto).
- Para Avalonia: usar una librería de iconos equivalente (`Material.Icons.Avalonia` con set "Outlined"/"Rounded" fino, o empaquetar el propio Material Symbols variable font y renderizar por codepoint) manteniendo trazo fino y sin relleno.

---

## 5. Texturas y efectos de superficie

**Fuente:** `src/styles/main.css`, `src/pages/admin-dashboard.js`.

### 5.1 Grain overlay (grano de película)
```css
.grain-overlay {
  background-image: url("data:image/svg+xml,...<feTurbulence type='fractalNoise' baseFrequency='0.85' numOctaves='4' .../>...");
  pointer-events: none;
}
```
Aplicado con `opacity-[0.02]` a `opacity-[0.03]` sobre hero de detalle de proyecto y sobre el shell del admin (`class="grain-overlay fixed inset-0 z-[1] pointer-events-none opacity-[0.02]"`). Efecto: textura de ruido fractal sutilísima para romper el negro plano. En Avalonia: una `Image`/`Border` con un `TileBrush` de ruido pre-generado (PNG de ruido tileable) a opacidad ~2–3%, o un shader de ruido si se usa `Effect` custom.

### 5.2 Glow radial de fondo (admin)
```html
<div class="pointer-events-none fixed -top-32 -left-32 w-[440px] h-[440px] bg-white/[0.025] rounded-full blur-[110px] z-0"></div>
```
Un círculo blanco al 2.5% de opacidad, muy desenfocado (`blur-[110px]`), posicionado fuera del viewport para crear un resplandor ambiental sutil en la esquina superior izquierda del panel admin. Replicable en Avalonia con un `Ellipse` + `BlurEffect Radius="110"` + opacidad baja.

### 5.3 Cabecera "glass" (glassmorphism)
```css
.glass {
  background: rgba(255, 255, 255, 0.05);
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
  border: 1px solid rgba(255, 255, 255, 0.1);
}
```
Usada únicamente en el `<header>` flotante (nav principal), que además "flota" con margen (`p-3 md:p-4`) en vez de pegarse al borde de la ventana, y gana sombra/opacidad de borde al hacer scroll (`initScrollAwareHeader` en `src/main.js` / `src/components/Header.js`). En Avalonia: `ExperimentalAcrylicBorder`/`ExperimentalAcrylicMaterial` con `TintColor` blanco, `TintOpacity` bajo y `MaterialOpacity` para el blur, dentro de un `Border` flotante con `Margin`.

### 5.4 Sombra de la foto de perfil
```css
.profile-image-frame {
  box-shadow: 0 0 0 1px rgba(255,255,255,0.08), 0 24px 48px rgba(0,0,0,0.4);
}
.profile-image-frame:hover {
  box-shadow: 0 0 0 1px rgba(255,255,255,0.2), 0 32px 64px rgba(0,0,0,0.6);
}
```
Es de los pocos sitios del diseño con sombra "real" (drop shadow difuminada), para dar profundidad al retrato. El resto de la UI evita sombras.

---

## 6. Movimiento e interacción

**Fuente:** `src/main.js`, `src/styles/main.css`, `src/styles/admin-dashboard.css`.

### 6.1 Reveal al hacer scroll (GSAP + ScrollTrigger)
Patrón aplicado a **todas** las secciones de la home vía `initScrollAnimations()`:
```js
gsap.fromTo(elements,
  { y: 24, opacity: 0 },
  { y: 0, opacity: 1, duration: 0.7, stagger: 0.1, ease: 'power2.out',
    scrollTrigger: { trigger: section, start: 'top 80%', once: true } }
);
```
Grids (`#work .grid`, `#stack .grid`, `#shovels .grid`) tienen su propio stagger más marcado: `y: 32→0, duration: 0.6, stagger: 0.08`. El timeline de "Journey" anima además el trazo de la línea (`scaleX`/`scaleY` de 0→1) sincronizado con el scroll (`scrub: 1`), y cada nodo aparece con `opacity 0.4→1, y:16→0` en cascada. Todo se desactiva completamente si `prefers-reduced-motion: reduce`.

**Equivalente en Avalonia:** no hay "scroll trigger" nativo tan directo, pero para vistas que aparecen (páginas, paneles, listas) usar `Avalonia.Animation` con `KeyFrame`s de `Opacity` y `TranslateTransform.Y` (24px→0, 0.6–0.7s, `CubicEaseOut`), con `Stagger` manual vía `DelayedBinding`/incrementos de `BeginTime` por índice de item.

### 6.2 Micro-interacciones de hover (constantes en todo el sitio)

| Patrón | Implementación CSS | Dónde |
|---|---|---|
| **Inversión de color** | fondo transparente/borde blanco → `hover:bg-white hover:text-black` | Botones outline, badges de stack, footer nav |
| **Lift** (elevación) | `hover:-translate-y-1` / `.hover-lift:hover { transform: translateY(-4px) }` | Tarjetas de proyecto, tool cards |
| **Grayscale → color** | `.grayscale` → `.grayscale:hover { filter: grayscale(0%) }` | Fotos de proyecto, retrato, thumbnails |
| **Zoom de imagen** | `group-hover:scale-105` / `scale-110` sobre `transition-transform duration-700` | Cards, galería, next-project |
| **Subrayado que crece** | `span.nav-underline { transform: scaleX(0) } .nav-link:hover & { scaleX(1) }`, `origin-left`, `duration-300` | Nav links del header |
| **Translate lateral** | `hover:translate-x-2` | Links de contacto (redes sociales) |
| **Opacidad reducida** | `hover:opacity-60/70/80` | Nav, links secundarios |
| **Glow en hover** | `hover:shadow-[0_0_40px_rgba(255,255,255,0.15)]` + invierte a blanco | Celdas de la rejilla Stack |

Duraciones estándar: `transition-all duration-300` (la inmensa mayoría), `duration-700` para transformaciones de imagen grandes, `duration-500` para thumbnails de admin.

### 6.3 Tilt 3D del retrato del hero
`initHeroPortraitTilt()` en `src/main.js`: sigue el cursor sobre la sección hero y aplica `perspective(1000px) rotateX() rotateY()` al frame de la foto (máx. 6°), desactivado en touch y con `prefers-reduced-motion`.

### 6.4 Header consciente del scroll
El header gana `box-shadow` y opacidad de borde tras 20px de scroll (`shadow-lg shadow-black/20`, `border-color: rgba(255,255,255,0.15)`), vía `requestAnimationFrame` throttling.

---

## 7. Inventario de componentes (specs exactas)

### 7.1 Header / navegación principal
*(`src/components/Header.js`)*

- Contenedor flotante fijo arriba, centrado, con `padding: 0.75–1rem` alrededor (efecto "isla flotante", no pegado a los bordes).
- `<header>` interno: clase `.glass` (ver §5.3), `max-w-[1350px]`, `flex justify-between items-center`, padding `p-3 md:p-4`.
- Logo: icono SVG monocromo (`currentColor`) de 20px + wordmark "Rezzt" con efecto **typewriter** que alterna "Rezzt" ↔ "Juan García" (`TypewriterEffect`, 120ms/carácter tecleando, 80ms borrando, pausa 10s).
- Nav (desktop, `lg:flex`): links en mayúsculas, `text-xs font-bold tracking-[0.2em]`, con subrayado animado al hover (§6.2).
- Botón de idioma: `border border-white/20`, altura 32px (`h-8`), icono `language` + texto "EN"/"ES", invierte a blanco en hover.
- CTA "Resume": mismo estilo de borde, descarga el PDF.
- Menú móvil (`lg:hidden`): hamburguesa de 3 líneas que se transforma en X; overlay a pantalla completa `bg-black/95 backdrop-blur-md` con links gigantes centrados (`text-3xl tracking-[0.2em]`).

### 7.2 Botones
*(patrón repetido en Hero, Contact, Journey, admin, etc.)*

Tres variantes, **sin radio de esquina, siempre mayúsculas y con tracking ancho**:

1. **Primario sólido** — `bg-white text-black px-8 py-4 text-xs font-bold uppercase tracking-widest hover:bg-white/90`
2. **Outline (invierte al hover)** — `border-2 border-white text-white px-8 py-4 text-xs font-bold uppercase tracking-widest hover:bg-white hover:text-black`
3. **Ghost / texto** — sin borde, `text-xs font-bold uppercase tracking-[0.2em] hover:opacity-60`, a veces con icono `arrow_forward`/`arrow_outward` que se desplaza en hover (`group-hover:translate-x-0.5`)

Botones de admin (más compactos): `px-6 py-3 text-[10px]` o `h-12 px-5 text-[10px]`, mismas reglas de mayúscula/tracking/inversión.

### 7.3 Inputs, textarea, select
*(`src/pages/admin-editor/components/fields.js`, filtros del admin dashboard)*

```
bg-[#141414] border border-[#474747]/30 p-3 text-sm text-white
placeholder:text-[#474747] focus:outline-none focus:border-white/40
transition-colors
```
- Sin radio de esquina, sin `box-shadow` de foco (Avalonia: quitar el `FocusAdorner`/glow azul por defecto de Fluent).
- Estado de error: borde `border-red-400/60` + mensaje bajo el campo en `text-[10px] uppercase tracking-widest text-red-400` con icono `error`.
- Label del campo: `text-[10px] font-bold uppercase tracking-widest text-[#c6c6c6]`, asterisco `*` en `text-white/30` si es obligatorio; hint alineado a la derecha en `text-[10px] text-[#474747]`.
- Select: mismo look, `appearance-none` + flecha custom en la web pública (`sort-select`).
- Search input (listado de proyectos): variante "underline", sin caja — solo icono `search` a la izquierda y texto, foco cambia el color del icono.

### 7.4 Toggle switch
*(`fields.js` `toggleField`, admin dashboard "Visible/Oculto")*

- Pista: `w-[36px] h-[18px]`, **rectangular sin radio** salvo el thumb.
- Color pista: blanco si activo (`bg-white`), gris oscuro si inactivo (`bg-[#353535]`).
- Thumb: `14×14px`, `bg-[#131313]` (oscuro) sobre pista blanca activa, `bg-[#919191]` sobre pista apagada; se desplaza `left-[2px]`/`right-[2px]` con `transition-all duration-300` (curva `cubic-bezier(0.34, 1.56, 0.64, 1)` — un ligero "overshoot" tipo iOS en `admin-dashboard.css`).
- Deshabilitado: `opacity-40 cursor-not-allowed`.

### 7.5 Tarjetas

**Project card** (`src/components/Projects.js`): `border border-white/10`, imagen `aspect-[16/10]` en grayscale que se colorea y hace zoom al hover, overlay `bg-black/40` con texto "View More" que sube desde abajo; footer con título `text-2xl font-black uppercase`, año en mono `white/30`, descripción `white/50`, tags en badges (§7.6). Hover: borde a `white/50` + `-translate-y-1`.

**Feature card** (detalle de proyecto): `border border-white/10 p-8 md:p-10 bg-white/[0.02]`, icono + título + descripción en fila, hover `border-white/30`.

**Stat card** (admin dashboard): `bg-[#1b1b1b] p-5 sm:p-8`, sin borde individual (el grid entero es una rejilla de `gap-[1px]` sobre fondo `bg-[#474747]/15`, ver §3.2), label mono arriba, número gigante `text-3xl sm:text-5xl font-bold`, caption mono abajo en `text-gray-500`. Estado activo: `bg-[#1f1f1f]` + barra vertical izquierda de 2px blanca (`::after`). Hover: `translateY(-2px)`.

**Tool/shovel card** (`src/components/Shovels.js`): `border border-white/10`, `aspect-square`, icono/imagen grayscale→color, overlay blanco al 5% de opacidad en hover, borde a blanco puro (no solo opacidad) en hover.

**Timeline card** (Journey): igual patrón de borde con `hover:border-white/30`, contenido: fecha en mono pequeño, título `font-black uppercase`, institución en `tracking-[0.15em]`, tags al pie.

### 7.6 Badges / tags / chips

Patrón único reutilizado en tags de proyecto, tech stack, features:
```
text-[9px]–[10px] font-bold uppercase tracking-wider px-2 py-1 (o px-3 py-1.5)
border border-white/10 (o /20) text-[#c6c6c6]
```
Badge "destacado"/estado (admin): fondo sólido blanco + texto negro, o borde sutil sin fondo para estados neutros ("Incompleto").

### 7.7 Listas tipo tabla (admin project rows)

Ver técnica de "gap-[1px] como separador" en §3.2. Cada fila: `bg-[#131313]`, hover `bg-[#181818]` + barra izquierda de 2px que aparece (`::before`, `background-color: rgba(255,255,255,0.5)`). Contenido: thumbnail cuadrado con padding e imagen `object-contain grayscale` que se colorea en hover del row completo (no solo de la imagen), metadata en mono uppercase, selects inline, toggle de visibilidad, acciones (editar/eliminar) separadas por `border-l`.

### 7.8 Sidebar admin (colapsable)

*(`src/pages/admin-dashboard.js`, `src/styles/admin-dashboard.css`)*

- Ancho expandido `w-64` (256px) → colapsado `5rem` (80px), transición `0.35s cubic-bezier(0.16, 1, 0.3, 1)` tanto en el propio sidebar como en el margen del contenido principal (`#admin-main`) y en el `left` del topbar — **los tres elementos se mueven sincronizados**.
- Botón de colapsar: circular pequeño (`w-6 h-6`) anclado al borde derecho del sidebar (`-right-3`), icono `chevron_left` que rota 180° al colapsar.
- Item de nav activo: fondo `#353535` + barra izquierda blanca de 3px.
- En modo colapsado, las labels de texto se ocultan vía `max-width: 0; opacity: 0` (no `display:none`, para poder animar), y los botones se centran y vuelven cuadrados (`3rem × 3rem`).
- CTA inferior "Nuevo Proyecto": botón blanco sólido a todo el ancho del sidebar.
- Footer del sidebar: punto verde pulsante (`admin-pulse-dot`, animación de anillo expandiéndose) + email de sesión.
- En móvil: el sidebar es un drawer que entra con `translate-x`, con backdrop `bg-black/70 backdrop-blur-sm`.

### 7.9 Toast
*(`src/pages/admin-shared/toast.js`)*

`bg-[#1B1B1B] border border-[#474747]/30 border-l-2` (acento izquierdo: blanco si éxito, `border-l-red-400` si error), `px-5 py-4`, icono + mensaje mono uppercase + botón de cierre. Entra con `opacity-0 translate-y-2 → opacity-100 translate-y-0` (300ms), se apila en `fixed bottom-6 right-6`, auto-dismiss a los 5s.

### 7.10 Confirm dialog / modal
*(`src/pages/admin-shared/confirmDialog.js`)*

Overlay `bg-[#050505]/90` a pantalla completa; caja `max-w-sm bg-[#1B1B1B] border border-[#474747]/30 p-8`; título `text-lg font-bold uppercase`, mensaje `text-[#c6c6c6]`; acciones alineadas a la derecha: "Cancelar" como texto plano gris, "Confirmar" como botón sólido (blanco/negro, o rojo si `danger:true`). Cierra con click fuera o `Escape`.

### 7.11 Timeline (Journey — educación)
*(`src/components/Journey.js`, `.timeline*` en `main.css`)*

- Línea vertical (móvil) u horizontal (desktop ≥1024px) de 2px, gradiente de `white/20` a `white/5`.
- Nodos circulares de 12px, `box-shadow: 0 0 0 4px rgba(255,255,255,0.1)` (halo), opacidad decreciente por posición (1, 0.85, 0.7, 0.55, 0.4 — sugiere "más reciente = más brillante").
- En desktop se convierte en grid de 5 columnas con la línea horizontal y los nodos centrados arriba de cada tarjeta.

### 7.12 Label de sección ("eyebrow")
Ver §2.2 — clase compartida `.section-label`. Aparece siempre inmediatamente antes de cada `<h2>` de sección y en la etiqueta "Overview/Process/Features/Gallery" del detalle de proyecto.

### 7.13 Imagen de perfil
*(`src/components/ProfileImage.js`)*

Frame cuadrado (nunca circular) `bg-surface-dark border border-white/10 p-2` (padding interno visible alrededor de la foto, como una "passe-partout"), imagen en `object-cover grayscale`, hover: `grayscale-0 scale-105` + sombra reforzada (§5.4). Tamaños predefinidos (`sm/md/lg/xl`) todos cuadrados.

### 7.14 Scrollbar custom
```css
::-webkit-scrollbar { width: 8px; }
::-webkit-scrollbar-track { background: #121212; }
::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.2); border-radius: 4px; }
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.3); }
```
Nota: es el único sitio del diseño con `border-radius` distinto de 0 (4px) — detalle menor, ignorable si Avalonia no expone scrollbar custom fácilmente, pero replicable con `ScrollBar` template si se quiere fidelidad total.

### 7.15 Selección de texto
`body { selection:bg-white selection:text-black }` — seleccionar texto invierte a blanco/negro, coherente con el resto del sistema de inversión.

---

## 8. Patrones de layout

- **Secciones "zebra":** alternan fondo `background-dark` (#121212) / `#0D0D0D` con separadores `border-y border-white/5`, para dar ritmo vertical sin usar color.
- **Header de sección estándar:** label pequeño + `<h2>` grande a la izquierda, opcionalmente un párrafo descriptivo corto a la derecha en mayúsculas/mono (`flex justify-between items-end`).
- **Grid de 2 columnas asimétrico** para "overview" de detalle de proyecto: contenido `lg:col-span-7` + panel meta pegajoso (`lg:sticky lg:top-32`) `lg:col-span-5` con fondo `bg-white/[0.02]` y borde.
- **CTA final centrado** con líneas horizontales a los lados (`<span class="h-px w-8 md:w-16 bg-white/20">`) — patrón "separador con texto en medio" usado en "Next project".
- **Contraste de densidad:** marketing site = mucho whitespace, tipografía enorme; admin console = más denso, tipografía pequeña, más líneas divisorias — mismo lenguaje visual, distinta densidad de información.

---

## 9. Cómo trasladar esto a Avalonia

### 9.1 Recursos de color (ResourceDictionary)
Define un único set de colores base y genera brushes de opacidad, replicando el sistema del §1.2:
```xml
<Color x:Key="ColorWhite">#FFFFFF</Color>
<Color x:Key="ColorBlack">#000000</Color>
<Color x:Key="ColorBg">#121212</Color>
<Color x:Key="ColorBgAlt">#0D0D0D</Color>
<Color x:Key="ColorSurface">#1A1A1A</Color>
<Color x:Key="ColorAdminBg">#131313</Color>
<Color x:Key="ColorAdminSurface">#1B1B1B</Color>
<Color x:Key="ColorAdminInput">#141414</Color>
<Color x:Key="ColorTextMuted">#474747</Color>
<Color x:Key="ColorTextSecondary">#C6C6C6</Color>
<Color x:Key="ColorDanger">#F87171</Color>   <!-- red-400 -->
<Color x:Key="ColorSuccess">#4ADE80</Color>  <!-- green-400 -->

<!-- Brushes de opacidad sobre blanco, réplica exacta del sistema web -->
<SolidColorBrush x:Key="White05" Color="{StaticResource ColorWhite}" Opacity="0.05"/>
<SolidColorBrush x:Key="White10" Color="{StaticResource ColorWhite}" Opacity="0.10"/>
<SolidColorBrush x:Key="White20" Color="{StaticResource ColorWhite}" Opacity="0.20"/>
<SolidColorBrush x:Key="White30" Color="{StaticResource ColorWhite}" Opacity="0.30"/>
<SolidColorBrush x:Key="White40" Color="{StaticResource ColorWhite}" Opacity="0.40"/>
<SolidColorBrush x:Key="White50" Color="{StaticResource ColorWhite}" Opacity="0.50"/>
<SolidColorBrush x:Key="White60" Color="{StaticResource ColorWhite}" Opacity="0.60"/>
<SolidColorBrush x:Key="White70" Color="{StaticResource ColorWhite}" Opacity="0.70"/>
<SolidColorBrush x:Key="White80" Color="{StaticResource ColorWhite}" Opacity="0.80"/>
```
Y **sobreescribe el `AccentColor` de Fluent a blanco/gris**, porque por defecto Avalonia usa azul para foco/selección/checked, lo cual rompe la identidad monocromo al instante.

### 9.2 Corner radius global = 0
Crea (o edita) `ControlTheme`s para `Button`, `TextBox`, `ComboBox`, `Border`, `ToggleSwitch`, `ListBoxItem`, etc. y fija `CornerRadius="0"` en todos salvo los indicadores circulares (§3.3). Es más rápido definir un `Style Selector="Button, TextBox, ComboBox, Border.card"` con `Setter Property="CornerRadius" Value="0"` a nivel de `App.axaml` que tocar cada template.

### 9.3 Tipografía
- Empaqueta **Space Grotesk** y **JetBrains Mono** como `avares://TuApp/Assets/Fonts/...ttf` y regístralas como `FontFamily` con `#Space Grotesk` / `#JetBrains Mono`.
- Define dos `FontFamily` de recurso: `FontDisplay` (Space Grotesk) para el 100% de la UI, `FontMono` (JetBrains Mono) solo para labels/números/mono.
- **`text-transform: uppercase` no existe en Avalonia** — o pasas el texto ya en mayúsculas desde el ViewModel/converter, o creas un `IValueConverter ToUpperConverter` y lo aplicas en cada `Binding` de texto de nav/botón/label.
- **`letter-spacing` sí existe** en Avalonia 11+ vía `TextBlock.LetterSpacing` (double, en unidades de diseño, no em) — úsalo para replicar `tracking-[0.2em]` a `tracking-[0.5em]` en labels y nav.
- Pesos: Avalonia soporta `FontWeight` completo (Light 300 → Black 900) si la fuente variable/estática incluye esos cortes — asegúrate de empaquetar los cortes 300/400/500/700/800/900 de Space Grotesk igual que hace la web.

### 9.4 Iconos
Usa `Material.Icons.Avalonia` (set outline) o empaqueta el propio Material Symbols variable font y renderiza glifos por su codepoint en un `TextBlock FontFamily="avares://.../MaterialSymbolsOutlined.ttf"`. Mantén trazo fino (peso bajo del eje `wght`), nunca relleno.

### 9.5 Vidrio esmerilado / blur
Para el header flotante y el glow radial del admin, usa `ExperimentalAcrylicBorder` (o `Border` + `Effect` con blur si tu versión de Avalonia lo soporta) con `TintColor` blanco y `TintOpacity` bajo (~0.05), dentro de un contenedor con `Margin` para lograr el efecto "isla flotante" en vez de pegarlo al borde de la ventana.

### 9.6 Textura de grano
Genera un PNG de ruido tileable (o usa el mismo SVG de `feTurbulence` rasterizado) y aplícalo como `ImageBrush TileMode="Tile"` en un `Border` superpuesto con `Opacity="0.02"–"0.03"`, `IsHitTestVisible="False"`.

### 9.7 Animaciones
- Hover invert / lift / grayscale-color: usa `Avalonia.Styling.Transitions` (`BrushTransition`, `TransformOperationsTransition`, `DoubleTransition` sobre `Opacity`) con duración `0.3s` (la constante del sitio), `CubicEaseOut`.
- Reveal de entrada (equivalente a GSAP fromTo): `Animation` con dos `KeyFrame`s (`Opacity` 0→1, `TranslateTransform.Y` 24→0), `Duration="0:0:0.7"`, y un `DelayedBinding`/incremento de `BeginTime` por índice de item para el stagger (0.08–0.1s entre elementos).
- Toggle switch con "overshoot": `CubicBezierEasing(0.34, 1.56, 0.64, 1)` si Avalonia expone easing custom, si no aproxima con `BackEaseOut`.

### 9.8 El truco de "gap de 1px como separador"
Para listas/tablas y grids de la Stack section: contenedor `Border`/`Panel` con `Background="{StaticResource Muted15}"` y `ItemsControl` con `Spacing="1"` (si usas `ItemsRepeater`/`StackPanel` como `ItemsPanel`), cada item con su propio `Background` sólido opaco — el fondo del contenedor aparece como línea divisoria de 1px sin declarar bordes individuales. Reproduce exactamente el efecto CSS de `gap-[1px] bg-[#474747]/15`.

---

## 10. Checklist de paridad visual

- [ ] `AccentColor`/`SystemAccentColor` de Fluent sobreescrito a blanco/negro (nada de azul en foco, checked, selección)
- [ ] `CornerRadius="0"` global en botones/inputs/tarjetas; solo círculos en indicadores de estado
- [ ] Space Grotesk como única fuente de UI, JetBrains Mono reservada a labels/mono
- [ ] Mayúsculas forzadas en nav, botones, badges, `<h2>` de sección (vía converter, no CSS)
- [ ] `LetterSpacing` amplio (0.2–0.5em equivalente) en todos los micro-labels
- [ ] Bordes de 1px en `rgba(255,255,255, 0.05–0.5)` en vez de sombras, salvo header y foto de perfil
- [ ] Patrón de inversión de color en hover (fondo↔texto) en todo elemento interactivo
- [ ] Imágenes en escala de grises que recuperan color al hover
- [ ] Grano de ruido a 2–3% de opacidad en heroes/paneles grandes
- [ ] Header con efecto vidrio (blur + `rgba(255,255,255,0.05)`) flotando con margen, no pegado al borde
- [ ] Transiciones de 300ms (`CubicEaseOut`) como duración por defecto de toda micro-interacción
- [ ] Selección de texto invertida a blanco/negro
- [ ] Contraste de escala tipográfica: titulares gigantes en negro (900) vs. labels minúsculos muy espaciados
