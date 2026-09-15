using System;
using System.Collections.Generic;

namespace enki.Models;

public sealed class AppSettings
{
    public string? VaultPath { get; set; }
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>BCP-47 code of the UI language the user picked ("es-ES", "en-US", "en-GB", "de-DE",
    /// "fr-FR", "es-419") — see docs/agent-docs/internacionalizacion-i18n.md §2.1. Null/empty means "not
    /// chosen yet", which is the signal the first-run flow uses to show the language picker before anything
    /// else (§3.1); once set it is always applied at startup (see <c>App.axaml.cs</c>) and can be changed
    /// later from Ajustes › General.</summary>
    public string? Language { get; set; }

    /// <summary>Which nav page (<c>ViewModels.AppPage</c> member name) the app lands on right after a
    /// vault finishes loading — Ajustes › General. Stored as a plain string for the same reason as
    /// <see cref="Theme"/>/<see cref="EditorColumnPosition"/>. If the stored page is hidden by another
    /// setting (e.g. "Calendar" while <see cref="CalendarPanelEnabled"/> is off) or isn't one of the
    /// selectable start pages, <c>MainWindowViewModel</c> falls back to "Dashboard".</summary>
    public string StartupPage { get; set; } = "Dashboard";

    public string Theme { get; set; } = "Dark";
    public double EditorFontSize { get; set; } = 14;
    public double EditorColumnWidth { get; set; } = 760;
    public string EditorColumnPosition { get; set; } = "Center";
    public bool EditorShowScrollbar { get; set; } = true;

    /// <summary>When true, Enki draws its own title bar (drag region + minimize/maximize/close) instead
    /// of the OS window decorations.</summary>
    public bool UseCustomTitleBar { get; set; } = true;

    /// <summary>Shows la barra de desplazamiento vertical del Dashboard — Ajustes › General. Por
    /// defecto activada (que es como se comportaba antes de existir el ajuste); apagarla solo oculta el
    /// raíl y el pulgar, el scroll con rueda/teclado sigue funcionando igual.</summary>
    public bool DashboardShowScrollbar { get; set; } = true;

    /// <summary>Muestra la fila de filtro por etiquetas del tablero; ocultarla no borra los filtros
    /// aplicados ni toca las etiquetas de las tarjetas.</summary>
    public bool KanbanShowTagFilter { get; set; } = true;

    /// <summary>"Vertical" o "Horizontal" — Ajustes › Apariencia. Vertical apila las tarjetas dentro de
    /// columnas lado a lado (la disposición clásica de un tablero Kanban); Horizontal gira cada columna
    /// 90° en una franja de ancho completo con sus tarjetas desplazándose de lado a lado. Stored as a
    /// plain string for the same reason as <see cref="Theme"/>/<see cref="EditorColumnPosition"/>.</summary>
    public string KanbanBoardOrientation { get; set; } = "Vertical";

    /// <summary>Ajustes › Apariencia › Logo — <c>true</c> pinta en el brand header del sidebar la imagen
    /// que el usuario importó (<see cref="Services.BrandLogoService"/>), <c>false</c> (por defecto) el
    /// icono de Enki adaptativo al tema. Solo el interruptor vive aquí: la imagen en sí es un archivo
    /// aparte en <c>%AppData%/Enki/</c>, así que apagar esto vuelve al icono por defecto sin perder la
    /// imagen importada, y volver a encenderlo la recupera tal cual.</summary>
    public bool UseCustomBrandLogo { get; set; }

    /// <summary>"Square" o "Circle" — con qué forma se recorta el logo del sidebar. La circular es la
    /// única excepción admitida al <c>CornerRadius="0"</c> del sistema de diseño
    /// (docs/agent-docs/sistema-diseno-web.md), y solo se aplica al logo personalizado: el icono por
    /// defecto se dibuja siempre cuadrado. Stored as a plain string for the same reason as
    /// <see cref="Theme"/>/<see cref="EditorColumnPosition"/>.</summary>
    public string BrandLogoShape { get; set; } = "Square";

    /// <summary>System font family name for the app UI. Null/empty means the bundled Space Grotesk.</summary>
    public string? InterfaceFontFamily { get; set; }

    /// <summary>System font family name for the markdown editor (edit + preview), independent of <see cref="InterfaceFontFamily"/>. Null/empty means the bundled Space Grotesk.</summary>
    public string? EditorFontFamily { get; set; }

    /// <summary>Ajustes › Apariencia › Tema personalizado — whether the "Oscuros" built-in theme list is
    /// collapsed.</summary>
    public bool BuiltInDarkThemesCollapsed { get; set; }

    /// <summary>Ajustes › Apariencia › Tema personalizado — whether the "Claros" built-in theme list is
    /// collapsed.</summary>
    public bool BuiltInLightThemesCollapsed { get; set; }

    /// <summary>Claves de las secciones de Estadísticas que el usuario ha plegado ("pulse", "rhythm",
    /// "tasks", "knowledge", "habits", "storage"). Se guarda la lista de plegadas y no un booleano por
    /// sección — mismo criterio que <see cref="CalendarHiddenLayerKeys"/>: el estado por defecto (todo
    /// desplegado) no ocupa nada, y añadir o quitar una sección en el futuro no obliga a migrar el JSON
    /// de ajustes de todo el mundo.</summary>
    public List<string> StatisticsCollapsedSections { get; set; } = [];

    // ===================== Notificaciones y recordatorios (Ajustes › Notificaciones) =====================

    /// <summary>Shows/hides the bell (system event log) button in the title bar entirely.</summary>
    public bool NotificationsPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the inbox (due-date reminders) button in the title bar entirely.</summary>
    public bool RemindersPanelEnabled { get; set; } = true;

    /// <summary>Whether a new notification push also raises an OS-style toast (see
    /// <c>enki.Services.ToastService</c>), on top of the bell badge.</summary>
    public bool ShowDesktopToasts { get; set; } = true;

    public bool MuteInfoNotifications { get; set; }
    public bool MuteSuccessNotifications { get; set; }
    public bool MuteWarningNotifications { get; set; }
    public bool MuteErrorNotifications { get; set; }

    /// <summary>How many days ahead a kanban card's due date counts as "próxima a vencer" in the
    /// reminder inbox — see <c>ReminderInboxViewModel.Reload</c>.</summary>
    public int ReminderSoonWindowDays { get; set; } = 7;

    // ===================== Vínculo con Polar (Ajustes › Cuenta) =====================
    // See docs/supabase-sync/ — enabling this unlocks the "Gestión de tareas" view in Kanban and turns
    // on background sync (task lists/tasks/subtasks/reminders) against the shared Supabase project.
    // Tokens are persisted here like the rest of Enki's local state (%AppData%/Enki/settings.json) —
    // this is a single-user desktop app with no other secrets-at-rest story, consistent with how
    // local.properties already keeps the Supabase anon key in plain text next to the executable.

    public bool PolarLinked { get; set; }
    public string? PolarUserId { get; set; }
    public string? PolarEmail { get; set; }
    public string? PolarAccessToken { get; set; }
    public string? PolarRefreshToken { get; set; }
    public long PolarAccessTokenExpiresAtUtcMs { get; set; }

    /// <summary>Sync cursor (epoch millis) — see <c>PolarSyncService</c>. A single global cursor is
    /// enough per docs/supabase-sync/04-estrategia-sincronizacion.md ("un global es suficiente si se usa
    /// el mínimo updated_at de las 4 consultas").</summary>
    public long PolarLastSyncAtUtcMs { get; set; }

    /// <summary>Shows/hides the tag-chip filter row in Kanban → "Gestión de tareas" (Ajustes › Cuenta).
    /// Defaults to hidden — the row starts collapsed behind the setting until the user opts in.</summary>
    public bool PolarShowTaskTags { get; set; }

    /// <summary>Estado de despliegue de cada bloque lateral del calendario, conservado entre arranques.</summary>
    public bool CalendarMiniCalendarExpanded { get; set; } = true;
    public bool CalendarLayersExpanded { get; set; }
    public bool CalendarSearchExpanded { get; set; }
    public bool CalendarFiltersExpanded { get; set; }
    public bool CalendarUnscheduledExpanded { get; set; }
    public bool CalendarSidebarCollapsed { get; set; }

    // ===================== Calendario (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1/§3) =====================
    // Fase 4: layer visibility persists as the set of *hidden* keys rather than the active ones, so a
    // newly created Polar list (or the fixed "kanban"/"reminder" layers) is active by default without any
    // migration step — matches "estado por defecto: todas las capas activas" without needing to enumerate
    // every possible key up front.

    /// <summary>Layer keys ("kanban", "reminder", or a <c>PolarTaskList.Id</c>) the user has switched off —
    /// see <c>CalendarViewModel.Layers</c>.</summary>
    public List<string> CalendarHiddenLayerKeys { get; set; } = [];

    /// <summary>Colores con los que el Calendario identifica cada origen, elegidos a mano por el usuario
    /// (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1b) — clave de color ("kanban",
    /// "kanban:&lt;id de columna&gt;", "reminder" o un <c>PolarTaskList.Id</c>) → HEX de 6 dígitos.
    ///
    /// Solo guarda las capas cuyo color se cambió: lo que no está aquí lo reparte
    /// <see cref="Services.CalendarLayerPalette"/> de forma determinista, así que una columna o lista
    /// nueva ya nace con color propio sin migración. Vive en los ajustes y no en <c>KanbanColumn</c>/
    /// <c>PolarTaskList</c> porque es una preferencia de esta pantalla: el color no se ve en Kanban ni en
    /// "Gestión de tareas", y <c>PolarTaskList.Color</c> además se sincroniza con Supabase.</summary>
    public Dictionary<string, string> CalendarLayerColors { get; set; } = [];

    /// <summary>"Ocultar completadas" filter (doc 07 §3) — defaults to off (completed items stay visible,
    /// dimmed) because seeing "hoy completé esto" is more useful in a calendar than hiding it outright.</summary>
    public bool CalendarHideCompletedItems { get; set; }

    /// <summary>"Ocultar ocurrencias fantasma" (doc 07 §3, shared with doc 09) — defaults to off so the
    /// recurrence preview that's the whole point of Fase 4's ghost occurrences is visible out of the box.</summary>
    public bool CalendarHideGhostOccurrences { get; set; }

    /// <summary>Shows the vertical scrollbar on the Calendario sidebar (mini-calendario, filtros, sin
    /// programar) — Ajustes › Calendario. Defaults to off (hidden), matching how it's hidden by default
    /// everywhere else in the app. Scrolling still works either way, only the track/thumb are affected.</summary>
    public bool CalendarShowSidebarScrollbar { get; set; }

    // ===================== Calendario (docs/calendar-feature/09-configuracion-y-ajustes.md §1, Fase 5) =====================

    /// <summary>Which view (<c>ViewModels.CalendarViewMode</c> member name) the Calendario page opens on —
    /// stored as a string rather than the enum itself so <c>Models</c> never has to reference
    /// <c>ViewModels</c>, same reasoning as <see cref="Theme"/>/<see cref="EditorColumnPosition"/> already
    /// being plain strings instead of enums.</summary>
    public string CalendarDefaultView { get; set; } = "Month";

    /// <summary>Whether the week starts on Monday (Spanish/European convention, the default) or Sunday —
    /// affects the Mes/Semana grids and the mini-calendario.</summary>
    public bool CalendarWeekStartsOnMonday { get; set; } = true;

    /// <summary>Shows/hides Saturday/Sunday in the Mes/Semana grids, for whoever only plans working days.</summary>
    public bool CalendarShowWeekends { get; set; } = true;

    /// <summary>Working-hours band (0–23) highlighted in the Semana/Día hour grid and used as the initial
    /// scroll position on those views.</summary>
    public int CalendarWorkingHoursStart { get; set; } = 9;

    public int CalendarWorkingHoursEnd { get; set; } = 18;

    /// <summary>24h vs. 12h AM/PM time labels in the hour grid/chips.</summary>
    public bool CalendarTimeFormat24h { get; set; } = true;

    /// <summary>Shows/hides the "Calendario" entry in the main nav entirely — same spirit as
    /// <see cref="RemindersPanelEnabled"/>/<see cref="NotificationsPanelEnabled"/>, for whoever prefers not
    /// to see it.</summary>
    public bool CalendarPanelEnabled { get; set; } = true;

    // ===================== IA Skills (docs/ia-skills/especificacion-desarrollo.md) =====================

    /// <summary>Shows/hides the "IA SKILLS" entry in the main nav and settings section.
    /// Disabled by default because it is intended for advanced users.</summary>
    public bool SkillsPanelEnabled { get; set; }

    // La biblioteca de skills es un árbol de carpetas, y la sección se puede navegar de dos formas
    // distintas, elegidas con el conmutador de su propia barra de herramientas: "Browser" es el
    // explorador (panel de carpetas + rejilla de tarjetas, como la sección Archivos) y "Tree" es el
    // árbol (panel lateral que entremezcla carpetas y skills + detalle a la derecha, como la vista
    // simplificada de Notas). Se guarda como string plano por el mismo motivo que
    // <see cref="NotesViewMode"/>: no acoplar Models a un enum de UI.

    /// <summary>"Browser" o "Tree" — qué disposición usa la sección IA Skills.</summary>
    public string SkillsViewMode { get; set; } = "Browser";

    /// <summary>Rutas de carpeta expandidas en el panel de carpetas del modo explorador.</summary>
    public List<string> SkillsExpandedFolderPaths { get; set; } = [];

    /// <summary>Rutas de carpeta expandidas en el árbol del modo árbol. Deliberadamente separado de
    /// <see cref="SkillsExpandedFolderPaths"/>, por el mismo motivo que
    /// <see cref="NotesSimpleExpandedFolderPaths"/> lo está de <see cref="NotesExpandedFolderPaths"/>:
    /// son dos árboles distintos —este incluye skills y el otro no— y compartir el estado haría que
    /// navegar en uno reorganizara el otro a la espalda del usuario.</summary>
    public List<string> SkillsTreeExpandedFolderPaths { get; set; } = [];

    /// <summary>Si el panel lateral del modo árbol está desplegado. Es estado de ventana, no una
    /// preferencia de Ajustes: se guarda al pulsar el botón de mostrar/ocultar para que la app reabra tal
    /// como se dejó.</summary>
    public bool SkillsTreeSidebarVisible { get; set; } = true;

    /// <summary>Ancho en píxeles del panel lateral del modo árbol, arrastrable por su separador. Se acota
    /// al rango permitido al leerlo.</summary>
    public double SkillsTreeSidebarWidth { get; set; } = 272;

    // ===================== Secciones de la barra lateral (Ajustes › General) =====================
    // Same "hide the nav entry entirely" idea as CalendarPanelEnabled/SkillsPanelEnabled above, extended to
    // every other always-on sidebar section so the user can declutter the app down to only what they use.
    // Settings itself has no toggle here — it's opened from its own title-bar button, not a NavItems entry,
    // so it stays reachable no matter which of these are off.

    /// <summary>Shows/hides the "Panel" entry in the main nav.</summary>
    public bool DashboardPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Kanban" entry in the main nav.</summary>
    public bool KanbanPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Documentos" entry in the main nav.</summary>
    public bool NotesPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Archivos" entry in the main nav.</summary>
    public bool FilesPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Sesiones" entry in the main nav.</summary>
    public bool SessionsPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Papelera" entry in the main nav.</summary>
    public bool TrashPanelEnabled { get; set; } = true;

    // ===================== Zona de peligro (docs/ajustes-docs/01-continuidad-y-proteccion-de-datos.md) =====================

    /// <summary>Whether the four "RequestDelete*" confirmation dialogs (Kanban card, calendar item, Polar
    /// task, skill) show up before deleting — Ajustes › General. Defaults to true (current behavior).
    /// Permanent deletion from the trash always confirms regardless of this setting, since that step is
    /// irreversible.</summary>
    public bool ConfirmBeforeDelete { get; set; } = true;

    // ===================== Editor (docs/ajustes-docs/04-editor-y-notificaciones.md §1) =====================

    /// <summary>Number of spaces a Tab press inserts in the note editor — Ajustes › Editor.</summary>
    public int EditorTabSize { get; set; } = 4;

    /// <summary>Shows line numbers in the note editor — Ajustes › Editor.</summary>
    public bool EditorLineNumbers { get; set; }

    /// <summary>Enables word wrap in the note editor — Ajustes › Editor.</summary>
    public bool EditorWordWrap { get; set; } = true;

    /// <summary>Si el editor descarga y dibuja las imágenes escritas con una URL http(s) — Ajustes ›
    /// Editor. Las imágenes del propio vault y las <c>data:</c> incrustadas se dibujan siempre; estas
    /// otras convierten abrir una nota en una petición a un servidor de terceros, así que se pueden
    /// apagar. Por defecto activo, como en cualquier editor Markdown.</summary>
    public bool EditorRemoteImages { get; set; } = true;

    /// <summary>Whether the editor's side panel (outline, backlinks, stats) is visible.</summary>
    public bool EditorSidePanelVisible { get; set; } = true;

    /// <summary>Which tab is active in the editor's side panel: "Outline", "Backlinks" or "Stats".</summary>
    public string EditorSidePanelTab { get; set; } = "Outline";

    /// <summary>Whether the formatting toolbar (all groups) is shown at all, independent of which
    /// individual groups below are expanded/collapsed.</summary>
    public bool EditorToolbarVisible { get; set; } = true;

    // Formatting-toolbar group visibility (Fase 4 §"Editor de notas", gap 17) — each group (text style,
    // headings, lists, insert, code) can be collapsed independently via its own toggle chevron, and the
    // choice is remembered across notes/tabs and app restarts, same persistence shape as
    // EditorSidePanelVisible above.
    public bool EditorToolbarTextStyleExpanded { get; set; } = true;
    public bool EditorToolbarHeadingsExpanded { get; set; } = true;
    public bool EditorToolbarListsExpanded { get; set; } = true;
    public bool EditorToolbarInsertExpanded { get; set; } = true;
    public bool EditorToolbarCodeExpanded { get; set; } = true;

    // ===================== Vista del editor de notas (completa / simplificada) =====================
    // La página de Notas se puede dibujar de dos formas distintas, elegidas en Ajustes › Editor:
    // "Full" es la vista de siempre (portada con rejilla de tarjetas, panel de carpetas, barra de
    // formato y panel lateral de esquema/enlaces/estadísticas) y "Simple" es una vista mínima al
    // estilo Obsidian — árbol de carpetas y notas a la izquierda, pestañas y editor a la derecha —
    // pensada para escribir sin distracciones. Se guarda como string plano por el mismo motivo que
    // <see cref="Theme"/>/<see cref="EditorColumnPosition"/>: no acoplar Models a un enum de UI.

    /// <summary>"Full" o "Simple" — qué disposición usa la página de Notas. Ajustes › Editor.</summary>
    public string NotesViewMode { get; set; } = "Full";

    /// <summary>Si el panel lateral (árbol/búsqueda) de la vista simplificada está desplegado. Es estado
    /// de ventana, no una preferencia de Ajustes: se guarda al pulsar el botón de mostrar/ocultar para
    /// que la app reabra tal como se dejó.</summary>
    public bool NotesSimpleSidebarVisible { get; set; } = true;

    /// <summary>Ancho en píxeles del panel lateral de la vista simplificada, arrastrable por su
    /// separador. Se acota al rango del GridSplitter al leerlo.</summary>
    public double NotesSimpleSidebarWidth { get; set; } = 272;

    /// <summary>Rutas de carpeta expandidas en el árbol de la vista simplificada. Deliberadamente
    /// separado de <see cref="NotesExpandedFolderPaths"/> (el panel de carpetas de la vista completa):
    /// son dos árboles distintos — este incluye notas y el otro no — y compartir el estado haría que
    /// navegar en uno reorganizara el otro a la espalda del usuario.</summary>
    public List<string> NotesSimpleExpandedFolderPaths { get; set; } = [];

    /// <summary>Id de la sesión elegida en el selector de espacio de trabajo de la página de Notas, o null
    /// si lo elegido era el vault entero. Es estado de ventana como <see cref="NotesSimpleSidebarVisible"/>,
    /// no una preferencia de Ajustes: se guarda al cambiar de sesión para que la app reabra en el mismo
    /// espacio en el que se cerró, en vez de volver siempre al vault. Una sesión que ya no exista —borrada,
    /// o de otro vault tras cambiar de vault— cae en silencio al vault, mismo criterio tolerante que
    /// <see cref="OpenNotePaths"/>.</summary>
    public string? NotesSessionId { get; set; }

    // ===================== Notificaciones — horario "no molestar" y sonido =====================
    // docs/ajustes-docs/04-editor-y-notificaciones.md §2 — ventana horaria en la que un evento nuevo se
    // sigue registrando en la campana pero no dispara el toast del sistema, y sonido opcional al llegar.

    public bool DoNotDisturbEnabled { get; set; }

    /// <summary>"HH:mm" — mismo criterio que <see cref="Theme"/> de guardar como string en vez de un tipo
    /// más rico, para no acoplar <c>Models</c> a nada de formato de UI.</summary>
    public string DoNotDisturbStart { get; set; } = "22:00";

    public string DoNotDisturbEnd { get; set; } = "08:00";

    public bool NotificationSoundEnabled { get; set; }

    /// <summary>Volumen del sonido de notificación, 0–100. En 0 no suena nada aunque
    /// <see cref="NotificationSoundEnabled"/> esté activo — equivale a silenciarlo sin tocar el
    /// interruptor. Ver <c>NotificationSoundPlayer.Play</c>: los backends que reproducen el asset
    /// empaquetado (afplay/paplay) sí escalan el volumen real; el pitido de <c>Console.Beep</c> y el
    /// sonido nativo de Windows (winmm) no exponen control de volumen en su propia API, así que ahí solo
    /// se respeta el "silencio en 0", no la escala intermedia.</summary>
    public double NotificationSoundVolume { get; set; } = 70;

    // ===================== Restaurar pestañas abiertas (docs/ajustes-docs/02-productividad-como-app-de-escritorio.md §2) =====================

    /// <summary>Rutas completas de las notas abiertas en pestañas al cerrar Enki, en el orden en que
    /// estaban — <see cref="ViewModels.NotesViewModel"/> las reabre al arrancar. Una ruta que ya no exista
    /// se ignora sin error.</summary>
    public List<string> OpenNotePaths { get; set; } = [];

    /// <summary>Cuál de <see cref="OpenNotePaths"/> era la pestaña activa.</summary>
    public string? ActiveNotePath { get; set; }

    /// <summary>Últimas notas abiertas o editadas, más reciente primero. Se limita a 10 entradas en
    /// <see cref="ViewModels.NotesViewModel"/>.</summary>
    public List<string> RecentNotePaths { get; set; } = [];

    // ===================== Diagnóstico (Ajustes › General, Fase 0 de docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2) =====================

    /// <summary>Baja el nivel mínimo de <c>LogService</c> de <c>Warning</c> (por defecto) a <c>Debug</c>.
    /// Apagado por defecto para no llenar el fichero de log con detalle innecesario en uso normal; se
    /// activa desde Ajustes › General cuando hace falta diagnosticar un problema concreto.</summary>
    public bool DebugLoggingEnabled { get; set; }

    // ===================== Backup automático (docs/analisis-proyecto/roadmap.md, Fase 1) =====================
    // Complemento al backup manual (zip, todo-o-nada) de la zona de peligro: sin esto, una edición
    // destructiva entre dos backups manuales no tenía ninguna versión anterior a la que volver.

    /// <summary>Activa el backup automático periódico — Ajustes › General, zona de peligro. Apagado por
    /// defecto: es un ajuste nuevo y opt-in, no un cambio de comportamiento para instalaciones existentes.</summary>
    public bool AutoBackupEnabled { get; set; }

    /// <summary>Cada cuántos días se crea un backup automático nuevo, contados desde
    /// <see cref="LastAutoBackupUtc"/>.</summary>
    public int AutoBackupIntervalDays { get; set; } = 1;

    /// <summary>Cuántos backups automáticos se conservan en <c>%AppData%/Enki/backups/</c> antes de que
    /// <see cref="Services.VaultBackupService.RunScheduledBackupIfDue"/> empiece a borrar los más
    /// antiguos.</summary>
    public int AutoBackupRetentionCount { get; set; } = 5;

    /// <summary>Cuándo se creó el último backup automático — null antes del primero. Persistido para que
    /// el intervalo se respete correctamente aunque Enki se cierre y reabra varias veces al día.</summary>
    public DateTime? LastAutoBackupUtc { get; set; }

    // ===================== Atajos de teclado remapeables (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) =====================

    /// <summary>Overrides del usuario para los atajos remapeables — clave: <c>Services.ShortcutIds</c>
    /// constant; valor: gesto en el mismo formato de texto ("Ctrl+Shift+F") que
    /// <c>Avalonia.Input.KeyGesture</c> parsea/formatea. Solo contiene entradas que difieren del gesto por
    /// defecto de <c>Services.ShortcutCatalog</c> — un atajo nunca tocado por el usuario no aparece aquí.
    /// Ver <c>Services.ShortcutRegistry</c>.</summary>
    public Dictionary<string, string> ShortcutOverrides { get; set; } = new();

    // ===================== Temas personalizados (importación de temas, Ajustes › Apariencia) =====================
    // Cada tema importado se valida contra Services.ThemeCatalog antes de aceptarse (solo los valores de
    // color son modificables — ver Services.ThemeCssParser) y su archivo .css vive en
    // "%AppData%/Enki/themes/{Id}.css", junto a este settings.json. Este registro solo guarda lo necesario
    // para listarlos; los colores en sí se releen del archivo cada vez que se activan.

    /// <summary>Temas personalizados importados por el usuario. Ver <see cref="Services.CustomThemeService"/>.</summary>
    public List<CustomThemeInfo> CustomThemes { get; set; } = [];

    /// <summary>Id (<see cref="CustomThemeInfo.Id"/>) del tema personalizado activo, o <c>null</c> si se usa
    /// el tema claro/oscuro incorporado (<see cref="Theme"/>) — mutuamente excluyente con
    /// <see cref="Theme"/>: mientras esto tenga valor, el interruptor claro/oscuro queda deshabilitado en
    /// Ajustes (ver <c>SettingsViewModel.IsCustomThemeActive</c>).</summary>
    public string? ActiveCustomThemeId { get; set; }

    // ===================== Estado de carpetas contraídas/expandidas =====================
    // Rutas de las carpetas que el usuario tiene expandidas en el panel lateral de cada sección.
    // Se persiste para que al reabrir Enki (o cambiar de sección) el árbol de carpetas aparezca
    // en el mismo estado en que el usuario lo dejó. Carpetas no listadas aquí arrancan contraídas.
    // La lista de Notas y la de Archivos son independientes entre sí.

    public List<string> NotesExpandedFolderPaths { get; set; } = [];
    public List<string> FilesExpandedFolderPaths { get; set; } = [];

    // ===================== Archivos: las dos formas de navegar (docs/agent-docs/seccion-archivos.md) =====================
    // La sección Archivos se puede navegar de dos formas distintas, elegidas con el conmutador de su
    // propia barra de herramientas: "Browser" es el explorador (panel de carpetas + rejilla de tarjetas)
    // y "Tree" es el árbol (panel lateral que entremezcla carpetas y archivos + previsualización a la
    // derecha, como la vista simplificada de Notas). Se guarda como string plano por el mismo motivo que
    // <see cref="NotesViewMode"/> y <see cref="SkillsViewMode"/>: no acoplar Models a un enum de UI.

    /// <summary>"Browser" o "Tree" — qué disposición usa la sección Archivos.</summary>
    public string FilesViewMode { get; set; } = "Browser";

    /// <summary>Orden de los archivos en la rejilla del modo explorador: "Modified", "Name", "Size" o "Type"
    /// (<c>FileSortMode</c>). Mismo criterio de string plano que <see cref="FilesViewMode"/>; un valor
    /// desconocido —editado a mano, o de una versión futura— cae al orden por fecha de siempre.</summary>
    public string FilesSortMode { get; set; } = "Modified";

    /// <summary>Rutas de carpeta expandidas en el árbol del modo árbol. Deliberadamente separado de
    /// <see cref="FilesExpandedFolderPaths"/> (el panel de carpetas del modo explorador), por el mismo
    /// motivo que <see cref="SkillsTreeExpandedFolderPaths"/> lo está de
    /// <see cref="SkillsExpandedFolderPaths"/>: son dos árboles distintos —este incluye archivos y el
    /// otro no— y compartir el estado haría que navegar en uno reorganizara el otro a la espalda del
    /// usuario.</summary>
    public List<string> FilesTreeExpandedFolderPaths { get; set; } = [];

    /// <summary>Si el panel lateral del modo árbol de Archivos está desplegado. Es estado de ventana, no
    /// una preferencia de Ajustes: se guarda al pulsar el botón de mostrar/ocultar para que la app reabra
    /// tal como se dejó.</summary>
    public bool FilesTreeSidebarVisible { get; set; } = true;

    /// <summary>Ancho en píxeles del panel lateral del modo árbol de Archivos, arrastrable por su
    /// separador. Se acota al rango permitido al leerlo.</summary>
    public double FilesTreeSidebarWidth { get; set; } = 272;

    /// <summary>Id de la sesión elegida en el selector de espacio de trabajo de la página de Archivos, o
    /// null si lo elegido era el vault entero. Gemelo de <see cref="NotesSessionId"/> y por los mismos
    /// motivos —es estado de ventana, no una preferencia de Ajustes, y una sesión que ya no exista cae en
    /// silencio al vault—, pero con clave propia: son dos selectores independientes, y compartir la clave
    /// haría que cambiar de espacio en una sección moviera la otra a la espalda del usuario.</summary>
    public string? FilesSessionId { get; set; }

    // ===================== Hábitos y Estadísticas (propuestas 13/15, docs/propuesta-nuevas-funcionalidades.md) =====================

    /// <summary>Shows/hides the "Hábitos" entry in the main nav.</summary>
    public bool HabitsPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Estadísticas" entry in the main nav.</summary>
    public bool StatisticsPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Recordatorios" entry in the sidebar's fixed bottom group.</summary>
    public bool RemindersSidebarEnabled { get; set; } = true;

    /// <summary>Shows/hides the "Notificaciones" entry in the sidebar's fixed bottom group.</summary>
    public bool NotificationsSidebarEnabled { get; set; } = true;

    // ===================== Orden de las secciones de la barra lateral (Ajustes › General) =====================
    // Qué sección va en el grupo principal desplazable frente al grupo fijo inferior (máx. 3, junto a
    // Ajustes) y en qué orden — arrastrable en Ajustes › General › Secciones de la barra lateral. Guarda
    // nombres de ViewModels.AppPage como texto; vacío significa "usar el orden por defecto" (ver
    // ViewModels.SidebarSectionCatalog), y una clave nueva que no esté en ninguna de las dos listas
    // (una sección añadida en una versión posterior de Enki) se añade sola a su grupo por defecto.

    /// <summary>Orden de las secciones en la lista principal desplazable del sidebar.</summary>
    public List<string> SidebarMainSectionOrder { get; set; } = [];

    /// <summary>Orden de las hasta 3 secciones fijadas en el grupo inferior del sidebar, junto a Ajustes.</summary>
    public List<string> SidebarBottomSectionOrder { get; set; } = [];
}
