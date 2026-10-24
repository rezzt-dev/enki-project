# Asistente de IA con Claude Code

> Referencia viva del módulo de IA. Concreta, para Claude, la propuesta de
> `.docs/agent-docs/implement-ia/implementacion-ia-desde-cero.md`. Si algo de aquí deja de coincidir con
> el código, el código manda.

## Resumen

La sección **Asistente** es un chat con Claude dentro de Enki, con historial de conversaciones, notas del
vault como contexto y skills como instrucciones de partida. No habla con la API de Anthropic ni con
ningún endpoint privado: lanza como subproceso **el Claude Code (CLI oficial) que el propio usuario
instaló y en el que inició sesión**, en modo no interactivo (`claude -p`), le pasa el mensaje por stdin y
lee la respuesta en streaming. La IA está **apagada por defecto**, no se enciende sin aceptar un aviso de
privacidad y términos, y apagada no ejecuta nada.

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

Todo esto se explica en **Ajustes › Inteligencia artificial › Cómo se usa Claude** (visible también con la
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
- No hay interfaz `IAiBackend`: hoy existe un único backend real. Los tests usan un ejecutable `claude`
  falso (script de shell, `tests/enki.Tests/Services/FakeClaudeCli.cs`) en vez de un doble en memoria, así
  que el camino probado es el mismo que en producción. Si se añaden Codex u Ollama, ese es el momento de
  extraer la interfaz que propone la guía.

## Cada turno

```
claude -p --output-format stream-json --verbose --include-partial-messages
       --tools "" --strict-mcp-config --disable-slash-commands --safe-mode
       --system-prompt-file <temporal privado> [--model <alias>] [--resume <id>]
stdin  ← [<enki_context>notas</enki_context>] + mensaje
stdout → líneas JSON (init, fragmentos de texto, límite de uso, resultado)
```

- **Sin herramientas** (`--tools ""`), sin MCP, sin comandos ni personalizaciones del usuario
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
con las entidades reales. Ver las fases 2 y 3 de la guía de implementación.
