# Interfaz y experiencia de los agentes de IA de Enki

> Fecha: 2026-10-25. Decisiones de producto confirmadas por el responsable del proyecto en conversación.
> Este documento especifica la evolución del asistente existente. No autoriza una reimplementación
> desde cero ni presenta todos los flujos descritos como terminados. La matriz final distingue el
> desarrollo de esta entrega del trabajo posterior.

## 1. Referencias y precedencia

- Propuesta inicial: [Implementación desde cero](implementacion-ia-desde-cero.md).
- Referencia del módulo existente: [Asistente de IA](../../project-info/asistente-ia.md).
- [Sistema de diseño](../../project-info/sistema-diseno-web.md), `AGENTS.md` y código real.
- [Cambios externos en el vault](../../project-info/cambios-externos-en-el-vault.md).

Las decisiones de producto de este documento actualizan la propuesta inicial: el asistente puede
consultar y actuar mediante las herramientas de Enki. La regla inicial de producir exclusivamente texto
ya no describe el alcance acordado. Siguen siendo obligatorios el aislamiento, la trazabilidad,
las comprobaciones de escritura y las confirmaciones para operaciones irreversibles.

En el código inspeccionado solo existe el backend de Claude Code (`ClaudeCliService`). No hay backends
de Codex ni Ollama, aunque el responsable indica que están implementados. Antes de desarrollar su
interfaz, localizar la rama o los archivos correspondientes; no mostrar conexiones ficticias ni sustituir
la integración de Claude. Esta discrepancia no impide concretar la UX común.

## 2. Producto acordado

Un agente es una **instancia de chat independiente**: tiene conversación, modelo, capacidad, ámbito,
permisos y contexto propios. No presupone una jerarquía de agentes, ejecución paralela ni un catálogo
nuevo de personalidades. Las skills y los archivos se adjuntan voluntariamente.

El usuario puede pedir al agente las operaciones que realiza normalmente mediante la aplicación,
dentro de su ámbito y de las capacidades disponibles. El agente usa las entidades y servicios reales:
no mantiene copias de notas, tareas, recordatorios o sesiones como almacenes alternativos.

La IA sigue siendo opcional y apagada por defecto. Ninguna función básica de Enki depende de ella.

## 3. Dos superficies, una conversación

### Sección Asistente

- Historial a la izquierda; conversación y compositor a la derecha.
- Historial separado por vault y, dentro de él, agrupado en «Vault» y sesiones.
- Cada conversación muestra título, fecha y estado cuando está ejecutándose o esperando una decisión.
- Buscar filtra sin alterar el ámbito ni los permisos; un grupo sin coincidencias desaparece.
- Crear un chat permite elegir «Vault» o una sesión desbloqueada antes del primer envío.
- La cabecera muestra siempre ámbito, modelo y capacidad. El título puede renombrarse.

### Panel lateral

- Se abre junto al contenido de trabajo; su apertura no navega a otra sección ni selecciona otra nota.
- Reutiliza el mismo estado y los mismos comandos que la sección Asistente.
- Expandir a la sección o volver al panel conserva chat, borrador, adjuntos, permisos, respuesta y scroll.
- Cerrar el panel oculta la superficie y conserva la ejecución; «Detener» es una acción distinta.
- Historial y controles secundarios se despliegan bajo demanda para dejar espacio a la conversación.
- No montar dos compositores editables simultáneos contra el mismo borrador.

Esquema orientativo de distribución:

```text
Sección
┌─────────────────────┬──────────────────────────────────────────────┐
│ HISTORIAL       [+] │ Título        [Ámbito] [Modelo] [Capacidad]   │
│ Buscar              ├──────────────────────────────────────────────┤
│ VAULT               │ Conversación                                │
│   Chat A            │ Respuesta y actividad del agente             │
│ SESIÓN PROYECTO     │ Confirmación cuando corresponda              │
│   Chat B            ├──────────────────────────────────────────────┤
│   Chat C            │ Adjuntos / skill                             │
│ Estado / Ajustes    │ Mensaje                              Enviar  │
└─────────────────────┴──────────────────────────────────────────────┘

Panel lateral
┌───────────────────────────────┬────────────────────────────────────┐
│ Contenido actual de Enki      │ Chat [Historial] [Ampliar] [Cerrar] │
│ Conserva pestaña y selección  │ Ámbito · modelo · capacidad        │
│                               │ Conversación / actividad           │
│                               │ Adjuntos, mensaje y enviar         │
└───────────────────────────────┴────────────────────────────────────┘
```

## 4. Separación de ámbitos

| Ámbito del chat | Puede consultar y actuar | Fuera de alcance |
|---|---|---|
| Una sesión | Documentos y archivos de esa sesión | Raíz del vault, otras sesiones y módulos globales |
| Vault | Raíz del vault y módulos globales disponibles | Sesiones sin acceso expresamente habilitado |
| Vault con acceso a sesiones | Lo anterior y sesiones sin protección incluidas | Sesiones protegidas ajenas |

Un chat de sesión **no tiene modo auto para salir de su sesión**. Para operar en otro ámbito se crea
otro chat. El servicio debe rechazar una llamada fuera del ámbito aunque el modelo la solicite, un
archivo de historial antiguo conserve `FullVaultAccess=true` o el control visual esté deshabilitado.

El espacio queda fijado tras el primer envío. El permiso de búsqueda no amplía ese espacio. El acceso
a sesiones desde un chat del vault se muestra explícitamente y no incluye sesiones protegidas ajenas.
Una sesión protegida requiere un chat propio dentro de ella y que esté desbloqueada. Si se bloquea,
desaparece o deja de estar disponible, la operación se detiene con explicación y acción de recuperación;
nunca se redirige a la raíz del vault.

Grupos del historial y selector de ámbito son proyecciones de `SessionService` y `AiChatSession`, sin
crear un segundo modelo persistente de sesiones. Las sesiones con igual nombre se distinguen por su id.
Renombrar o borrar una sesión no debe reasignar sus chats a otra de nombre coincidente.

## 5. Permiso de búsqueda una vez por chat

El usuario puede enviar contexto adjunto sin habilitar búsquedas generales. Cuando el agente necesita
descubrir o leer contenido adicional, se muestra una tarjeta dentro de la conversación:

> «¿Permitir que este chat busque y consulte documentos de [ámbito]? El permiso se conserva para este
> chat. No permite acceder a otras sesiones ni amplía los permisos de edición.»

Acciones: **Permitir en este chat** y **Ahora no**. La tarjeta identifica el ámbito y el proveedor que
recibirá el contenido. No cambia de pantalla ni abre la configuración del sistema.

- La aceptación se guarda por chat y vault, no como consentimiento global.
- Una vez concedida, no se repite en los turnos siguientes ni al reabrir la conversación.
- Denegar permite seguir conversando con el texto y los adjuntos disponibles; no reintentar en bucle.
- La cabecera permite revisar y revocar el permiso. Revocar impide lecturas posteriores, pero no borra
  el contenido ya enviado al proveedor; explicarlo en el control.
- Ampliar el acceso del vault a sesiones exige aceptar el nuevo alcance: un permiso anterior para la
  raíz no autoriza silenciosamente el acceso adicional.
- Detener, desactivar la IA o cambiar de vault cancela una solicitud pendiente sin conceder acceso.
- Las herramientas verifican el permiso antes de leer; no basta con indicarlo en el prompt.

El consentimiento de activación, la confirmación de adjuntos y el permiso de búsqueda son decisiones
distintas. La interfaz debe explicar qué está autorizando cada una sin presentar diálogos redundantes.

## 6. Modelo y capacidad

La capacidad expresa esfuerzo de razonamiento; no es una temperatura, prioridad de tareas ni límite de
acceso. Se cambia mediante **slider discreto**, con nombre accesible, etiquetas visibles y teclado.

Para Claude en esta entrega:

- Modelo predeterminado de instalaciones nuevas: **Sonnet 5.5**, id `claude-sonnet-5-5`.
- Capacidad inicial por chat: **Media / Medium**.
- Tres posiciones: **Ligera / Low**, **Media / Medium**, **Alta / High**.
- El valor viaja al CLI mediante `--effort low|medium|high` cuando su ayuda anuncia ese flag.
- Se conserva por conversación y afecta a la siguiente respuesta; no cambia una ejecución en curso.
- Al comenzar otro chat vuelve a Medium; el modelo procede del ajuste predeterminado del usuario.
- Con CLI incompatible el slider queda deshabilitado y se explica que hay que actualizarlo. No fingir
  que se envió un nivel que el backend no admite.
- Una capacidad mayor puede tardar y consumir más cuota. Es una ayuda breve, no una promesa de calidad.
- Respetar la elección explícita del usuario y los chats anteriores; no migrar todos a otro modelo.

Los proveedores futuros deben declarar sus capacidades reales: no mapear arbitrariamente este slider
a parámetros de Ollama o a niveles no admitidos por un modelo. Los niveles adicionales, como xhigh o
max, requerirán una matriz de compatibilidad antes de exponerse.

Referencias oficiales consultadas para el transporte y el modelo:
[CLI de Claude Code](https://code.claude.com/docs/en/cli-reference),
[configuración de modelos](https://code.claude.com/docs/en/model-config).

## 7. Contexto, archivos y skills

- Adjuntar es voluntario: nota activa, selección o archivos elegidos explícitamente.
- Antes de enviar, mostrar nombre, origen y si hay recorte o un formato no compatible; permitir quitar
  cada adjunto. No enviar archivos silenciosamente por abrir una nota.
- La nota con cambios pendientes usa el flujo de guardado y comprobación existente. Un conflicto no se
  resuelve descartando el borrador ni enviando una copia obsoleta sin indicarlo.
- El presupuesto es compartido por los adjuntos. Mostrar el recorte antes del envío; no presentar
  resultados como si el agente hubiera leído la parte excluida.
- Los archivos que excedan el límite o no puedan leerse quedan identificados con una acción para
  retirarlos o escoger otro. No cargar binarios como texto ni ejecutar su contenido.
- Las skills reutilizan `SkillService`; no crear otra biblioteca. La implementación actual fija una
  skill al comenzar el chat. Mantener esa regla visible hasta diseñar el cambio de instrucciones entre
  turnos; adjuntar skills no implica implementar subagentes autónomos.
- La selección del contexto respeta el ámbito, incluso mediante rutas, enlaces o adjuntos indirectos.
- No enviar automáticamente todo el vault: poder consultar todos los documentos del ámbito no exige
  transmitirlos todos en cada turno.

## 8. Consultar y actuar sin interrumpir al usuario

El agente usa las herramientas de notas, archivos, tareas, agenda y recordatorios disponibles, con los
mismos límites del producto. El control global de solo lectura se conserva y prevalece sobre el chat.

- Cada operación muestra acción, destino y estado dentro de la respuesta: pendiente, en curso,
  completada, denegada o fallida. Mantener la actividad aunque falle el texto final.
- Crear, editar, mover o archivar no navega al destino. «Abrir resultado» es una acción explícita.
- Actualizar pestañas abiertas cuando cambie su entidad sin robar foco, cambiar selección ni abrir
  pestañas nuevas. La pestaña afectada por un borrado sigue las reglas normales de Enki.
- Para borrar definitivamente u otras operaciones que ya exigen aprobación, conservar la tarjeta de
  confirmación con los destinos afectados. El permiso de búsqueda no aprueba esos cambios.
- Las ediciones respetan `WriteContentChecked`, la huella de lectura, el historial local y el flujo de
  conflictos. Un error no se comunica como éxito ni provoca una sobrescritura automática.
- Si se conserva una acción manual «Guardar como nota», diferenciarla de la creación realizada por el
  agente; el comportamiento de apertura debe ser explícito en su nombre o acción posterior.

## 9. Historial, borradores y navegación

Decisión confirmada: historial **local separado por vault**, con agrupación por sesión. La configuración
del proveedor pertenece a la instalación; el historial no tiene por qué viajar dentro del vault.

El diseño de persistencia debe identificar inequívocamente el vault de cada chat antes de retomarlo.
No atribuir conversaciones antiguas sin identidad al vault que esté abierto por casualidad. Conservar
esos archivos y ofrecer una asociación explícita en una migración; no borrarlos ni mezclarlos.

Conservar borradores por conversación y un borrador nuevo por ámbito: texto, adjuntos referenciados,
skill elegida y capacidad. Al restaurar se vuelven a comprobar existencia, bloqueo y ámbito de los
adjuntos. No guardar copias del contenido como otro almacén de notas.

Navegar por Enki durante una respuesta está permitido. Mostrar un indicador global con estados
«Trabajando», «Necesita tu decisión» y «Respuesta lista», con acción para volver al chat. No usar un
toast por fragmento ni por herramienta. El aviso importante usa `NotificationService`.

Cambiar de vault cancela la ejecución y revoca sus herramientas antes de trabajar con el nuevo vault;
no es equivalente a navegar a otra sección. Cambiar de proveedor no reutiliza identificadores de
sesión de otro CLI. La primera versión no necesita ejecutar varios chats a la vez: no confundir
instancias independientes con concurrencia ya implementada.

## 10. Estados de interfaz

| Estado | Presentación y recuperación |
|---|---|
| IA apagada | Activación en Ajustes con consentimiento; los flujos normales siguen disponibles |
| Comprobando proveedor | Progreso breve; evitar dobles comprobaciones y dobles envíos |
| No instalado / sin sesión | Instrucciones oficiales, copiar comando y comprobar de nuevo |
| Sin chats | Explicar cómo crear uno y elegir su ámbito |
| Sin resultados | Conservar filtro y permitir limpiarlo |
| Chat vacío | Sugerencias que rellenan el compositor sin enviar automáticamente |
| Preparando contexto | Mostrar progreso; preservar texto y permitir cancelar |
| Respondiendo / actuando | Streaming, actividad y detener; modelo y capacidad deshabilitados |
| Esperando permiso | Tarjeta contextual; indicador global si el chat no está visible |
| Completado | Texto y actividad revisables; copiar o abrir resultados bajo demanda |
| Detenido | Conservar respuesta parcial, actividad y marca de interrupción |
| Cuota agotada | Motivo, reinicio si se conoce y reintento manual; sin bucles automáticos |
| Error de conexión / modelo | Mensaje concreto y recuperación sin perder el mensaje preparado |
| Conflicto / contenido bloqueado | No escribir; indicar la acción normal de Enki para resolverlo |
| Fallo al guardar historial | Avisar y conservar el estado en memoria para reintentar |
| Capacidad no compatible | Slider deshabilitado con explicación; conversación sigue disponible |

Un reintento de un turno con herramientas no debe repetir cambios ya completados por omitir la
actividad previa. Diferenciar reintentar el texto, continuar y volver a ejecutar una acción.

## 11. Diseño, teclado y accesibilidad

- Reutilizar recursos del tema, Space Grotesk, JetBrains Mono y Material Icons existentes.
- Esquinas rectas, bordes finos y componentes compartidos. Popovers con `Popup` y
  `Border.dropdown-popover`; evitar `Flyout`, que queda fuera del mecanismo compartido de zoom.
- Jerarquía y espaciado coherentes con Habilidades y Notas; sin estética o colores exclusivos de IA.
- Títulos de grupo legibles; icono o texto además de color para estado y alcance.
- Botones de icono con nombre accesible y tooltip. El slider anuncia nombre y valor.
- Recorrido de teclado estable; Escape cierra popovers y devuelve el foco al disparador.
- Mantener las convenciones del compositor; no capturar atajos globales mientras se escribe.
- El streaming no roba el scroll si se están leyendo mensajes anteriores; ofrecer «Ir al final».
- Al reducir el espacio, plegar historial y llevar modelo/capacidad al menú de cabecera. En el panel,
  reorganizar controles en filas; nunca reducir el editor a un ancho inutilizable.
- Validar zoom y temas con los recursos reales, textos largos y nombres de sesión coincidentes.
- Cadenas en los siete recursos de `localization/`; documentación y XML-doc en español.

## 12. Integración y validación para agentes implementadores

Mantener MVVM y composición manual. Reutilizar `AssistantViewModel`, sus parciales, `AiChatService`,
`ClaudeCliService`, `AiAccessScope`, `AiVaultToolbox`, los viewmodels de actividad y los componentes de
selección existentes. Extraer una abstracción de proveedores solo cuando haya implementaciones reales.

| Parte | Situación de esta entrega |
|---|---|
| Historial agrupado en vault y sesiones | Desarrollado sobre los chats existentes |
| Sesión sin salida a otro ámbito | Desarrollado en el servicio, incluso para acceso ampliado heredado |
| Sonnet 5.5 inicial y slider de tres niveles por chat | Desarrollado con detección del flag del CLI |
| Panel lateral y sección con estado compartido | UX acordada; desarrollo posterior |
| Búsqueda autorizada una vez por chat y revocación | UX acordada; desarrollo posterior |
| Identidad de vault, migración e historial separado | UX acordada; desarrollo posterior; el almacén actual es global |
| Borradores persistentes e indicador global | UX acordada; desarrollo posterior |
| Adjuntos de archivos genéricos | UX acordada; ampliar el selector actual de notas en desarrollo posterior |
| Codex y Ollama | No encontrados en esta copia; localizar implementación antes de integrarlos |

Criterios de aceptación del desarrollo actual:

1. Elegir una sesión, enviar y volver a abrir mantiene el ámbito y agrupa el chat correctamente.
2. Una herramienta dirigida al vault u otra sesión desde un chat de sesión falla sin modificar datos,
   incluso si el JSON conserva un acceso ampliado antiguo. Los adjuntos usan el mismo límite.
3. Un chat de vault puede mantener su acceso explícito a sesiones no protegidas; no hereda el de otro chat.
4. Un chat nuevo parte de Medium y, sin una preferencia previa, de Sonnet 5.5. Cambiar el slider y enviar
   pasa el esfuerzo real al CLI; reabrir recupera el valor; otro chat conserva el suyo.
5. Un CLI sin `--effort` no recibe ese argumento y muestra el control deshabilitado con su explicación.
6. Durante la respuesta no se puede modificar capacidad desde la interfaz. Cancelar y reintentar siguen
   usando el flujo y la actividad existentes.
7. Las pruebas de servicios, viewmodels, localización y XAML compilan y verifican el aislamiento y
   el transporte. Revisar además los controles con el proyecto de pruebas de interfaz.

Para los desarrollos posteriores, añadir pruebas de permiso concedido/denegado/revocado y persistencia,
cambio de vault durante aprobación, historial sin identidad, borradores y cambio entre panel/sección,
lecturas de adjuntos bloqueados y ausencia de navegación al completar herramientas. No dar esos flujos
por implementados a partir de esta especificación.
