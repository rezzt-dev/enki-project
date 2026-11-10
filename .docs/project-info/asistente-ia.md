# Asistente de IA: Claude, OpenAI, Gemini y modelos locales

> Referencia viva del módulo de IA. Concreta, para los proveedores implementados, la propuesta de
> `.docs/agent-docs/implement-ia/implementacion-ia-desde-cero.md`. Si algo de aquí deja de coincidir con
> el código, el código manda.

## Resumen

La sección **Asistente** permite elegir Claude, OpenAI mediante la cuenta de Codex, Gemini con la clave de
API de Google AI Studio del usuario o modelos locales servidos por Ollama. Comparte historial, notas adjuntas, skills, ámbitos y herramientas del vault,
y puede buscar en internet y leer páginas web cuando el chat lo permite (ver "Acceso a internet").
La IA está **apagada por defecto**, exige aceptar el aviso de privacidad y términos y, apagada, no ejecuta
comprobaciones ni envía contenido. Claude conserva su integración mediante el CLI oficial (`claude -p`), con
la suscripción del usuario o con su propia clave de API.

La conversación se usa desde dos superficies sobre el mismo estado: la **sección Asistente** y un **panel
lateral** que se abre junto a cualquier otra sección. El historial es **de cada vault**, cada chat pide
**permiso una vez** antes de buscar y leer por su cuenta, y lo que se está escribiendo se conserva como
**borrador** por conversación.

## Dos superficies: sección y panel lateral

- `AssistantViewModel` es una sola instancia. La sección (`AssistantView`) y el panel (`AssistantPanelView`)
  se componen de los mismos controles: `AssistantTranscriptView` (puesta en marcha, hilo, bienvenida, error),
  `AssistantWelcomeView` (bienvenida de un chat vacío y sugerencias), `AssistantComposerView`,
  `AssistantScopeButton`, `AssistantModelControls` y `AssistantHistoryList`. El panel los marca con la clase
  `compact`. Los diálogos (`AssistantDialogs`) los monta una sola vez el shell.
- El compositor reúne el texto, los adjuntos y la barra de acciones en un único marco de borde recto, con
  el mismo fondo y sin línea entre ambos; el borde sube de tinta al pasar el cursor y al recibir el foco, y
  pulsar en cualquier hueco del marco lleva al cuadro de texto. Enviar (flecha, sólido) o detener
  (contorno) son un cuadrado fijo a la derecha; enviar deshabilitado es un hueco apenas marcado, para que se
  note al instante cuándo hay algo que mandar. Las acciones auxiliares pasan a otra línea cuando falta
  ancho. Mientras se escribe, la sección enseña los atajos de teclado junto a las acciones; el botón de
  información abre al pulsarlo un popover con el ámbito del chat, el aviso del proveedor y los atajos, sin
  ocupar altura bajo el compositor. Se cierra pulsando fuera, de nuevo en el botón o con Escape.
- **Chat vacío en la sección.** La bienvenida (`AssistantWelcomeView` con la clase `hero`) se centra en la
  vista con el compositor en medio: el titular encima, las sugerencias debajo. Cabecera y sugerencias
  comparten alto (`SharedSizeGroup`), así que el compositor queda en el centro exacto vertical y
  horizontal; si la ventana es baja, la bienvenida se desplaza. Al enviar el primer mensaje (o abrir un
  chat con mensajes) el compositor baja al pie del hilo, y vuelve al centro con un chat nuevo. Es siempre
  el mismo control, que `AssistantView` cambia de hueco: el borrador, los adjuntos, los popovers y el foco
  no se pierden. El hilo compartido se marca `external-welcome` para no pintar su propia bienvenida
  detrás. En el panel, por su ancho, la bienvenida queda en el hilo y el compositor fijo abajo.
- El panel vive en la columna derecha de `ShellView`, con ancho ajustable (`AiSettings.PanelWidth`,
  340–720 px). Su vista y los diálogos se construyen la primera vez que hacen falta, para no encarecer el
  arranque con la IA apagada.
- `MainWindowViewModel.Assistant.cs` decide qué se ve: el panel solo se enseña con la IA encendida, fuera del
  modo enfoque y sin la sección Asistente delante, así que nunca hay dos compositores a la vista. "Ampliar"
  lleva el chat a la sección; "Ver como panel" vuelve a la sección anterior con el panel abierto; cerrar
  el panel solo lo oculta y la respuesta sigue. Abrirlo no navega ni cambia la nota seleccionada.
- Accesos: botón del asistente en la barra de título, `Ctrl+Shift+9` (remapeable), la paleta de comandos.
  "Usar en el asistente", "Preguntar al asistente" de Sesiones y la paleta abren el chat en el panel si
  está abierto y, si no, en la sección.
- El punto de lectura del hilo (`TranscriptScrollOffset`) se conserva al cambiar de superficie. Si el usuario
  sube a releer mientras llega una respuesta, el hilo no se mueve y aparece "Ir al final".
- **Indicador global.** El botón de la barra de título refleja `AssistantActivityState`: girando mientras
  trabaja, "!" cuando una respuesta espera un permiso o una confirmación, y una marca de "respuesta lista"
  cuando terminó sin estar a la vista (se borra al enseñar el chat). Cada fila del historial dice además si
  su respuesta está en curso o esperando. Solo las decisiones pendientes fuera de la vista generan una
  notificación; nunca un aviso por fragmento ni por herramienta.

## Permiso de consulta por chat

Adjuntar contenido no exige ningún permiso. Para que el asistente **busque y lea por su cuenta**, el chat
necesita `AiChatSession.ReadAccess` (`AiReadGrant`):

- Lo exige `AiVaultToolbox.ExecuteAsync` antes de cada herramienta de lectura, a través de
  `AiToolContext.ReadAccess` (`AiReadAccess`): no basta con decirlo en el prompt. Sin permiso, la primera
  lectura enseña en la respuesta la tarjeta "¿Permitir que este chat busque y consulte documentos de …?",
  con el ámbito y el proveedor que recibirá el contenido. Las lecturas simultáneas comparten una sola
  petición.
- "Permitir en este chat" lo guarda en la conversación y no se vuelve a preguntar, tampoco al reabrirla.
  "Ahora no" vale para el resto del turno: el modelo recibe que no puede buscar y no se pregunta en bucle.
  Detener, apagar la IA, cambiar de vault o dejar pasar cinco minutos cuentan como "Ahora no".
- En un chat del vault, el permiso de la raíz no cubre el acceso a sesiones activado después
  (`AiReadGrant.IncludesSessions`): hay que aceptarlo de nuevo.
- El menú de ámbito enseña el estado y permite conceder o revocar el permiso. Revocar impide las lecturas
  siguientes, incluso dentro de una respuesta en curso, pero no borra lo que ya se envió al proveedor; el
  menú lo explica. El bloque `<enki_scope>` de cada turno le dice al modelo si puede buscar.
- El permiso de consulta, el consentimiento de activación, la confirmación de adjuntos y las aprobaciones
  de borrados son decisiones distintas, y ninguna implica a las demás.

## Historial por vault y borradores

- Cada vault tiene una identidad estable (`VaultSettings.VaultId`, en `.enki/settings.json`), que se crea
  la primera vez que se guarda un chat o un borrador; consultarla nunca escribe en el vault. Si el vault no
  se puede escribir se usa una identidad derivada de su ruta (`VaultSettingsService.PathIdentity`), y los
  chats guardados con ella siguen siendo de ese vault.
- Cada chat guarda `VaultId` y `VaultName`. El historial solo enseña los del vault abierto; el último chat se
  recuerda por vault (`AiSettings.LastChatByVault`). Cambiar de vault corta la respuesta, cierra el chat y,
  cuando los ajustes del vault nuevo están cargados (`AssistantViewModel.ReloadForVault`), carga su historial.
- Las conversaciones de antes de esta separación (sin `VaultId`) no se atribuyen al vault abierto: aparecen
  en el grupo "Sin vault asignado", se pueden leer y, para continuarlas, se asocian con "Asociar a este
  vault" (`AiChatService.AssignToVault`). Nunca se borran ni se reasignan solas.
- **Borradores.** Cada conversación guarda lo que se estaba escribiendo (`AiChatSession.Draft`) y cada vault
  y ámbito, el de su chat nuevo (`ai/drafts.json`): texto, adjuntos como rutas relativas, skill y capacidad.
  Se guardan poco después de dejar de escribir, al cambiar de chat, de superficie o de vault (antes del
  cambio, mientras las rutas siguen siendo las del vault anterior) y al cerrar Enki. Al restaurarlos se
  comprueba cada adjunto: si ya no existe, quedó fuera del ámbito o está bloqueado, se retira y el compositor
  lo dice.

## Configurar OpenAI

1. Instalar el [CLI oficial de Codex](https://developers.openai.com/codex/cli) e iniciar sesión desde una
   terminal con `codex login`. El propio CLI administra la cuenta; Enki no lee ni copia sus tokens.
2. En Ajustes › Inteligencia artificial, seleccionar **OpenAI · ChatGPT**, activar el asistente y comprobar
   la conexión. Se puede indicar la ruta del ejecutable si no se encuentra automáticamente.
3. El modelo vacío usa el predeterminado de Codex; un identificador explícito se configura en Ajustes y
   aparece en el selector del chat. El acceso efectivo al modelo lo verifica la respuesta del proveedor.

La [autenticación de Codex](https://developers.openai.com/codex/auth) puede usar una cuenta de ChatGPT
con acceso a Codex o una clave API configurada por el usuario en el CLI. La interfaz distingue ambos
métodos; el uso de API se factura por separado. Enki no implementa un inicio de sesión alternativo.

Los turnos usan `codex exec --strict-config --json --ephemeral --ignore-user-config --ignore-rules`, directorio privado,
sandbox de solo lectura, búsqueda web propia de Codex y herramientas de shell desactivadas (la web llega, como
en el resto de proveedores, por las herramientas de Enki: ver "Acceso a internet"). No se carga la configuración
personal de MCP, reglas o instrucciones del proyecto. Se exige que el CLI anuncie los flags de aislamiento;
una versión antigua muestra el estado de actualización y no ejecuta el chat. El único MCP configurado es
el de Enki; el token efímero viaja por una variable del proceso hijo, nunca por argumentos. Se exigen tanto
el evento `turn.completed` como un código de salida correcto. Los mensajes del agente se muestran conforme
se completan; el razonamiento y los resultados internos de MCP no se pintan como respuesta.

## Configurar Gemini

1. Crear una clave en [Google AI Studio](https://aistudio.google.com/apikey) con una cuenta de Google. El
   nivel gratuito no exige facturación.
2. En Ajustes › Inteligencia artificial, seleccionar **Google Gemini**, pegar la clave y guardarla. El
   asistente enseña el mismo acceso a Ajustes y a AI Studio mientras no haya clave.
3. "Comprobar" lista los modelos de chat que esa clave puede usar (`GET models`) y los ofrece en Ajustes y en
   el selector del chat. "Predeterminado del proveedor" usa `gemini-flash-latest` o, si la clave no lo tiene,
   el primer Flash disponible: sigue valiendo cuando Google retira versiones.

**Por qué una clave y no la cuenta de Google (ni el plan de estudiantes).** Comprobado el 2026-11-04:

- Desde el 18 de junio de 2026, Gemini CLI y Gemini Code Assist dejaron de atender a los planes de consumo
  (Gemini Code Assist para particulares, Google AI Pro y Google AI Ultra); las [notas de versión de Gemini
  Code Assist](https://docs.cloud.google.com/gemini/docs/codeassist/release-notes) remiten a Antigravity.
  "Sign in with Google" en el CLI devuelve "This client is no longer supported".
- La [FAQ de Antigravity](https://antigravity.google/docs/faq) dice que envolver `agy` en aplicaciones de
  terceros "is not supported under consumer plans", aunque sea el binario oficial sin modificar, y que hacerlo
  puede suspender la cuenta. Para aplicaciones de terceros recomienda una clave de Google AI Studio o de
  Gemini Enterprise.
- Google AI Pro para estudiantes es un plan de consumo. Ajustes lo explica con una nota y propone la clave
  gratuita de AI Studio con la misma cuenta. Si Google publica una vía permitida para usar esos planes desde
  terceros, se añadiría como otro modo de autenticación del mismo proveedor, como la suscripción de Claude.

Los [términos de la API de Gemini](https://ai.google.dev/gemini-api/terms) exigen tener 18 años o más. En el
nivel gratuito, fuera del EEE, Suiza y Reino Unido, Google puede usar el contenido y revisarlo con personas
para mejorar sus productos; con facturación activa, no. Ajustes lo dice antes de pegar la clave.

### Transporte

`GeminiApiService` llama directamente a `generativelanguage.googleapis.com/v1beta` con `HttpClient`, sin
SDK ni CLI que instalar:

- La clave viaja solo en la cabecera `x-goog-api-key`, nunca en la URL ni en el log. Vive en el almacén
  seguro (`AiApiKeyStore` con `AiApiKeyKind.Gemini`, entrada `gemini`, distinta de la de Anthropic) y solo se
  lee con Gemini seleccionado. Ajustes valida la forma, enseña sus últimos caracteres y permite borrarla.
- `streamGenerateContent?alt=sse` con `systemInstruction` y el turno como un único mensaje de usuario. Como
  Ollama, reconstruye el contexto desde el historial de Enki (60 000 caracteres). El texto de razonamiento
  (`thought`) no se pinta. Se exige `finishReason`: una respuesta cortada conserva el texto como interrumpido.
- Capacidad: Gemini 3 y posteriores reciben `thinkingLevel` (`low`, `medium`, `high`); 2.x, un
  `thinkingBudget` (1024, dinámico, 16384). Si el modelo rechaza la configuración, el primer envío se repite
  sin ella.
- Herramientas: el catálogo del MCP del vault se declara como `functionDeclarations` con
  `parametersJsonSchema`. Cada `functionCall` se ejecuta por `EnkiMcpClient`, el mismo cliente MCP que usa
  Ollama, contra el mismo `EnkiMcpServer`. Ámbito, permisos en caliente, confirmaciones y conflictos de disco
  siguen en `AiVaultToolbox`. Las partes del modelo se devuelven tal cual, con su `thoughtSignature`. Un
  nombre de herramienta inventado vuelve al modelo como error sin llegar al servidor. Mismo límite que
  Ollama: 16 rondas y 32 llamadas por ronda.
- Errores de Google (`error.status`, `ErrorInfo.reason`, `RetryInfo.retryDelay`): clave inválida o
  denegada → clave rechazada (acceso a Ajustes); `RESOURCE_EXHAUSTED` → límite de uso con la hora de
  reinicio; `NOT_FOUND` → modelo no disponible; `UNAVAILABLE`/5xx → saturación; región no admitida y bloqueos
  de seguridad, con su propio mensaje.

## Configurar modelos locales

1. Instalar [Ollama](https://docs.ollama.com/quickstart), descargar un modelo, por ejemplo con
   `ollama pull qwen3:8b`, e iniciar el servidor con `ollama serve`.
2. Seleccionar **Ollama** en Ajustes y comprobar la dirección, por defecto `http://localhost:11434`.
   Solo se admiten direcciones HTTP/HTTPS de loopback, sin rutas ni credenciales. El cliente de producción
   no usa proxies ni sigue redirecciones.
3. Elegir uno de los modelos instalados y abrir un chat. No se descarga ni instala nada automáticamente.

La API `/api/tags` descubre el catálogo y `/api/show` comprueba las capacidades del modelo seleccionado.
Los modelos que no anuncian herramientas funcionan con texto y notas adjuntas. Los que anuncian `tools`
reciben el catálogo existente mediante MCP y sus acciones pasan por los mismos controles de ámbito,
confirmaciones, conflictos de disco y permisos en caliente. No hay una segunda implementación del vault.
Los modelos remotos de Ollama se excluyen del catálogo y se rechazan antes de enviar una conversación,
incluso cuando tienen un alias local. El esfuerzo usa el predeterminado del modelo y el control se muestra
deshabilitado. Las respuestas se leen como NDJSON; una conexión cortada sin `done` conserva el texto
parcial como interrumpido. Los ciclos de herramientas tienen un límite de 16 rondas y 32 llamadas por ronda.

Cada chat conserva `AiChatSession.Provider`; los archivos anteriores sin ese campo pertenecen a Claude. Los
valores nuevos de `AiProvider` se añaden al final (`Gemini` = 3) para no reinterpretar lo ya guardado.
Cambiar de proveedor abre otro chat; reabrir uno restaura su proveedor. Los modelos predeterminados se
guardan por proveedor, y los selectores se bloquean durante una respuesta. Codex, Gemini y Ollama reconstruyen el
contexto desde el historial local de Enki con el mismo límite de recuperación existente (60 000 caracteres),
sin reutilizar identificadores de sesión de Claude. Los adjuntos antiguos conservan sus metadatos, no una
copia del contenido; para volver a enviar ese contenido hay que adjuntarlo de nuevo o consultarlo con herramientas.

## Cómo se usa Claude (y por qué así)

La página de [legal y cumplimiento de Claude Code](https://code.claude.com/docs/en/legal-and-compliance)
(consultada el 2026-10-24) fija las reglas que condicionan el diseño:

- Los desarrolladores de terceros **no pueden ofrecer el inicio de sesión de Claude.ai en sus
  aplicaciones**, ni enrutar peticiones con credenciales de planes Free/Pro/Max en nombre de sus usuarios,
  ni recoger, guardar o intermediar credenciales o tokens: el inicio de sesión tiene que completarse en el
  flujo de Anthropic.
- Eso **no impide** que un usuario inicie sesión con su propia suscripción en el binario de Claude Code sin
  modificar.
- El binario debe ejecutarse tal cual lo publica Anthropic, sin quitar ni restringir sus métodos de
  autenticación, y cada usuario se autentica con sus propias credenciales y paga su propio uso.
- Se puede decir en texto plano que la app "ejecuta Claude Code", pero no usar los nombres o logos de
  Claude Code o Anthropic como nombre de una funcionalidad propia ni sugerir respaldo de Anthropic.

Traducido a Enki:

| Regla | Implementación |
|---|---|
| Sin login propio | Enki no tiene botón "Iniciar sesión con Claude". Si no hay sesión, enseña la orden `claude auth login` para ejecutarla en una terminal (con botón de copiar) y "Comprobar de nuevo". |
| Sin tocar credenciales | Nunca se lee `~/.claude/` ni ningún token. La cuenta que se muestra sale de `claude auth status --json`, que imprime el propio CLI. |
| Binario sin modificar | Enki lo busca (`ClaudeCliLocator`), no lo instala, no lo empaqueta ni lo parchea. Los flags que usa son públicos (`claude --help`). |
| Uso del usuario | Cada usuario usa su propia cuenta; las respuestas cuentan contra su plan. Enki no paga ni revende uso. |
| Nombres y marcas | La sección se llama "Asistente"; los textos dicen "a través de Claude Code" y que Enki es independiente, no afiliada ni respaldada por Anthropic. Sin logos. |

Todo esto se explica en **Ajustes › Inteligencia artificial › Cómo funciona el asistente** (visible también con la
IA apagada), en el aviso que aparece la primera vez que se activa, en el panel de puesta en marcha y en una
línea bajo el compositor del chat. **Decisión pendiente del responsable del proyecto**: antes de
distribuir una versión, volver a leer esos términos; si cambian, el backend se puede retirar sin tocar el
resto (ver "Arquitectura").

## Personalidad: nombre y contexto por capas

El usuario puede dar al asistente un nombre y un contexto que lo acompaña en todas las conversaciones, y
matizarlo después para cada sesión y para cada chat. Así el asistente se adapta a dónde se está trabajando
sin tener que repetirle en cada mensaje quién es el usuario o qué se busca.

| capa | dónde se edita | dónde se guarda | alcance |
| --- | --- | --- | --- |
| nombre y contexto general | `ajustes > inteligencia artificial > personalidad del asistente` | `ai/persona.json` (`AiPersona.Name`, `AiPersona.Context`) | todos los chats |
| contexto de la sesión | menú de personalidad de la cabecera, en un chat de sesión | `ai/persona.json` (`AiPersona.SessionContexts`, clave `<vaultId>/<sessionId>`) | todos los chats de esa sesión |
| contexto del chat | menú de personalidad de la cabecera | `AiChatSession.Context` (o `AiChatDraft.Context` en un chat sin empezar) | esa conversación |

- **Cada turno.** `AssistantViewModel.BuildPersonaNotice` lee las tres capas al enviar y
  `AiPromptBuilder.WithPersona` antepone un bloque `<enki_persona>` (nombre, `<general_context>`,
  `<session_context session="…">` y `<chat_context>`) delante de `<enki_scope>`. Sin nada definido el turno se
  envía igual que antes. Va en cada turno y no en el prompt de sistema porque el CLI conserva el prompt con que
  empezó la conversación: así un cambio a mitad de chat se aplica en el mensaje siguiente.
- **Prompt de sistema.** Un párrafo fijo (`PersonaSystemPrompt`) explica el bloque: adoptar el nombre, que lo
  más concreto prevalece (chat, después sesión, después general), que el último bloque sustituye a los
  anteriores y que nunca pasa por encima de las instrucciones de Enki, del ámbito, de las reglas sobre datos ni
  de lo que se pide en el mensaje actual. Las etiquetas del bloque se neutralizan dentro del texto como las de
  `<enki_context>`, para que un contexto no pueda cerrarlas.
- **Historial.** Los mensajes guardados son lo que escribió el usuario, sin el bloque; la recuperación de una
  sesión perdida del CLI vuelve a añadir la personalidad vigente.
- **Interfaz.** Con nombre, la bienvenida («Soy Ada. ¿En qué te ayudo?»), el texto del compositor, la etiqueta
  de cada respuesta y el botón de personalidad lo usan; sin él se mantiene «Asistente». El botón de la cabecera
  (sección y panel lateral, `AssistantPersonaButton`) muestra un punto cuando hay algún contexto activo y su
  menú enseña la capa general en solo lectura —se edita en Ajustes, para no tener dos editores del mismo
  texto— y edita las de sesión y chat con contador, guardar y descartar. El contexto de sesión no se puede
  editar si la sesión ya no está en el vault abierto, ni el de chat en un chat sin vault asociado.
- **Límites.** Nombre de 40 caracteres en una línea, contexto general de 4000 y contextos de sesión y chat de
  2000 cada uno (`AiPersona.Max*`), recortados sin partir un emoji.
- **Privacidad y persistencia.** `AiPersonaService` (expuesto como `AiChatService.Persona`) guarda en la misma
  carpeta privada que el historial y revierte el estado en memoria si el disco falla, para que la interfaz no
  enseñe algo que no se guardó; Ajustes y el menú muestran el error. El contexto de sesión vive en la
  instalación y no en el manifiesto de la sesión porque ese manifiesto se lee sin desbloquear las sesiones
  protegidas. La personalidad no está en `settings.json`, así que no entra en el paquete de diagnóstico ni en
  el perfil de personalización portable, y vaciar el historial no la borra. El texto se envía al proveedor
  elegido en cada mensaje, y así lo indica Ajustes.

## Arquitectura

```
models/ai/AiSettings.cs              preferencias (clave de instalación de SettingsService)
models/ai/AiChatSession.cs           conversación, mensajes y notas adjuntas tal como se guardan
services/ai/CliProcessRunner.cs      subproceso: argumentos con ArgumentList, stdin, stdout por líneas,
                                     inactividad, cancelación con Kill(entireProcessTree)
services/ai/ClaudeCliLocator.cs      ruta configurada → PATH → ~/.local/bin, ~/.claude/local, npm, Homebrew
services/ai/ClaudeStreamParser.cs    salida stream-json → eventos (sesión, fragmento, límite, resultado)
services/ai/ClaudeCliService.cs      detección (versión, flags, sesión) y ejecución de un turno
services/ai/CodexCliService.cs       cuenta de ChatGPT por CLI oficial, turnos efímeros JSONL
services/ai/OllamaService.cs         catálogo local, capacidades, streaming y herramientas por MCP
services/ai/GeminiApiService.cs      API de Gemini con la clave del usuario: catálogo, SSE, capacidad, herramientas
services/ai/EnkiMcpClient.cs         cliente MCP del vault para los proveedores que ejecutan su ciclo de herramientas
services/ai/AiBackendService.cs      selección de transporte y estado compartido
services/ai/AiPromptBuilder.cs       prompt de sistema + skill, contexto de notas, recuperación de historial
services/ai/AiChatService.cs         historial: un JSON por chat en %AppData%/Enki/ai/chats/, borradores de
                                     chats nuevos en ai/drafts.json
services/ai/AiApiKeyStore.cs         claves de API (Anthropic, Gemini, Brave Search) en el almacén seguro del sistema
services/ai/AiWebService.cs          acceso a internet: búsqueda (DuckDuckGo, Brave Search) y lectura de páginas
services/WebFetch.cs                 descarga acotada de páginas públicas, compartida con el web clipper
services/ai/AiFileReader.cs          lectura de archivos por formato, común a adjuntos y read_file (ver "Lectura de archivos")
services/PdfTextExtractor.cs         texto por página y páginas renderizadas de un PDF con PDFium (candado PdfiumGate)
services/SpreadsheetTextExtractor.cs XLSX y ODS como filas separadas por tabuladores, hoja a hoja
services/ai/ChatMarkdownParser.cs    subconjunto de Markdown para pintar las respuestas
controls/ChatMarkdownView.cs         render de esos bloques con los recursos del tema
viewmodels/ai/                       AssistantViewModel (+ .Composer, .Scope, .Effort, .Drafts, .Vault,
                                     .Activity, .NoteInsert, .Web), ClaudeCliStatusViewModel, filas y tarjetas
views/ai/AssistantView.axaml         sección Asistente
views/ai/AssistantPanelView.axaml    panel lateral; ambos componen los controles compartidos de views/ai/
viewmodels/shell/MainWindowViewModel.Assistant.cs  panel, ampliar/volver e indicador global
viewmodels/settings/SettingsViewModel.Ai.cs (+ .AiWeb) + sección "Ai" de SettingsView.axaml
```

- Composición manual en `MainWindowViewModel.Composition.cs`: `ClaudeCliService`, `AiChatService` y
  `ClaudeCliStatusViewModel` se crean siempre (no cuestan nada hasta que se usan) y el estado del CLI se
  comparte entre Ajustes y el Asistente.
- La sección es una entrada más de `SidebarSectionCatalog` (`"Assistant"`, `AppPage.Assistant`) cuyo flag
  de visibilidad **es** `AiSettings.Enabled`: el interruptor de Ajustes › Inteligencia artificial y la fila
  de Ajustes › Navegación son el mismo y pasan por el mismo aviso. `Ctrl+9` navega a ella solo si está
  encendida.
- `AiBackendService` selecciona los servicios concretos `ClaudeCliService`, `CodexCliService`,
  `GeminiApiService` y `OllamaService`, sin contenedor de DI ni jerarquías genéricas. Los contratos de turno y
  estado originales conservan sus nombres `Claude*` por compatibilidad y se reutilizan en los cuatro
  transportes. `AiBackendService.Models` expone el catálogo descubierto (Ollama, Gemini) a los selectores. Ajustes y el
  Asistente comparten una única instancia de este selector a través del estado compuesto por el shell.
- Los tests usan ejecutables falsos de los CLI, HTTP inyectable para Ollama y Gemini y el MCP real con
  servicios aislados. Comprueban streaming, cancelación, autenticación, historial, aislamiento y permisos del vault.

## Cada turno

```
claude -p --output-format stream-json --verbose --include-partial-messages
       --tools "" --strict-mcp-config --disable-slash-commands --safe-mode
       --system-prompt-file <temporal privado> [--model <alias>] [--resume <id>]
stdin  ← [<enki_context>notas</enki_context>] + mensaje
stdout → líneas JSON (init, fragmentos de texto, límite de uso, resultado)
```

- **Modo texto cuando no hay herramientas del vault disponibles:** sin herramientas integradas
  (`--tools ""`), sin MCP, sin comandos ni personalizaciones del usuario
  (`--safe-mode`: hooks, plugins, `CLAUDE.md`…). Claude solo devuelve texto; no puede leer el vault ni
  ejecutar nada, y el contenido de las notas se marca como datos, no instrucciones (defensa ante inyección
  de prompt). Los flags opcionales se activan solo si `claude --help` los anuncia; en un CLI sin `--tools`
  se vetan las herramientas con `--disallowedTools`.
- **Carpeta de trabajo** privada y vacía (`%AppData%/Enki/ai/claude-workspace`, `.devdata/` en Debug),
  fija porque el CLI agrupa sus conversaciones por carpeta y `--resume` solo encuentra las de la misma.
- **Prompt de sistema** en un temporal privado (`PrivateTempFile`) que se borra al terminar. Con un
  envoltorio `.cmd`/`.bat` de npm en Windows y un CLI sin `--system-prompt-file`, viaja por stdin para no
  pasar por la reinterpretación de `cmd.exe`.
- **Entorno**: se quitan las variables con las que Claude Code marca una sesión padre (`CLAUDECODE`,
  `CLAUDE_CODE_SESSION_ID`, socket de mensajes…) — si Enki se abrió desde una terminal de Claude Code el
  hijo se comportaría como su subagente — y credenciales de otros servicios (`OPENAI_*`, `SUPABASE_*`,
  tokens de GitHub). Las `ANTHROPIC_*` sí pasan: son configuración del usuario para su CLI.
- **Inactividad**: si el CLI no emite nada durante `IdleTimeoutSeconds` (180 s por defecto) se mata. Es un
  límite de silencio, no de duración: una respuesta larga que sigue llegando no se corta.
- **Log**: solo metadatos (duración, código de salida, caracteres enviados y recibidos, resultado). El
  contenido de prompts y respuestas nunca pasa por `LogService`.

## Historial y sesiones del CLI

- El panel de conversaciones se pliega con la flecha de su cabecera y se despliega desde la barra
  estrecha resultante. El chat gana ese espacio sin perder la conversación, el borrador ni la búsqueda.
  La barra conserva los accesos a nuevo chat y ajustes y el indicador del estado del CLI. El estado
  plegado se mantiene al navegar entre secciones durante la ejecución actual.

- Enki guarda su propia copia de cada conversación (`AiChatService`) para mostrar el historial sin leer
  los archivos internos del CLI. El CLI guarda la suya, que se retoma con `--resume <CliSessionId>`.
- Si el CLI ya no tiene la conversación ("No conversation found": su historial se borró, o el chat viene
  de otra instalación), `AssistantViewModel` abre una sesión nueva y le pasa el historial guardado en
  `<enki_previous_conversation>` (los mensajes más recientes que quepan, 60 000 caracteres). El usuario no
  ve ningún error.
- El historial vive en la instalación, no en el vault, igual que `AiSettings`: depende del CLI de este
  equipo y no debe viajar por Syncthing/Dropbox. Dentro de la instalación se separa por vault (ver "Historial
  por vault y borradores"). "Borrar historial" en Ajustes elimina la copia de Enki y los borradores, no la
  del CLI.

## Lectura de archivos

El asistente lee cualquier archivo que acepta la sección Archivos y del que se puede sacar contenido, con una sola
herramienta, `read_file`, y la misma lógica para los adjuntos (`AiFileReader`). Reutiliza los lectores que ya
usa el visor de Archivos y nunca interpreta ni ejecuta el contenido; los archivos de una sesión protegida se
leen descifrados en memoria con `VaultService.ReadFileBytes`, sin copias en disco.

| formato (`AiFileFormat`) | extensiones | qué recibe el modelo | límite |
| --- | --- | --- | --- |
| texto | código y texto plano (`VaultFileKind.Code`) | el contenido tal cual (sin BOM) | 1 MB |
| PDF | `.pdf` | texto por página con marcas `[page N]` (PDFium) y, si hace falta, páginas renderizadas | 64 MB |
| Word | `.docx`, `.docm`, `.dotx`, `.dotm` | texto con títulos `#`, listas, tablas `\| a \| b \|`, cuadros de texto, cabecera y pie, notas y comentarios (`OfficeTextExtractor`) | 32 MB |
| PowerPoint | `.pptx`, `.pptm`, `.potx`, `.potm`, `.ppsx`, `.ppsm` | `## Slide N: título` en el orden de la presentación, texto por nivel, tablas y notas del orador; las ocultas, marcadas | 32 MB |
| OpenDocument de texto o presentación | `.odt`, `.odp` | texto de párrafos (`ContainerPreviewService`) | 32 MB |
| hoja de cálculo | `.xlsx`, `.xlsm`, `.xltx`, `.xltm`, `.ods` | una sección por hoja con su rango y filas separadas por tabuladores | 32 MB |
| archivo comprimido | `.zip`, `.jar`, `.nupkg` | listado de rutas y tamaños, sin extraer nada | 32 MB |
| imagen | las raster del visor y `.svg` | la imagen reducida, en JPEG | 40 MB |
| desconocido | cualquier otra | texto, solo si su contenido es UTF-8 válido sin bytes nulos | 1 MB |
| audio y vídeo | las del visor | nada: no hay transcripción local; la herramienta lo explica | — |

**Gastar los tokens justos.**

- El texto se extrae compacto: en un PDF se quitan los caracteres de control y los espacios repetidos y queda
  como mucho una línea en blanco seguida; en un documento, igual. Una hoja de cálculo no repite la referencia de
  cada celda: cada fila empieza por su número y sigue con sus valores alineados por tabuladores desde la primera
  columna con datos (`## Ventas · A1:D120 · row number, then columns A-D`). Las filas vacías se omiten, las
  repetidas de ODS se escriben una vez, las fechas guardadas como número se escriben como fecha según el estilo
  de la celda (también en libros de 1904), las fórmulas aportan su último valor calculado, sin recalcular (o
  su texto, `=A1*2`, si el libro se guardó sin calcularla) y las hojas ocultas se marcan. En Word se omiten el
  texto borrado con control de cambios, los códigos de campo y las ramas `mc:Fallback`, que repetirían los cuadros
  de texto; la cabecera y el pie se escriben una vez aunque varias secciones los repitan.
- `read_file` devuelve tramos de `max_chars` (20 000 por defecto, hasta 60 000) con `total_chars` y, si queda
  más, `next_offset`; el corte cae en un salto de línea del último quinto del tramo para no partir una fila ni un
  párrafo. Un PDF se puede pedir por páginas (`pages`: `"3"`, `"2-5"`, `"1,4,7-9"`). El prompt de sistema le
  pide al modelo leer solo lo que la tarea necesita.
- La extracción completa (hasta 1 000 000 de caracteres) se guarda en una caché en memoria de cuatro entradas,
  con la ruta, el tamaño, la fecha y las páginas como clave: pedir el tramo siguiente no vuelve a procesar el
  archivo. Se vacía al cambiar de vault.
- Una imagen se envía con su lado mayor en 1024 px (`detail`: `low` 512, `high` 1568), orientada según EXIF, sin
  metadatos, con las transparencias sobre blanco y en JPEG: su coste en tokens depende de esos píxeles, no del
  tamaño del original. El resultado JSON lleva las dimensiones originales y las enviadas, y la fecha de captura
  EXIF si la hay. Un adjunto de imagen no se decodifica: solo se leen sus dimensiones.
- Una página de PDF sin capa de texto (escaneada o solo imagen) aparece como `[page N: no text layer]` y en
  `pages_without_text`. Si ninguna página pedida tiene texto se renderizan sin pedirlo, y con `as_images=true` se
  renderizan las pedidas, para ver gráficos o tablas; como mucho 3 por llamada.

**Cómo llegan las imágenes a cada proveedor.** `AiToolCall.Images` → `AiToolOutcome.Images` → el servidor MCP
añade un contenido `image` (base64) detrás del texto del resultado:

- Claude Code y Codex reciben el resultado MCP tal cual.
- Gemini: `EnkiMcpClient` devuelve las imágenes aparte y `GeminiApiService` las añade como partes `inlineData`
  del mismo turno, detrás de las respuestas de función.
- Ollama: se adjuntan en `images` del mensaje `tool` solo si `/api/show` anuncia la capacidad `vision`; si no,
  el modelo recibe los datos y una línea que dice que no puede ver imágenes.

**Permiso.** `read_file` es una lectura: necesita el permiso de consulta del chat, salvo para los archivos que
el usuario adjuntó al mensaje del turno (`AiToolContext.AttachedFiles`), que ya compartió él. Así el modelo
puede ver una imagen adjunta sin otra pregunta, y cualquier otro archivo sigue pidiéndolo.

**PDFium.** PDFium no admite llamadas concurrentes: el visor (`PdfRenderHelper`, a través de PDFtoImage) y la
lectura de texto (`PdfTextExtractor`, con llamadas directas a la misma biblioteca nativa) comparten el candado
`PdfiumGate.Sync`. Un PDF protegido con contraseña o dañado se rechaza con su motivo.

**Límites conocidos.** No hay OCR local: el texto de un escaneado solo lo lee un modelo con visión a partir de
la página renderizada. Los formatos antiguos de Office (`.doc`, `.xls`, `.ppt`), RAR y 7z no se leen. Las
hojas de cálculo no calculan fórmulas ni aplican formatos numéricos distintos de fecha. Que el modelo vea una
imagen depende de que el proveedor y el modelo elegidos tengan visión.

## Contexto, skills y salida

- **Adjuntos** (clip, "Adjuntar «nota activa»", o "Preguntar al asistente sobre esta nota" en la
  paleta): notas o cualquier archivo que el asistente sepa leer, hasta 5 por mensaje. El selector tiene dos
  pestañas, Notas y Archivos; el audio, el vídeo, los formatos binarios desconocidos y los que pasan del límite
  de su formato se listan deshabilitados con el motivo. Un archivo viaja como `<file>` dentro de
  `<enki_context>` con las mismas reglas que `read_file` (`AiFileReader`, ver "Lectura de archivos"): texto,
  PDF, documentos y hojas de cálculo como texto extraído; una imagen, como sus datos y los argumentos exactos
  de `read_file` para verla. Si al enviar un adjunto ya no se puede leer, su chip enseña el motivo y
  no se envía nada hasta retirarlo. Antes de leerlas se vuelcan los editores con cambios sin guardar. El
  presupuesto `MaxContextChars` (24 000 por defecto) se reparte a partes iguales y lo que una nota corta no
  usa pasa a las demás; lo recortado se marca en el chip. De un archivo solo se extrae su parte del
  presupuesto, y si no cabe entero el bloque termina con cuánto falta y con qué `offset` seguir. Se envía la
  ruta relativa al vault, nunca la
  absoluta. Las sesiones cifradas bloqueadas no aparecen en el selector.
- **Confirmación**: con notas adjuntas y `ConfirmBeforeSendingContext` (activo por defecto), un diálogo
  enseña qué notas y cuánto se enviará; el contenido que se aprueba es exactamente el que se envía.
- **Skills**: "Usar en el asistente" en el detalle de una skill, o el selector del compositor antes del
  primer mensaje. El cuerpo de la skill (sin frontmatter) se copia al chat y forma parte del prompt de
  sistema de toda la conversación, aunque la skill se edite después.
- **Salida**: la respuesta nunca se escribe sola en una nota. Acciones explícitas: copiar; "Guardar como
  nota nueva y abrirla" (crea una nota en la raíz del espacio del chat; nunca sobrescribe una existente); y
  "Añadir a «nota activa»", que enseña antes las líneas que se añaden al final de la nota abierta en
  Documentos y escribe por el mismo camino que las herramientas (`AiVaultToolbox.AppendToNote`: versión
  "Asistente" en el historial, `WriteContentChecked` contra lo previsualizado, pestaña actualizada). Si la
  nota cambió desde la vista previa no se escribe nada. No navega a la nota.
- **Reintentar o continuar**: si una respuesta interrumpida ya había completado acciones en el vault, el
  botón pasa a "Continuar" y el turno lleva `<enki_previous_attempt>` con esas acciones para que el modelo no
  las repita (`AiPromptBuilder.WithCompletedActions`).

## Estados y errores

| Estado del CLI | Qué ve el usuario |
|---|---|
| Comprobando | indicador de carga |
| No instalado | orden de instalación de su plataforma, guía oficial, "Comprobar de nuevo", ruta personalizada |
| Sin sesión | `claude auth login` para una terminal y "Comprobar de nuevo" |
| Versión antigua o inesperada | versión encontrada, mínima admitida (`2.0.0`) y orden para reinstalar |
| Listo | chat; en el pie del historial, cuenta y plan (`claude auth status`) |

Los fallos de un turno se clasifican (`ClaudeCliService.Classify`): límite de uso (con hora de reinicio si
el CLI la da), sesión no iniciada, modelo no disponible, saturación, inactividad, conversación perdida
(se recupera sola) y fallo genérico con el detalle del CLI. Se muestran bajo la conversación con
"Reintentar" (reenvía el mismo turno sin volver a leer las notas) y se notifican con
`NotificationService`, una sola vez por tipo hasta que vuelve a haber un turno correcto. Detener una
respuesta conserva lo que llegó, marcado como interrumpido.

## Ajustes

`AiSettings` (clave de instalación `Ai` en `settings.json`): `Enabled`, `ConsentAcceptedUtc`, `Provider`,
`ClaudeCliPath`, `ClaudeUseApi`, `DefaultModel` (alias de `claude --model`: `fable`, `opus`, `sonnet`,
`haiku`, o vacío para el del CLI), los modelos de cada proveedor (`OpenAiModel`, `LocalModel`, `GeminiModel`), `IdleTimeoutSeconds` (30–1800),
`MaxContextChars` (2 000–200 000), `ConfirmBeforeSendingContext`, `AllowReadAccessByDefault`, `AllowVaultChanges`, `PanelWidth`
(340–720) y `LastChatByVault`. `Normalize()` apaga la IA si falta el consentimiento.

### Claude con clave de API propia

En Ajustes › Inteligencia artificial, con Claude seleccionado, "Cómo se usa Claude" elige entre
**Suscripción** (la cuenta iniciada en Claude Code) y **Clave de API propia**. Es la misma integración con el
mismo CLI oficial, no un backend aparte: solo cambia la credencial con la que se lanza cada turno.

- La clave se guarda en el almacén seguro del sistema (`AiApiKeyStore` sobre `PolarCredentialStore` con su
  propia entrada: DPAPI, Keychain o Secret Service), nunca en `settings.json`, en el vault ni en el log. Solo
  se lee con ese modo activo, para que nadie que use su suscripción vea un aviso del llavero.
- Cada turno la pasa al proceso hijo como `ANTHROPIC_API_KEY` (`ClaudeCliService.BuildEnvironmentOverrides`)
  y quita las credenciales que competirían con ella (`ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CODE_OAUTH_TOKEN`); nunca
  viaja por argumentos ni por stdin.
- Sin clave guardada, el estado es "sin sesión" con `NeedsApiKey`: el asistente enseña "Añade tu clave de
  API de Anthropic" con acceso a Ajustes en vez de la orden de inicio de sesión. Una clave rechazada se
  explica con el mismo acceso. Ajustes valida la forma (`sk-ant-…`), enseña solo sus últimos caracteres y
  permite borrarla.
- Se eligió pasar la clave al CLI oficial en vez de llamar a la API de mensajes directamente: conserva el
  aislamiento, las herramientas MCP, `--effort` y `--resume` que ya existen y no añade dependencias. El uso
  se factura por API al usuario.

## Pruebas

- `Services/ClaudeStreamParserTests` (con líneas reales del CLI 2.1.x), `ClaudeCliServiceTests`
  (argumentos, flags, entorno, clasificación y turnos completos contra el CLI falso),
  `CliProcessRunnerTests` (eco, argumentos sin inyección, inactividad, cancelación, entorno),
  `ClaudeCliLocatorTests`, `AiPromptBuilderTests`, `AiChatServiceTests`, `ChatMarkdownParserTests`.
- `ViewModels/AssistantViewModelTests` (turnos, `--resume`, recuperación, confirmación, fallos y
  reintento, detener, skills, modelo, historial) y `SettingsViewModelAiTests` (apagado por defecto,
  consentimiento, persistencia fuera del vault).
- `enki.UITests/AssistantViewUiTests`: cada estado sobre el XAML real; con `ENKI_GRAPH_PREVIEW=<carpeta>`
  guarda capturas.
- Personalidad: `Services/AiPersonaServiceTests` (limpieza, límites, capas por vault y sesión, archivo
  dañado, reversión ante fallo de disco, vaciar historial), `AiPromptBuilderTests` (orden de capas, bloque
  ausente sin datos, etiquetas neutralizadas, párrafo del prompt de sistema), `ViewModels/AssistantViewModelPersonaTests`
  (nombre en la interfaz, capas que llegan al CLI falso y cambian entre turnos, borrador del chat nuevo,
  contexto compartido por los chats de una sesión) y `SettingsViewModelAiTests` (guardar con botón, fuera de
  `settings.json`, recarga desde otro sitio y error de guardado).
- `ViewModels/AssistantViewModelWorkspaceTests`: historial por vault y asociación de chats antiguos, cambio
  de vault, borradores por chat y por ámbito, adjuntos de archivos, "Añadir a la nota activa", indicador de
  actividad y permiso de consulta. `AiVaultToolboxTests` cubre la puerta de lectura (pregunta única,
  denegación por turno, cancelación) y `AppendToNote`; `ClaudeCliServiceTests` y `AiApiKeyStoreTests`, el
  modo de clave de API; `SettingsViewModelAiTests`, su gestión desde Ajustes.
- `Services/GeminiApiServiceTests`: clave ausente o rechazada, catálogo filtrado, clave solo en cabecera,
  SSE sin razonamiento, `finishReason` exigido, clasificación de errores y `retryDelay`, reintento sin
  capacidad, bloqueo de seguridad, firmas devueltas y herramientas por el MCP real con permisos en caliente.
  `AssistantViewUiTests` cubre el panel "Añade tu clave de API de Gemini" y Ajustes con Gemini.
- `enki.UITests/AssistantViewUiTests` incluye el panel lateral, la tarjeta de permiso y el aviso de chat sin
  vault.
- QA manual: secciones 8.9 y 8.10 de `qa-manual-release-candidate.md`.

## Fuera de alcance

- `codex app-server` (proceso persistente por JSON-RPC): evaluado y aplazado. El arranque de `codex exec` por
  turno es aceptable y `exec` es la interfaz con los flags de aislamiento que Enki exige.
- Varios chats respondiendo a la vez y subagentes: cada chat es una instancia independiente, pero solo una
  responde en cada momento.
- Niveles de capacidad `xhigh`/`max`: requieren una matriz de compatibilidad por modelo antes de exponerse.
- Cambiar la skill de un chat entre turnos: se fija antes del primer mensaje.
- Extraer tareas a Kanban o Polar ya lo hacen las herramientas del vault sobre las entidades reales; no hay
  un flujo aparte de propuestas.

## Acceso a internet

El asistente puede **buscar en internet y leer páginas web** con cualquiera de los cuatro proveedores. No usa la
búsqueda propia de cada proveedor (Claude Code, Codex o Gemini la tienen, Ollama no): son dos herramientas más
del catálogo de Enki, `web_search` y `web_fetch` (`AiVaultToolbox.Web.cs`), servidas por el mismo MCP y con las
mismas reglas de actividad, confirmación y cancelación que las del vault. Así el comportamiento, los permisos y
lo que se ve en la respuesta son iguales con todos, y Enki decide qué se puede abrir.

### Cómo se activa

- **Por chat**, con el botón del globo del compositor ("Web"). Encendido, el chip sube de tinta y el icono cambia;
  el estado se guarda en `AiChatSession.WebAccess` y se recupera al reabrir el chat. Se aplica en caliente: el
  servidor lo lee en cada llamada (`AiToolContext.WebAccess`), así que apagarlo durante una respuesta impide la
  búsqueda siguiente. Solo aparece con herramientas disponibles (en modo texto el modelo no puede llamar a nada).
- **Chats nuevos**: Ajustes › Inteligencia artificial › Acceso a internet › "Internet en los chats nuevos"
  (`AiSettings.WebAccessForNewChats`, apagado por defecto). Un chat por empezar sigue a ese ajuste mientras el
  usuario no toque el globo.
- Es independiente del **permiso de consulta** del vault: buscar en internet no lee el vault, y leer el vault no
  da acceso a internet. El bloque `<enki_scope>` de cada turno dice "Web: on/off", y el resumen del ámbito bajo
  el compositor lo repite.

### Buscadores

`AiSettings.WebSearchEngine`:

- **DuckDuckGo** (predeterminado): sin cuenta ni clave, por su versión HTML (`html.duckduckgo.com`). Se descartan
  los anuncios y se deshacen sus enlaces de redirección. Si DuckDuckGo contesta con su verificación anti-robots,
  la herramienta lo dice ("no devolvió resultados ahora mismo") y Ajustes sugiere Brave Search.
- **Brave Search API** con la clave del usuario (nivel gratuito), en el almacén seguro (`AiApiKeyKind.BraveSearch`,
  entrada `brave-search`). Solo se lee con Brave elegido; viaja solo en la cabecera `X-Subscription-Token`. Clave
  ausente, rechazada (401/403) o límite (429) tienen su propio mensaje.

### Lectura de páginas

`AiWebService.FetchAsync` sobre `WebFetch`, la misma descarga acotada que el web clipper:

- Solo `http`/`https`, sin credenciales en la URL y solo **direcciones públicas**: las IP literales privadas se
  rechazan antes de conectar, y el handler de `RemoteImagePolicy` resuelve el nombre y conecta solo a direcciones
  públicas (cierra el DNS rebinding). Sin proxy del sistema, para que ese filtro vea la dirección real. El modelo
  no puede llegar a `localhost`, al router, a Ollama ni al propio MCP de Enki.
- Redirecciones seguidas a mano (5), sin bajar de `https` a `http`; 3 MB como máximo, cortados al leer; 20 s por
  operación. Solo tipos de texto (HTML, texto, Markdown, CSV, JSON, XML/RSS); un PDF o una imagen se rechazan
  con el motivo.
- El HTML pasa por `HtmlReadability.ExtractPage`: el artículo si lo hay y, si no (portadas, listados), todo el
  `body`; enlaces convertidos en absolutos (respetando `<base href>`) para poder seguirlos, imágenes fuera. El
  análisis va fuera del hilo de interfaz.
- Las páginas largas se devuelven por tramos (`start`, `max_chars`, `next_start`), cortando en un salto de línea.
  Las últimas ocho páginas se recuerdan diez minutos en memoria para pasar de tramo sin volver a descargarlas;
  nada se escribe en disco y la caché se vacía al apagar la IA, cambiar de vault o salir.

### Defensa ante instrucciones escondidas

Una página puede pedirle al modelo que meta contenido del vault en una URL suya. Por eso `web_fetch` abre sin
preguntar solo las direcciones **conocidas** del chat (`AiWebUrlSet`): las que el usuario escribió en sus mensajes,
las de los resultados de búsqueda, los enlaces de las páginas ya leídas y las páginas leídas en turnos anteriores.
Cualquier otra enseña la tarjeta de confirmación con la **dirección completa** y una explicación propia; denegar o
dejar pasar el tiempo no descarga nada. El prompt de sistema trata las páginas como datos de terceros, nunca como
instrucciones, y pide no poner contenido del vault ni datos personales en búsquedas ni URLs salvo petición expresa.

### Actividad y errores

"Buscar en internet" muestra lo buscado; "Leer página web", el título de la página, su dirección completa al pasar
el cursor y un botón para abrirla en el navegador (`UrlOpener`). Cada fallo llega al modelo con qué hacer y al
usuario con su motivo (`AiVaultToolbox.Describe(AiWebException…)`): dirección no válida o local, sin conexión,
tiempo agotado, error HTTP, contenido no legible, demasiado grande, demasiadas redirecciones, buscador no
disponible, clave ausente o rechazada y límite de búsquedas. Ni las consultas ni las direcciones pasan por el log.

Pruebas: `Services/AiWebServiceTests` (DuckDuckGo y Brave, clave solo en cabecera, errores, HTML a Markdown,
redirecciones a direcciones privadas, tipos y tamaños, caché, `ParseUrl`, `AiWebUrlSet`),
`Services/AiVaultToolboxWebTests` (interruptor en caliente, independencia del permiso de consulta, confirmación de
direcciones desconocidas, tramos, errores), `HtmlReadabilityTests.ExtractPage_*`, `AiPromptBuilderTests`,
`AssistantViewModelWorkspaceTests.WebAccess_*`, `SettingsViewModelAiTests.WebAccess_*` y, sobre el XAML real,
`AssistantViewUiTests.WebAccess_*` y `Settings_InternetAccess_*`. QA manual: sección 8.12 de
`qa-manual-release-candidate.md`.

## Herramientas del vault y ámbitos de conversación

El asistente dispone de un catálogo independiente del proveedor en `services/ai/vault/AiVaultToolbox.*`.
Cada herramienta describe su entrada JSON, su efecto (lectura, cambio recuperable o irreversible) y su
ámbito. Opera sobre `VaultService`, `SessionService`, `KanbanService`, `PolarTaskService`,
`ReminderService` y `CalendarAggregationService`; no mantiene otro almacén de notas o tareas.

- **Documentos:** listar, buscar, leer, crear, reemplazar, editar fragmentos, añadir texto, cambiar
  etiquetas/propiedades, renombrar, mover/copiar con adjuntos, consultar enlaces y enviar a la papelera.
- **Sesiones:** crear, renombrar, cambiar color y enviar a la papelera. Las herramientas de contenido
  aceptan el identificador de sesión como `workspace`. Contraseñas y recuperación siguen en la interfaz.
- **Carpetas y archivos:** listar, crear carpetas, renombrar, mover/copiar y enviar a la papelera;
  lectura acotada de archivos de texto. No interpreta binarios ni ejecuta archivos.
- **Kanban:** listar tableros y tarjetas, crear/renombrar tableros, crear/editar/mover tarjetas,
  gestionar subtareas y notas vinculadas, archivar y enviar a la papelera.
- **Polar:** listas, tareas y subtareas sobre las entidades sincronizadas existentes; solo con cuenta
  vinculada, con fechas, prioridades, recurrencia, etiquetas y completado.
- **Recordatorios y agenda:** crear, listar, reprogramar, completar y enviar a la papelera; consultar
  el calendario agregado en intervalos de hasta 62 días.
- **Papelera:** listar, restaurar y eliminar definitivamente elementos de los módulos anteriores.

El chip de la cabecera muestra el espacio del chat. Antes del primer mensaje permite elegir el vault o
una sesión desbloqueada; después el espacio queda fijado. El historial agrupa las conversaciones del
vault y las de cada sesión. Un chat de sesión no puede ampliar su acceso a otros espacios, aunque un
historial antiguo conserve `FullVaultAccess=true`: el servicio ignora ese valor para chats de sesión.
En un chat del vault se conserva el acceso opcional a sesiones sin protección. Siempre se excluyen las
sesiones protegidas ajenas, incluso desbloqueadas. Para trabajar con una sesión protegida hay que abrir
un chat dentro de ella. Los adjuntos respetan el mismo ámbito y se vuelven a comprobar al enviarlos.

El modelo inicial de instalaciones nuevas es Sonnet 5.5 (`claude-sonnet-5-5`); las preferencias existentes
se conservan. Cada chat tiene capacidad propia, inicialmente media, ajustable con un slider de tres
posiciones (ligera, media, alta). El nivel se pasa con `--effort` si el CLI anuncia ese flag; de lo
contrario el control está deshabilitado y explica cómo actualizar. Durante un turno queda deshabilitado.
La capacidad se guarda en `AiChatSession.Effort` y se recupera al reabrir el chat; los archivos antiguos
sin ese campo usan media. Ver la especificación de evolución de UX en
[Interfaz y agentes](../agent-docs/implement-ia/interfaz-ux-agentes-ia.md).

`AiSettings.AllowVaultChanges` permite pasar a solo lectura en caliente. Las herramientas de escritura
se ocultan y sus llamadas se rechazan. La actividad de cada respuesta muestra acción, destino y estado,
con acceso a las notas y tarjetas resultantes; se conserva en el historial aunque se interrumpa el turno.
Borrar definitivamente o sacar contenido de una sesión protegida exige una tarjeta de confirmación en
el chat. Denegar, detener o agotar cinco minutos cancela esa acción. La espera no dispara el timeout
de inactividad del CLI. Desactivar las escrituras durante la confirmación impide ejecutarla.

### Transporte y aislamiento

`EnkiMcpServer` expone el catálogo por MCP sobre HTTP en `127.0.0.1`, con puerto efímero y un token
aleatorio de 256 bits por turno. Rechaza peticiones sin autenticación, con `Origin` o con un `Host` no
local. Las operaciones se despachan al hilo de interfaz. El token se revoca al acabar el turno; apagar
la IA, cambiar de vault o salir detiene también el servidor.

Solo se activa con un CLI que anuncie `--tools`, `--mcp-config`, `--strict-mcp-config`,
`--allowedTools` y `--setting-sources`. Las herramientas integradas siguen desactivadas (`--tools ""`),
solo se permite `mcp__enki`, y no se cargan configuraciones del usuario. La configuración MCP con el
token va en un temporal privado que se elimina al terminar. Sin esas capacidades se mantiene el modo
texto anterior, indicado bajo el compositor. El catálogo y el servidor pueden reutilizarse con futuros
proveedores MCP; los cuatro proveedores usan este mismo catálogo y sus permisos.

Las rutas son relativas a cada espacio; se rechazan rutas absolutas, segmentos `..`, carpetas internas
y enlaces simbólicos. Una nota vinculada fuera del ámbito no revela título ni ruta. Los cambios de
contenido pasan por `WriteContentChecked`, con instantánea previa marcada como «Asistente»; reemplazar
una nota entera exige haberla leído completa en ese turno y conservar su huella. Las pestañas abiertas
se guardan antes de actuar y se actualizan después, sin resolver conflictos por cuenta del modelo.

Pruebas específicas: `Services/AiVaultToolboxTests` (servicios reales aislados, permisos, conflictos,
papelera, cancelación y autenticación MCP), `ClaudeCliServiceTests` (restricción de herramientas y
texto entre acciones) y `AssistantViewUiTests` (actividad y confirmación sobre el XAML real).


### Permisos por defecto e indicadores de carga

En Ajustes › Inteligencia artificial › Herramientas del vault, **Permitir consultas por defecto**
autoriza al asistente a buscar y leer dentro del ámbito de cada chat sin la petición inicial. Nace
apagado y se guarda en la instalación. Se aplica en caliente a chats nuevos y existentes; no amplía
sus espacios ni accede a sesiones protegidas ajenas. Una revocación explícita desde la cabecera del
chat prevalece sobre el ajuste y se conserva en el historial (`ReadAccessRevoked`). Al desactivar el
ajuste dejan de autorizarse las consultas heredadas; los permisos concedidos expresamente por chat
se conservan. **Permitir cambios en el vault** controla por separado la creación, edición,
organización y eliminación de notas, archivos, skills, tarjetas Kanban, tareas Polar y recordatorios.

Las herramientas `list_skills`, `read_skill`, `create_skill`, `write_skill` y `delete_skill` operan
sobre el mismo `SkillService` que la biblioteca. Solo se ofrecen en chats del vault; para Polar sigue
siendo necesaria una cuenta vinculada. Editar una skill exige leerla antes en ese turno y no pisa
cambios externos. Las skills empaquetadas no se sobrescriben ni eliminan. Borrar una skill es
irreversible y pide confirmación, igual que el borrado definitivo de otros elementos.

Los indicadores del historial, comprobación del proveedor, respuesta, actividad y botón del panel usan
`LoadingSpinner`: una pista circular completa y un arco que gira alrededor del centro, escalados
juntos a 10, 12, 14 o 22 px para evitar recortes y mantener visible la rueda completa. Los iconos
rotatorios de comprobación usan también un origen relativo explícito (`50%,50%`).

La selección de mensajes usa los dos recursos del tema: `SelectionBackgroundBrush` para el fondo y
`SelectionForegroundBrush` para el texto. El estilo de `SelectableTextBlock` cubre mensajes del usuario,
párrafos Markdown, títulos, listas, citas, enlaces, tablas y código, también al cambiar de tema en caliente.

Las acciones de cada respuesta se agrupan bajo una cabecera desplegable con el contador actualizado
en vivo. Nacen plegadas, también al reabrir el historial. Añadir o actualizar acciones durante un turno
no cambia el estado elegido por el usuario. El detalle mantiene los accesos a notas y tarjetas. Las
peticiones de permiso y las confirmaciones permanecen visibles fuera del bloque plegable.
