# 04 · FICHAS TÉCNICAS DE LAS PROPUESTAS CLAVE

> Enfoque de implementación, archivos afectados y criterio de cierre de las propuestas de mayor impacto o
> riesgo. Las rutas son de `enki/src/enki/` y deben comprobarse antes de empezar; si el código ha cambiado,
> manda el código.

**Fecha:** 2026-11-09 · rama `beta-v2.0`

---

## AIA-01 · CONTEXTO AUTOMÁTICO

- **Enfoque:** ajuste `AiSettings.IncludeActiveViewContext` (apagado por defecto). `AssistantViewModel` recibe un
  callback desde `MainWindowViewModel` con nota activa y selección; `AiPromptBuilder` añade `<enki_view>` junto a
  `<enki_scope>` con las mismas neutralizaciones de etiquetas.
- **Afecta a:** `models/ai/AiSettings.cs`, `services/ai/AiPromptBuilder.cs`,
  `viewmodels/ai/AssistantViewModel.Scope.cs`, composición del shell, Ajustes.
- **Cuidado:** sesión protegida bloqueada → nada; nota fuera del ámbito del chat → nada; tope de caracteres
  dentro de `MaxContextChars`; mostrar al usuario un chip «Viendo: nota X» para que sepa qué se envía.
- **Cierre:** con el ajuste apagado el prompt es idéntico al actual; encendido, el turno incluye solo ruta relativa y
  selección; prueba de que no se filtra una nota de otro ámbito.

## AIA-02 · NAVEGACIÓN

- **Enfoque:** nuevas herramientas `AiToolScope.Content`/`Vault` con efecto `Read` que invocan un callback de
  navegación inyectado en `AiVaultToolbox` (como ya se hace con las pestañas abiertas). Respetar la regla de que
  abrir no cambia datos; en el panel lateral no debe cambiar de sección sin que el usuario lo pida.
- **Afecta a:** `services/ai/vault/AiVaultToolbox*.cs`, `viewmodels/shell/MainWindowViewModel.Assistant.cs`.
- **Cuidado:** la actividad de cada respuesta ya enlaza a notas y tarjetas; reutilizar ese enlace en vez de
  duplicarlo. Con la sección Kanban o Calendario desactivada por perfil de uso, devolver `Unavailable`.
- **Cierre:** el modelo abre una nota existente y un tablero; un destino inexistente o fuera de ámbito devuelve error
  sin revelar rutas.

## AIA-03 · AYUDA INTEGRADA

- **Enfoque:** generar en compilación un índice de ayuda (Markdown troceado por secciones) a partir de la documentación
  de usuario y de `ShortcutCatalog`; empaquetarlo como recurso. Herramienta `search_help` de solo lectura, sin
  depender del ámbito ni del permiso de consulta (no lee el vault).
- **Afecta a:** nuevo `services/ai/AiHelpService.cs`, `ShortcutCatalog.cs`, script en `scripts/`, i18n.
- **Cuidado:** los atajos son remapeables: leer el valor actual de `ShortcutRegistry`, no el predeterminado; separar
  ayuda por idioma de la interfaz.
- **Cierre:** «¿cómo abro el asistente?» devuelve el atajo real del usuario; sin red ni vault accesibles sigue
  funcionando.

## AIA-06 · DESHACER POR TURNO

- **Enfoque:** registrar en `AiToolActivity` la huella previa y la versión creada por cada acción `Change`
  (`VaultService.NoteHistory`). «Revertir» recorre las acciones en orden inverso y restaura solo si el estado actual
  coincide con el que dejó el asistente.
- **Afecta a:** `models/ai/AiToolActivity.cs`, `AiVaultToolbox*.cs`, `viewmodels/ai/AiToolActivityViewModel.cs`,
  vistas de actividad.
- **Cuidado:** acciones sin vuelta atrás (ya piden confirmación) se excluyen y se avisa; mover/renombrar necesitan la
  operación inversa; Kanban y recordatorios no tienen historial y requieren guardar el estado previo.
- **Cierre:** crear, editar y mover una nota y revertir deja el vault idéntico; si el usuario editó después, no se
  pisa y se explica.

## AIA-16 · BÚSQUEDA SEMÁNTICA LOCAL

- **Enfoque:** servicio de indexado por lotes con un modelo de embeddings de Ollama; almacén vectorial simple en la
  carpeta de la instalación (no en el vault); actualización incremental con `VaultChangeWatcher`; herramienta
  `semantic_search` que devuelve fragmentos con ruta y puntuación.
- **Afecta a:** nuevo `services/ai/AiEmbeddingService.cs`, `NoteSearchService.cs`, Ajustes (activar, estado, reconstruir).
- **Cuidado:** consumo de CPU y disco en vaults grandes; sesiones cifradas nunca se indexan; indexar es opt-in y se
  puede borrar; no enviar texto a proveedores remotos para crear embeddings.
- **Cierre:** una consulta con sinónimos recupera la nota correcta en un vault de prueba; el índice se reconstruye tras
  cambios externos; borrar el índice no afecta a las notas.

## AIA-21 · TAREAS PROGRAMADAS Y PROACTIVIDAD

- **Enfoque:** rutinas definidas por el usuario (prompt, horario, ámbito, herramientas permitidas) que se ejecutan solo
  con Enki abierto y modo de solo lectura por defecto; el resultado llega como notificación con enlace al chat.
- **Afecta a:** nuevo servicio de rutinas, `NotificationService`, `AiSettings`, Ajustes; requiere AIA-12.
- **Cuidado:** permisos permanentes acotados por rutina, tope de coste y de duración, cancelación visible, ninguna
  acción irreversible sin presencia del usuario, registro de lo ejecutado.
- **Cierre:** una rutina de briefing diario se ejecuta una vez al día, no escribe nada en el vault y se puede pausar.
