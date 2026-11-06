# Zoom de interfaz

Referencia técnica del zoom global de la interfaz de Enki: qué escala, dónde se controla, cómo se
aplica visualmente y cómo persiste. Documenta el comportamiento tal y como está implementado hoy.

## 1. Qué es

Un factor de escala aplicado a **toda la interfaz de la aplicación** (sidebar, contenido, botones
flotantes de la barra de título) — no un zoom local de un documento o de una pantalla concreta.
Rango 50%–200%, en pasos de 10 puntos porcentuales, con 100% como valor por defecto (el tamaño con el
que la app ya se distribuía antes de existir esta funcionalidad).

Estado central: `MainWindowViewModel.UiZoomScale` (`double`, `[ObservableProperty]`), definido en
`enki/src/enki/viewmodels/shell/MainWindowViewModel.cs`. Es el factor de escala real (`percent /
100.0`), no el porcentaje en sí — el porcentaje entero vive en `VaultSettings.UiZoomPercent` y en el
espejo `SettingsViewModel.UiZoomPercent` (ver §2).

## 2. Dónde vive el valor: por-vault, no global

A diferencia de casi todo el resto de ajustes de la app (que persisten en
`%AppData%/Enki/settings.json` vía `SettingsService`, un único archivo global compartido por
cualquier vault que se abra), el zoom persiste **por vault**, en `<vault>/.enki/settings.json`, vía
`VaultSettingsService` y el modelo `enki/src/enki/models/VaultSettings.cs`:

```csharp
public sealed class VaultSettings
{
    public int UiZoomPercent { get; set; } = 100;
}
```

`VaultSettings.UiZoomPercent` es, según su propio comentario XML-doc, el primer campo de esta clase —
futuros ajustes por-vault crecerán `VaultSettings` de la misma forma en que `AppSettings` ya crece.
La razón de que viaje con el vault en vez de ser global: abrir un vault distinto debe ver su propio
nivel de zoom, no el que dejó el último vault abierto.

Tres puntos de sincronización con este archivo:

- **Constructor de `MainWindowViewModel`** (`MainWindowViewModel.Composition.cs`, línea ~87):
  `_uiZoomScale = _vaultSettingsService.Settings.UiZoomPercent / 100.0` — valor inicial antes de que
  exista ninguna ventana.
- **`InitializeShell()`** (`MainWindowViewModel.Composition.cs`, línea ~249): vuelve a leer el valor
  tras `_vaultSettingsService.Reload()`. Necesario aquí (no solo en el constructor) porque este método
  también se ejecuta después de que `VaultSetupViewModel` acabe de apuntar `VaultService` a un vault
  recién creado — algo que ocurre *después* de que `_vaultSettingsService` ya se hubiera cargado la
  primera vez, así que hay que recargar para no quedarse con el zoom del vault anterior (o el
  default).
- **`OnVaultChanged()`** (`MainWindowViewModel.Callbacks.cs`, línea ~248): mismo patrón, al cambiar de
  vault desde Ajustes.

`SettingsViewModel.UiZoomPercent` (el espejo editable desde la barra de Ajustes) se inicializa desde
`vaultSettingsService.Settings.UiZoomPercent` en su constructor (línea ~443) y, en su setter parcial
`OnUiZoomPercentChanged` (línea ~737), escribe de vuelta en `_vaultSettingsService.Settings.UiZoomPercent`,
dispara un guardado debounced propio (`_vaultSettingsSaveTimer`, 300 ms — un temporizador *distinto*
del que usa el resto de `SettingsViewModel`, porque persisten en dos archivos diferentes y un arrastre
de slider en uno no debería forzar la escritura del otro) y notifica al callback
`_onUiZoomChanged`, cableado en la composición a `MainWindowViewModel.OnUiZoomChanged` — que aplica el
nuevo `UiZoomScale` en caliente sobre la ventana ya en pantalla, sin esperar a la siguiente
navegación.

`SettingsViewModel.FlushPendingSave()` fuerza a disco cualquier guardado pendiente de ambos
temporizadores (ajustes globales y ajustes de vault); se llama desde el `Closing` de `MainWindow.axaml.cs`
junto al resto de flushes de autosave (editor de notas, editor de skills), para que un cambio de zoom
justo antes de cerrar la app no se pierda.

## 3. Cómo se controla

Tres superficies, las tres pasan por el mismo rango 50–200 y el mismo paso de 10:

### a) Barra de zoom en Ajustes › Apariencia

`enki/src/enki/views/settings/SettingsView.axaml` (~línea 301–348, tarjeta "Zoom de la interfaz"):
etiqueta con el porcentaje actual (`UiZoomPercentLabel`, formateado como `"{0} %"`), botón "−"
(`ZoomOutCommand`), botón "+" (`ZoomInCommand`) y una **barra de solo lectura** entre ambos.

La barra es deliberadamente no arrastrable — así lo indica el comentario en el propio AXAML: el
ajuste se hace con los botones, no arrastrando la barra. Se construye con dos columnas `Grid` de
ancho tipo `Star` (proporción del ancho disponible, no píxeles fijos):

- `SettingsViewModel.UiZoomBarFillStar` = `new GridLength(UiZoomPercent - 50, GridUnitType.Star)`
- `SettingsViewModel.UiZoomBarRemainderStar` = `new GridLength(200 - UiZoomPercent, GridUnitType.Star)`

Juntas suman siempre el rango completo (150 puntos), así que la primera columna representa la
fracción rellena de la barra sobre cualquier ancho de tarjeta/ventana. Ambas se recalculan en
`OnUiZoomPercentChanged` vía `OnPropertyChanged`, porque no son valores propios — se derivan de
`UiZoomPercent`.

Dentro de esa barra hay un **marcador fijo en el 100%**: un `Grid` de columnas `1*,2*` con un
`Border` de 2px de ancho anclado a la derecha de la primera columna. Como el rango es fijo (50–200),
la posición del marcador (a 1/3 desde la izquierda) es una proporción constante — no depende del zoom
actual, solo del rango.

Tanto el relleno de la barra como el marcador de 100% usan `CornerRadius="0"` en sus `Border`, en
línea con la regla de `docs/sistema-diseno-web.md` de "CornerRadius=0 en todo salvo indicadores
circulares" — no hay ninguna excepción pendiente en este control.

### b) Atajos de teclado

`enki/src/enki/views/shell/MainWindow.axaml` (`Window.KeyBindings`, ~línea 83–92), estilo Obsidian:

| Gesto | Comando |
|---|---|
| `Ctrl+OemPlus` / `Ctrl+Add` (numérico) | `ZoomInCommand` |
| `Ctrl+OemMinus` / `Ctrl+Subtract` (numérico) | `ZoomOutCommand` |
| `Ctrl+D0` / `Ctrl+NumPad0` | `ZoomResetCommand` |

Ambas variantes de teclado (fila OEM y teclado numérico) están cubiertas para cada gesto.

`MainWindowViewModel.ZoomIn`/`ZoomOut` (`[RelayCommand]`) llaman a `AdjustUiZoom(±UiZoomStepPercent)`
(`UiZoomStepPercent = 10`, la misma constante que usa la barra de Ajustes). `ZoomReset` fija
`Settings.UiZoomPercent = 100` directamente.

### c) Ctrl+rueda del ratón

No expresable como `KeyBinding` porque necesita interceptar el evento de scroll *antes* de que llegue
a un `ScrollViewer` normal (listas, tablero Kanban, etc.), algo que un `KeyBinding` declarativo no
puede hacer. Se registra a mano en el constructor de `MainWindow.axaml.cs`:

```csharp
AddHandler(PointerWheelChangedEvent, OnCtrlWheelChanged, RoutingStrategies.Tunnel);
```

En fase *Tunnel* (de la ventana hacia el control bajo el cursor), `OnCtrlWheelChanged` solo actúa
cuando `KeyModifiers.Control` está activo — en ese caso llama a `viewModel.AdjustUiZoom(...)` con el
signo según `e.Delta.Y` y marca `e.Handled = true`, evitando que el gesto llegue al scroll normal.
Sin Ctrl pulsado, el evento pasa de largo sin tocar el zoom.

### Punto de entrada común

Los tres caminos b) y c) (atajos y Ctrl+rueda) convergen en `MainWindowViewModel.AdjustUiZoom(int
deltaPercent)`, que escribe en `SettingsViewModel.UiZoomPercent` (no directamente en
`_vaultSettingsService.Settings`) para reutilizar su mismo debounce/guardado/callback (§2) en vez de
duplicarlos. El camino a) (barra de Ajustes) escribe directamente en `SettingsViewModel.UiZoomPercent`
también, así que los tres terminan en el mismo setter parcial. Todos usan
`Math.Clamp(valor, 50, 200)` para no salirse del rango.

## 4. Cómo se aplica visualmente

`enki/src/enki/views/shell/MainWindow.axaml` (~línea 95–103) envuelve el `Grid` raíz completo en un
`LayoutTransformControl`:

```xml
<LayoutTransformControl>
  <LayoutTransformControl.LayoutTransform>
    <ScaleTransform ScaleX="{Binding UiZoomScale}" ScaleY="{Binding UiZoomScale}" />
  </LayoutTransformControl.LayoutTransform>
  <Grid> ... todo el shell ... </Grid>
</LayoutTransformControl>
```

Se eligió `LayoutTransformControl` en vez de un `RenderTransform` suelto porque mide a su hijo y
reporta el tamaño *ya escalado* como su propio `DesiredSize` — así el layout entero (sidebar +
contenido + botones flotantes de la barra de título) reflowea correctamente al hacer zoom, en vez de
recortarse visualmente o dejar hueco muerto alrededor (que es lo que pasaría con un `RenderTransform`
puro, que solo transforma el pintado, no afecta el layout).

**Popups y el `LayoutTransformControl`**: por defecto, Avalonia renderiza cualquier `Popup` (y por
tanto todo lo construido sobre él — `ComboBox`, `ContextMenu`, `ToolTip`) como una superficie de
ventana nativa aparte, fuera del árbol visual — así que no hereda el `ScaleTransform` del
`LayoutTransformControl`. `enki/src/enki/styles/Styles.axaml` (~línea 22–37) resuelve esto con un
único selector global:

```xml
<Style Selector="Popup">
  <Setter Property="ShouldUseOverlayLayer" Value="True" />
</Style>
```

Con `ShouldUseOverlayLayer=True`, el popup se dibuja dentro de la propia `Window` (su `OverlayLayer`,
ya presente en la plantilla por defecto), como un control más del árbol visual — así queda dentro del
alcance del `ScaleTransform` y escala junto con el resto de la interfaz. Este selector cubre *todos*
los `Popup` de la app: según el propio comentario del código (auditado en la Fase 1 del roadmap de
mejora continua), no existe ningún `Flyout` ni ningún diálogo implementado como `Window` secundaria en
`views/` — solo `MainWindow.axaml` es una `Window` real — así que no hay ningún popover/diálogo que
dependiera de una regla propia. El propio comentario deja constancia de que solo falta una
verificación visual interactiva de este comportamiento (no había entorno gráfico disponible en la
sesión donde se escribió, para confirmarlo a ojo) — es decir, la cobertura de código está completa por
construcción, pero no había sido comprobada visualmente en el momento de escribirse ese comentario.

## 5. Panel informativo de atajos en Ajustes

Además de la tarjeta con la barra de zoom (§3a), Ajustes tiene una segunda tarjeta puramente
informativa, "Zoom de la vista" (`SettingsView.axaml`, ~línea 960–975), que lista los tres atajos como
texto (no son controles interactivos, solo documentación en pantalla):

| Columna "tecla" (mono, negrita) | Columna descripción |
|---|---|
| `Ctrl++ / Ctrl+-` (texto literal, sin localizar) | "Aumentar o disminuir el nivel de zoom" |
| `Ctrl+0` (texto literal, sin localizar) | "Restablecer el zoom al 100%" |
| `{loc:Tr Settings_ViewZoom_WheelKey}` | "Aumentar o disminuir el nivel de zoom" |

**Inconsistencia observada**: en la fila del ratón, la columna "tecla" (estilada en fuente monoespacio
en negrita, como si fuera un atajo tipo `Ctrl+0`) está enlazada a la clave de localización
`Settings_ViewZoom_WheelKey`, cuyo valor en español es la frase completa "Zoom con la rueda del
ratón" — no un texto corto de tipo tecla como en las otras dos filas (que además están escritas
literalmente en el AXAML, sin pasar por localización). El resultado visible es una fila donde la
"tecla" es una oración larga en mono/negrita, en vez de algo como "Ctrl+Rueda". Se documenta aquí como
comportamiento observado, no como algo corregido.

## 6. Resumen de rango y valores

| Constante | Valor | Dónde |
|---|---|---|
| Mínimo | 50% | `MainWindowViewModel.UiZoomMinPercent`, `SettingsViewModel.UiZoomMinPercent` (constantes privadas duplicadas en ambas clases, mismo valor) |
| Máximo | 200% | `MainWindowViewModel.UiZoomMaxPercent`, `SettingsViewModel.UiZoomMaxPercent` |
| Paso | 10 puntos porcentuales | `MainWindowViewModel.UiZoomStepPercent` (pública, la usa `MainWindow.axaml.cs` para Ctrl+rueda), `SettingsViewModel.UiZoomStepPercent` (privada) |
| Por defecto | 100% | `VaultSettings.UiZoomPercent` (valor inicial del campo) |

Las constantes de rango/paso están duplicadas (no compartidas) entre `MainWindowViewModel` y
`SettingsViewModel` — ambas clases las definen por separado con el mismo valor literal, no hay una
fuente única compartida para 50/200/10.
