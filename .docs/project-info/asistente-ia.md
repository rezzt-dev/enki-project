# Asistente de IA: Claude, OpenAI y modelos locales

> Referencia viva del módulo de IA. Concreta, para los proveedores implementados, la propuesta de
> `.docs/agent-docs/implement-ia/implementacion-ia-desde-cero.md`. Si algo de aquí deja de coincidir con
> el código, el código manda.

## Resumen

La sección **Asistente** permite elegir Claude, OpenAI mediante la cuenta de Codex o modelos locales
servidos por Ollama. Comparte historial, notas adjuntas, skills, ámbitos y herramientas del vault.
La IA está **apagada por defecto**, exige aceptar el aviso de privacidad y términos y, apagada, no ejecuta
comprobaciones ni envía contenido. Claude conserva su integración mediante el CLI oficial (`claude -p`).

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
sandbox de solo lectura, búsqueda web y herramientas de shell desactivadas. No se carga la configuración
personal de MCP, reglas o instrucciones del proyecto. Se exige que el CLI anuncie los flags de aislamiento;
una versión antigua muestra el estado de actualización y no ejecuta el chat. El único MCP configurado es
el de Enki; el token efímero viaja por una variable del proceso hijo, nunca por argumentos. Se exigen tanto
el evento `turn.completed` como un código de salida correcto. Los mensajes del agente se muestran conforme
se completan; el razonamiento y los resultados internos de MCP no se pintan como respuesta.

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

Cada chat conserva `AiChatSession.Provider`; los archivos anteriores sin ese campo pertenecen a Claude.
Cambiar de proveedor abre otro chat; reabrir uno restaura su proveedor. Los modelos predeterminados se
guardan por proveedor, y los selectores se bloquean durante una respuesta. Codex y Ollama reconstruyen el
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
services/ai/AiBackendService.cs      selección de transporte y estado compartido
services/ai/AiPromptBuilder.cs       prompt de sistema + skill, contexto de notas, recuperación de historial
services/ai/AiChatService.cs         historial: un JSON por chat en %AppData%/Enki/ai/chats/
services/ai/ChatMarkdownParser.cs    subconjunto de Markdown para pintar las respuestas
controls/ChatMarkdownView.cs         render de esos bloques con los recursos del tema
viewmodels/ai/                       AssistantViewModel (+ .Composer), ClaudeCliStatusViewModel, filas
views/ai/AssistantView.axaml         sección Asistente
viewmodels/settings/SettingsViewModel.Ai.cs + sección "Ai" de SettingsView.axaml
```

- Composición manual en `MainWindowViewModel.Composition.cs`: `ClaudeCliService`, `AiChatService` y
  `ClaudeCliStatusViewModel` se crean siempre (no cuestan nada hasta que se usan) y el estado del CLI se
  comparte entre Ajustes y el Asistente.
- La sección es una entrada más de `SidebarSectionCatalog` (`"Assistant"`, `AppPage.Assistant`) cuyo flag
  de visibilidad **es** `AiSettings.Enabled`: el interruptor de Ajustes › Inteligencia artificial y la fila
  de Ajustes › Navegación son el mismo y pasan por el mismo aviso. `Ctrl+9` navega a ella solo si está
  encendida.
- `AiBackendService` selecciona los servicios concretos `ClaudeCliService`, `CodexCliService` y
  `OllamaService`, sin contenedor de DI ni jerarquías genéricas. Los contratos de turno y estado originales
  conservan sus nombres `Claude*` por compatibilidad y se reutilizan en los tres transportes. Ajustes y el
  Asistente comparten una única instancia de este selector a través del estado compuesto por el shell.
- Los tests usan ejecutables falsos de los CLI, HTTP inyectable para Ollama y el MCP real con servicios
  aislados. Comprueban streaming, cancelación, autenticación, historial, aislamiento y permisos del vault.

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

## Historial y sesiones

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
  equipo y no debe viajar por Syncthing/Dropbox. "Borrar historial" en Ajustes elimina la copia de Enki,
  no la del CLI.

## Contexto, skills y salida

- **Notas adjuntas** (clip, "Adjuntar «nota activa»", o "Preguntar al asistente sobre esta nota" en la
  paleta): hasta 5 por mensaje. Antes de leerlas se vuelcan los editores con cambios sin guardar. El
  presupuesto `MaxContextChars` (24 000 por defecto) se reparte a partes iguales y lo que una nota corta no
  usa pasa a las demás; lo recortado se marca en el chip. Se envía la ruta relativa al vault, nunca la
  absoluta. Las sesiones cifradas bloqueadas no aparecen en el selector.
- **Confirmación**: con notas adjuntas y `ConfirmBeforeSendingContext` (activo por defecto), un diálogo
  enseña qué notas y cuánto se enviará; el contenido que se aprueba es exactamente el que se envía.
- **Skills**: "Usar en el asistente" en el detalle de una skill, o el selector del compositor antes del
  primer mensaje. El cuerpo de la skill (sin frontmatter) se copia al chat y forma parte del prompt de
  sistema de toda la conversación, aunque la skill se edite después.
- **Salida**: la respuesta nunca se escribe sola en una nota. Acciones explícitas: copiar, o "Guardar como
  nota nueva" (crea una nota en la raíz del vault y la abre; nunca sobrescribe una existente).

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

`AiSettings` (clave de instalación `Ai` en `settings.json`): `Enabled`, `ConsentAcceptedUtc`,
`ClaudeCliPath`, `DefaultModel` (alias de `claude --model`: `fable`, `opus`, `sonnet`, `haiku`, o vacío
para el del CLI), `IdleTimeoutSeconds` (30–1800), `MaxContextChars` (2 000–200 000),
`ConfirmBeforeSendingContext` y `LastChatId`. `Normalize()` apaga la IA si falta el consentimiento.

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
- QA manual: sección 8.9 de `qa-manual-release-candidate.md`.

## Fuera de alcance (siguiente fase)

Backends de Codex y Ollama, modo con API key propia (almacén seguro del sistema), insertar una respuesta
en la nota abierta con diff (`TextDiffService` + `WriteContentChecked`) y extraer tareas a Kanban o Polar
con las entidades reales mediante propuestas revisables. La gestión directa del vault ya está conectada
por las herramientas descritas a continuación. Ver las fases 2 y 3 de la guía de implementación.

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
[Interfaz y agentes](../agent-docs/implement-ia/interfaz-ux-agentes-ia.md), que distingue estas mejoras
implementadas del panel lateral, permiso de búsqueda por chat e historial separado por vault pendientes.

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
proveedores MCP; los tres proveedores usan este mismo catálogo y sus permisos.

Las rutas son relativas a cada espacio; se rechazan rutas absolutas, segmentos `..`, carpetas internas
y enlaces simbólicos. Una nota vinculada fuera del ámbito no revela título ni ruta. Los cambios de
contenido pasan por `WriteContentChecked`, con instantánea previa marcada como «Asistente»; reemplazar
una nota entera exige haberla leído completa en ese turno y conservar su huella. Las pestañas abiertas
se guardan antes de actuar y se actualizan después, sin resolver conflictos por cuenta del modelo.

Pruebas específicas: `Services/AiVaultToolboxTests` (servicios reales aislados, permisos, conflictos,
papelera, cancelación y autenticación MCP), `ClaudeCliServiceTests` (restricción de herramientas y
texto entre acciones) y `AssistantViewUiTests` (actividad y confirmación sobre el XAML real).
