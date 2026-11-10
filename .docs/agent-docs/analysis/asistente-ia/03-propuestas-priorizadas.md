# 03 · PROPUESTAS PRIORIZADAS PARA EL ASISTENTE

> Funcionalidades y complementos que se podrían añadir, ordenados **primero por prioridad** y **después por
> dificultad y tiempo**. Cada una se resume en dos líneas; el detalle de las más grandes está en
> [`04-fichas-tecnicas.md`](./04-fichas-tecnicas.md).

**Fecha:** 2026-11-09 · rama `beta-v2.0`

## CRITERIOS

- **Prioridad:** valor para el usuario × hueco detectado en [`02`](./02-uso-nativo-y-ayuda.md) × riesgo de no hacerlo.
- **Dificultad:** `baja` (extiende código existente) · `media` (pieza nueva acotada) · `alta` (arquitectura, datos o
  varios módulos).
- **Tiempo:** jornadas de una persona conocedora del código, con pruebas y documentación. Son estimaciones, no
  compromisos; no incluyen revisión de QA manual.
- Toda propuesta debe respetar MVVM, composición manual, `WriteContentChecked`, ámbitos, permisos y confirmaciones
  ya existentes, y no crear almacenes paralelos.

---

## TABLA GLOBAL (ORDEN DE IMPLEMENTACIÓN SUGERIDO)

| id | funcionalidad | prioridad | dificultad | tiempo |
| --- | --- | --- | --- | --- |
| AIA-01 | Contexto automático: nota activa y selección | alta | baja | 2–3 d |
| AIA-02 | Herramientas de navegación (abrir en la interfaz) | alta | baja | 2–3 d |
| AIA-03 | Ayuda integrada sobre Enki | alta | baja | 3–4 d |
| AIA-04 | Herramientas para hábitos, notas diarias y plantillas | alta | baja | 3–5 d |
| AIA-05 | Contador de uso y coste | alta | baja | 2–3 d |
| AIA-06 | Deshacer por turno | alta | media | 4–6 d |
| AIA-07 | Revisión y aprobación de cambios por lotes | media | media | 5–7 d |
| AIA-08 | Herramientas de etiquetas globales y estadísticas | media | baja | 3–4 d |
| AIA-09 | Exportar conversación a nota | media | baja | 1–2 d |
| AIA-10 | Prompts guardados y barra `/` | media | baja | 3–5 d |
| AIA-11 | Acciones rápidas sobre la selección del editor | media | media | 6–8 d |
| AIA-12 | Resumen diario y semanal bajo demanda | media | media | 4–6 d |
| AIA-13 | Notas relacionadas y enlaces sugeridos con IA | media | media | 5–7 d |
| AIA-14 | Memoria persistente del usuario | media | media | 5–7 d |
| AIA-15 | Imágenes y PDF como adjuntos | media | alta | 8–12 d |
| AIA-16 | Búsqueda semántica local | media | alta | 10–15 d |
| AIA-17 | Edición de ajustes por el asistente (con confirmación) | baja | media | 5–7 d |
| AIA-18 | Herramientas de calendario completas (capas, ICS) | baja | media | 4–6 d |
| AIA-19 | Dictado por voz | baja | media | 7–10 d |
| AIA-20 | Varios chats en paralelo | baja | alta | 8–12 d |
| AIA-21 | Tareas programadas y proactividad | baja | alta | 12–18 d |
| AIA-22 | Evaluación local de calidad por proveedor | baja | media | 5–6 d |

**Total orientativo:** alta ≈ 14–21 d · media ≈ 42–61 d · baja ≈ 37–54 d.

---

## PRIORIDAD ALTA

> Cierran los huecos que más se notan en el uso diario y casi todos reutilizan piezas que ya existen.

### AIA-01 · CONTEXTO AUTOMÁTICO: NOTA ACTIVA Y SELECCIÓN
*baja · 2–3 d*
Incluye, si el usuario lo activa, la ruta de la nota abierta y el texto seleccionado en un bloque `<enki_view>` de cada turno.
Evita adjuntar a mano lo que ya está a la vista y respeta ámbito, permiso de consulta y sesiones protegidas.

### AIA-02 · HERRAMIENTAS DE NAVEGACIÓN
*baja · 2–3 d*
Herramientas `open_note`, `open_kanban_board` y `open_section` que despachan al hilo de interfaz sin cambiar datos.
El chat deja de decir «está en tal nota» y pasa a ofrecer el enlace que la abre; efecto `Read`, sin confirmación.

### AIA-03 · AYUDA INTEGRADA SOBRE ENKI
*baja · 3–4 d*
Herramienta `search_help` sobre un manual empaquetado (atajos, módulos, ajustes) generado desde `.docs/project-info/` y `ShortcutCatalog`.
Permite preguntar «¿cómo hago X?» y recibir pasos exactos del idioma y versión de la app, sin enviar el vault.

### AIA-04 · HÁBITOS, NOTAS DIARIAS Y PLANTILLAS
*baja · 3–5 d*
Herramientas sobre `HabitService`, `DailyNoteService` y `NoteTemplateService`: consultar, marcar hábito, abrir o añadir a la nota del día, crear desde plantilla.
Cierra los módulos hoy invisibles; las escrituras pasan por las mismas rutas seguras que `append_to_note`.

### AIA-05 · CONTADOR DE USO Y COSTE
*baja · 2–3 d*
Muestra tokens por respuesta y total por chat cuando el proveedor los informa (API de Gemini, Claude y Codex en su resultado final).
Aporta transparencia a quien usa clave propia; opcional y sin guardar contenido, solo cifras.

### AIA-06 · DESHACER POR TURNO
*media · 4–6 d*
Botón «Revertir esta respuesta» que restaura las notas tocadas desde su instantánea «Asistente» y devuelve tarjetas o recordatorios a su estado previo.
Solo revierte lo recuperable, avisa si el usuario editó después y nunca pisa cambios ajenos (`NoteDiskStamp`).

---

## PRIORIDAD MEDIA

> Mejoran calidad y comodidad; algunas son las bases de las funciones de prioridad baja.

### AIA-07 · REVISIÓN Y APROBACIÓN DE CAMBIOS POR LOTES
*media · 5–7 d*
Modo opcional en el que las escrituras del turno quedan en una cola con diff y se aplican juntas tras aprobarlas.
Da control sin interrumpir cada acción; se apoya en `TextDiffService` y en el modelo de confirmaciones actual.

### AIA-08 · ETIQUETAS GLOBALES Y ESTADÍSTICAS
*baja · 3–4 d*
Herramientas sobre `TagManagerService` (listar, renombrar, fusionar) y `StatisticsAggregationService` (solo lectura).
Permite limpiar etiquetas con lenguaje natural y responder «¿cómo voy esta semana?».

### AIA-09 · EXPORTAR CONVERSACIÓN A NOTA
*baja · 1–2 d*
Guarda todo el chat (o una selección de mensajes) como nota Markdown con propiedades de origen.
Hoy solo se guarda la última respuesta; reutiliza «Guardar como nota nueva».

### AIA-10 · PROMPTS GUARDADOS Y BARRA `/`
*baja · 3–5 d*
Escribir `/` en el compositor abre una lista de prompts y skills con vista previa y parámetros simples.
Acelera tareas repetidas sin crear otro almacén: usa las skills existentes.

### AIA-11 · ACCIONES RÁPIDAS SOBRE LA SELECCIÓN
*media · 6–8 d*
Menú contextual del editor con resumir, reescribir, corregir o traducir la selección y reemplazarla con vista previa.
Requiere AIA-01; el resultado se aplica con `WriteContentChecked` y se ve como diff antes de aceptar.

### AIA-12 · RESUMEN DIARIO Y SEMANAL BAJO DEMANDA
*media · 4–6 d*
Un botón o skill genera el briefing del día: agenda, tareas vencidas, recordatorios y notas editadas recientemente.
Usa solo lectura y las herramientas existentes; es el primer paso hacia la proactividad (AIA-21).

### AIA-13 · NOTAS RELACIONADAS Y ENLACES SUGERIDOS CON IA
*media · 5–7 d*
Propone wikilinks y notas afines a la actual con una explicación breve, que el usuario acepta o descarta.
Funciona primero con búsqueda léxica y mejora con AIA-16; el vault ya tiene un módulo de relacionadas que se puede aprovechar.

### AIA-14 · MEMORIA PERSISTENTE DEL USUARIO
*media · 5–7 d*
Notas cortas que el asistente recuerda entre chats (preferencias, proyectos), visibles, editables y borrables.
Extiende `AiPersonaService` y se guarda en la instalación, nunca en el vault; el usuario aprueba cada entrada.

### AIA-15 · IMÁGENES Y PDF COMO ADJUNTOS
*alta · 8–12 d*
Adjuntar capturas, fotos de apuntes y PDF a proveedores con visión; extracción de texto local para el resto.
Cambia el pipeline de adjuntos y el presupuesto de contexto; exige avisar de qué proveedor recibirá la imagen.

### AIA-16 · BÚSQUEDA SEMÁNTICA LOCAL
*alta · 10–15 d*
Índice de embeddings generado con un modelo local (Ollama), almacenado en la instalación y actualizado con `VaultChangeWatcher`.
Nueva herramienta `semantic_search`; resuelve sinónimos y preguntas por significado sin enviar el vault a la nube.

---

## PRIORIDAD BAJA

> Valiosas, pero dependen de las anteriores o atienden a un público menor.

### AIA-17 · EDICIÓN DE AJUSTES POR EL ASISTENTE
*media · 5–7 d*
Lista blanca de ajustes seguros (tema, zoom, módulos visibles) que el asistente propone y el usuario confirma.
Nunca toca claves, proveedores ni privacidad; cada cambio es reversible y se anuncia con `NotificationService`.

### AIA-18 · CALENDARIO COMPLETO
*media · 4–6 d*
Herramientas para capas, eventos de calendarios externos (solo lectura) y exportación `.ics`.
Complementa `get_agenda` con lo que hoy solo se ve en la interfaz.

### AIA-19 · DICTADO POR VOZ
*media · 7–10 d*
Botón de micrófono con reconocimiento local (por ejemplo Whisper en Ollama o motor del sistema) que rellena el compositor.
Multiplataforma y sensible en permisos de audio; no envía audio a la nube sin aviso.

### AIA-20 · VARIOS CHATS EN PARALELO
*alta · 8–12 d*
Cada chat con su turno, servidor MCP y token propios, y el indicador global agregando estados.
Hoy `AssistantViewModel` es una instancia con un turno a la vez; hay que aislar estado y cancelación por chat.

### AIA-21 · TAREAS PROGRAMADAS Y PROACTIVIDAD
*alta · 12–18 d*
Rutinas con horario (briefing, repaso de tareas) ejecutadas con la app abierta y resultados como notificación.
Exige permisos permanentes acotados, límites de coste y una política clara de qué puede hacer sin supervisión.

### AIA-22 · EVALUACIÓN LOCAL DE CALIDAD POR PROVEEDOR
*media · 5–6 d*
Batería de pruebas de tareas típicas (crear tarjeta, editar nota) que puntúa cada proveedor y modelo.
Ayuda a recomendar modelos Ollama con `tools` fiables; sin telemetría, resultados solo en local.

---

## DEPENDENCIAS ENTRE PROPUESTAS

```
AIA-01 ──► AIA-11            AIA-16 ──► AIA-13 (mejora)
AIA-06 ──► AIA-07            AIA-12 ──► AIA-21
AIA-14 ──► AIA-21 (opcional) AIA-15 ──► (independiente; condiciona al proveedor)
```

## RIESGOS TRANSVERSALES

- **Superficie de ataque:** cada herramienta nueva debe declarar efecto (`Read`/`Change`/`Irreversible`) y ámbito.
- **Inyección de prompt:** contenido de notas y web sigue siendo dato; la memoria (AIA-14) no puede cargarse sin aprobación.
- **Paridad entre proveedores:** si una función necesita visión o herramientas, debe degradarse con un mensaje claro.
- **Idioma:** textos nuevos en el sistema i18n (`es-ES` con `ñ`, y su par en inglés).
