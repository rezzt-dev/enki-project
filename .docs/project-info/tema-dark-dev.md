# Tema "Dark Dev" — tema, estilo y diseño de Enki

> Referencia completa del aspecto visual de Enki tomando como tema de trabajo **Dark Dev**, el tema
> oscuro de negro puro que viene incorporado en la aplicación. Todos los valores están sacados del código
> (los ficheros se citan entre paréntesis) y, cuando un estilo usa un token, se da además el valor que
> resuelve con Dark Dev activo, para poder diseñar o revisar una pantalla sin tener que buscarlo.
>
> Complementa a `sistema-diseno-web.md`, que explica de dónde viene el lenguaje visual (la web
> rezzt.dev) y cómo se llevó a Avalonia. Este documento describe cómo está montado **hoy** dentro de Enki.
> Si algo de aquí deja de coincidir con el código, manda el código.

---

## 0. Resumen en una línea por idea

- **Estética:** brutalismo editorial suizo, monocromo y pensado primero para modo oscuro. Dark Dev
  la lleva al extremo: fondo `#000000` real (ideal para pantallas OLED) y tinta `#FFFFFF`.
- **Color:** no hay color de marca. El 90% de los matices son la tinta (blanco) a distintas opacidades
  sobre negro (la rampa `Ink05…Ink90`). Solo hay color real para el estado: peligro, éxito y resaltado.
- **Forma:** `CornerRadius="0"` en todo. Solo son circulares los indicadores (puntos de prioridad,
  el círculo de "hoy" en el calendario, el punto de "ahora") y, si el usuario lo elige, el logo.
- **Profundidad:** bordes finos de 0.75–1.5 px en lugar de sombras.
- **Interacción:** el patrón principal es invertir colores (contorno → relleno de tinta, texto → negro).
  Las transiciones duran 150 ms con `CubicEaseOut`.
- **Tipografía:** Space Grotesk para la interfaz y JetBrains Mono para micro-etiquetas, cifras y código.
  Las etiquetas van en MAYÚSCULAS con mucho espaciado entre letras.

---

## 1. Cómo funciona el sistema de temas

### 1.1 Las tres capas de color

| Capa | Fichero | Qué hace |
|---|---|---|
| Paleta base Dark/Light | `styles/Colors.axaml` (`ResourceDictionary.ThemeDictionaries`) | Define los 19 `Color` "slot" para las variantes `Dark` y `Light` de Fluent. Es lo que se ve si no hay ningún tema activo. |
| Brushes semánticos | `styles/Colors.axaml` (fuera de `ThemeDictionaries`) | `SolidColorBrush` que leen los slots con `{DynamicResource}`: `AppBackgroundBrush`, `TextPrimaryBrush`, la rampa `Ink05…Ink90`, `DangerBrush`… Todos los estilos y vistas consumen **brushes**, nunca hex sueltos. |
| Tema activo (incorporado o personalizado) | `services/ThemeRegistry.cs` | Escribe los 19 slots directamente en `Application.Current.Resources`. Las claves de ese diccionario ganan a las de `ThemeDictionaries`, así que el tema se impone sea cual sea la variante Dark/Light. |

Como los brushes leen sus slots con `DynamicResource`, al activar Dark Dev se repinta toda la
interfaz al momento, sin reconstruir estilos.

### 1.2 Los 19 slots (`services/ThemeCatalog.cs`)

`ThemeCatalog.All` es la única lista de colores que puede definir un tema. La comparten el parser CSS,
la plantilla exportable, el editor de temas y `BuiltInThemeCatalog`. Están agrupados en
**Fondos (9) · Texto (3) · Tinta y acento (3) · Scroll (1) · Estado (3)**.

### 1.3 Dónde vive Dark Dev

- Está declarado en `services/BuiltInThemeCatalog.cs` con `Id = "dark-dev"`, `Name = "Dark Dev"` y
  `Appearance = ThemeAppearance.Dark`. Está compilado dentro de la app, así que no hay ningún `.css` en
  disco y el usuario no lo puede editar, renombrar ni borrar (`CustomThemeService.Save/Delete/Rename`
  no hacen nada con ids incorporados).
- Se activa desde **Ajustes › Apariencia › Temas › Oscuros › Dark Dev**. Al activarlo,
  `CustomThemeService.Activate("dark-dev")` aplica los colores y guarda
  `"ActiveCustomThemeId": "dark-dev"` en `settings.json`.
- "Usar tema del sistema" (`UseSystemThemeCommand`) lo desactiva y vuelve a la paleta base
  Dark/Light de `Colors.axaml`.
- Mientras Dark Dev esté activo, el interruptor claro/oscuro queda deshabilitado
  (`SettingsViewModel.IsCustomThemeActive`).

> **Ojo con la variante Fluent de fondo.** Dark Dev solo sustituye los 19 slots y la paleta
> `SystemAccentColor*`. El resto de brushes internos de Fluent (los que Enki no re-tematiza) siguen la
> `RequestedThemeVariant` que marque `AppSettings.Theme`. Por tanto, Dark Dev debe usarse con la base
> **Dark** (`"Theme": "Dark"`, que es el valor por defecto). Si el usuario tenía activada la base clara
> antes de elegir Dark Dev, algún control Fluent sin re-tematizar se pintaría con chrome claro.

### 1.4 Acento de Fluent derivado (`ThemeRegistry.ApplySystemAccent`)

A partir del slot `accent`, se generan las siete claves `SystemAccentColor*` bajando la luminosidad
HSL en pasos de 0.08. Así los anillos de foco, los estados `checked` y la selección de Fluent nunca
salen en el azul por defecto. Con Dark Dev (`accent = #E0E0E0`):

| Clave | Valor |
|---|---|
| `SystemAccentColor` | `#E0E0E0` |
| `SystemAccentColorDark1` | `#CBCBCB` |
| `SystemAccentColorDark2` | `#B7B7B7` |
| `SystemAccentColorDark3` | `#A2A2A2` |
| `SystemAccentColorLight1…3` | `#E0E0E0` |

---

## 2. Paleta de Dark Dev

### 2.1 Los 19 slots

Columna "Dark base" = valor por defecto de `Colors.axaml` (variante `Dark`), para comparar.

| # | Variable CSS | Clave de recurso | **Dark Dev** | Dark base | Para qué se usa |
|---|---|---|---|---|---|
| 1 | `--app-background` | `AppBackgroundColor` | **`#000000`** | `#121212` | Fondo de la `Window`, del shell y de las celdas del mes del calendario. |
| 2 | `--app-background-alt` | `AppBackgroundAltColor` | **`#050505`** | `#0D0D0D` | Fondo alterno de secciones (alias `EditorBackgroundBrush`) y base del fondo del grafo. |
| 3 | `--sidebar-background` | `SidebarBackgroundColor` | **`#050505`** | `#131313` | Barra lateral principal. |
| 4 | `--surface` | `SurfaceColor` | **`#0A0A0A`** | `#1A1A1A` | Superficie elevada genérica (hoy casi sin consumidores directos en las vistas; el editor de temas la usa para comprobar contraste). |
| 5 | `--panel-background` | `PanelBackgroundColor` | **`#0D0D0D`** | `#1B1B1B` | Tarjetas, popovers, modales, menús y tarjetas Kanban (alias `CardBackgroundBrush`). |
| 6 | `--panel-background-hover` | `PanelBackgroundHoverColor` | **`#141414`** | `#1F1F1F` | Hover de tarjetas y de stat cards (alias `CardBackgroundHoverBrush`; también es el fondo de las etiquetas del grafo). |
| 7 | `--input-background` | `InputBackgroundColor` | **`#080808`** | `#141414` | Inputs, ComboBox, NumericUpDown, selector de fecha y fondo de los bloques de código del editor. |
| 8 | `--row-background` | `RowBackgroundColor` | **`#080808`** | `#131313` | Fila de lista (reservado; hoy sin consumidores directos). |
| 9 | `--row-background-hover` | `RowBackgroundHoverColor` | **`#121212`** | `#181818` | Hover de fila (reservado). |
| 10 | `--text-primary` | `TextPrimaryColor` | **`#FFFFFF`** | `#F5F5F5` | Texto principal. Es el `Foreground` global de todo `TextBlock`. |
| 11 | `--text-secondary` | `TextSecondaryColor` | **`#A0A0A0`** | `#C6C6C6` | Subtítulos, items de navegación en reposo, metadatos e iconos inactivos. |
| 12 | `--text-muted` | `TextMutedColor` | **`#555555`** | `#474747` | Placeholders, `TextBlock.faint`, días fuera del mes y tareas completadas. |
| 13 | `--ink` | `InkColor` | **`#FFFFFF`** | `#FFFFFF` | **La tinta.** Base de la rampa `Ink05…Ink90`, bordes activos, rellenos invertidos y selección de texto. |
| 14 | `--ink-on` | `InkOnColor` | **`#000000`** | `#000000` | Texto o icono que va encima de un relleno de tinta (botón primario, segmento activo, hoy). |
| 15 | `--accent` | `AccentColor` | **`#E0E0E0`** | `#FFFFFF` | De aquí sale la paleta `SystemAccent*` de Fluent (§1.4). Uso directo mínimo: icono de Captura rápida y punto de vista previa en la lista de temas. |
| 16 | `--scroll-track` | `ScrollTrackColor` | **`#000000`** | `#121212` | Pista del scroll (reservado). |
| 17 | `--danger` | `DangerColor` | **`#EF4444`** | `#F87171` | Errores, borrado, cerrar ventana, vencidos, Papelera y línea de "ahora". |
| 18 | `--success` | `SuccessColor` | **`#22C55E`** | `#4ADE80` | Notificaciones de éxito y atajos personalizados. |
| 19 | `--highlight` | `HighlightColor` | **`#EAB308`** | `#F5C542` | Resaltado `==texto==` del editor (al 28 %) y avisos (`Warning` del estilo Markdown). |

**En qué se diferencia de la base Dark:**

1. **Negro real en lugar de gris carbón.** El fondo pasa de `#121212` a `#000000` y todas las
   superficies bajan en bloque (`#050505` / `#080808` / `#0A0A0A` / `#0D0D0D` / `#141414`). La escala
   de superficies queda muy comprimida, así que la jerarquía la marcan **los bordes** y no los cambios
   de fondo.
2. **Texto con más jerarquía.** El primario sube a blanco puro y el secundario baja a `#A0A0A0`, con
   lo que se separan más que en la base (`#F5F5F5`/`#C6C6C6`). El atenuado sube un poco (`#555555`)
   para seguir viéndose sobre negro puro.
3. **Acento algo apagado (`#E0E0E0`).** Los estados de Fluent (foco, `checked`) se ven un punto por
   debajo del blanco de la tinta y no compiten con los bordes activos.
4. **Colores de estado más saturados.** Pasan de la serie Tailwind 400 a la 500 (`red-500`,
   `green-500`, `yellow-500`), que sobre negro puro tienen más presencia sin deslumbrar.

### 2.2 Plantilla CSS equivalente

Es el formato que lee y escribe `ThemeCssParser` (Ajustes › Apariencia › Importar/Exportar tema).
Como Dark Dev no se puede editar, la forma de partir de él es crear un tema personalizado y pegar estos
valores:

```css
:root {
  /* Fondos */
  --app-background: #000000;
  --app-background-alt: #050505;
  --sidebar-background: #050505;
  --surface: #0A0A0A;
  --panel-background: #0D0D0D;
  --panel-background-hover: #141414;
  --input-background: #080808;
  --row-background: #080808;
  --row-background-hover: #121212;

  /* Texto */
  --text-primary: #FFFFFF;
  --text-secondary: #A0A0A0;
  --text-muted: #555555;

  /* Tinta y acento */
  --ink: #FFFFFF;
  --ink-on: #000000;
  --accent: #E0E0E0;

  /* Scroll */
  --scroll-track: #000000;

  /* Estado */
  --danger: #EF4444;
  --success: #22C55E;
  --highlight: #EAB308;
}
```

---

## 3. Colores derivados

### 3.1 La rampa de tinta (`Ink05…Ink90`)

Cada una es un `SolidColorBrush` con `Color = InkColor` y una `Opacity` fija (`Colors.axaml`). Es **el
verdadero sistema de color**: bordes, hovers, rellenos sutiles, separadores y etiquetas atenuadas salen
de aquí. Con Dark Dev la tinta es blanca, así que el color resultante depende del fondo que tenga debajo:

| Brush | Opacidad | Sobre `#000000` (fondo) | Sobre `#0D0D0D` (panel) | Uso típico |
|---|---|---|---|---|
| `Ink05` | 5 % | `#0D0D0D` | `#191919` | Hover de fila/pestaña/menú, fondo de columna Kanban, fondo de avisos, tarjeta activa. |
| `Ink10` | 10 % | `#1A1A1A` | `#252525` | **Borde por defecto** de tarjetas e inputs, `GridSeamBrush` y `BorderBrush1`, item activo, pulsado. |
| `Ink15` | 15 % | `#262626` | `#313131` | Bordes de pestañas en reposo y **destino de arrastre** (`drag-over`). |
| `Ink20` | 20 % | `#333333` | `#3D3D3D` | Borde de popovers, modales y menús, borde de columna y tarjeta Kanban, pista del toggle apagado. |
| `Ink30` | 30 % | `#4D4D4D` | `#565656` | Borde de tarjeta en hover, chevrons de barra de herramientas y botón de copiar código en reposo. |
| `Ink40` | 40 % | `#666666` | `#6E6E6E` | **Eyebrows**, borde de input con foco, borde de checkbox y tirador del toggle apagado. |
| `Ink50` | 50 % | `#808080` | `#868686` | Iconos de severidad neutros y prioridad media. |
| `Ink60` | 60 % | `#999999` | `#9E9E9E` | Anillo de foco por teclado de checkbox y slider. |
| `Ink70` / `Ink80` | 70 / 80 % | `#B3B3B3` / `#CCCCCC` | — | Tramo activo del slider (`Ink80`) y nodos del grafo. |
| `Ink90` | 90 % | `#E6E6E6` | — | Borde del capturador de atajos mientras escucha. |

`BorderSubtleBrush` es `Ink` al 5 % y `GridSeamBrush`/`BorderBrush1` son `Ink` al 10 %: son alias con
nombre propio.

### 3.2 Brushes compuestos

| Brush | Definición | Dark Dev |
|---|---|---|
| `ActivePillBackgroundBrush` / `ActivePillForegroundBrush` | `Ink` / `InkOn` | `#FFFFFF` / `#000000` |
| `SelectionBackgroundBrush` / `SelectionForegroundBrush` | `Ink` / `InkOn` | Selección de texto invertida: blanco con texto negro. |
| `DangerMutedBrush` | `Danger` al 60 % | `#EF4444` al 60 % (≈ `#8F2929` sobre negro): "vence hoy" y aviso. |
| `GraphChipBackgroundBrush` | `PanelBackground` al 80 % | Chips flotantes del grafo. |
| `ModalOverlayBrush` | `#000000` fijo al 15 % | No depende del tema (ver §11.1). |
| Fondo `==resaltado==` (editor) | `Highlight` al 28 % | `#EAB308` al 28 % (≈ `#413203` sobre negro). |

### 3.3 Colores que **no** cambian con el tema

Son hex fijos a propósito, elegidos para leerse en cualquier tema:

| Qué | Valor | Fuente |
|---|---|---|
| Columna Kanban "pendientes" | `#D5585C` | `services/CalendarLayerPalette.cs` |
| Columna Kanban "en proceso" | `#B28426` | idem |
| Columna Kanban "realizadas" | `#3E9767` | idem |
| Acentos de listas Polar y columnas de usuario (8) | `#328BA7` cian · `#5980CD` azul · `#C552C5` magenta · `#5B86A7` acero · `#8D76B5` malva · `#B963A5` ciruela · `#3785CA` azul cielo · `#AB61D5` violeta | idem (`AccentPool`) |
| Capa de recordatorios del calendario | `#818CF8` (índigo) | `viewmodels/calendar/CalendarItemViewModel.cs` |
| Capa Kanban del calendario | `#E5E5E5` | idem |

Toda la paleta del calendario tiene una luminancia relativa de ~0.22, validada contra `#1B1B1B` y
`#EAEAEA`. Sobre los paneles `#0D0D0D` de Dark Dev el contraste es algo **mayor**, así que no hace
falta ajustarla. En la vista de mes, las pastillas pintan ese color al 8 % de relleno y al 40 % de borde.

---

## 4. Contraste (WCAG, aproximado)

| Par | Sobre `#000000` | Sobre `#0D0D0D` | Lectura |
|---|---|---|---|
| `text-primary` `#FFFFFF` | 21 : 1 | 19.4 : 1 | Excelente. |
| `accent` `#E0E0E0` | 15.9 : 1 | — | Excelente. |
| `highlight` `#EAB308` | 11 : 1 | 10.1 : 1 | Excelente. |
| `success` `#22C55E` | 9.2 : 1 | 8.5 : 1 | Excelente. |
| `text-secondary` `#A0A0A0` | 8.0 : 1 | 7.4 : 1 | AAA para texto normal. |
| `danger` `#EF4444` | 5.6 : 1 | 5.2 : 1 | AA. |
| Eyebrow (`Ink40` → `#666666`) | 3.7 : 1 | — | Solo AA para texto grande o componentes de UI. Vale para micro-etiquetas decorativas, **no para contenido**. |
| `text-muted` `#555555` | 2.8 : 1 | 2.6 : 1 | Por debajo de AA. Solo para placeholders, deshabilitado y metadatos prescindibles. |

El editor de temas (`ThemeEditorViewModel.ContrastPairs`) avisa si algún par de lectura baja de 3 : 1.
Dark Dev supera todos los pares que comprueba.

---

## 5. Tipografía

### 5.1 Familias (`styles/Colors.axaml`, `assets/fonts/`)

| Clave | Fuente por defecto | Uso | ¿Se puede cambiar? |
|---|---|---|---|
| `DisplayFontFamily` | Space Grotesk | Toda la interfaz (`Window`, `UserControl`, menús). | Sí: Ajustes › Apariencia › Fuente de la interfaz. |
| `ContentControlThemeFontFamily` | Space Grotesk | Lo que pinta Fluent (`TextBox`, placeholder, `ToolTip`). | Sí, con la misma opción. |
| `InterfaceLabelFontFamily` | JetBrains Mono Nerd Font Mono | Micro-etiquetas: eyebrows, `faint`, contadores, ejes de gráficas, días del calendario. | Sí, con la misma opción. |
| `MonoFontFamily` | JetBrains Mono Nerd Font Mono | Etiquetas que **deben** ser monoespaciadas (extensiones de archivo, códigos de recuperación, cifras). | No. |
| `CodeFontFamily` | JetBrains Mono Nerd Font Mono | Código en el editor. | Sí: Ajustes › Editor. |

Cortes empaquetados: Space Grotesk **Light, Regular, Medium, SemiBold y Bold** (no hay 800/900, así
que el titular más pesado es `Bold`); JetBrains Mono **Regular, Medium, Bold e Italic**.

### 5.2 Escala tipográfica real (`styles/Styles.axaml`)

En Avalonia, `LetterSpacing` va en píxeles (DIP), no en `em`.

| Estilo | Tamaño | Peso | LetterSpacing | Color (Dark Dev) |
|---|---|---|---|---|
| `TextBlock.page-title` | 34 | Bold | −1 | `#FFFFFF` |
| `TextBlock.trash-overview-title` | 24 | Bold | −0.5 | `#FFFFFF` |
| `TextBlock.page-subtitle` | 13.5 | Regular | 0.1 | `#A0A0A0` |
| `TextBlock.section-title` | 12 | Bold | 1.5 | `#FFFFFF` |
| `TextBlock.eyebrow` | 10.5 · mono | Bold | **4** | `Ink40` → `#666666` |
| `TextBlock.faint` | 10.5 · mono | Regular | — | `#555555` |
| Item de navegación (`Button.nav-item TextBlock`) | 11.5 | Bold | 1.5 | `#A0A0A0` → activo `#FFFFFF` |
| Botones `pill` / `pill-primary` | 11 | Bold | 1.5 | — |
| `Button.ghost` | 11 | Bold | 2 | — |
| `MenuItem` | 12 | SemiBold | 0.6 | `#FFFFFF` |
| `RadioButton.side-panel-tab` | 9.5 | Bold | 0.6 | `#A0A0A0` → `#FFFFFF` |
| `Button.breadcrumb` | 10 | Bold | 1.2 | `#A0A0A0` |
| `TextBlock.card-caption` | 11.5 | Regular | — | `#555555` |
| `TextBlock.trash-card-title` | 14 / LH 18 | Bold | 0.2 | `#FFFFFF` |
| `Border.file-ext-badge TextBlock` | 9.5 · mono | Bold | 0.4 | `#A0A0A0` |
| `TextBlock.titlebar-badge-text` | 8.5 | Bold | — | `#000000` sobre `#EF4444` |
| Etiquetas de gráficas (`ChartBase`) | 9 · mono | — | — | `Ink40` |

Tamaños más usados directamente en las vistas, por orden de frecuencia: **11.5, 13, 12, 9.5, 12.5,
10.5, 11, 14**. Los titulares grandes de las vistas usan 19, 26 y 30.

**Editor Markdown** (`views/editor/Markdown/MarkdownStyle.cs`): cuerpo a 14 px por defecto
(`EditorFontSize`), columna de 760 px (`EditorColumnWidth`) y encabezados escalados
H1 ×1.80 · H2 ×1.55 · H3 ×1.32 · H4 ×1.15 · H5 ×1.05 · H6 ×0.95.

### 5.3 Mayúsculas

Avalonia no tiene `text-transform`, así que las etiquetas de navegación, botones, eyebrows y menús de
bandeja pasan por `UpperCaseConverter` o `UpperCaseSpacedBulletConverter` (declarados en `App.axaml`).
**El texto en mayúsculas no se escribe a mano en los recursos de localización.**

---

## 6. Forma, bordes y espaciado

### 6.1 Radios

`CornerRadius="0"` en botones, inputs, ComboBox, NumericUpDown (incluidos los botones del spinner),
tarjetas, popovers, modales, menús, pestañas, toggle, checkbox, slider y badges. Las únicas excepciones
son estas:

| Elemento | Radio |
|---|---|
| `Ellipse.priority-dot` (7×7) y `Ellipse.calendar-now-dot` (7×7) | círculo |
| `Border.calendar-day-badge` (22×22, `CornerRadius 11`): círculo de "hoy" | círculo |
| `Border.calendar-mini-dot` (4×4, `CornerRadius 2`) | círculo |
| `ToggleButton.calendar-layer-color` (`CornerRadius 8`) | el propio punto de color de la capa |
| Logo personalizado con forma "Circular" (Ajustes › Apariencia › Logo) | círculo opcional; el icono de Enki siempre es cuadrado |

### 6.2 Grosores de borde

| Grosor | Dónde |
|---|---|
| 0.75 | Pestañas del editor (`Border.editor-tab`) y botón "+" (`Button.tab-add`). |
| 1 | Por defecto: tarjetas, inputs, popovers, modales, menús, chips, segmentos y badges. |
| 1.2 | Botones de la barra de título (`Button.titlebar-btn`). |
| 1.25 | Chips con hora en Semana/Día (`calendar-chip-timed`). |
| 1.5 | Botones `pill`, `pill-primary`, `pill-primary-danger`, `ghost-card`, `link-mode-toggle` y el subrayado de `title-edit`. |
| 2 | Barra de acento de los avisos (`trash-hint`, `board-hint`), página activa del PDF y subrayado de "hoy" en la semana. |
| 3 (solo a la izquierda) | Item de navegación activo, fila de carpeta activa y barra de urgencia de recordatorios. |

### 6.3 Separadores: la costura de 1 px

En lugar de poner un borde en cada fila, el contenedor lleva `GridSeamBrush` (`Ink10`, que sobre negro
da `#1A1A1A`) y cada hijo tiene su propio fondo sólido con un margen de 1 px. Lo que asoma entre ellos es
una línea perfecta y sin bordes duplicados. Se usa en `ItemsControl.seam-grid` y en
`Border.calendar-month-grid` / `calendar-month-cell` (las celdas de la última columna o fila no llevan
ese margen).

### 6.4 Medidas recurrentes

| Elemento | Medida |
|---|---|
| Sidebar | 248 px desplegada / 76 px plegada (raíl), sin margen, borde derecho `Ink10`, padding `14,18,14,16`. |
| Items del raíl, logo y toggle de la sidebar | 32 × 32. |
| Barra de título | Fila de 44 px. Botones de ventana de 26 × 26 con margen 5. |
| Botón de icono (`icon-btn`) | 34 × 34 (30 × 30 en tarjetas de Papelera, 40 × 40 en la barra del calendario). |
| Botón `pill` | Alto 40, padding `20,11` (compacto: alto 28, padding `12,0`). |
| Segmento / chip de barra | Alto 34. |
| Pestaña del editor / `tab-add` | Alto 41. |
| Tarjeta de navegador (notas, archivos, papelera) | 224 × 150 con calle de 16. |
| Modal | `MaxWidth 380`, padding 28. |
| Popover desplegable | Padding 16, `MaxHeight 420` (búsqueda global: 600 × 520). |
| Popover de calendario | Padding 18. |
| Paneles laterales redimensionables (árbol de Archivos, Skills, vista simple de Notas) | 272 px por defecto. |
| Zoom de interfaz | 50 %–200 % en pasos de 10 (también escala los popups, gracias a `Popup.ShouldUseOverlayLayer = True`). |

---

## 7. Iconografía

- Una sola librería: **Material.Icons.Avalonia** (`mi:MaterialIcon`), con estilo de contorno y trazo fino.
- Tamaños habituales: 14 px en acciones de fila y adornos de búsqueda, 12 px en el check del
  checkbox Markdown y 19 px en la barra de título (búsqueda, campana y bandeja).
- **Color:** `MaterialIcon` no hereda el `Foreground` del botón de forma fiable, así que cada familia
  de botones fija el color del icono de forma explícita en reposo, en hover y en activo (se ve en
  `pill`, `icon-btn`, `nav-item`, `tag-chip`…). Cualquier botón nuevo con icono tiene que hacer lo mismo.
- El icono de Papelera de la sidebar es rojo (`danger`) también en reposo, y no se aclara al hacer hover.
- Spinner: clase `mi|MaterialIcon.spin`, que da una vuelta completa por segundo en bucle.

---

## 8. Texturas de superficie

| Textura | Definición | Dónde |
|---|---|---|
| **Grano de película** (`GrainBrush`) | `DrawingBrush` en mosaico de 48 × 48 con 18 puntos de 0.5 px pintados con `InkBrush`, al 5 % de opacidad del brush. | Capa sobre todo el área de contenido del shell (`ShellView.axaml`, `IsHitTestVisible="False"`). Sobre el negro puro de Dark Dev el grano es casi imperceptible: rompe el "negro digital muerto" sin ensuciar. |
| **Rejilla de puntos del grafo** (`GraphBackdropDotsBrush`) | Puntos de 1 px cada 26 px en `Ink05`. | Pantalla de carga del grafo de sesiones, alineada píxel a píxel con el fondo del WebView. |
| **Halo del grafo** (`GraphBackdropBloomBrush`) | Radial centrado en (50 %, 28 %), de tinta a transparente, al 5 %. | Idem. |

No hay sombras (`BoxShadow`) en la interfaz. La profundidad la dan los bordes y el desplazamiento
vertical en hover.

---

## 9. Movimiento (`services/MotionService.cs`)

| Recurso | Normal | "Animaciones sutiles" activadas | Uso |
|---|---|---|---|
| `MotionInteractionDuration` | **150 ms** | 90 ms | Todas las `BrushTransition`, `DoubleTransition` y `TransformOperationsTransition` de los estilos. |
| Reveal de sección (`MotionReveal`) | 180 ms, desplazamiento de 6 px | 100 ms, sin desplazamiento | Entrada de cada página al navegar. |
| `MotionCardLift` | `translateY(-4px)` | `translateY(0)` | Hover de `note-card`, `file-card`, `skill-card` y `Button.card`. |
| `MotionSmallLift` | `translateY(-1px)` | `translateY(0)` | Hover de chips y tarjetas de agenda del calendario. |

- Curva única: **`CubicEaseOut`**.
- **Regla técnica:** la transición se declara en el elemento que **pinta** la propiedad. En un
  `Button`, eso es `/template/ ContentPresenter` (fondo y borde) y los `TextBlock`/`MaterialIcon` hijos
  (color del texto). Si se declara en el propio `Button`, se anima una propiedad que nadie lee y el
  cambio salta de golpe.
- Se anima `Opacity` y no `IsVisible` para revelar acciones (tarjetas Kanban, filas de carpeta), así
  el layout no cambia de alto al pasar el ratón.

---

## 10. Catálogo de componentes (valores resueltos con Dark Dev)

### 10.1 El patrón de estados

| Estado | Tratamiento general |
|---|---|
| Reposo | Fondo transparente o `PanelBackground` (`#0D0D0D`), borde `Ink10` (`#1A1A1A`). |
| Hover | Borde a `Ink30`/`Ink40`, o fondo `Ink05`; en los botones con contorno, **inversión** (relleno `#FFFFFF`, texto `#000000`). |
| Pulsado | `Ink10` de fondo, u opacidad 0.8 / 0.7. |
| Activo / seleccionado | Fondo `Ink05`/`Ink10` + borde `InkBrush` (`#FFFFFF`), texto en Bold. |
| Destino de arrastre | Fondo `Ink15` + borde `InkBrush`. |
| Foco por teclado | Borde `Ink60`, o borde `InkBrush` en tarjetas. |
| Deshabilitado | Opacidad 0.35–0.4, cursor `Arrow` y **los mismos brushes de reposo** (se anulan los grises de Fluent). |
| Cortado (portapapeles) | Opacidad 0.42. |

> `Styles.axaml` empieza neutralizando el chrome de Fluent: en `:pointerover`, `:pressed` y `:checked`,
> el `ContentPresenter` de todo `Button`/`ToggleButton` vuelve a los valores del propio control. Sin
> eso, cada botón mostraría el recuadro gris (o azul en `checked`) de Fluent. Una clase nueva que quiera
> fondo en hover tiene que declararlo **sobre su `ContentPresenter`** y después de esas reglas.

### 10.2 Botones

| Clase | Reposo | Hover | Notas |
|---|---|---|---|
| `Button.pill` (contorno) | Transparente, borde 1.5 `#FFFFFF`, texto `#FFFFFF` | Relleno `#FFFFFF`, texto/icono `#000000` | Alto 40. Pulsado: opacidad 0.8. Deshabilitado: 0.35. Variante `pill-compact`. |
| `Button.pill-primary` (sólido) | Relleno `#FFFFFF`, texto `#000000` | Pasa a contorno: fondo transparente, texto `#FFFFFF`, opacidad 0.85 | Es la inversión del `pill`. Pulsado: 0.7. |
| `Button.pill-primary-danger` | Relleno `#EF4444`, texto `#000000` | Contorno rojo, texto `#EF4444` | Reservado a confirmar un borrado. |
| `Button.ghost` | Sin borde ni fondo, LS 2 | Opacidad 0.6 (pulsado: 0.4) | `ghost.modal-cancel` iguala padding y borde con su pareja primaria. |
| `Button.icon-btn` | 34 × 34, sin caja, icono `#A0A0A0` | Icono `#FFFFFF` | `.active` mantiene el icono en `#FFFFFF`. `icon-btn-danger` se pone en `#EF4444` en hover. |
| `Button.titlebar-btn` | 26 × 26, borde 1.2 `#FFFFFF` | Relleno `#FFFFFF`, icono `#000000` | `titlebar-btn-close` hace lo mismo en `#EF4444`. `titlebar-btn-plain` va sin borde. |
| `Button.tab-add` | 41 × 41, borde 0.75 `#FFFFFF` | Relleno `#FFFFFF`, icono `#000000` | |
| `Button.code-copy-btn` | Fondo `#080808`, borde `Ink10`, icono `Ink30` | Fondo `Ink10`, borde `Ink20`, icono `#FFFFFF` | Va sobre los bloques de código. |
| `Button.ghost-card` | Transparente, borde 1.5 `Ink10` | Borde `#FFFFFF` | Tarjeta de "añadir". |
| `Button.link-mode-toggle` | Contorno 1.5 `#FFFFFF` | — | `.active` usa relleno `#FFFFFF` y texto `#000000`. |

### 10.3 Selectores y controles

| Control | Diseño |
|---|---|
| `RadioButton.segment` | Alto 34, borde 1 `Ink10`, texto 11 Bold `#A0A0A0`. Marcado: relleno `#FFFFFF` y texto `#000000`. Hover sin marcar: `Ink05`. |
| `RadioButton.side-panel-tab` | Solo borde inferior. Reposo: `Ink10` y texto `#A0A0A0`. Marcado: borde inferior `#FFFFFF` y texto `#FFFFFF`. |
| `ToggleButton.tag-chip` | Contorno 1 `#FFFFFF`, padding `10,6`. Hover: `Ink05`. Marcado: relleno `#FFFFFF` y texto/icono `#000000`. `.toolbar-chip`: alto 34. |
| `ToggleSwitch` (`ControlThemes.axaml`) | Pista rectangular de 36 × 18 y tirador cuadrado de 14 × 14. Apagado: pista `Ink20`, tirador `Ink40`. Encendido: pista `#FFFFFF`, tirador `#000000` desplazado 18 px. |
| `CheckBox` (`RectangularCheckBox`) | Caja de 16 × 16 con borde `Ink40`. Marcado: relleno `#FFFFFF` y check `#000000`. Foco: `Ink60`. |
| `CheckBox.markdown-checkbox` | Igual que el anterior a 16 × 16, alineado con la línea del editor. |
| `Slider` (`RectangularSlider`) | Pista de 2 px (tramo activo `Ink80`, resto `Ink20`) y tirador rectangular de 12 × 18 en `#FFFFFF`. |

### 10.4 Entradas de texto

| Control | Diseño |
|---|---|
| `TextBox` (global) | Radio 0, selección invertida (blanco con texto negro). |
| `TextBox.search` | Fondo `#080808`, borde 1 `Ink10`, padding `12,8`, caret `#FFFFFF`. Con foco: borde `Ink40`. Una sola línea: centrado vertical. |
| `TextBox.ghost` | Sin fondo, sin borde y sin padding. |
| `TextBox.title-edit` | Renombrar en línea: subrayado de 1.5 transparente. Hover: `Ink05` y subrayado `Ink20`. Foco: `Ink05` y subrayado `#FFFFFF`. |
| `ComboBox` / `ComboBoxItem` | Mismo lenguaje que `search`. Item seleccionado: `Ink10`. Hover: `Ink05`. |
| `NumericUpDown` | Mismo lenguaje, fuente de etiqueta (mono). Hover y foco: borde `Ink40`. Botones del spinner sin radio. |
| `Button.date-picker-trigger` | Mismo lenguaje que un input, hover con borde `Ink40`. |
| `Border.time-stepper` | Input de 92 × 34 con el valor en mono 14 Bold. |

### 10.5 Tarjetas y superficies

| Clase | Fondo | Borde | Hover |
|---|---|---|---|
| `Border.card` | `#0D0D0D` | 1 `Ink10` | — (estática) |
| `Button.card` | `#0D0D0D` | 1 `Ink10` | Borde `Ink30` y −4 px |
| `Button.note-card` / `Button.file-card` | `#0D0D0D` | 1 `Ink10` | Borde `Ink30` y −4 px. `.active`: `Ink05` y borde `#FFFFFF`. |
| `Button.skill-card` | `#0D0D0D` | 1 `Ink10` | `#141414`, borde `Ink30` y −4 px |
| `Border.stat-card` | `#0D0D0D` | sin borde propio (va dentro de una rejilla con costura) | `#141414` |
| `Border.card.trash-card` | `#0D0D0D` | 1 `Ink10` | `#141414` y borde `Ink20` (sin desplazamiento: la tarjeta no navega) |
| `Button.note-card.activity-row` | `#0D0D0D` (sin leer: `Ink05`) | solo inferior | Sin desplazamiento |
| `Border.modal-card` | `#0D0D0D` | 1 `Ink20` | Padding 28, ancho máximo 380 |
| `Border.dropdown-popover` / `Border.calendar-popover` | `#0D0D0D` | 1 `Ink20` | Los dos únicos estilos de popover. Hay que reutilizarlos. |
| `MenuFlyoutPresenter`, `ContextMenu` | `#0D0D0D` | 1 `Ink20` | Items de 36 px, 12 SemiBold |
| `Border.graph-chip` | `#0D0D0D` al 80 % | 1 `Ink10` | — |

### 10.6 Navegación y estructura

| Elemento | Diseño |
|---|---|
| `Button.nav-item` (sidebar) | Texto 11.5 Bold LS 1.5 en `#A0A0A0`, padding `13,10`. Hover: `Ink05`. Pulsado: `Ink10`. **Activo:** `Ink10` + barra izquierda de 3 px `#FFFFFF` + texto `#FFFFFF` Bold. Plegada: cuadro de 32 × 32 sin caja, y el icono pasa a `#FFFFFF` en hover o activo. |
| `Border.folder-row` | Barra izquierda de 3 px transparente. Hover: `Ink05`. Activa: `Ink10` + barra `#FFFFFF` + Bold. Arrastre encima: `Ink15`. Las acciones de fila solo se ven en hover, en la fila activa o con foco. |
| `Border.editor-tab` | Alto 41, borde 0.75 `Ink15`. Hover: `Ink05` y borde `Ink40`. Activa: `Ink05`, borde `#FFFFFF` y Bold. |
| `Button.breadcrumb` | Texto 10 Bold `#A0A0A0`. Hover: `Ink10` y texto `#FFFFFF`. El tramo actual va en `#FFFFFF` y no reacciona al cursor. |
| `Button.section-header` | Fila entera clicable. En hover el eyebrow pasa de `#666666` a `#FFFFFF`. |
| `GridSplitter.folder-panel-splitter` | Invisible en reposo. Hover: `Ink10`. Arrastrando: `Ink15`. |
| `Rectangle.section-rule` | Regla horizontal de 1 px en `Ink10`. |

### 10.7 Badges, avisos y notificaciones

| Elemento | Diseño |
|---|---|
| `Border.titlebar-badge` | Cuadrado (nunca píldora), mínimo 12 × 12, relleno `#EF4444` y cifra de 8.5 Bold en `#000000`. |
| `Border.trash-count-badge` / `Border.kanban-count` | Caja `Ink05`/`Ink10` (con borde `Ink10` en Papelera) y cifra mono de 10 Bold. |
| `Border.file-ext-badge` | Fondo `#000000`, borde `Ink10`, mono 9.5 Bold en `#A0A0A0`. |
| `Border.trash-hint` / `Border.board-hint` | Fondo `Ink05` con barra izquierda de 2 px `Ink20`. |
| Iconos de severidad | Neutro `Ink50` · éxito `#22C55E` · aviso `DangerMuted` · error `#EF4444`. |
| `Button.dropdown-item.reminder-item` | Barra izquierda de 3 px: `Ink10` normal · `DangerMuted` vence hoy · `#EF4444` vencido · `Ink20` y opacidad 0.72 si está aplazado. |
| `Border.shortcut-capture` | `Ink05` con borde `Ink10`. Hover: `Ink30`. Escuchando: `Ink90`. Personalizado: `#22C55E`. |

### 10.8 Kanban

| Elemento | Diseño |
|---|---|
| `Border.kanban-column` | Fondo `Ink05`, borde 1 `Ink20`. Arrastre encima: borde `#FFFFFF`. Cabecera y pie separados con líneas `Ink20`. |
| `Button.kanban-card` | `#0D0D0D`, borde 1 `Ink20`, padding 16. Hover: `#141414` y borde `Ink30`. Arrastre encima: `Ink10` y borde `#FFFFFF`. Las acciones aparecen con hover o `:focus-within`. |
| `Ellipse.priority-dot` | 7 × 7, **sin color de marca**: baja `Ink20` · media `Ink50` · alta `#FFFFFF`. |
| Vencida | Texto e icono en `#EF4444`. Es el único color real del tablero, aparte de los colores de columna. |
| `Button.kanban-add-card` | Fila plana. Texto `#A0A0A0`, que en hover pasa a `Ink10` con texto `#FFFFFF`. |

### 10.9 Calendario

| Elemento | Diseño |
|---|---|
| Rejilla del mes | Costura `Ink10` y celdas en `#000000`. Arrastre encima: `Ink15` y borde `#FFFFFF`. |
| Número de día | Mono 11 en `#A0A0A0`; fuera del mes, `#555555`. **Hoy:** círculo de 22 px en `#FFFFFF` con número `#000000` Bold. |
| `ToggleButton.calendar-chip` | Fondo y borde con el color de la capa. Hover: −1 px. Completado: tachado en `#555555`. Ocurrencia fantasma: opacidad 0.55. |
| Mini-calendario (`Button.calendar-day`) | 30 × 30. Hover: `Ink10`. Hoy: borde `Ink30`. Seleccionado: relleno `#FFFFFF` con texto `#000000`. |
| Semana/Día | Filas de hora con línea `Ink05` y horario laboral en `Ink05`. Columnas separadas por `Ink10`. Cabecera de hoy subrayada 2 px en `#EF4444`. |
| Línea de "ahora" | 2 px en `#EF4444` con punto de 7 px. Es el único color saturado de la rejilla. |
| Barra de herramientas | Las variantes `calendar-toolbar-btn` suben botones y segmentos a alto 40 y tamaño de fuente 12. |

### 10.10 Editor Markdown y grafo

El **editor** (`MarkdownStyle.FromApp`) toma sus colores de los brushes del tema, así que con Dark Dev:

| Elemento Markdown | Brush | Dark Dev |
|---|---|---|
| Texto, secundario y atenuado | `TextPrimary` / `TextSecondary` / `TextMuted` | `#FFFFFF` / `#A0A0A0` / `#555555` |
| Énfasis y barra de cita | `InkBrush` (no hay color de acento) | `#FFFFFF` |
| Código en línea y en bloque | `InputBackground` + borde `Ink10` | `#080808` / `#1A1A1A` |
| Tablas | Borde `Ink10`, cabecera `Ink05` | — |
| Regla horizontal | `BorderSubtleBrush` (`Ink` al 5 %) | — |
| `==resaltado==` | `Highlight` al 28 % | `#EAB308` al 28 % |
| Peligro, éxito y aviso | `Danger` / `Success` / `Highlight` | `#EF4444` / `#22C55E` / `#EAB308` |

Los diagramas Mermaid (`MarkdownMermaidCache`) usan los mismos colores de texto, fondo de código y borde.

El **grafo de sesiones** (`SessionGraphRenderer.GraphTheme.FromApp`) tiene fondo `AppBackgroundAlt`
(`#050505`) con la rejilla de puntos y el halo. Los nodos activos son `#FFFFFF`, los enlaces wikilink
tinta al 60 %, los enlaces manuales al 90 % y las partículas al 50 %. Los tooltips usan `#0D0D0D` con
borde `Ink20` y las etiquetas de sesión `#141414`.

---

## 11. Particularidades de Dark Dev (qué vigilar)

Al ser negro puro, algunas técnicas que funcionan en la base Dark (`#121212`) se comportan distinto:

1. **El velo de los modales casi no oscurece.** `ModalOverlayBrush` es `#000000` al 15 % y no depende
   del tema; sobre un fondo que ya es `#000000` apenas cambia nada. En Dark Dev, el modal se separa de la
   página por **su borde `Ink20` y su panel `#0D0D0D`**, y el bloqueo de clics lo sigue garantizando
   `ModalOverlayHost`. Por eso **no hay que quitar el borde de `modal-card` ni de los popovers**: aquí
   son lo único que los recorta.
2. **Las tarjetas Kanban tienen el mismo relleno que su columna.** `Ink05` sobre negro da `#0D0D0D`, el
   mismo color que `PanelBackground`, así que la tarjeta y la columna se funden y la separación la pone
   el **borde `Ink20`** de la tarjeta. No hay que bajarlo a `Ink10`.
3. **Las superficies están muy juntas** (`#000` → `#050505` → `#080808` → `#0D0D0D` → `#141414`). Un
   cambio de fondo en hover (`#0D0D0D` → `#141414`) se nota poco, así que en tarjetas clicables el hover
   tiene que cambiar también el borde (`Ink30`) o desplazarlas (−4 px), como ya hace el catálogo.
4. **Costuras y huecos.** Con un fondo `#000` contra una sidebar `#050505`, cualquier hueco de pocos
   píxeles entre paneles se lee como una costura y no como separación intencionada. Por eso la sidebar
   va pegada al borde de la ventana sin margen (`ShellView.axaml`). Al maquetar, o los paneles se tocan
   y una línea `Ink10` hace de separador, o se separan del todo con borde propio.
5. **Atenuado bajo.** `#555555` sobre negro da 2.8 : 1. No sirve para texto que haya que leer: ahí se
   usa `TextSecondary` (`#A0A0A0`).
6. **Rojo más vivo.** `#EF4444` tiene más presencia que el `#F87171` de la base. Se mantiene para lo
   destructivo, lo vencido y "ahora", y no se usa como decoración.
7. **Slots casi sin uso.** `surface`, `row-background`, `row-background-hover` y `scroll-track` existen
   para que el tema esté completo, pero hoy ninguna vista los consume directamente (el scroll se atenúa
   con `ScrollBar Opacity 0.6`). Cambiarlos en un tema derivado de Dark Dev apenas se notará.

---

## 12. Reglas para extender la interfaz sin romper el tema

**Sí:**

- Consumir siempre **brushes por `{DynamicResource}`** (`PanelBackgroundBrush`, `Ink10`, `TextSecondaryBrush`…)
  para que funcione con Dark Dev, con la base Dark/Light y con cualquier tema personalizado.
- Antes de crear un estilo nuevo, buscar una clase existente (`pill`, `icon-btn`, `card`,
  `dropdown-popover`, `calendar-popover`, `segment`, `tag-chip`…) y, si hace falta, añadirle un
  **modificador** (como `pill-compact` o `calendar-toolbar-btn`).
- Expresar los matices con la **rampa de tinta**: hover `Ink05`, borde `Ink10`, popover `Ink20`, hover
  de borde `Ink30`, foco `Ink40`.
- Declarar los fondos de hover y pulsado **sobre `/template/ ContentPresenter`**, y fijar el color de
  `TextBlock` y `MaterialIcon` de forma explícita en cada estado.
- Duración `{DynamicResource MotionInteractionDuration}` y `Easing="CubicEaseOut"` en toda transición, y
  `MotionCardLift`/`MotionSmallLift` para las elevaciones (respetan "animaciones sutiles").
- Para las etiquetas: `UpperCaseConverter` + `LetterSpacing` amplio + `InterfaceLabelFontFamily`.

**No:**

- Hex sueltos en vistas o estilos, salvo los colores de datos fijos del §3.3.
- `CornerRadius` distinto de 0 (salvo los indicadores del §6.1).
- `BoxShadow`, degradados decorativos o un color de marca nuevo. El acento es la tinta.
- Usar `AccentBrush` como color de énfasis de la interfaz. Su función es alimentar los estados de Fluent.
- Usar `TextMuted` para texto que haya que leer.
- Inventar un tercer estilo de popover o de aviso.

### Checklist de revisión visual con Dark Dev activo

- [ ] Ningún control muestra azul de Fluent (foco, `checked`, selección).
- [ ] Ningún recuadro gris de Fluent aparece en hover, pulsado o deshabilitado.
- [ ] Todas las esquinas son rectas, salvo los indicadores.
- [ ] Las tarjetas y paneles se distinguen del fondo por el borde y no solo por el relleno.
- [ ] Los modales y popovers tienen borde `Ink20` visible.
- [ ] El texto que hay que leer usa `TextPrimary` o `TextSecondary`, nunca `TextMuted`.
- [ ] El rojo solo aparece en peligro, vencido, cerrar, Papelera y "ahora".
- [ ] Los iconos de los botones cambian de color en hover y en activo (no se quedan blancos sobre un relleno blanco).
- [ ] Las transiciones van a 150 ms y desaparecen o se acortan con "animaciones sutiles".
- [ ] La pantalla sigue viéndose bien al cambiar a la base Dark y a un tema claro (prueba de que solo se usan tokens).

---

## 13. Ficheros de referencia

| Fichero | Contenido |
|---|---|
| `enki/src/enki/services/BuiltInThemeCatalog.cs` | Definición de Dark Dev y del resto de temas incorporados. |
| `enki/src/enki/services/ThemeCatalog.cs` | Los 19 slots, sus variables CSS y los valores por defecto. |
| `enki/src/enki/services/ThemeRegistry.cs` | Aplicación en vivo y derivación de `SystemAccentColor*`. |
| `enki/src/enki/services/CustomThemeService.cs` | Activar, desactivar, importar y exportar temas. |
| `enki/src/enki/services/MotionService.cs` | Duraciones y desplazamientos de las animaciones. |
| `enki/src/enki/services/CalendarLayerPalette.cs` | Colores fijos de capas, Kanban y Polar. |
| `enki/src/enki/styles/Colors.axaml` | Paleta base, brushes, rampa de tinta, fuentes y texturas. |
| `enki/src/enki/styles/Styles.axaml` | Todos los estilos por clase. |
| `enki/src/enki/styles/ControlThemes.axaml` | Re-templates de `ToggleSwitch`, `RectangularCheckBox` y `RectangularSlider`. |
| `enki/src/enki/views/editor/Markdown/MarkdownStyle.cs` | Correspondencia entre el tema y el editor Markdown. |
| `enki/src/enki/views/sessions/SessionGraphRenderer.cs` | Correspondencia entre el tema y el grafo. |
| `.docs/project-info/sistema-diseno-web.md` | Origen del lenguaje visual (web rezzt.dev). |
| `.docs/project-info/animaciones-y-rendimiento.md` | Detalle del sistema de movimiento. |
