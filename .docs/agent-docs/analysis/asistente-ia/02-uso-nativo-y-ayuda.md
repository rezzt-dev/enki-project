# 02 · USO «NATIVO» DE LA APP Y AYUDA AL USUARIO

> Valoración de cuánto puede el asistente **usar Enki como lo haría una persona** y cuánto **ayuda a quien la
> usa**. Se apoya en [`01-estado-actual.md`](./01-estado-actual.md).

**Fecha:** 2026-11-09 · rama `beta-v2.0`

---

## 1. USO NATIVO: COBERTURA POR MÓDULO

Escala: **alta** = puede leer y actuar casi como el usuario · **media** = parcial · **nula** = no lo ve.

| módulo de Enki | cobertura | comentario |
| --- | --- | --- |
| Notas (Documentos) | **alta** | crear, editar por fragmentos, mover, etiquetar, propiedades, enlaces; escritura segura |
| Archivos y carpetas | **alta** | organización completa; solo lee texto, no binarios |
| Kanban | **alta** | tableros, tarjetas, subtareas, notas vinculadas, enlaces web |
| Polar (tareas) | **alta** | solo con cuenta vinculada |
| Recordatorios y calendario | **media** | crea y reprograma recordatorios y consulta la agenda; no gestiona capas, ICS externos ni eventos del calendario como tales |
| Sesiones | **media** | gestiona su ciclo de vida; contraseñas y recuperación quedan en la interfaz (correcto) |
| Skills | **alta** | CRUD completo con protección de las empaquetadas |
| Papelera | **alta** | restaurar y borrar |
| Hábitos | **nula** | existe `HabitService`, sin herramientas |
| Notas diarias y plantillas | **nula** | `DailyNoteService` y `NoteTemplateService` sin herramientas |
| Etiquetas globales | **media** | solo por nota; sin renombrar o fusionar globalmente (`TagManagerService`) |
| Estadísticas y panel | **nula** | no puede responder «¿cómo voy esta semana?» |
| Grafo y análisis de sesiones | **nula** | `SessionGraphAnalysisService` sin exponer |
| Exportación (PDF, imagen, ICS) | **nula** | |
| Ajustes, temas, atajos | **nula** | por diseño prudente; ver propuesta de ajustes con confirmación |
| Captura rápida y web clipper | **nula** | el asistente tiene su propia lectura web, pero no guarda el recorte |
| Editor (selección, cursor) | **nula** | no sabe qué hay seleccionado ni dónde está el cursor |
| Interfaz (navegar, abrir pestañas) | **nula** | no puede «llevar» al usuario a un resultado |

**Lectura:** el asistente es muy sólido como **operador de datos** (CRUD con seguridad) pero casi ciego como
**copiloto de la interfaz**. Falta el puente entre lo que dice y lo que el usuario ve.

## 2. AYUDA AL USUARIO: QUÉ APORTA Y QUÉ NO

### Lo que aporta hoy

- Resumir, reescribir y extraer tareas de notas adjuntas.
- Organizar: mover, etiquetar, crear tarjetas y recordatorios a partir de una conversación.
- Investigar en la web y guardar el resultado como nota.
- Trabajar en local y en privado con Ollama.
- Adaptarse al contexto con personalidad por capas y skills.

### Lo que no aporta todavía

| carencia | efecto para el usuario |
| --- | --- |
| No conoce la propia app | no puede contestar «¿cómo hago X en Enki?» ni sugerir atajos o ajustes |
| No ve la nota activa ni la selección | el usuario debe adjuntar a mano lo que ya tiene delante |
| No navega | «aquí tienes tu nota» sin enlace que la abra |
| No actúa por iniciativa | no hay resumen diario, repaso de tareas vencidas ni avisos útiles |
| No recuerda entre chats | cada conversación parte de cero salvo la personalidad |
| No revierte | un error del modelo se corrige a mano |
| No muestra cambios agrupados | cada acción se ve suelta; no hay «revisar y aprobar los 8 cambios» |
| Búsqueda léxica | «¿qué apunté sobre la reunión con el banco?» falla si la nota dice «entidad» |

## 3. FRICCIONES DE USO DETECTADAS

1. **Permisos repetidos:** cada chat nuevo vuelve a pedir permiso de consulta (mitigado con el ajuste global).
2. **Contexto manual:** hasta 5 adjuntos explícitos, aunque la nota abierta sea evidente.
3. **Calidad desigual por proveedor:** Ollama sin `tools` pierde todo lo «nativo».
4. **Sin métricas:** ni gasto ni número de tokens, relevante con clave de API propia.
5. **Confirmaciones solo para lo irreversible:** bien, pero un lote de cambios recuperables no se ve como conjunto.

## 4. CONCLUSIÓN

Cubrir el hueco «copiloto» da más valor que ampliar proveedores. Las cinco carencias de mayor retorno, que se
desarrollan en [`03-propuestas-priorizadas.md`](./03-propuestas-priorizadas.md), son:

1. Contexto automático de la nota activa y la selección.
2. Navegación (abrir resultados en la interfaz).
3. Ayuda integrada sobre Enki (documentación como herramienta).
4. Deshacer por turno.
5. Herramientas para los módulos invisibles (hábitos, diarias, plantillas, etiquetas, estadísticas).
