# ÍNDICE · ANÁLISIS DEL ASISTENTE DE IA

> Punto de entrada. Qué hace hoy el asistente de Enki, cuánto aprovecha la app y qué se puede añadir, con
> prioridad, dificultad y tiempo estimado.

**Fecha:** 2026-11-09 · rama `beta-v2.0`
**Método:** lectura de `enki/src/enki/services/ai/`, `viewmodels/ai/`, `views/ai/` y de la documentación. No se ha
ejecutado la aplicación. Si algo no coincide con el código real, manda el código.

## DOCUMENTOS

| orden | fichero | para qué sirve |
| --- | --- | --- |
| 1 | [`01-estado-actual.md`](./01-estado-actual.md) | qué hace hoy: proveedores, superficies, herramientas, seguridad y límites |
| 2 | [`02-uso-nativo-y-ayuda.md`](./02-uso-nativo-y-ayuda.md) | cobertura por módulo y valor de ayuda real al usuario |
| 3 | [`03-propuestas-priorizadas.md`](./03-propuestas-priorizadas.md) | 22 propuestas por prioridad, dificultad y tiempo |
| 4 | [`04-fichas-tecnicas.md`](./04-fichas-tecnicas.md) | enfoque técnico de las más importantes |

Referencia técnica previa (viva): [`.docs/project-info/asistente-ia.md`](../../../project-info/asistente-ia.md).

## EVOLUCIÓN POSTERIOR

AIA-17, AIA-18, AIA-20 y AIA-22 ya tienen implementación y pruebas. Los detalles y límites actuales
figuran en [la referencia técnica](../../../project-info/asistente-ia.md): ajustes visuales confirmados,
capas y exportación de calendario, turnos simultáneos y batería sintética con resultados locales.
El análisis que sigue conserva el estado de su fecha original.

## CONCLUSIONES EN BREVE

- **Fortaleza:** operador de datos muy completo y seguro (más de 50 herramientas, ámbitos, permisos, confirmaciones,
  escrituras comprobadas) y cuatro proveedores con paridad, incluido uno local.
- **Debilidad:** casi no ve ni controla la interfaz. No conoce la nota activa, no navega, no explica la app y no
  alcanza hábitos, diarias, plantillas, etiquetas globales ni estadísticas.
- **Recomendación:** empezar por las seis propuestas de prioridad alta (≈ 14–21 jornadas), que cierran esos huecos
  casi sin arquitectura nueva; después deshacer por lotes y búsqueda semántica.

## RESUMEN DE FUNCIONALIDADES (2 LÍNEAS CADA UNA)

### Prioridad alta

- **AIA-01 · Contexto automático** *(baja, 2–3 d)* — Envía la nota activa y la selección sin adjuntarlas a mano, solo si se activa.
  Respeta ámbito y sesiones protegidas.
- **AIA-02 · Navegación** *(baja, 2–3 d)* — El asistente abre notas, tableros o secciones en la interfaz.
  Sin cambiar datos ni pedir confirmación.
- **AIA-03 · Ayuda sobre Enki** *(baja, 3–4 d)* — Manual y atajos reales consultables por el asistente.
  Contesta «¿cómo hago X?» sin tocar el vault.
- **AIA-04 · Hábitos, diarias y plantillas** *(baja, 3–5 d)* — Herramientas para los módulos hoy invisibles.
  Escribe por las rutas seguras ya existentes.
- **AIA-05 · Uso y coste** *(baja, 2–3 d)* — Tokens por respuesta y total por chat cuando el proveedor los da.
  Útil con clave de API propia.
- **AIA-06 · Deshacer por turno** *(media, 4–6 d)* — «Revertir esta respuesta» restaura lo que cambió el asistente.
  No pisa ediciones posteriores del usuario.

### Prioridad media

- **AIA-07 · Cambios por lotes** *(media, 5–7 d)* — Cola de cambios con diff para aprobarlos juntos.
  Control sin interrumpir cada acción.
- **AIA-08 · Etiquetas y estadísticas** *(baja, 3–4 d)* — Renombrar o fusionar etiquetas y consultar el progreso.
  Responde «¿cómo voy esta semana?».
- **AIA-09 · Exportar conversación** *(baja, 1–2 d)* — Guarda el chat entero como nota Markdown.
  Hoy solo se guarda la última respuesta.
- **AIA-10 · Prompts y barra `/`** *(baja, 3–5 d)* — Lista de prompts y skills al escribir `/`.
  Acelera tareas repetidas sin almacén nuevo.
- **AIA-11 · Acciones sobre la selección** *(media, 6–8 d)* — Resumir, reescribir o corregir el texto seleccionado con vista previa.
  Depende de AIA-01.
- **AIA-12 · Resumen diario y semanal** *(media, 4–6 d)* — Briefing con agenda, tareas vencidas y notas recientes, bajo demanda.
  Base de la proactividad.
- **AIA-13 · Relacionadas y enlaces sugeridos** *(media, 5–7 d)* — Propone wikilinks y notas afines con explicación.
  Mejora con la búsqueda semántica.
- **AIA-14 · Memoria del usuario** *(media, 5–7 d)* — Notas que el asistente recuerda entre chats, visibles y editables.
  Guardadas en la instalación, no en el vault.
- **AIA-15 · Imágenes y PDF** *(alta, 8–12 d)* — Adjuntos con visión o extracción local de texto.
  Avisa de qué proveedor recibe la imagen.
- **AIA-16 · Búsqueda semántica local** *(alta, 10–15 d)* — Índice de embeddings con Ollama, actualizado con los cambios del vault.
  Resuelve sinónimos sin enviar nada a la nube.

### Prioridad baja

- **AIA-17 · Ajustes por el asistente** *(media, 5–7 d)* — Cambia ajustes de una lista blanca segura, con confirmación.
  Nunca toca claves ni privacidad.
- **AIA-18 · Calendario completo** *(media, 4–6 d)* — Capas, calendarios externos y exportación `.ics`.
  Completa `get_agenda`.
- **AIA-19 · Dictado por voz** *(media, 7–10 d)* — Micrófono con reconocimiento local que rellena el compositor.
  Sin enviar audio a la nube sin aviso.
- **AIA-20 · Chats en paralelo** *(alta, 8–12 d)* — Varias respuestas a la vez con turno y token propios.
  Exige aislar el estado de `AssistantViewModel`.
- **AIA-21 · Proactividad y rutinas** *(alta, 12–18 d)* — Rutinas programadas con la app abierta y aviso del resultado.
  Con permisos acotados y tope de coste.
- **AIA-22 · Evaluación de calidad** *(media, 5–6 d)* — Pruebas de tareas típicas que puntúan cada proveedor y modelo.
  Resultados solo en local.

## ORDEN RECOMENDADO

1. **Oleada 1 (≈ 1–2 semanas):** AIA-01, 02, 03, 05, 09.
2. **Oleada 2 (≈ 2 semanas):** AIA-04, 06, 08, 10.
3. **Oleada 3 (≈ 3–4 semanas):** AIA-07, 11, 12, 14.
4. **Oleada 4 (según demanda):** AIA-16 y AIA-15, después AIA-13.
5. **Más adelante:** AIA-17 a AIA-22.
