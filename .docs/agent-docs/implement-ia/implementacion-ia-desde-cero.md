# IMPLEMENTACION DE IA EN ENKI DESDE CERO

> guia para el desarrollador que vaya a conectar enki con un modelo de ia real. fecha de redaccion: 2026-10-24.
> estado: propuesta, nada de esto esta implementado todavia.

> Actualización 2026-10-25: el estado anterior describe la propuesta original. Ya existe un asistente
> con Claude Code y herramientas del vault. Para su estado real consultar
> [Asistente de IA](../../project-info/asistente-ia.md); para las decisiones confirmadas de producto,
> interfaz y aislamiento consultar [Interfaz y agentes](interfaz-ux-agentes-ia.md).

## RESUMEN

la ia en enki debe ser **opcional, apagada por defecto y sin claves gestionadas por la app**. la prioridad es usar la suscripcion de pago que el usuario ya tiene (claude pro/max o chatgpt plus/pro) en lugar de una api key con cobro por token. el camino viable no es llamar a los endpoints privados de esas suscripciones con tokens extraidos, sino **lanzar como subproceso el cli oficial que el usuario ya tiene instalado y autenticado**: `claude -p` para claude y `codex exec` para openai. enki nunca ve ni guarda el token, solo envia el prompt por stdin y lee la respuesta. como tercera via, para quien quiera privacidad total o no tenga suscripcion, se habla con un servidor local de ollama (qwen, llama, etc.) por http en `localhost:11434`.

en codigo encaja sin romper la arquitectura: un `AiService` en `services/` (composicion manual en `MainWindowViewModel`, sin di) que delega en tres backends con la misma forma minima (cli de claude, cli de codex, ollama), una seccion nueva de ajustes con la ia apagada, y un boton "ejecutar con ia" sobre las skills que ya existen en `SkillService`. el resultado de la ia **nunca se escribe solo en una nota**: se muestra como vista previa o diff y la escritura pasa por `VaultService.WriteContentChecked`. **aviso importante de terminos**: anthropic prohibe usar los tokens oauth de cuentas free/pro/max en productos de terceros (incluido el agent sdk); openai si documenta "sign in with chatgpt" en codex. por eso el backend de claude debe tratarse como "usa tu cli ya instalado, bajo tu responsabilidad", con la api key como alternativa soportada, y hay que revisar los terminos vigentes antes de publicar (ver seccion 2).

## 1. CONTEXTO Y ENCAJE CON EL PROYECTO

- `.docs/project-info/propuesta-nuevas-funcionalidades.md` (propuesta 10) y `.docs/agent-docs/analysis/roadmap-publicacion-y-mejora-continua.md` (prioridad cuatro, "ia acotada") ya piden una ia opt-in, con clave propia o endpoint local tipo ollama. este documento concreta como hacerlo.
- el modulo de skills (`SkillService`, archivos markdown con frontmatter yaml en `<vault>/.enki/skills/`) hoy solo son plantillas de prompt para pegar en un asistente externo. es el punto de entrada natural: el "ejecutar con ia" actua sobre una skill existente + el contexto que el usuario elija.
- reglas del proyecto que aplican (ver `AGENTS.md`):
  - mvvm estricto, sin contenedor di, composicion manual en `viewmodels/shell/MainWindowViewModel.cs`.
  - persistencia con el mismo patron de los demas servicios (`storageDirectory` opcional, `Save()` + evento `Action?`).
  - avisos al usuario solo con `_notificationService.Push(...)`.
  - escrituras al vault solo con `VaultService.WriteContentChecked`.
  - cero excepciones al sistema de diseno: reutilizar estilos de popover/borde existentes, `CornerRadius="0"`.
  - textos de interfaz por `localization/`, igual que el resto de la app.

## 2. COMO "USAR LOS TOKENS DE LA CUENTA DE PAGO" SIN API

una suscripcion de chat (claude pro/max, chatgpt plus/pro) y una api key son productos distintos con facturacion distinta. no existe una api publica que acepte "la cuenta de pago" directamente. lo que si existe son los **clientes oficiales** (`claude`, `codex`) que se autentican con la suscripcion del usuario y gastan de su cuota. el patron que propone este documento es el unico que mantiene a enki fuera del manejo de credenciales:

```
enki  --prompt por stdin-->  cli oficial (ya logueado por el usuario)  --> modelo
enki  <--texto/json por stdout--  cli oficial
```

### 2.1 claude (anthropic)

- el cli oficial es claude code; en modo no interactivo se invoca con `claude -p "<prompt>"` (flag `--output-format` para texto, json o stream-json).
- **restriccion de terminos (verificar antes de publicar)**: segun la documentacion legal de claude code, los tokens oauth obtenidos con cuentas free, pro o max no pueden usarse en otros productos, herramientas o servicios, agent sdk incluido; los productos de terceros deben usar api key de claude console o un proveedor cloud soportado. anthropic empezo a bloquear estos usos en clientes de terceros en enero de 2026 y actualizo la documentacion en febrero de 2026. anthropic ha dicho que anima al uso local y la experimentacion con `claude -p`.
- consecuencias de diseno:
  - enki **no** debe leer `~/.claude/` ni extraer el token oauth, ni usar el agent sdk con login de suscripcion.
  - lanzar el binario `claude` que el usuario instalo e inicio sesion es la opcion menos arriesgada, pero sigue siendo una zona gris para un producto distribuido. en la interfaz hay que decirlo claro ("usa tu instalacion local de claude code; consulta los terminos de anthropic").
  - ofrecer siempre en paralelo el modo **api key propia** (`ANTHROPIC_API_KEY` guardada en el almacen seguro del so, ver 5.2) como via plenamente soportada.
- decision pendiente para el responsable del proyecto: si tras revisar los terminos se considera demasiado arriesgado, el backend de claude por suscripcion se omite y se deja solo api key.

### 2.2 openai (chatgpt / codex)

- codex cli soporta oficialmente dos autenticaciones: "sign in with chatgpt" (suscripcion) y api key. la primera es la ruta por defecto del cli. para entornos sin navegador existe `codex login --device-auth`.
- no encontre documentacion oficial que diga que reenviar el token de suscripcion a una herramienta de terceros este permitido, asi que se aplica la misma regla: **enki lanza `codex exec` como subproceso y no toca el token**. la integracion mas robusta a futuro es `codex app-server`, el protocolo con el que codex se integra en editores; evaluarlo en la fase 3 una vez leida su especificacion en el repositorio `openai/codex`.
- no usar endpoints no documentados (por ejemplo la url interna de chatgpt que usa codex) desde codigo propio: pueden cambiar o bloquearse sin aviso.

### 2.3 ollama (local)

- ollama expone una api http en `http://localhost:11434`. `POST /api/chat` con `{"model":"qwen2.5:7b","messages":[...],"stream":true}` devuelve ndjson (una linea json por fragmento, con `message.content` y `done`).
- `GET /api/tags` lista los modelos instalados: sirve para poblar el selector de modelo y para detectar si el servidor esta levantado.
- modelos razonables para empezar: familia qwen (`qwen2.5`, `qwen3`), llama, mistral. tamanos de 7b a 14b funcionan en un portatil con 16 gb; avisar en la interfaz de que los modelos pequeños resumen bien pero razonan peor.
- cero credenciales y cero salida de datos de la maquina: es el backend recomendado para vaults sensibles.

## 3. ARQUITECTURA PROPUESTA

```
views/settings/AiSettingsView.axaml        ajustes de ia (apagada por defecto)
viewmodels/settings/AiSettingsViewModel.cs
viewmodels/ai/AiRunViewModel.cs            panel "ejecutar con ia": entrada, progreso, vista previa
models/AiSettings.cs                       poco: backend elegido, modelo, endpoint, timeouts
models/AiRequest.cs / AiResponse.cs        prompt de sistema, contexto, texto de salida, uso si existe
services/AiService.cs                      fachada: valida que este activada, construye prompt, elige backend
services/ai/IAiBackend.cs                  contrato minimo (ver nota)
services/ai/ClaudeCliBackend.cs
services/ai/CodexCliBackend.cs
services/ai/OllamaBackend.cs
services/ai/CliProcessRunner.cs            lanza el subproceso, stdin/stdout, timeout, cancelacion
services/ai/AiPromptBuilder.cs             junta skill + contexto + limites de tamaño
```

**nota sobre la interfaz**: `AGENTS.md` prohibe interfaces genericas sin motivo. aqui hay tres implementaciones reales con la misma operacion y hace falta un backend falso en los tests, igual que ya ocurre con `IPolarCredentialStore`. se mantiene minima:

```csharp
public interface IAiBackend
{
    string Id { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(AiRequest request, CancellationToken ct);
}
```

reglas:

- `AiService` se instancia en `MainWindowViewModel` junto al resto de servicios y se le pasan los callbacks (`_notificationService.Push` en fallos).
- sin `AiService.IsEnabled` a `true` no se ejecuta nada y los botones de ia ni se muestran: ningun flujo basico (notas, tareas, calendario) depende de ia.
- la salida se entrega en streaming (`IAsyncEnumerable<string>`) para que la interfaz muestre texto a medida que llega y el usuario pueda cancelar.

## 4. IMPLEMENTACION DE LOS BACKENDS

### 4.1 `CliProcessRunner` (comun a claude y codex)

- usar `System.Diagnostics.Process` con `RedirectStandardInput/Output/Error`, `UseShellExecute = false`, `CreateNoWindow = true`. nunca construir el comando como una sola cadena de shell: pasar argumentos con `ArgumentList` para que el texto de una nota no pueda inyectar nada.
- enviar el prompt **por stdin**, no como argumento: evita el limite de longitud de la linea de comandos (sobre todo en windows) y no deja el contenido de las notas visible en la lista de procesos.
- ejecutar con directorio de trabajo temporal y vacio (reutilizar `PrivateTempFile` o la logica de `PrivateDataPermissions`), para que un agente de codigo no vea ni modifique el vault.
- timeout configurable (por defecto 120 s) y `CancellationToken`: al cancelar, `Kill(entireProcessTree: true)`.
- leer stderr en paralelo; si el codigo de salida no es 0 convertirlo en un mensaje legible y registrar el detalle en `LogService` (sin incluir el contenido del prompt).
- resolver el binario: primero la ruta configurada por el usuario, si no, buscar `claude` / `codex` en `PATH`. en windows contemplar `.cmd`/`.exe`. si no existe, `IsAvailableAsync` devuelve `false` y la interfaz explica como instalarlo.

### 4.2 `ClaudeCliBackend`

- invocacion base: `claude -p --output-format stream-json` (o `text` para empezar), con el modelo elegido si el usuario fijo uno.
- **desactivar herramientas**: enki solo quiere texto, no un agente que edite archivos ni ejecute comandos. buscar en `claude --help` el flag vigente para restringir herramientas (`--allowedTools` / `--disallowedTools`) y fijarlo siempre. los flags cambian entre versiones: fijar una version minima probada y comprobar `claude --version` al detectar el cli.
- detectar sesion no iniciada: si el cli responde con error de autenticacion, mostrar "inicia sesion ejecutando `claude` en una terminal" en vez de intentar autenticar desde enki.
- modo alternativo con api key: llamar directamente a la api de mensajes con `HttpClient`, clave desde el almacen seguro. se implementa como una segunda configuracion del mismo backend, no como un cuarto backend.

### 4.3 `CodexCliBackend`

- invocacion base: `codex exec` en modo no interactivo, leyendo el prompt de stdin, con sandbox de solo lectura y sin requerir un repositorio git (revisar en `codex exec --help` los flags exactos: `--sandbox read-only`, `--skip-git-repo-check`, `--json` para salida estructurada).
- igual que con claude: no iniciar sesion desde enki. si falla por autenticacion, indicar `codex login` (o `codex login --device-auth` en maquinas sin navegador).
- fase posterior: sustituir el subproceso por `codex app-server` (proceso persistente, protocolo json-rpc por stdio) para evitar el coste de arranque en cada peticion.

### 4.4 `OllamaBackend`

- `HttpClient` unico reutilizado, `BaseAddress` = endpoint configurado (por defecto `http://localhost:11434`).
- `IsAvailableAsync`: `GET /api/tags` con timeout corto (2 s). si falla, notificar "ollama no responde" una sola vez por sesion, no en cada intento.
- `StreamAsync`: `POST /api/chat` con `stream: true`, leer el cuerpo linea a linea (`StreamReader.ReadLineAsync`), deserializar cada linea y emitir `message.content` hasta `done: true`.
- parametros utiles: `options.num_ctx` (ventana de contexto; el valor por defecto de ollama es pequeño y recorta notas largas), `options.temperature` baja (0.2-0.4) para resumenes y reescrituras.
- permitir endpoint remoto (otro equipo de la red) pero avisar de que el trafico va sin cifrar si no es `https`.

## 5. AJUSTES, PERSISTENCIA Y SECRETOS

### 5.1 `AiSettings`

se guarda en el `SettingsService` existente (json en `AppDataDirectory.Resolve()`), nunca dentro del vault, para que un vault sincronizado no arrastre configuracion de una maquina a otra:

```json
{
  "enabled": false,
  "backend": "ollama",
  "model": "qwen2.5:7b",
  "ollamaEndpoint": "http://localhost:11434",
  "claudeCliPath": "",
  "codexCliPath": "",
  "timeoutSeconds": 120,
  "maxContextChars": 24000,
  "confirmBeforeSend": true
}
```

- `enabled` nace en `false`. activar la ia muestra un dialogo de consentimiento que explica a donde viaja el texto en cada backend (nube de anthropic / openai o solo tu maquina).
- `confirmBeforeSend` muestra, antes de enviar, un resumen de lo que se manda (nombre de la skill, nota o seleccion, numero de caracteres).

### 5.2 api keys (modo alternativo)

si se implementa el modo api key, reutilizar el patron de `PolarCredentialStore`: almacen del sistema (dpapi en windows, keychain en macos, libsecret en linux), nunca en el json de ajustes ni en el vault, y nunca en `LogService`.

### 5.3 cuotas de suscripcion

una suscripcion tiene limites por ventana de tiempo y los comparte con el uso normal del usuario en la web o en el cli. enki debe:

- no lanzar peticiones automaticas ni en segundo plano: toda llamada es una accion explicita del usuario.
- reconocer los errores de limite alcanzado y mostrarlos con `_notificationService.Push(...)` en lugar de reintentar en bucle.

## 6. INTEGRACION CON LAS SKILLS Y EL VAULT

1. el usuario abre una skill (`SkillService`) y pulsa "ejecutar con ia".
2. `AiPromptBuilder` compone: contenido de la skill como instruccion + contexto elegido (nota activa, seleccion de texto, o nada) recortado a `maxContextChars` indicando al usuario si se ha truncado.
3. `AiService` elige el backend segun ajustes y empieza el streaming.
4. el panel muestra el texto a medida que llega, con boton de cancelar.
5. al terminar, las acciones son siempre explicitas: copiar, insertar en la nota abierta o reemplazar la seleccion. reutilizar `TextDiffService` para mostrar el diff cuando la accion modifica texto existente.
6. cualquier escritura sobre una nota pasa por `VaultService.WriteContentChecked`; si el disco cambio mientras la ia trabajaba, la escritura aborta y se avisa (ver `.docs/project-info/cambios-externos-en-el-vault.md`).

casos de uso para una primera version: resumir nota, reescribir seleccion, extraer tareas de una nota y proponer tags. dejar para despues "preguntar al vault" (requiere busqueda semantica) y sugerencia de enlaces.

## 7. SEGURIDAD Y PRIVACIDAD

- **inyeccion de prompt**: el contenido de una nota o de una pagina clipeada puede contener instrucciones maliciosas. por eso la ia solo produce texto que el usuario revisa; no se le da ninguna herramienta (ni de archivos ni de shell) y la salida nunca se ejecuta.
- **minimo necesario**: enviar solo la skill y el contexto elegido, nunca el vault entero ni rutas absolutas del equipo.
- **secretos**: no registrar prompts ni respuestas en `LogService`; registrar solo backend, duracion, codigo de salida y longitud.
- **entorno del subproceso**: limpiar variables de entorno innecesarias antes de lanzar el cli; no heredar claves de otros servicios.
- **transparencia**: la interfaz indica siempre que backend esta activo y si el texto sale de la maquina.
- **desactivacion limpia**: al apagar la ia se cancelan los subprocesos en curso y desaparecen los botones de ia.

## 8. PRUEBAS

xUnit en `enki/tests/enki.Tests/`, carpeta `Services/` (y `ViewModels/` para los viewmodels):

- `CliProcessRunner` contra un ejecutable de prueba trivial (por ejemplo un script que hace eco de stdin): salida correcta, timeout, cancelacion, codigo de salida distinto de cero, argumentos con comillas y saltos de linea sin inyeccion.
- `OllamaBackend` con un `HttpMessageHandler` falso que devuelve ndjson: fragmentos en orden, `done`, servidor caido, respuesta corrupta.
- `AiService` con un `IAiBackend` falso: ia desactivada no ejecuta nada, truncado de contexto, cancelacion, notificacion en fallos.
- `AiPromptBuilder`: limites de tamaño y orden de las secciones.
- ningun test debe tocar el disco real del desarrollador: usar el `storageDirectory` temporal, como el resto de servicios.

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

## 9. FASES DE IMPLEMENTACION

**fase 0 - decisiones previas**
- [ ] revisar los terminos vigentes de anthropic y openai sobre uso de suscripciones con clientes de terceros y decidir si el backend de claude por suscripcion se publica.
- [ ] fijar versiones minimas probadas de `claude`, `codex` y ollama.

**fase 1 - base y ollama (sin dependencias de cuentas)**
- [ ] `AiSettings` en `SettingsService`, seccion de ajustes de ia apagada por defecto y dialogo de consentimiento.
- [ ] `IAiBackend`, `AiService`, `AiPromptBuilder`, `OllamaBackend`.
- [ ] panel "ejecutar con ia" sobre una skill, con streaming, cancelacion y vista previa.
- [ ] cadenas en `localization/` y pruebas de la seccion 8.

**fase 2 - clis de suscripcion**
- [ ] `CliProcessRunner` + `CodexCliBackend` (la ruta con respaldo oficial mas claro).
- [ ] `ClaudeCliBackend` con herramientas desactivadas, o su omision segun la decision de la fase 0.
- [ ] deteccion de cli instalado y de sesion no iniciada, con mensajes de ayuda.

**fase 3 - refinamiento**
- [ ] modo api key con almacen seguro del sistema.
- [ ] evaluar `codex app-server`.
- [ ] acciones adicionales: extraer tareas hacia kanban/polar usando las entidades reales (`KanbanCard`, `PolarTask`), sin crear almacenes paralelos.
- [ ] entrada en `.docs/project-info/qa-manual-release-candidate.md` y documento de referencia viva en `.docs/project-info/`.

al cerrar cada fase: entrada en `## SIN PUBLICAR / UNRELEASED` de `CHANGELOG.md` (bilingue, categorias cerradas) y commits con el formato de `.docs/commit-guidelines.md`.

## 10. RIESGOS Y DECISIONES ABIERTAS

| riesgo | mitigacion |
|---|---|
| los terminos de anthropic prohiben el uso de tokens de suscripcion en productos de terceros | no tocar tokens; solo lanzar el cli del usuario; ofrecer api key; decision explicita en fase 0 |
| los flags de `claude` y `codex` cambian entre versiones | version minima probada, comprobacion al detectar el cli, mensaje claro si es incompatible |
| el usuario agota su cuota de suscripcion | solo acciones explicitas, nada en segundo plano, mensaje especifico de limite alcanzado |
| modelos locales de poca calidad | recomendar tamaño minimo en la interfaz y dejar `temperature` baja |
| envio accidental de contenido sensible | opt-in, consentimiento, `confirmBeforeSend`, contexto acotado |
| arranque lento de un cli por peticion | aceptable en fase 2; `codex app-server` en fase 3 |

## FUENTES

- [anthropic prohibe la autenticacion por suscripcion en terceros (gigazine)](https://gigazine.net/gsc_news/en/20260220-anthropic-third-party-block)
- [anthropic bans claude subscription oauth in third-party apps (winbuzzer)](https://winbuzzer.com/2026/02/19/anthropic-bans-claude-subscription-oauth-in-third-party-apps-xcxwbn/)
- [anthropic agent sdk confusion (the new stack)](https://thenewstack.io/anthropic-agent-sdk-confusion/)
- [authentication de codex (openai)](https://developers.openai.com/codex/auth)
- [codex cli (openai)](https://developers.openai.com/codex/cli)
- los terminos de anthropic y de openai citados arriba proceden de fuentes secundarias en parte: contrastar con la pagina oficial de legal y compliance de claude code y con la documentacion de codex antes de decidir.
