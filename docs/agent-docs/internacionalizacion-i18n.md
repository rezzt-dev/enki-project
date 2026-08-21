# Internacionalización (i18n) de Enki — plan e inventario completo

> **Estado: implementado.** Este documento se escribió como plan de implementación y hoy describe,
> en su mayor parte, un sistema que ya existe en el código tal cual se diseñó aquí: `localization/`
> con 7 `.resx` (`Strings.resx` neutral/fuente en `en-US` + `en-GB`/`de-DE`/`es-ES`/`es-419`/`fr-FR`),
> **1028 claves por idioma** con paridad verificada por test (`enki.Tests`), `LocalizationService` +
> `TrExtension`, selector de idioma en primer arranque (`LanguageSetupView`) y `Settings.Language`
> persistido. Las secciones §1–§7 describen el diseño tal y como se construyó (quedan como
> referencia arquitectónica del sistema actual, no como trabajo pendiente); la única sección
> genuinamente prescriptiva a día de hoy es **§8, "Proceso para añadir una clave nueva en el
> futuro"** — léela antes de tocar cualquier texto de UI. Si algún detalle puntual de estas
> secciones ya no coincide exactamente con el código (algún ejemplo de clave, algún matiz de un
> caso difícil), el código manda, como en el resto de `docs/`.

Documento de referencia para implementar soporte multiidioma profesional en Enki. Escrito
como fuente de verdad completa para quien ejecute el trabajo (humano o agente) — no hace
falta releer el historial de chat que lo originó.

## 0. Objetivo

Hoy Enki es una aplicación **100% hardcodeada en español** (con algún resto de inglés
suelto) y **sin ninguna infraestructura de localización**: no hay `.resx`, no hay
`IStringLocalizer`, no hay ninguna propiedad de idioma en los ajustes, y el único uso de
`CultureInfo` en todo el código es `CultureInfo.InvariantCulture` para redondeo numérico
interno (nunca para texto de UI).

El objetivo es que Enki soporte estos **6 idiomas** como aplicación profesional
multiidioma:

| Idioma | Código BCP-47 | Notas |
|---|---|---|
| Inglés (EE. UU.) | `en-US` | Formato de fecha M/d/yyyy, unidades imperiales si aplican. |
| Inglés (Reino Unido) | `en-GB` | Formato de fecha d/M/yyyy, ortografía británica (`colour`, `organise`...). |
| Alemán | `de-DE` | |
| Español (España) | `es-ES` | Con `ñ`, idioma origen actual del contenido. Vosotros/vosotras si se usa 2ª persona plural. |
| Francés | `fr-FR` | |
| Español latinoamericano | `es-419` | Código UN M49 "Latinoamérica y Caribe", reconocido por .NET/ICU. Ustedes en vez de vosotros, vocabulario neutro (evitar modismos de un solo país). |

Requisitos explícitos del usuario que gobiernan todo el diseño de abajo:

1. **Cero fugas de idioma.** Si la app está en francés, no puede aparecer ni un solo texto
   en castellano (ni en ningún otro idioma) salvo contenido que el propio usuario haya
   escrito (sus notas, nombres de sus columnas/tarjetas, etc.).
2. **Selección de idioma en el primer arranque.** La primera vez que se abre Enki, antes de
   nada, se le pregunta al usuario en qué idioma quiere la app.
3. **La nota de bienvenida se crea en el idioma elegido**, no en español fijo como ahora.
4. Implementación **profesional y completa**: nada de "traducir solo lo que se vea a
   primera vista" — cobertura total, arquitectura limpia, mantenible a futuro (añadir un
   7º idioma o una nueva pantalla no debe ser doloroso).

Este documento cubre: arquitectura técnica recomendada, cambios de modelo de datos, el
flujo de primer arranque, el **inventario exhaustivo de todo lo que hay que traducir**
(con archivo y línea), los casos difíciles que requieren una decisión de diseño (no solo
traducción literal), y un plan de implementación por fases.

---

## 1. Arquitectura técnica recomendada

### 1.1 Formato de recursos: `.resx` + `ResourceManager`

Se recomienda **`.resx`** (el estándar .NET) en vez de JSON casero:

- Tooling maduro (editor de Rider/VS, detección de claves huérfanas, comentarios por
  clave).
- `ResourceManager` con fallback automático de cultura (`fr-FR` → `fr` → neutral) gratis.
- Compila a satellite assemblies o se puede empotrar — cualquiera de las dos sirve para
  una app de escritorio autocontenida como Enki (`net8.0`, `WinExe`).

Estructura propuesta, en un nuevo proyecto/carpeta `enki/src/enki/localization/`:

```
localization/
  Strings.resx            ← idioma "neutral"/fuente. Recomendado: en-US como fuente
                             (convención habitual en .NET; es-ES es hoy el idioma real
                             del contenido pero como código fuente de claves conviene
                             inglés neutro para que el resto de traductores no arrastren
                             modismos de es-ES). Decisión abierta — ver §1.6.
  Strings.en-US.resx
  Strings.en-GB.resx
  Strings.de-DE.resx
  Strings.es-ES.resx
  Strings.es-419.resx
  Strings.fr-FR.resx
  LocalizationService.cs
  TrExtension.cs          ← MarkupExtension para XAML
  SupportedLanguage.cs    ← enum/registro de los 6 idiomas + metadatos (nombre nativo, bandera/código)
```

Cada clave sigue una convención `Area_Elemento` en PascalCase, por ejemplo:
`Nav_Dashboard`, `Nav_Calendar`, `Kanban_ColumnTodo_DefaultTitle`,
`Settings_General_Theme_Label`, `Common_Cancel`, `Common_Delete`,
`Dialog_ConfirmDelete_Title`, `Notification_SessionCreated_Title`. Namespacing por área
evita colisiones entre las ~800-1000 claves que va a tener esta app (ver inventario §3) y
hace grepeable cada pantalla.

### 1.2 `LocalizationService` — el corazón del sistema

Avalonia no tiene binding nativo a `.resx`, así que se necesita un servicio singleton que:

```csharp
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture { get; private set; } = new("es-ES");

    public string this[string key] =>
        Strings.ResourceManager.GetString(key, CurrentCulture) ?? $"[[{key}]]";

    public void SetLanguage(string cultureCode)
    {
        CurrentCulture = new CultureInfo(cultureCode);
        CultureInfo.CurrentCulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = CurrentCulture;

        // Indexer refresca TODOS los bindings "{Binding [Clave]}" activos de golpe —
        // patrón estándar de Avalonia/WPF para cambiar idioma en caliente sin reiniciar.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Avalonia.Data.Binding.IndexerName));
    }
}
```

Puntos clave de diseño:

- **`$"[[{key}]]"` como fallback visible** en vez de silenciosamente devolver la clave o
  vacío: en build Debug esto hace evidente al instante cualquier clave sin traducir en
  algún idioma; en Release podría degradar a la variante `en-US` en su lugar (ver §1.5,
  fallback chain). Decisión: usar `[[clave]]` solo en Debug, fallback silencioso a
  `en-US` en Release, para que un idioma incompleto en producción degrade con gracia en
  vez de mostrar corchetes al usuario final.
- El cambio de idioma **no requiere reiniciar la app** — todo lo bindeado vía el indexer
  se refresca solo. Esto es importante porque el usuario pidió "aplicación profesional":
  un selector de idioma en Ajustes que exige reiniciar es una experiencia de segunda.
- `CultureInfo.CurrentCulture`/`CurrentUICulture` también se actualizan, así que
  cualquier `date.ToString("...")` sin formato explícito de cultura (la mayoría del código
  actual, ver §3.6) automáticamente empieza a usar la cultura correcta sin tocar esas
  líneas — **excepto** que ahora mismo 6 sitios usan arrays de meses hardcodeados en vez
  de `CultureInfo` (ver §3.5); esos si hay que tocarlos, y de hecho conviene **borrarlos**
  en vez de traducirlos 6 veces.

### 1.3 Uso desde XAML

Dos opciones, se recomienda la (b) por brevedad en 41 vistas:

**(a) Binding directo al indexer:**
```xml
<TextBlock Text="{Binding [Nav_Dashboard], Source={x:Static loc:LocalizationService.Instance}}"/>
```

**(b) `MarkupExtension` dedicada** (más corta, mismo mecanismo por debajo):
```csharp
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider sp) =>
        new Binding($"[{Key}]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay };
}
```
```xml
<TextBlock Text="{loc:Tr Nav_Dashboard}"/>
<Button Content="{loc:Tr Common_Save}" ToolTip.Tip="{loc:Tr Editor_Toolbar_Bold_Tooltip}"/>
```

Para los `StringFormat` con interpolación (`'Sincronizado con Polar · {0}'`, `'COMPLETADAS
({0})'`, `'DÍA {0}'`, etc. — ver §3.1), **no usar `StringFormat` con orden fijo de
palabras**: cada idioma puede necesitar reordenar. Usar en su lugar
`string.Format(loc["Key_WithPlaceholder"], value)` con la clave conteniendo el `{0}`, p.
ej. clave `Reminders_LastSynced` = `"Sincronizado con Polar · {0}"` en es-ES pero libre de
reordenarse en cada idioma (p. ej. alemán podría preferir `"Zuletzt mit Polar
synchronisiert: {0}"`). Esto aplica tanto a los `StringFormat` en XAML como a las
interpolaciones `$"..."` en C#.

### 1.4 Pluralización

.NET no trae reglas de plural CLDR out-of-the-box. Hoy el código ya tiene pluralización
manual rota en varios sitios (`"skill(s)"` en `SkillsViewModel.cs:419`, concatenación de
sufijos `s` condicional en `TrashViewModel.cs:261-268` — ver §3.4). Para 6 idiomas con
reglas de plural distintas (el francés trata el 0 como singular; el resto de idiomas
objetivo son todos "singular en 1, plural en el resto", así que no hace falta ICU
completo, pero sí una regla explícita por si el 7º idioma futuro la necesita) se
recomienda un helper mínimo:

```csharp
public static class Plural
{
    // key_one / key_other por convención. Para es-419/es-ES/en-US/en-GB/de-DE: singular=1, plural≠1.
    // Para fr-FR: singular = 0 o 1.
    public static string Get(string key, int count, params object[] args)
    {
        var isSingular = LocalizationService.Instance.CurrentCulture.TwoLetterISOLanguageName == "fr"
            ? count is 0 or 1
            : count == 1;
        var resolved = LocalizationService.Instance[isSingular ? $"{key}_one" : $"{key}_other"];
        return string.Format(resolved, args);
    }
}
```
Uso: `Plural.Get("Trash_DocumentsDeleted", notesDeleted, notesDeleted)` con claves
`Trash_DocumentsDeleted_one` = `"{0} documento"` / `Trash_DocumentsDeleted_other` =
`"{0} documentos"` (y equivalentes en los otros 5 idiomas). Esto reemplaza por completo el
patrón actual de `TrashViewModel.BuildEmptyPushMessage` (concatenación manual con `" y "` —
tampoco generaliza a otros idiomas, hay que reescribirlo como una sola frase con plurales
resueltos por locale, no como lista de fragmentos pegados).

### 1.5 Cadena de fallback

`fr-FR` → `fr` (no existe, se salta) → `en-US` (fuente) → clave literal entre corchetes
(solo Debug). Configurar `ResourceManager` con `UICulture` explícito por llamada en vez de
depender solo del hilo, para que el cambio de idioma en caliente (§1.2) sea instantáneo sin
importar en qué hilo se invoque.

### 1.6 Separación estricta: cultura de UI vs. cultura de almacenamiento

**Regla dura:** todo lo que se serializa a disco (JSON de `settings.json`, `kanban.json`,
IDs, claves de diccionario tipo `CalendarHiddenLayerKeys`) sigue usando
`CultureInfo.InvariantCulture` o formatos fijos (ISO 8601 para fechas, `.` para decimales)
— **nunca** la cultura activa del usuario. Solo el texto que se **muestra** en pantalla usa
`CurrentCulture`. Esto ya se respeta hoy en los pocos sitios que usan
`InvariantCulture` (renderizado de color RGBA — ver inventario §3.6); hay que mantenerlo
como regla explícita al tocar cualquier `ToString`/`Parse` durante la implementación, para
no romper la reproducibilidad de archivos guardados al cambiar de idioma.

### 1.7 Formato de fechas: eliminar los arrays hardcodeados, no traducirlos

El código actual tiene **6 copias independientes** de arrays de meses/días en español
(`SpanishMonths`, `SpanishMonthsShort`, `SpanishWeekdaysFull`, etc.) repartidas en
`CalendarViewModel.cs`, `CalendarAgendaGroupViewModel.cs`, `CalendarQuickCreateViewModel.cs`,
`KanbanCardEditorViewModel.cs`, `PolarTaskEditorViewModel.cs`,
`ReminderEditorViewModel.cs` — con un comentario explícito en el código admitiendo que es
intencional para evitar depender de `CultureInfo`. Como `CultureInfo` para los 6 idiomas
objetivo ya trae nombres de mes/día correctos de fábrica (`.NET`/ICU), la recomendación es
**borrar los 6 arrays y sustituir por `CultureInfo.CurrentCulture.DateTimeFormat`**
(`GetAbbreviatedMonthName`, `MonthNames`, `DayNames`, etc.) en vez de traducirlos a mano 6
idiomas × 6 archivos = 36 traducciones evitables. Esto también arregla de raíz que hoy la
etiqueta de rango de fechas concatene literalmente `" DE "` en español
(`CalendarAgendaGroupViewModel.cs:30`) — hay que convertir esas construcciones a claves de
plantilla con `{0}`/`{1}` (ver §1.3) para que el orden de palabras se pueda adaptar por
idioma, no solo el nombre del mes.

---

## 2. Modelo de datos y persistencia

### 2.1 `AppSettings.cs`

Siguiendo la convención ya establecida en el archivo (propiedades tipo `string` planas
para valores tipo-enum, para que `Models` no dependa de `ViewModels` — ver comentarios
existentes en `Theme`/`EditorColumnPosition`):

```csharp
/// <summary>Código de idioma BCP-47 elegido por el usuario ("es-ES", "en-US", "en-GB",
/// "de-DE", "fr-FR", "es-419"). Cadena vacía/null significa "todavía no elegido" — dispara
/// la pantalla de selección de idioma en el primer arranque (ver LanguageSetupViewModel).</summary>
public string? Language { get; set; }
```

`Language` nulo/vacío es la señal de "primer arranque, aún no preguntamos". Una vez
elegido, se persiste siempre (igual que `VaultPath`), y **se puede cambiar más adelante**
desde Ajustes › General (nueva sección "Idioma", con el mismo patrón de radio buttons /
combo que ya usa `StartupPage` en `SettingsView.axaml`).

### 2.2 Arranque de la app

En `App.axaml.cs` (o donde se resuelva `SettingsService` al arrancar), aplicar el idioma
guardado **antes** de construir cualquier ventana/viewmodel:

```csharp
var lang = settingsService.Settings.Language;
if (!string.IsNullOrEmpty(lang)) LocalizationService.Instance.SetLanguage(lang);
// si es null, se deja la UI en el idioma por defecto temporalmente — la pantalla de
// selección de idioma (ver §2.3) se muestra antes que ninguna otra pantalla y, al elegir,
// llama a SetLanguage + persiste.
```

`SettingsService.ResetToDefaults()` (línea 98-103 hoy) preserva solo `VaultPath` al
resetear — decisión de diseño pendiente: ¿"restablecer ajustes" debe volver a preguntar el
idioma, o preservarlo igual que preserva el vault? Recomendación: **preservar el idioma**
(el usuario ya lo eligió activamente, resetear temas/paneles no debería obligarle a
re-elegir idioma) — añadir `Language` a la lista de campos preservados en
`ResetToDefaults()`, igual que `VaultPath`.

---

## 3. Flujo de primer arranque

### 3.1 Dónde encaja

Hoy el "primer arranque" ya tiene una pantalla: `VaultSetupView.axaml` /
`VaultSetupViewModel.cs`, que se muestra cuando no hay vault configurado (`VaultPath` vacío
o inválido) y deja elegir "crear vault nuevo" / "abrir vault existente".

Hay que insertar **una pantalla nueva antes de esa**: `LanguageSetupView` /
`LanguageSetupViewModel`, que se muestra cuando `Settings.Language` es nulo/vacío,
independientemente de si ya hay vault o no (un usuario que actualiza desde una versión sin
esta funcionalidad y ya tiene vault configurado también debe pasar por aquí una vez).

Flujo resultante:

```
¿Settings.Language vacío?
  sí → LanguageSetupView (elige 1 de 6 idiomas, con nombre nativo: "Español (España)",
       "English (US)", "English (UK)", "Deutsch", "Français", "Español (Latinoamérica)")
       → SetLanguage(código) + Settings.Language = código + Save()
       → continúa al siguiente paso
  no → sigue directo
¿VaultPath vacío/inválido?
  sí → VaultSetupView (ya existente, ahora ya en el idioma correcto)
  no → arranca la shell normal
```

La pantalla de idioma **debe renderizar cada opción en su propio idioma nativo** (no
traducir "Alemán" al idioma que esté activo en ese momento — un hablante de alemán tiene
que poder reconocer "Deutsch" aunque la UI arranque en otro idioma por defecto). Esto es
contenido estático, no pasa por `LocalizationService` — es la única pantalla de la app que
no depende del idioma activo, por diseño.

### 3.2 Nota de bienvenida localizada

`VaultService.InitializeVault(path, seedWelcomeNote)` (líneas 27-39) escribe hoy
`Bienvenida.md` con contenido fijo en español (`WelcomeNoteContent`, líneas 451-472). Hay
que:

1. Mover `WelcomeNoteContent` de constante fija a **una clave de recurso por idioma**
   (`WelcomeNote_Content`), con el markdown completo traducido en los 6 `.resx`.
2. El **nombre del archivo** también depende del idioma — no forzar
   `"Bienvenida.md"` siempre. Recomendación: usar una clave `WelcomeNote_FileName` por
   idioma (`"Bienvenida.md"` es-ES/es-419, `"Welcome.md"` en-US/en-GB, `"Willkommen.md"`
   de-DE, `"Bienvenue.md"` fr-FR).
3. `InitializeVault` debe leer `LocalizationService.Instance["WelcomeNote_Content"]` /
   `["WelcomeNote_FileName"]` en el momento de crear el vault — como el flujo de primer
   arranque ya aplicó `SetLanguage` en el paso anterior (§3.1), esto sale gratis sin pasar
   el idioma como parámetro adicional.

Importante: esto **solo afecta vaults nuevos** (`seedWelcomeNote: true`, solo desde
"Crear vault nuevo"). Abrir un vault existente (`seedWelcomeNote: false`) nunca toca esto,
así que no hay migración de notas ya existentes que hacer.

### 3.3 Cambiar idioma después del primer arranque

Añadir un selector de idioma en Ajustes › General (mismo patrón visual que el resto de la
pantalla — ver `SettingsView.axaml` para el estilo de radios/combos ya usado en
`StartupPage`/`Theme`). Al cambiar:

- Llama a `LocalizationService.Instance.SetLanguage(nuevoCódigo)` → refresca toda la UI en
  caliente vía el indexer (§1.2), sin reiniciar.
- Persiste `Settings.Language` + `Save()`.
- **Ojo con los textos ya computados y cacheados en ViewModels** que no están bindeados al
  indexer sino calculados una vez en C# (p. ej. `PriorityLabel`, `SourceLabel`,
  `BucketLabel`, todas las etiquetas relative-time — ver §4.7/§4.8): esas propiedades
  `[ObservableProperty]`/get-only deben, o (a) convertirse en propiedades computadas que se
  re-evalúan cuando cambia el idioma (suscribirse a
  `LocalizationService.Instance.PropertyChanged` y forzar `OnPropertyChanged` sobre esas
  propiedades derivadas), o (b) recalcularse al recargar la vista. La opción (a) es la
  correcta para una app "profesional" (cambio de idioma instantáneo en todas partes, no
  solo en lo bindeado directo al indexer) — hay que auditar cada label calculado del
  inventario (§4.7) y engancharlo al evento de cambio de idioma.
- No hace falta reiniciar la app en ningún caso (a diferencia de otros ajustes actuales
  como el tema, que si lo requieren hoy).

---

## 4. Inventario exhaustivo de contenido a traducir

Basado en una auditoría completa del código (41 vistas `.axaml`, 86 viewmodels, todos los
servicios y modelos). Todo lo listado abajo está **hoy hardcodeado**, mayormente en
español.

### 4.1 Vistas XAML (`views/**/*.axaml`) — 16 archivos con texto

| Archivo | Carga de texto | Contenido típico |
|---|---|---|
| `views/settings/SettingsView.axaml` | **Muy alta (~290 cadenas)** | Toda la pantalla de Ajustes: cabeceras de sección, párrafos descriptivos, radios (`DASHBOARD`/`CALENDARIO`/`KANBAN`...), sufijos de unidad vía `StringFormat` (`{0:0} ESPACIOS`, `{0:0} DÍAS`, `{0}%`). |
| `views/kanban/KanbanView.axaml` | **Muy alta (~190)** | Tablero + "Gestión de tareas" (Polar): cabeceras de columna, stat tiles, opciones de ordenación. |
| `views/calendar/CalendarView.axaml` | **Alta (~100)** | Vistas Agenda/Mes/Semana, filtros, menús contextuales (`EDITAR`/`ELIMINAR`/`CREAR TARJETA KANBAN AQUÍ`...). |
| `views/skills/SkillsView.axaml` | Media (~50) | Barra de import/export, panel de detalle de skill. |
| `views/editor/EditorView.axaml` | Media (~50) | Tooltips de la barra de formato Markdown, panel lateral (esquema/backlinks/estadísticas). |
| `views/dashboard/DashboardView.axaml` | Media (~49) | Títulos de widgets, estados vacíos. |
| `views/reminders/RemindersView.axaml` | Media (~40) | Incluye letras de día de semana hardcodeadas (`LU`,`MA`,`MI`...) — otra duplicación a fusionar con `CultureInfo` (§1.7). |
| `views/shell/MainWindow.axaml` | Media (~29) | `Window.Title="Enki — Notas Markdown"` (línea 16), placeholders de búsqueda global, popovers de notificaciones/recordatorios, tooltips de la barra de título. |
| `views/files/FilesView.axaml` | Media-baja (~27) | Import, menú contextual, estados vacíos, mensajes de fallback del visor de PDF. |
| `views/trash/TrashView.axaml` | Media-baja (~25) | 4 secciones × estado vacío + diálogo de confirmación. |
| `views/sessions/SessionsView.axaml` | Media-baja (~23) | Grafo de sesiones, instrucciones de "modo enlace". |
| `views/notes/NotesView.axaml` | Baja (~22) | |
| `views/notifications/NotificationsView.axaml` | Baja (~13) | |
| `views/shell/VaultSetupView.axaml` | Baja (~5) | Pantalla de primer arranque (vault). |
| `views/common/TagFilterView.axaml` | Muy baja (1) | `Text="Sin etiquetas"`. |
| `views/skills/SkillEditorView.axaml` | Ninguna | Solo hospeda `AvaloniaEdit.TextEditor`, sin texto propio. |

`ToolTip.Tip` (propiedad adjunta, fácil de pasar por alto con un grep ingenuo) aparece en
**15 de los 16 archivos** — auditar cada uno explícitamente al extraer claves, no fiarse
solo de `Text=`/`Content=`/`Header=`.

Ejemplos de `StringFormat` con orden de palabras fijo que **no se puede traducir
literalmente** (ver §1.3 para el patrón correcto):
- `RemindersView.axaml:37` — `'Sincronizado con Polar · {0}'`
- `KanbanView.axaml:601` — `'ÚLTIMA SINCRONIZACIÓN: {0}'`
- `KanbanView.axaml:759` — `'COMPLETADAS ({0})'` (cuenta sin manejo de plural)
- `CalendarView.axaml:630` — `'DÍA {0}'`
- `TrashView.axaml:184` — `'Estaba en: {0}'`
- `SettingsView.axaml:408` — `{0:0} ESPACIOS` (unidad pegada al número)
- `SettingsView.axaml:501` — `{0:0} DÍAS`

### 4.2 ViewModels — texto embebido en C#

Prácticamente todos los ~86 viewmodels tienen alguna cadena de usuario. Los de mayor
densidad: `CalendarViewModel.cs`, `MainWindowViewModel.cs`, `SettingsViewModel.cs`,
`KanbanCardEditorViewModel.cs`, `CalendarItemViewModel.cs`, `DashboardViewModel.cs`,
`SkillsViewModel.cs`, `SlashCommandsViewModel.cs`, `CalendarQuickCreateViewModel.cs`,
`PolarTaskManagementViewModel.cs`, `SessionsViewModel.cs`, `PolarTaskEditorViewModel.cs`,
`CalendarAgendaGroupViewModel.cs`, `TrashViewModel.cs`, `KanbanViewModel.cs`,
`NotesViewModel.cs`, `PolarTaskRowViewModel.cs`, `EditorViewModel.cs`,
`ReminderEditorViewModel.cs`, `RemindersViewModel.cs`, `FilesViewModel.cs`,
`NotificationItemViewModel.cs`, `ReminderItemViewModel.cs`, y unos 35 archivos más con
cadenas puntuales.

Categorías con ejemplos concretos (todas requieren claves de recurso nuevas):

- **Nombres por defecto / datos semilla**: `NotesViewModel.cs:289` (`"Nueva nota"`),
  `KanbanViewModel.cs:187` (`"Nueva columna"`), `VaultService.cs:149` (`"Nueva carpeta"`),
  `VaultService.cs:264` (`"Sin título"`), `VaultService.cs:288,444` (`"Nueva nota"`),
  `SessionService.cs:64` (`"Nueva sesión"`).
- **Notificaciones/toasts** (`NotificationService.Push(title, message, severity)`, texto
  siempre en el llamador, ~25+ sitios): `SessionsViewModel.cs:236`,
  `SkillsViewModel.cs:419-424`, `TrashViewModel.cs:261-268`,
  `MainWindowViewModel.cs:178-183,239-247,282-283,591,604`,
  `DashboardViewModel.cs:236-237`, `SettingsViewModel.cs:906-1000`.
- **Mensajes de validación**: `SkillsViewModel.cs:268` (`"Escribe un nombre."`),
  `VaultSetupViewModel.cs:64` (`$"No se pudo usar esa carpeta: {ex.Message}"` — ver §5.3,
  concatena `Exception.Message` del SO, que puede venir en el idioma del sistema
  operativo, no de Enki).
- **Diálogos de confirmación**: `TrashViewModel.cs:63-82` (switch de 5 vías, título +
  cuerpo por tipo de elemento a borrar), más diálogos inline en
  `RemindersView.axaml:347-358` y `NotificationsView.axaml:137-148`.
- **Etiquetas de nav del sidebar**, definidas en C# no en XAML (fácil de pasar por alto):
  `MainWindowViewModel.cs:340-349,432,455` — `"Dashboard"`, `"Calendario"`, `"Kanban"`,
  `"Documentos"`, `"Archivos"`, `"IA SKILLS"`, `"Sesiones"`, `"Papelera"`. `MainWindow.axaml`
  solo bindea `{Binding Label}`, el texto real vive en el viewmodel.
- **Comandos slash del editor** (`/tarea`, `/cita`...) —
  `SlashCommandsViewModel.cs:13-24`: cada comando tiene `Key` (cadena de coincidencia
  tecleada, p. ej. `"tarea"`), `Label` (texto mostrado, p. ej. `"Lista de tareas"`) e
  `InsertText` (contenido Markdown que se **inserta en la nota del usuario**, p. ej. la
  tabla `"| Columna 1 | Columna 2 |..."`). Ver §5.5 para la decisión de diseño necesaria
  aquí — no es solo traducir `Label`.
- **Etiquetas calculadas por switch de enum/estado** (labels en mayúsculas, muchas,
  repartidas): `KanbanCardViewModel.cs:19-23` (`PriorityLabel`),
  `PolarTaskRowViewModel.cs:29-45` (prioridad + `RecurrenceLabel`: `"DIARIA"`, `"LUN Y
  MIÉ"`, `"DÍA 1 DE MES"`...), `CalendarItemViewModel.cs:202-208` (`SourceLabel`,
  `CompletedToggleLabel`, `GhostOccurrenceNoteLabel`), `ReminderItemViewModel.cs:25`
  (`BucketLabel`), `NotificationItemViewModel.cs:35` (`SeverityLabel`).

### 4.3 Servicios

- `KanbanService.cs:114-172` (`CreateSeedBoard()`) — tablero/columnas/tarjetas de ejemplo
  del primer tablero Kanban (`"Tablero principal"`, `"Por hacer"`, `"En progreso"`,
  `"Hecho"` + 4 tarjetas de muestra), escrito a `kanban.json` la primera vez que no existe
  el archivo. Mismo caso que la nota de bienvenida (§3.2): **debe generarse en el idioma
  activo en el momento de la creación**, vía claves de recurso, no texto fijo.
  `MergeLegacyReviewColumn` además compara contra títulos normalizados hardcodeados
  (`"EN REVISION"`/`"EN PROGRESO"`) — es lógica de migración ligada a nombres de columna en
  español; auditar que no se rompa si el usuario ya renombró columnas en otro idioma (riesgo
  preexistente, no exclusivo de i18n, pero se agrava con múltiples idiomas de origen).
- `VaultService.cs` — ver §3.2 (nota de bienvenida) y nombres por defecto ya listados en
  §4.2.
- `SkillService.cs:158,169` — excepciones que llegan a la UI:
  `$"Ya existe una skill con el nombre \"{safeId}\"."`, `"No se pueden eliminar las skills
  por defecto."`.
- **Títulos y filtros de diálogos de archivo** (`StorageProvider`, definidos en los propios
  ViewModels, no hay un servicio de diálogos centralizado):
  `SettingsViewModel.cs:843,894,931,963` (`"Elige la carpeta del vault"`, `"Guardar copia de
  seguridad"`, `"Exportar ajustes"`, `"Importar ajustes"`, filtros `"Archivo ZIP"`/`"JSON"`),
  `SkillsViewModel.cs:332,349,454`, `NotesViewModel.cs:317`, `FilesViewModel.cs:180`,
  `VaultSetupViewModel.cs:31,34`.

### 4.4 App shell / título de ventana

`views/shell/MainWindow.axaml:16` — `Title="Enki — Notas Markdown"` (también visible en la
barra de tareas / Alt-Tab del SO). Decisión de diseño: `"Enki"` es nombre de marca, no se
traduce; `"Notas Markdown"` sí es descriptivo y debe traducirse por idioma (`"Enki — Markdown
Notes"` en inglés, etc.) — ver §5.1 sobre qué NO se traduce.

`App.axaml`, `App.axaml.cs`, `Styles.axaml`, `ControlThemes.axaml` y los 16 archivos
`*.axaml.cs` (code-behind) **no contienen texto de usuario** — confirmado, no hace falta
tocarlos para el inventario (sí habrá que tocar `App.axaml.cs` para aplicar el idioma al
arrancar, ver §2.2).

### 4.5 Modelos

`AppSettings.cs` no tiene texto propio (los `string` tipo-enum como `Theme="Dark"` son
identificadores internos, no se traducen — ver §5.1). Las etiquetas de prioridad/estado no
están en los enums (`KanbanPriority`, `CalendarItemPriority`...) sino resueltas por `switch`
en los ViewModels (§4.2) — los enums en sí no necesitan tocarse.

### 4.6 Formato de fecha/hora — normalizar antes de traducir

Todas las llamadas a `ToString("dd MMM...")` actuales dependen de la cultura ambiente del
hilo (no de `InvariantCulture`), lo cual hoy es inconsistente porque no hay idioma
controlado por el usuario. Lista completa a auditar cuando `LocalizationService.SetLanguage`
empiece a fijar `CultureInfo.CurrentCulture`:

`KanbanCard.cs:54`, `NoteFile.cs:31`, `VaultFile.cs:26`, `CalendarItemViewModel.cs:186,197`,
`CalendarQuickCreateViewModel.cs:85`, `KanbanCardEditorViewModel.cs:135`,
`PolarTaskEditorViewModel.cs:88` (formato `dd/MM/yyyy` — **importante**: en-US espera
M/d/yyyy, este patrón fijo se leerá al revés para usuarios de EE. UU. si no se cambia a un
formato dependiente de cultura), `EditorStatsViewModel.cs:60`,
`PolarTaskManagementViewModel.cs:165`, `RemindersViewModel.cs:79` (con fallback
`"NUNCA"`), `SettingsViewModel.cs:1011` (mismo fallback pero `"Nunca"`, inconsistencia de
mayúsculas ya existente, aprovechar para unificar vía clave `Common_Never`),
`GlobalSearchViewModel.cs:292-293`, `NotificationItemViewModel.cs:20,49`,
`TrashedReminderCardViewModel.cs:23`, `TrashedTaskCardViewModel.cs:22`.
`NotesViewModel.cs:99` y `EditorView.axaml.cs:386` usan `"yyyy-MM-dd"` (ISO, para nombre de
archivo de nota diaria) — **no tocar**, es un identificador interno, no texto mostrado.

### 4.7 Tiempo relativo ("hace 2 min") — duplicado 5 veces

`NoteFile.cs:22-32`, `VaultFile.cs:17-27`, `TrashedReminderCardViewModel.cs:15-24`,
`TrashedTaskCardViewModel.cs:14-23`, `NotificationItemViewModel.cs:45-49` — cada uno
reimplementa el mismo patrón:
```csharp
if (delta.TotalMinutes < 1) return "ahora mismo";
if (delta.TotalMinutes < 60) return $"hace {(int)delta.TotalMinutes} min";
if (delta.TotalHours < 24) return $"hace {(int)delta.TotalHours} h";
if (delta.TotalDays < 7) return $"hace {(int)delta.TotalDays} d";
return local.ToString("dd MMM yyyy");
```
Recomendación: extraer un único `RelativeTimeFormatter.Format(DateTime)` usado por los 5
sitios, con claves de plantilla (`Common_RelativeTime_MinutesAgo` = `"hace {0} min"`, etc.)
resueltas vía `LocalizationService` — de paso arregla que hoy ninguno de los 5 maneja
singular/plural (`"hace 1 min"` ya suena raro en español, y en otros idiomas sería
directamente incorrecto sin pluralización — usar el helper `Plural` de §1.4).

### 4.8 Casos ya semi-atados a `CultureInfo` (revisar, no romper)

`HexColorToBrushConverter.cs:23`, `DoubleToTranslateYConverter.cs:19`,
`SessionGraphRenderer.cs:1128,1168` usan `CultureInfo.InvariantCulture` correctamente para
formatear números internos (colores RGBA), no texto de usuario — **no tocar**, ya siguen la
regla de §1.6.

---

## 5. Decisiones de diseño necesarias (no son traducción literal)

### 5.1 Qué NO se traduce

- **"Enki"** (nombre de marca).
- **"Markdown"**, **"Kanban"** — términos técnicos ya adoptados tal cual en los 6 idiomas
  objetivo (verificar en cada idioma que efectivamente no tienen traducción de uso común —
  es el caso en los 6).
- **"Polar"**, **"Supabase"**, **"Git"**, **"Dropbox"** — nombres de producto de terceros.
- Identificadores internos tipo-enum en `AppSettings` (`Theme="Dark"`,
  `StartupPage="Dashboard"`, `EditorSidePanelTab="Outline"`, `CalendarDefaultView="Month"`)
  — son claves técnicas, no texto de UI; su *representación visible* (el label del radio
  button que dice "Dashboard") sí se traduce, el valor almacenado no.
- Nombres de archivo con formato ISO (`"yyyy-MM-dd.md"` para notas diarias) — identificador,
  no texto.
- El nombre de sesión literal `"default"` (`SessionService.cs:91-94`, único resto en
  inglés de todo el código) — es un identificador interno de sesión, no una etiqueta que el
  usuario vea traducida en ningún sitio confirmado; **decisión pendiente**: si en algún
  punto de la UI se muestra tal cual al usuario, sí necesita traducirse o sustituirse por
  una clave de recurso; si es puramente interno, dejarlo como está. Verificar antes de
  tocarlo.

### 5.2 Contenido generado por el usuario nunca se traduce

Nombres de notas, carpetas, columnas Kanban, tarjetas, tags, contenido de las notas — todo
lo que el usuario haya escrito o renombrado se queda exactamente como está,
independientemente del idioma de la UI. Esto ya es la premisa correcta y no requiere
cambios de comportamiento, solo tenerlo presente al implementar: **nunca** re-traducir datos
ya persistidos en disco al cambiar de idioma, solo la interfaz alrededor de ellos.

### 5.3 Mensajes de excepción del sistema operativo

`VaultSetupViewModel.cs:64` — `$"No se pudo usar esa carpeta: {ex.Message}"` concatena
`Exception.Message`, que en muchos casos viene ya en el idioma del SO (mensajes de I/O del
runtime de .NET/SO), no de Enki. Esto es una fuga de idioma potencial fuera del control de
`LocalizationService`. Recomendación: para las excepciones más comunes y esperables (ruta
sin permisos, disco lleno, ruta inválida) capturar el tipo específico
(`UnauthorizedAccessException`, `IOException`, `PathTooLongException`...) y mostrar un
mensaje 100% traducido propio; usar `ex.Message` crudo solo como último recurso genérico
(y documentarlo como limitación conocida, no como bug a "arreglar del todo" — el mensaje
de una excepción de plataforma nunca va a estar garantizado en el idioma de Enki salvo que
Enki lo intercepte por tipo).

### 5.4 Semilla de datos (nota de bienvenida, tablero Kanban inicial)

Ver §3.2 y §4.3. Ambos casos comparten el mismo patrón: contenido que se escribe **una
sola vez** a disco en la creación, en el idioma activo en ese momento. Una vez escrito, es
contenido del usuario (§5.2) — si luego cambia el idioma de la app, esas notas/tarjetas ya
creadas **no se retraducen**, exactamente igual que cualquier otro contenido que el usuario
haya tocado después.

### 5.5 Comandos slash (`/tarea`, `/tabla`...)

`SlashCommandsViewModel.cs` mezcla tres cosas con necesidades distintas:

- `Key` (lo que el usuario teclea para invocar el comando, p. ej. `"tarea"`): decisión
  abierta entre (a) mantener las keys estables en un solo idioma (español o inglés) para
  que un usuario que cambie el idioma de la UI no tenga que re-aprender qué teclear, o (b)
  traducir también las keys por idioma. Se recomienda **(a) manteniendo las keys actuales
  en español como identificador interno estable** y traduciendo únicamente `Label`
  (texto mostrado en el menú) — evita que cambiar de idioma rompa el "muscle memory" de
  atajos ya aprendidos, y es coherente con que las keys nunca se muestran en pantalla,
  solo se comparan contra lo tecleado.
- `Label`: traducción directa, sin complicación.
- `InsertText`: es **contenido que se inserta en la nota del usuario** (p. ej. una tabla
  Markdown con `"Columna 1"`/`"Columna 2"` como placeholders). Esto sí debe traducirse por
  idioma como cualquier otra clave de recurso — es texto que ve el usuario en su nota
  recién creada, aunque técnicamente "salga" del sistema de plantillas y no de un binding
  de UI. Tratarlo igual que la nota de bienvenida (§3.2): una clave de recurso por
  comando/idioma, resuelta en el momento de la inserción con el idioma activo en ese
  instante (no en el de creación de la nota completa, sino en el de cada inserción — el
  usuario puede cambiar de idioma entre una inserción y otra dentro de la misma sesión de
  edición).

---

## 6. Plan de implementación por fases

**Fase 1 — Infraestructura** (sin tocar aún ninguna pantalla existente):
`LocalizationService`, `TrExtension`, `Plural`, `RelativeTimeFormatter`, estructura de
carpetas `localization/`, `Strings.resx` fuente (recomendado en inglés neutro, ver §1.1)
con el primer lote de claves comunes (`Common_*`, botones genéricos), `AppSettings.Language`
+ wiring en `App.axaml.cs`/`SettingsService`. Tests unitarios de paridad de claves (ver §7).

**Fase 2 — Extracción sin traducción**: recorrer todo el inventario de §4, reemplazar cada
cadena hardcodeada por su clave (`{loc:Tr Clave}` en XAML, `LocalizationService.Instance["Clave"]`
en C#), rellenando **solo** `Strings.resx` (fuente) por ahora — el resto de idiomas quedan
con claves sin traducir (fallback a la fuente). Esta fase es la de mayor volumen de cambio
de código y mayor riesgo de regresión visual — hacerla archivo por archivo, con especial
cuidado en los `StringFormat`/interpolaciones (§1.3) y en no tocar accidentalmente
identificadores internos (§5.1). Incluye borrar los 6 arrays de meses/días duplicados
(§1.7) y unificar los 5 formateadores de tiempo relativo (§4.7).

**Fase 3 — Traducción a los 5 idiomas restantes**: rellenar
`Strings.en-US/en-GB/de-DE/es-ES/fr-FR/es-419.resx` con traducción profesional real
(revisada por hablante nativo o servicio de traducción de calidad — no traducción
automática sin revisión, dado el requisito explícito de "cero fugas ni errores"),
respetando las variantes regionales (es-ES vs. es-419: ustedes/vosotros, vocabulario
neutro; en-US vs. en-GB: ortografía y formato de fecha).

**Fase 4 — Selector de primer arranque + ajustes**: `LanguageSetupView`/ViewModel (§3.1),
nota de bienvenida y tablero Kanban semilla localizados (§3.2, §4.3), selector de idioma en
Ajustes › General con cambio en caliente (§3.3), auditoría de labels calculados que
necesitan re-evaluarse al cambiar idioma (§3.3 último punto).

**Fase 5 — QA y control de calidad** (ver §7 — bloqueante antes de considerar la
funcionalidad "completa").

---

## 7. Checklist de control de calidad (evitar fugas de idioma)

Requisito explícito del usuario: cero texto en un idioma que no sea el activo (salvo
contenido del propio usuario). Para garantizarlo de forma sostenible, no solo en la
revisión inicial:

1. **Test de paridad de claves**: test automatizado (en `enki.Tests`) que compare el set de
   claves de `Strings.resx` (fuente) contra cada uno de los otros 5 `.resx` y falle si falta
   una sola clave en cualquiera de ellos. Correr en CI/`dotnet test` siempre.
2. **Test de "sin cadenas sueltas"**: heurística de grep/regex sobre todos los `.axaml`
   (`Text="..."`, `Content="..."`, `Header="..."`, `ToolTip.Tip="..."`) que falle si detecta
   un literal no vacío que no sea un identificador conocido en la lista blanca (nombres de
   propiedad de binding, valores tipo-enum como `"Dark"`) — para evitar que una vista nueva
   añadida en el futuro reintroduzca texto hardcodeado sin que nadie lo note hasta
   producción.
3. **Pseudo-localización manual**: antes de tener las 6 traducciones completas, generar una
   variante de prueba donde cada valor de `Strings.resx` se envuelve en marcadores
   (`»texto«`) — al ejecutar la app con esa variante, cualquier texto que aparezca SIN los
   marcadores es una cadena que se escapó del sistema de localización. Método rápido y
   fiable para encontrar fugas antes de traducir de verdad.
4. **Recorrido manual completo de cada idioma**: con la app en cada uno de los 6 idiomas,
   pasar por las 8 secciones de nav (Dashboard, Calendario, Kanban, Documentos, Archivos, IA
   Skills, Sesiones, Papelera) + Ajustes completo + el flujo de primer arranque desde cero
   (vault nuevo) verificando la nota de bienvenida y el tablero Kanban semilla — confirmar
   visualmente que no hay ni una palabra en otro idioma.
5. **Revisión de mensajes de excepción**: confirmar que los catch-blocks que hoy
   interpolan `ex.Message` (§5.3) están acotados a los tipos de excepción esperables con
   mensaje propio, no exponiendo texto crudo del SO como comportamiento por defecto.
6. **Verificar formato de fecha/número por idioma**: especialmente en-US (M/d/yyyy) vs. el
   resto (d/M/yyyy), y que ningún `ToString` con patrón fijo tipo `"dd/MM/yyyy"` sobreviva
   sin pasar por `CurrentCulture` (§4.6).

---

## 8. Proceso para añadir una clave nueva en el futuro

Para que esto no se degrade con el tiempo a medida que se añaden pantallas:

1. Toda cadena de usuario nueva se añade como clave en `Strings.resx` (fuente) primero,
   nunca como literal en el `.axaml`/`.cs`.
2. Se traduce a los 5 idiomas restantes en el mismo cambio que la introduce (no dejar
   "ya lo traduciré después" — el test de paridad de claves de §7.1 debería impedir
   mergear/compilar si no se hace, actuando como red de seguridad).
3. Si la cadena tiene un placeholder numérico/variable, la clave lleva `{0}`/`{1}` con
   nombre descriptivo en el comentario del `.resx`, nunca concatenación de fragmentos
   (§1.3).
4. Si la cadena depende de una cantidad, usa el helper `Plural` (§1.4) con claves `_one`/
   `_other`, no una condición manual de `s`/`es`.
