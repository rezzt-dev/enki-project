using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace enki.Models;

public sealed class AppSettings
{
    /// <summary>Reduce los desplazamientos decorativos y acorta las transiciones de interfaz.</summary>
    public bool UseSubtleAnimations { get; set; }

    /// <summary>Acota las preferencias numéricas y migra las claves heredadas de sesión. Lo llama
    /// <see cref="Services.SettingsService"/> tras cada deserialización —carga, cambio de vault e
    /// importación— igual que <see cref="VaultSettings.Normalize"/> hace con las suyas.
    ///
    /// <para>Hasta ahora solo acotaban los comandos de la interfaz (<c>Math.Clamp</c> en los
    /// <c>ZoomIn</c>/<c>ZoomOut</c> y compañía), no la lectura del archivo. Un <c>settings.json</c> dañado,
    /// editado a mano o traído de otra versión podía dejar valores imposibles en sitios sin red: un
    /// <see cref="EditorFontSize"/> de 0 o negativo hace que Avalonia rechace la fuente y tumbe el editor,
    /// y "Importar ajustes" acepta cualquier JSON cuyas claves existan. El caso grave es el de
    /// <c>VaultSettings.UiZoomPercent</c>, que escala <em>toda</em> la ventana: a 0 no queda ningún control
    /// con el que volver a Ajustes a arreglarlo (.docs/auditoria-seguridad-y-fallos-criticos.md, BUG-02).</para>
    ///
    /// <para>Deliberadamente no toca las preferencias de texto (<see cref="Theme"/>,
    /// <see cref="StartupPage"/>, <see cref="EditorColumnPosition"/>…): esas ya las resuelve cada
    /// consumidor con su propio <c>Parse</c> y su valor de salida, así que un valor desconocido allí no
    /// rompe nada.</para></summary>
    public void Normalize()
    {
        // Las versiones anteriores guardaban dos elecciones. Notas tiene prioridad si ambas existían.
        WorkspaceSessionId ??= NotesSessionId ?? FilesSessionId;
        NotesSessionId = null;
        FilesSessionId = null;
        // Los rangos son los de los controles de Ajustes (views/settings/SettingsView.axaml); cambiar uno
        // allí sin cambiarlo aquí solo significa que el archivo admite algo que la UI no deja elegir.
        EditorFontSize = Clamp(EditorFontSize, 12, 20, 14);
        EditorColumnWidth = Clamp(EditorColumnWidth, 480, 1400, 760);
        EditorTabSize = Clamp(EditorTabSize, 2, 8, 4);
        ReminderSoonWindowDays = Clamp(ReminderSoonWindowDays, 1, 30, 7);
        NotificationSoundVolume = Clamp(NotificationSoundVolume, 0, 100, 70);
        SnoozePrimaryMinutes = Clamp(SnoozePrimaryMinutes, Services.SnoozeSchedule.MinMinutes, Services.SnoozeSchedule.MaxMinutes, 10);
        SnoozeSecondaryMinutes = Clamp(SnoozeSecondaryMinutes, Services.SnoozeSchedule.MinMinutes, Services.SnoozeSchedule.MaxMinutes, 60);
        SnoozeCustomMinutes = Clamp(SnoozeCustomMinutes, Services.SnoozeSchedule.MinMinutes, Services.SnoozeSchedule.MaxMinutes, 30);
        SnoozeEveningHour = Clamp(SnoozeEveningHour, 0, 23, 18);
        SnoozeTomorrowHour = Clamp(SnoozeTomorrowHour, 0, 23, 9);
        AutoBackupIntervalDays = Clamp(AutoBackupIntervalDays, 1, 30, 1);
        AutoBackupRetentionCount = Clamp(AutoBackupRetentionCount, 1, 30, 5);
        Ai ??= new AiSettings();
        Ai.Normalize();

        // La franja horaria laboral necesita además que el final sea posterior al principio: dos valores
        // individualmente válidos pero cruzados dejan el calendario sin ninguna hora que pintar.
        CalendarWorkingHoursStart = Clamp(CalendarWorkingHoursStart, 0, 23, 9);
        CalendarWorkingHoursEnd = Clamp(CalendarWorkingHoursEnd, 1, 24, 18);
        if (CalendarWorkingHoursEnd <= CalendarWorkingHoursStart)
        {
            CalendarWorkingHoursStart = 9;
            CalendarWorkingHoursEnd = 18;
        }

        // Vaults recientes (T4): un settings.json de otra versión —o editado a mano— puede traer la lista
        // a null, con entradas sin ruta o con la misma carpeta repetida. El selector no es sitio para
        // descubrirlo, así que se sanea aquí igual que el resto: fuera las entradas sin ruta, una sola por
        // carpeta (la más reciente gana) y como mucho MaxRecentVaults, en orden de apertura.
        RecentVaults = (RecentVaults ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.Path))
            .GroupBy(v => v.Path, Services.VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => group.MaxBy(v => v.LastOpenedUtc)!)
            .OrderByDescending(v => v.LastOpenedUtc)
            .Take(MaxRecentVaults)
            .ToList();
    }

    /// <summary>Acota a [<paramref name="min"/>, <paramref name="max"/>] con dos excepciones que no son
    /// acotado sino reparación, las dos con el mismo criterio que <see cref="VaultSettings.Normalize"/>:
    ///
    /// <list type="bullet">
    /// <item>un cero cuando el mínimo es mayor que cero es el <c>default(T)</c> que deja
    /// <c>System.Text.Json</c> al leer un archivo escrito por una versión anterior a esta propiedad — no es
    /// "el usuario eligió cero", así que vuelve al valor por defecto en vez de al mínimo, que sería una
    /// elección que nadie hizo;</item>
    /// <item><c>NaN</c>/infinito, que <c>System.Text.Json</c> no produce pero sí puede traer un archivo
    /// escrito por otra herramienta, no se puede acotar de forma significativa.</item>
    /// </list></summary>
    private static double Clamp(double value, double min, double max, double fallback) =>
        !double.IsFinite(value) || (value == 0 && min > 0) ? fallback : Math.Clamp(value, min, max);

    /// <inheritdoc cref="Clamp(double, double, double, double)"/>
    private static int Clamp(int value, int min, int max, int fallback) =>
        value == 0 && min > 0 ? fallback : Math.Clamp(value, min, max);

    public string? VaultPath { get; set; }

    /// <summary>Cuántas carpetas conserva <see cref="RecentVaults"/> antes de descartar la más antigua.</summary>
    public const int MaxRecentVaults = 10;

    /// <summary>Carpetas que se han abierto como vault en esta instalación, de la más reciente a la más
    /// antigua — el respaldo del selector "Cambiar de vault" (T4,
    /// .docs/project-info/multi-vault-en-caliente.md). Global, no por vault: ver <see cref="RecentVault"/>.
    /// La lista la mantienen <c>SettingsService.RegisterRecentVault</c>/<c>RemoveRecentVault</c>, nunca los
    /// viewmodels directamente, para que el orden y el tope sean los mismos vengan de donde vengan.</summary>
    public List<RecentVault> RecentVaults { get; set; } = [];

    /// <summary>"Mantener Enki en la bandeja al cerrar la ventana" (Ajustes › General, T2). Apagado por
    /// defecto a propósito: cerrar una ventana y que el proceso siga vivo es justo el comportamiento que
    /// sorprende a quien no lo ha pedido. Con él activo, cerrar la ventana principal la oculta y Enki
    /// sigue disponible desde el icono de bandeja (captura rápida incluida) hasta elegir "Salir".</summary>
    public bool MinimizeToTray { get; set; }

    public bool SidebarExpanded { get; set; } = true;

    /// <summary>Posición de los paneles de sección; false conserva la disposición izquierda original.</summary>
    public bool NotesPanelOnRight { get; set; }
    public bool FilesPanelOnRight { get; set; }
    public bool SkillsPanelOnRight { get; set; }
    public bool AssistantPanelOnRight { get; set; }

    /// <summary>Muestra/oculta la fila "Cambiar de vault" entre la cabecera de marca y la navegación
    /// (T4, .docs/project-info/multi-vault-en-caliente.md). Quien trabaja siempre sobre el mismo vault no
    /// necesita el selector ocupando una fila permanente del raíl; apagarlo no descarta los recientes,
    /// solo esconde el acceso, y la paleta de comandos sigue siendo la vía para cambiar de vault.</summary>
    public bool VaultSwitcherEnabled { get; set; } = true;

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

    /// <summary>Muestra los botones de minimizar, maximizar y cerrar en la barra propia.</summary>
    public bool ShowWindowControls { get; set; } = true;

    /// <summary>Shows la barra de desplazamiento vertical del Dashboard — Ajustes › General. Por
    /// defecto activada (que es como se comportaba antes de existir el ajuste); apagarla solo oculta el
    /// raíl y el pulgar, el scroll con rueda/teclado sigue funcionando igual.</summary>
    public bool DashboardShowScrollbar { get; set; } = true;

    /// <summary>Muestra la fila de filtro por etiquetas del tablero; ocultarla no borra los filtros
    /// aplicados ni toca las etiquetas de las tarjetas.</summary>
    public bool KanbanShowTagFilter { get; set; } = true;

    /// <summary>Habilita la gestión de varios tableros locales en este vault. Se enciende sola en cuanto
    /// el almacén guarda más de un tablero (cualquier camino que cree uno lo activa), así que apagarla solo
    /// tiene efecto mientras quede uno: <c>SettingsViewModel.KanbanMultipleBoardsEnabled</c> avisa en vez
    /// de dejar tableros inalcanzables.</summary>
    public bool KanbanMultipleBoardsEnabled { get; set; }

    /// <summary>Último tablero abierto; una identidad obsoleta cae al predeterminado del almacén.</summary>
    public string? KanbanLastSelectedBoardId { get; set; }

    /// <summary>Muestra el botón de filtro rápido "Alta prioridad" en la barra de herramientas del
    /// tablero Kanban. Apagado, el botón desaparece (sus tarjetas siguen existiendo y el filtro manual
    /// sigue disponible en el desplegable de ordenación).</summary>
    public bool KanbanShowHighPriorityButton { get; set; } = true;

    /// <summary>Muestra el botón de filtro rápido "Vencidas" en la barra de herramientas del tablero
    /// Kanban.</summary>
    public bool KanbanShowOverdueButton { get; set; } = true;

    /// <summary>Muestra el botón "Archivadas" (puerta a la ventana de tareas archivadas) en la barra de
    /// herramientas del tablero Kanban.</summary>
    public bool KanbanShowArchiveButton { get; set; } = true;

    /// <summary>Muestra el botón de ordenación (desplegable de criterios) en la barra de herramientas del
    /// tablero Kanban.</summary>
    public bool KanbanShowSortButton { get; set; } = true;

    /// <summary>"Vertical" o "Horizontal" — Ajustes › Apariencia. Vertical apila las tarjetas dentro de
    /// columnas lado a lado (la disposición clásica de un tablero Kanban); Horizontal gira cada columna
    /// 90° en una franja de ancho completo con sus tarjetas desplazándose de lado a lado. Stored as a
    /// plain string for the same reason as <see cref="Theme"/>/<see cref="EditorColumnPosition"/>.</summary>
    public string KanbanBoardOrientation { get; set; } = "Vertical";

    /// <summary>Ajustes › Apariencia › Logo — <c>true</c> pinta en el brand header del sidebar la imagen
    /// que el usuario importó (<see cref="Services.BrandLogoService"/>), <c>false</c> (por defecto) el
    /// icono de Enki adaptativo al tema. Solo el interruptor vive aquí: la imagen es un archivo
    /// aparte en <c>&lt;vault&gt;/.enki/</c>, así que apagar esto vuelve al icono por defecto sin perder la
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

    /// <summary>Fuente del código Markdown y de los visores de código. Null usa JetBrains Mono NF.</summary>
    public string? CodeFontFamily { get; set; }

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

    /// <summary>Permite crear avisos de cualquier categoría. Al desactivarlo se conservan los avisos
    /// anteriores, pero no se añaden nuevos ni se disparan toasts o sonidos.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Shows/hides the bell (system event log) button in the title bar entirely.</summary>
    public bool NotificationsPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the reminders (due-date) button in the title bar entirely.</summary>
    public bool RemindersPanelEnabled { get; set; } = true;

    /// <summary>Shows/hides the inbox (captura rápida) button in the title bar entirely — el botón que
    /// apunta una idea suelta sin salir de donde estés. Hermano de
    /// <see cref="RemindersPanelEnabled"/>/<see cref="NotificationsPanelEnabled"/>: los tres botones de
    /// contenido de la barra de título se ocultan por separado desde Ajustes › Notificaciones.</summary>
    public bool InboxPanelEnabled { get; set; } = true;

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

    // ===================== Aplazar recordatorios (T5) =====================
    // Valores rápidos del menú "Aplazar" de la bandeja de recordatorios y del centro de notificaciones.
    // Ver Services.SnoozeSchedule, que es quien los convierte en un instante concreto (y quien los acota
    // por su cuenta, para que un valor imposible degrade al de fábrica en vez de romper el aplazamiento).

    /// <summary>Minutos de la primera opción rápida de "Aplazar" — "10 min" de fábrica.</summary>
    public int SnoozePrimaryMinutes { get; set; } = 10;

    /// <summary>Minutos de la segunda opción rápida de "Aplazar" — "1 h" de fábrica.</summary>
    public int SnoozeSecondaryMinutes { get; set; } = 60;

    /// <summary>Hora local (0–23) de la opción "Esta tarde".</summary>
    public int SnoozeEveningHour { get; set; } = 18;

    /// <summary>Hora local (0–23) de la opción "Mañana".</summary>
    public int SnoozeTomorrowHour { get; set; } = 9;

    /// <summary>Último valor usado en "Personalizado…", para que el control aparezca donde el usuario lo
    /// dejó en vez de volver siempre al valor de fábrica.</summary>
    public int SnoozeCustomMinutes { get; set; } = 30;

    // ===================== Aviso de versión nueva (T9) =====================
    // Ver Services.UpdateCheckService. La comprobación es una sola petición GET anónima a la API pública
    // de GitHub; todo lo que la gobierna está aquí y nada de ello viaja a ningún sitio.

    /// <summary>Si Enki comprueba al arrancar si hay una release más reciente. <b>Desactivado de
    /// fábrica</b> (§9 del documento de funcionalidades dejaba abierto el valor por defecto): con esto
    /// apagado la app no toca la red por su cuenta jamás, que es lo coherente con el resto del producto.
    /// El botón "Buscar ahora" de Ajustes sigue disponible siempre.</summary>
    public bool CheckUpdatesOnStartup { get; set; }

    /// <summary>Última comprobación, automática o manual — hace de límite de "una cada 24 h" para la
    /// automática.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Versión que el usuario pidió omitir con "Omitir esta versión". Solo silencia esa y las
    /// anteriores: una posterior vuelve a avisar.</summary>
    public string? SkippedUpdateVersion { get; set; }

    // ===================== Vínculo con Polar (Ajustes › Cuenta) =====================
    // See docs/supabase-sync/ — enabling this unlocks the "Gestión de tareas" view in Kanban and turns
    // on background sync (task lists/tasks/subtasks/reminders) against the shared Supabase project.
    // Los tokens viven en el almacén seguro del usuario; nunca viajan con el vault.

    public bool PolarLinked { get; set; }
    public string? PolarUserId { get; set; }
    public string? PolarEmail { get; set; }
    public string? PolarAccessToken { get; set; }
    public string? PolarRefreshToken { get; set; }
    public long PolarAccessTokenExpiresAtUtcMs { get; set; }

    /// <summary>Cursor heredado de instalaciones anteriores. Los vaults actuales guardan su propio
    /// cursor en <see cref="VaultSettings.PolarLastSyncAtUtcMs"/>.</summary>
    public long PolarLastSyncAtUtcMs { get; set; }

    /// <summary>Shows/hides the tag-chip filter row in Kanban → "Gestión de tareas" (Ajustes › Cuenta).
    /// Defaults to hidden — the row starts collapsed behind the setting until the user opts in.</summary>
    public bool PolarShowTaskTags { get; set; }

    /// <summary>Rotación automática de las esferas del grafo de Sesiones. Null mientras el usuario no la haya
    /// cambiado: entonces decide el perfil de movimiento (<see cref="UseSubtleAnimations"/>) y, en el visor web,
    /// la preferencia de movimiento reducido del sistema.</summary>
    public bool? SessionsGraphRotationEnabled { get; set; }

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

    // ===================== Asistente de IA (.docs/project-info/asistente-ia.md) =====================

    /// <summary>Preferencias del asistente de IA. Clave de instalación (ver
    /// <see cref="Services.SettingsService"/>): describe este equipo —dónde está el CLI, qué aceptó el
    /// usuario—, no el vault, así que no viaja con él.</summary>
    public AiSettings Ai { get; set; } = new();

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

    /// <summary>Shows/hides the "Etiquetas" entry (Gestor global de etiquetas) in the main nav.</summary>
    public bool TagManagerPanelEnabled { get; set; } = true;

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
    /// <summary>Justificación visual de párrafos; activa también en ajustes anteriores sin esta clave.</summary>
    public bool EditorJustifyParagraphs { get; set; } = true;

    public bool EditorAutoPairs { get; set; } = true;

    /// <summary>Si el editor descarga y dibuja las imágenes escritas con una URL http(s) — Ajustes ›
    /// Editor. Las imágenes del propio vault y las <c>data:</c> incrustadas se dibujan siempre; estas
    /// otras convierten abrir una nota en una petición a un servidor de terceros, así que se pueden
    /// apagar. Por defecto activo, como en cualquier editor Markdown.</summary>
    public bool EditorRemoteImages { get; set; } = true;

    /// <summary>Si Enki vigila el vault para enterarse de los cambios que otro programa (Syncthing,
    /// Dropbox, otro editor) haga sobre las notas abiertas — Ajustes › Editor. Apagarlo no deja el
    /// autoguardado desprotegido: la comprobación previa a escribir de
    /// <c>VaultService.WriteContentChecked</c> sigue en pie y no depende de esto. Ver
    /// <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.</summary>
    public bool VaultWatchExternalChanges { get; set; } = true;

    /// <summary>Si una nota abierta <em>sin</em> cambios locales se recarga sola cuando cambia fuera de
    /// Enki. Apagado, ese caso también pregunta en vez de recargar en silencio; con cambios locales
    /// siempre se pregunta, decida lo que decida este ajuste. Solo tiene efecto con
    /// <see cref="VaultWatchExternalChanges"/> encendido.</summary>
    public bool VaultAutoReloadExternalChanges { get; set; } = true;

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

    /// <summary>Sesión compartida por Notas y Archivos; null selecciona el vault entero.</summary>
    public string? WorkspaceSessionId { get; set; }

    /// <summary>Clave heredada de Notas, solo para migrar ajustes anteriores.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    /// <summary>Firma (<c>"{gesto}|{GlobalHotkeyStatus}"</c>) del último aviso de "atajo global no
    /// disponible" que ya se le enseñó al usuario, o null si nunca falló o el último intento sí se
    /// registró. Evita que un escritorio donde el atajo global nunca va a funcionar (Wayland sin portal)
    /// apile una notificación idéntica en cada arranque: solo se avisa cuando cambia el gesto o el motivo.
    /// Ver <c>ViewModels.Shell.MainWindowViewModel.ApplyQuickCaptureHotkey</c>.</summary>
    public string? QuickCaptureHotkeyWarningSignature { get; set; }

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

    /// <summary>Clave heredada de Archivos, solo para migrar ajustes anteriores.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    /// <summary>Nombres personalizados de las secciones del sidebar, por clave (<c>AppPage</c>). Una sección
    /// sin entrada usa su nombre localizado por defecto.</summary>
    public Dictionary<string, string> SidebarSectionNames { get; set; } = [];
}
