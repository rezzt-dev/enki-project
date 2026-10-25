# Asistente de IA: Claude, OpenAI y modelos locales

> Referencia viva del módulo de IA. Concreta, para los proveedores implementados, la propuesta de
> `.docs/agent-docs/implement-ia/implementacion-ia-desde-cero.md`. Si algo de aquí deja de coincidir con
> el código, el código manda.

## Resumen

La sección **Asistente** permite elegir Claude, OpenAI mediante la cuenta de Codex o modelos locales
servidos por Ollama. Comparte historial, notas adjuntas, skills, ámbitos y herramientas del vault.
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
  `AssistantComposerView`, `AssistantScopeButton`, `AssistantModelControls` y `AssistantHistoryList`. El panel
  los marca con la clase `compact`. Los diálogos (`AssistantDialogs`) los monta una sola vez el shell.
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
services/ai/AiChatService.cs         historial: un JSON por chat en %AppData%/Enki/ai/chats/, borradores de
                                     chats nuevos en ai/drafts.json
services/ai/AiApiKeyStore.cs         clave de API de Claude en el almacén seguro del sistema
services/ai/AiTextFiles.cs           reglas comunes para leer archivos de texto (adjuntos y read_text_file)
services/ai/ChatMarkdownParser.cs    subconjunto de Markdown para pintar las respuestas
controls/ChatMarkdownView.cs         render de esos bloques con los recursos del tema
viewmodels/ai/                       AssistantViewModel (+ .Composer, .Scope, .Effort, .Drafts, .Vault,
                                     .Activity, .NoteInsert), ClaudeCliStatusViewModel, filas y tarjetas
views/ai/AssistantView.axaml         sección Asistente
views/ai/AssistantPanelView.axaml    panel lateral; ambos componen los controles compartidos de views/ai/
viewmodels/shell/MainWindowViewModel.Assistant.cs  panel, ampliar/volver e indicador global
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

## Contexto, skills y salida

- **Adjuntos** (clip, "Adjuntar «nota activa»", o "Preguntar al asistente sobre esta nota" en la
  paleta): notas o archivos de texto, hasta 5 por mensaje. El selector tiene dos pestañas, Notas y Archivos;
  los archivos que no son texto (imágenes, PDF, multimedia) o pasan de 1 MB se listan deshabilitados con el
  motivo. Un archivo viaja como `<file>` dentro de `<enki_context>` con las mismas reglas que
  `read_text_file` (`AiTextFiles`). Si al enviar un adjunto ya no se puede leer, su chip enseña el motivo y
  no se envía nada hasta retirarlo. Antes de leerlas se vuelcan los editores con cambios sin guardar. El
  presupuesto `MaxContextChars` (24 000 por defecto) se reparte a partes iguales y lo que una nota corta no
  usa pasa a las demás; lo recortado se marca en el chip. Se envía la ruta relativa al vault, nunca la
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
`haiku`, o vacío para el del CLI), los modelos de cada proveedor, `IdleTimeoutSeconds` (30–1800),
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
- `ViewModels/AssistantViewModelWorkspaceTests`: historial por vault y asociación de chats antiguos, cambio
  de vault, borradores por chat y por ámbito, adjuntos de archivos, "Añadir a la nota activa", indicador de
  actividad y permiso de consulta. `AiVaultToolboxTests` cubre la puerta de lectura (pregunta única,
  denegación por turno, cancelación) y `AppendToNote`; `ClaudeCliServiceTests` y `AiApiKeyStoreTests`, el
  modo de clave de API; `SettingsViewModelAiTests`, su gestión desde Ajustes.
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
proveedores MCP; los tres proveedores usan este mismo catálogo y sus permisos.

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
