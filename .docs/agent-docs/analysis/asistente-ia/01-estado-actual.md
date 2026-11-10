# 01 · ESTADO ACTUAL DEL ASISTENTE

> Qué hace hoy el asistente de IA de Enki. Complementa la referencia técnica
> [`.docs/project-info/asistente-ia.md`](../../../project-info/asistente-ia.md); si algo no coincide con el
> código, manda el código.

**Fecha:** 2026-11-09 · rama `beta-v2.0`
**Método:** lectura de `enki/src/enki/services/ai/`, `viewmodels/ai/`, `views/ai/` y de la documentación. No se
ha ejecutado la aplicación; los recuentos de herramientas salen de las llamadas `Register(...)` del código.

---

## 1. RESUMEN EN CINCO LÍNEAS

- Chat con **cuatro proveedores**: Claude (CLI oficial), OpenAI (CLI de Codex), Gemini (clave de API) y Ollama (local).
- Apagado por defecto, con consentimiento explícito; historial, borradores y permisos **por vault**.
- Catálogo de **más de 50 herramientas** (MCP local) que operan sobre los servicios reales de Enki.
- Acceso a internet opcional por chat (`web_search`, `web_fetch`) con defensa ante inyección de prompt.
- Se usa desde la sección Asistente o desde un panel lateral junto a cualquier otra sección.

## 2. PROVEEDORES

| proveedor | transporte | autenticación | herramientas del vault |
| --- | --- | --- | --- |
| Claude | `claude -p` (CLI oficial, sin modificar) | suscripción del usuario o clave de API propia | sí, por MCP |
| OpenAI | `codex exec` efímero, sandbox de solo lectura | cuenta de ChatGPT o clave en el CLI | sí, por MCP |
| Gemini | HTTP directo a `generativelanguage.googleapis.com` | clave de AI Studio en el almacén seguro | sí, `functionDeclarations` → MCP |
| Ollama | HTTP a `localhost` (solo loopback) | ninguna | solo si el modelo anuncia `tools` |

Todos comparten historial, adjuntos, skills, ámbitos, permisos y el mismo catálogo; no hay una segunda
implementación del vault por proveedor.

## 3. SUPERFICIES DE USO

- **Sección Asistente** (`Ctrl+9`) con bienvenida centrada y sugerencias (resumir nota, extraer tareas, reescribir).
- **Panel lateral** de 340–720 px, abierto junto a cualquier sección (`Ctrl+Shift+9`, barra de título, paleta).
- **Entradas contextuales:** «Preguntar al asistente sobre esta nota», «Preguntar» en Sesiones, «Usar en el
  asistente» en una skill.
- **Indicador global** en la barra de título: trabajando, esperando decisión, respuesta lista.

## 4. QUÉ PUEDE HACER EL ASISTENTE (CATÁLOGO DE HERRAMIENTAS)

| módulo | lectura | cambio recuperable | irreversible (pide confirmación) |
| --- | --- | --- | --- |
| Notas | listar, buscar, leer, enlaces, resumen del espacio | crear, escribir, editar fragmento, añadir, renombrar, mover, etiquetas, propiedades | enviar a papelera |
| Carpetas y archivos | listar, leer texto acotado | crear, renombrar, mover, copiar | borrar |
| Sesiones | (vía `workspace`) | crear, renombrar, color | enviar a papelera |
| Kanban | tableros y tarjetas | crear/editar/mover tarjetas, subtareas, notas vinculadas, archivar | borrar tarjeta |
| Polar | listas y tareas | crear/editar tareas, listas, subtareas, recurrencia | borrar |
| Recordatorios y agenda | listar, agenda hasta 62 días | crear, reprogramar, completar | borrar |
| Skills | listar, leer | crear, escribir | borrar |
| Papelera | listar | restaurar | borrar definitivo, vaciar |
| Web | buscar, leer páginas | — | — |

## 5. SEGURIDAD Y CONTROL (LO QUE YA ESTÁ BIEN RESUELTO)

- **Ámbito por chat:** vault o una sesión; un chat de sesión no puede salir de ella; sesiones protegidas ajenas
  siempre excluidas.
- **Permiso de consulta** por chat antes de que busque o lea por su cuenta; revocable en caliente.
- **Interruptor «Permitir cambios en el vault»** que pasa a solo lectura en caliente.
- **Confirmación explícita** para lo irreversible y para URLs desconocidas; caducan a los cinco minutos.
- **Escrituras seguras:** `WriteContentChecked`, instantánea previa «Asistente» en el historial de versiones,
  reemplazo total solo tras haber leído la nota completa en ese turno.
- **MCP aislado:** `127.0.0.1`, puerto efímero, token de 256 bits por turno, rechazo de `Origin` y `Host` no locales.
- **Privacidad:** claves en el llavero del sistema; el contenido de prompts y respuestas no pasa por el log;
  web sin acceso a IP privadas (anti DNS-rebinding).

## 6. AYUDA AL USUARIO YA PRESENTE

- Personalidad por capas (general, sesión, chat) con nombre propio.
- Skills reutilizables como instrucciones de conversación.
- Capacidad de razonamiento por chat (ligera, media, alta).
- Salida controlada: copiar, guardar como nota, añadir a la nota activa con vista previa.
- Reintentar/continuar sin repetir acciones ya hechas; recuperación silenciosa si el CLI pierde la sesión.
- Errores clasificados con acción concreta (límite de uso, sin sesión, modelo no disponible…).

## 7. LÍMITES CONOCIDOS (BASE PARA LAS PROPUESTAS)

| límite | consecuencia |
| --- | --- |
| No hay herramientas de **navegación**: el asistente no puede abrir una nota, tablero o sección | responde con texto, el usuario debe ir a buscarlo |
| No ve **qué está haciendo el usuario** (nota activa, selección, sección) salvo adjuntos manuales | pide contexto que la app ya tiene |
| Búsqueda solo **léxica** (`search_notes`); sin embeddings | falla con sinónimos y preguntas de significado |
| Sin herramientas para **hábitos, notas diarias, plantillas, etiquetas globales, estadísticas, grafo, exportación** | esos módulos son invisibles para él |
| Sin **deshacer por turno**: la instantánea existe por nota, pero no hay botón «revertir lo que hizo» | revertir exige ir nota a nota |
| Sin **imágenes ni PDF** (se rechazan en adjuntos) | no sirve para apuntes escaneados o capturas |
| Una sola respuesta a la vez; sin tareas en segundo plano ni proactividad | solo reacciona a mensajes |
| Sin **contador de uso o coste** visible | el usuario con clave de API no sabe cuánto gasta |
| El asistente **no conoce Enki** como producto (atajos, ajustes, dónde está cada cosa) | no puede enseñar a usar la app |
| Modelos Ollama sin `tools` solo funcionan en modo texto | experiencia desigual entre proveedores |
