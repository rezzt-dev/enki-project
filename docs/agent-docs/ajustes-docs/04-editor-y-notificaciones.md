# Ajustes › Editor y Ajustes › Notificaciones — "no molestar" y sonido

> **Alcance**: §1 cubre **Ajustes › Editor** (`SettingsSection.Editor`,
> `SettingsView.axaml:369-484`) — todas las preferencias del editor de notas, agrupadas en el código
> bajo el comentario `Editor (docs/ajustes-docs/04-editor-y-notificaciones.md §1)` de
> `AppSettings.cs:156-185` más las que viven junto a ellas en la misma página. §2 cubre específicamente
> el horario "no molestar" y el sonido de notificación dentro de **Ajustes › Notificaciones**
> (`SettingsSection.Notifications`).
>
> **Fuera de alcance**: el resto de Ajustes › Notificaciones — mostrar/ocultar la campana y la bandeja
> de recordatorios en la barra de título, activar toasts de escritorio, silenciar por categoría
> (info/éxito/aviso/error) y la ventana de "recordatorio próximo a vencer" — no está citado hacia este
> documento en el código; es funcionalidad general del sistema de notificaciones, documentada en
> `docs/notificaciones-y-recordatorios.md`.
>
> **Fuente de verdad**: verificado contra
> `enki/src/enki/viewmodels/settings/SettingsViewModel.cs`, `enki/src/enki/models/AppSettings.cs`,
> `enki/src/enki/services/EditorPreferences.cs`, `enki/src/enki/services/DoNotDisturbSchedule.cs`,
> `enki/src/enki/services/NotificationSoundPlayer.cs` y
> `enki/src/enki/viewmodels/shell/NotificationCenterViewModel.cs`.

## §1 — Editor de notas

Todas las preferencias de esta sección se aplican **en caliente** a cualquier `EditorView` ya abierta:
`SettingsViewModel` escribe tanto en `AppSettings`/`SettingsService` (persistencia) como en
`EditorPreferences`, una clase estática con un evento `Changed` al que cada editor abierto está
suscrito — así un cambio hecho en Ajustes se refleja sin tener que cerrar y reabrir la nota
(`EditorPreferences.cs:5-8`).

| Ajuste | Rango / valores | Nota |
|---|---|---|
| Fuente del editor (`EditorFontFamily`) | fuentes del sistema + "por defecto" (Space Grotesk empaquetada) | Independiente de la fuente de la interfaz (`InterfaceFontFamily`, Ajustes › Apariencia) |
| Tamaño de fuente (`EditorFontSize`) | 12–20, guardado con debounce de 300 ms | Slider — arrastrar dispara decenas de cambios/seg.; solo la escritura a disco se retrasa, `EditorPreferences.FontSize` se actualiza al instante |
| Ancho de columna (`EditorColumnWidth`) | 480–1400 px, mismo debounce | "Ancho máximo de la columna de texto" |
| Posición de columna (`EditorColumnPosition`) | Centrada / Izquierda | Segmented control, persistido como string `"Center"`/`"Left"` |
| Ajuste de línea (`EditorWordWrap`) | on/off, por defecto **on** | "Ajustar líneas largas en vez de desplazar horizontalmente" |
| Barra de desplazamiento (`EditorShowScrollbar`) | on/off, por defecto **on** | Solo oculta raíl/pulgar; el scroll con rueda/teclado sigue funcionando |
| Números de línea (`EditorLineNumbers`) | on/off, por defecto **off** | |
| Panel lateral (`EditorSidePanelVisible`) | on/off, por defecto **on** | Esquema/enlaces inversos/estadísticas — solo visibilidad, no qué pestaña está activa |
| **Tamaño de tabulación** (`EditorTabSize`) | 2–8 espacios, por defecto **4**, mismo debounce | Aplicado a `AvaloniaEdit.TextEditor.Options.IndentationSize` con `ConvertTabsToSpaces = true` — inserta espacios reales, no un carácter tab literal |

- **Autoguardado**: la tarjeta "Autoguardado" de esta página es puramente informativa ("Las notas se
  guardan automáticamente mientras escribís") — no hay ningún ajuste que activar/desactivar; el editor
  autoguarda siempre.
- **Ajustes del editor que existen en `AppSettings` pero no tienen control en esta página**:
  `EditorSidePanelTab` (qué pestaña del panel lateral está activa — "Outline"/"Backlinks"/"Stats") y las
  cinco banderas de expansión de la barra de herramientas de formato
  (`EditorToolbarVisible`/`EditorToolbarTextStyleExpanded`/`…HeadingsExpanded`/`…ListsExpanded`/
  `…InsertExpanded`/`…CodeExpanded`, `AppSettings.cs:170-185`). `SettingsViewModel` solo las **lee** al
  arrancar para poblar `EditorPreferences` (`SettingsViewModel.cs:480-485`); se escriben desde dentro del
  propio editor (chevrons de cada grupo de la barra de herramientas, `EditorViewModel`), no desde
  Ajustes. Se persisten en el mismo `settings.json` y sobreviven a un reinicio, pero Ajustes no es su
  punto de entrada.

## §2 — Horario "no molestar" y sonido de notificación

Ventana horaria en la que un evento nuevo **se sigue registrando en la campana** (el centro de
notificaciones nunca deja de guardar eventos) pero **no dispara el toast del sistema ni el sonido** —
el filtro actúa solo sobre esas dos señales inmediatas, nunca sobre el registro persistente.

- **`DoNotDisturbEnabled`** (por defecto off), **`DoNotDisturbStart`**/**`DoNotDisturbEnd`** (formato
  `"HH:mm"`, por defecto `22:00`/`08:00`) — los dos campos de hora son cajas de texto libres
  (`PlaceholderText="22:00"`/`"08:00"`), no selectores de hora con validación en la UI.
- **Lógica de la ventana** — `DoNotDisturbSchedule.IsWithin(start, end, now)`
  (`DoNotDisturbSchedule.cs:17-26`), lógica pura sin dependencia de UI para poder testearla:
  - Si `start`/`end` no parsean como hora válida, o son **iguales**, la ventana se trata como
    **"nunca activa"** (no como "siempre activa") — una configuración accidentalmente rota silencia lo
    mínimo posible en vez de silenciarlo todo.
  - Soporta tanto una ventana dentro del mismo día (`13:00–18:00`) como una que cruza medianoche
    (`22:00–08:00`, el valor por defecto): si `start < end` es un rango normal; si no, es "desde `start`
    hasta medianoche, y de medianoche hasta `end`".
- **Dónde se aplica** — `NotificationCenterViewModel.OnNotificationAdded`
  (`NotificationCenterViewModel.cs:52-63`): la notificación ya se guardó en `NotificationService` antes
  de llegar aquí (o ni siquiera dispararía este evento si estuviera silenciada por categoría, ver
  `docs/notificaciones-y-recordatorios.md`). Si `DoNotDisturbEnabled` y `IsWithin(...)` con la hora local
  actual, se sale sin más: ni toast (`ShowDesktopToasts`) ni sonido (`NotificationSoundEnabled`), aunque
  ambos estén activados globalmente.
- **`NotificationSoundEnabled`** (por defecto off) + **`NotificationSoundVolume`** (0–100, por defecto
  70, slider con debounce): botón "Probar" (`TestNotificationSoundCommand`) reproduce el sonido a ese
  volumen sin esperar a una notificación real.
  - **Volumen 0 no equivale solo a "bajo"**: `NotificationSoundPlayer.Play` corta en seco si
    `volumePercent <= 0`, sin intentar ni siquiera el pitido de último recurso — es el "silencio"
    explícito de la cadena de fallback (`NotificationSoundPlayer.cs:26,36`).
  - **Cadena de fallback** al reproducir (`NotificationSoundPlayer.cs:9-56`): asset empaquetado
    (`avares://enki/assets/sounds/notification.wav`, extraído a un fichero temporal la primera vez) →
    sonido nativo del sistema operativo → `Console.Beep()` → silencio. Cada eslabón se intenta con su
    propio try/catch y solo se pasa al siguiente si el anterior falla o no está disponible en esa
    máquina. No hay ninguna librería de audio en el proyecto — "reproducir" siempre significa invocar un
    mecanismo ya presente en el SO:
    - **Windows**: P/Invoke directo a `winmm.dll` (`PlaySoundW`) — sin paquete NuGet adicional porque el
      proyecto apunta a `net8.0` puro, no `net8.0-windows`.
    - **macOS**: `afplay` con `-v` (ganancia lineal 0.0–1.0, escalada desde el 0–100 de Enki).
    - **Linux**: `paplay --volume=<unidades PulseAudio>` (65536 = 100 %); si `paplay` no está instalado,
      cae a `aplay` — que reproduce el mismo WAV pero **sin control de volumen propio** (mejor sonido a
      volumen del sistema que ningún sonido).
    - **Control de volumen real vs. solo "silencio en 0"**: solo el asset empaquetado reproducido por
      `afplay`/`paplay` respeta la escala 0–100 de verdad. El sonido nativo del sistema
      (`SystemAsterisk` en Windows, `Ping.aiff` en macOS, `message.oga` en Linux) y el pitido de
      `Console.Beep()` **no exponen control de volumen en su propia API** — para esos dos eslabones el
      ajuste de volumen solo decide si suena o no (0 = nada), no qué tan fuerte.

## Referencias cruzadas

- Registro de notificaciones, silenciar por categoría, badges de campana/bandeja: sistema general de
  notificaciones, `docs/notificaciones-y-recordatorios.md`.
- Zona de peligro / continuidad de datos: `01-continuidad-y-proteccion-de-datos.md`.
- Cuenta y sincronización Polar: `03-cuenta-y-sincronizacion-polar.md`.
