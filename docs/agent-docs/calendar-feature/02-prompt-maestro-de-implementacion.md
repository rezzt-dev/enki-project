# 02 — Prompt maestro de implementación

## Contexto rápido

Este documento es distinto de los demás: no es una especificación para leer, es **un prompt listo para
pegar** a un agente de codificación (Claude Code u otro) que vaya a implementar la funcionalidad de
Calendario en Enki de punta a punta. Está escrito en primera persona desde el punto de vista de quien da
la instrucción, y asume que el agente que lo reciba **no tiene memoria de esta conversación** — por eso
repite explícitamente dónde está todo.

Cópialo tal cual (desde "## Prompt" hasta el final del documento) como instrucción inicial de una sesión
nueva. Si la sesión se corta a mitad de una fase, el mismo prompt se puede volver a pegar sin problema:
el primer paso que pide (leer `00-README.md` y ver qué fase está marcada) hace que retome exactamente
donde se quedó.

---

## Prompt

Vas a implementar la funcionalidad de **Calendario** en Enki, una app de escritorio Avalonia/.NET (MVVM,
`CommunityToolkit.Mvvm`). Toda la especificación de producto, diseño y arquitectura ya está escrita en
`docs/calendar-feature/` — tu trabajo es ejecutarla, no reinventarla. Sigue este procedimiento:

### 1. Orientarte antes de escribir código

1. Lee `docs/calendar-feature/00-README.md` completo. Contiene el índice de todos los documentos y, al
   final, una lista de checkboxes con el estado de avance por fases — mira cuál es la primera fase sin
   marcar, es tu punto de partida.
2. Lee `docs/calendar-feature/01-contexto-y-vision-de-producto.md` para entender el porqué y los
   no-objetivos explícitos (no inventes funcionalidades de Google Calendar que ese documento descarta a
   propósito).
3. Lee `docs/calendar-feature/03-glosario-y-modelo-de-datos.md` antes de tocar cualquier modelo o
   servicio — define con precisión de qué habla cada término (en especial la diferencia entre
   "Recordatorio" y "bandeja de recordatorios", que se prestan a confusión) y de dónde sale cada dato que
   va a aparecer en el calendario.
4. Lee el resto de documentos numerados en orden (`04` a `13`) **antes de empezar la fase que vayas a
   implementar**, no todos de golpe si no hace falta: cada documento de fase en
   `12-plan-de-implementacion-por-fases.md` te dice exactamente qué otros documentos necesitas para esa
   fase concreta.
5. Lee también, fuera de esta carpeta:
   - `docs/notificaciones-y-recordatorios.md` (diseño actual de campana + bandeja — no lo dupliques ni lo
     rompas).
   - `docs/sistema-diseno-web.md` (sistema de diseño visual — es obligatorio para cualquier XAML nuevo).
   - `docs/supabase-sync/01-modelo-de-datos-local.md` y `02-logica-de-negocio.md` (modelo de datos y
     motor de recurrencia de Polar, que el calendario reutiliza sin reimplementar).
6. Inspecciona el código real antes de asumir nada de lo escrito en estos documentos:
   `enki/src/enki/models/`, `enki/src/enki/services/`, `enki/src/enki/viewmodels/`,
   `enki/src/enki/views/`. Estos documentos fueron escritos leyendo ese código en un momento dado — si
   algo ya cambió, el código manda, no el documento.

### 2. Cómo trabajar por bloques

- Implementa **una fase completa de `12-plan-de-implementacion-por-fases.md` a la vez**, nunca una mezcla
  de dos fases a medias. Cada fase tiene un criterio de "hecho" explícito en ese documento — no la des por
  terminada hasta cumplirlo entero.
- Al terminar una fase: compílala (`dotnet build`), corre los tests existentes y los nuevos que hayas
  añadido (`dotnet test enki/tests/enki.Tests/enki.Tests.csproj`), y **marca la casilla correspondiente en
  `docs/calendar-feature/00-README.md`** antes de parar o de pasar a la siguiente fase. Esa casilla es la
  única fuente de verdad de "por dónde vamos" entre sesiones distintas — mantenerla al día es parte del
  trabajo, no un extra.
- Si durante una fase descubres que hace falta un cambio en un servicio existente que no está descrito en
  `11-arquitectura-tecnica.md` (por ejemplo, un evento de cambio que falta en algún servicio), añádelo
  siguiendo el patrón exacto que ya usan los servicios vecinos (`KanbanService.BoardChanged`,
  `PolarTaskService.DataChanged`) y anota el cambio en ese documento para que quede reflejado.
- No implementes nada de la lista de "No-objetivos de esta fase" (`01-contexto-y-vision-de-producto.md`,
  §4) ni de la "Fase 6 (futuro opcional)" salvo instrucción explícita en contrario.

### 3. Estándares de código a seguir (ya vigentes en el repo — no los inventes distinto)

- **MVVM estricto**: modelos POCO en `models/`, lógica de persistencia en `services/`, estado y comandos
  en `viewmodels/` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` de
  `CommunityToolkit.Mvvm`), vistas declarativas en `views/*.axaml` con code-behind mínimo.
- **Persistencia**: mismo patrón que `KanbanService`/`PolarTaskService`/`ReminderService` — JSON en
  `%AppData%/Enki/*.json`, un método `Save()` que serializa y dispara un evento `Action?` de cambio,
  constructor que acepta un `storageDirectory` opcional para poder testear sin tocar el disco real del
  desarrollador.
- **Sin abstracciones nuevas si no hacen falta**: el calendario es una capa de *lectura y edición directa*
  sobre modelos existentes, no una reescritura de esos modelos. Evita introducir interfaces genéricas,
  repositorios o capas de mapeo que el resto del código no usa.
- **Diseño visual**: cero excepciones al sistema de diseño de `docs/sistema-diseno-web.md` —
  `CornerRadius="0"` salvo indicadores circulares, reutiliza `Border.calendar-popover` y
  `Border.dropdown-popover` literalmente en vez de crear estilos de popover nuevos, tipografía
  Space Grotesk/JetBrains Mono ya empaquetadas.
- **Tests**: xUnit en `enki/tests/enki.Tests/`, en la carpeta `Services/` o `ViewModels/` según
  corresponda, siguiendo el estilo de los tests ya existentes (`ReminderItemViewModelTests.cs`,
  `NotificationServiceTests.cs`).
- **Idioma**: comentarios de código y nombres de commit/PR en el mismo idioma que ya usa el repo
  (español en comentarios de documentación tipo XML-doc del código existente, inglés en identificadores
  de código — respeta la mezcla tal cual está, no la normalices a un solo idioma).

### 4. Qué hacer si algo no está claro

Si una fase requiere una decisión de producto que no está resuelta en ninguno de los documentos de
`docs/calendar-feature/` (por ejemplo, un comportamiento de borde no contemplado), no la inventes en
silencio: déjala explícita como pregunta abierta en el documento correspondiente bajo un encabezado
`## Preguntas abiertas` (créalo si no existe) y toma la opción más conservadora (la que menos amplía el
alcance) para poder seguir avanzando sin bloquear el resto del trabajo.

### 5. Definición de "terminado" para toda la funcionalidad

El Calendario se considera completo cuando:

- Las seis fases de `12-plan-de-implementacion-por-fases.md` están marcadas como hechas en `00-README.md`
  (la Fase 6 es opcional/futura y no cuenta para este criterio salvo que se pida explícitamente).
- `dotnet build` y `dotnet test` pasan limpios sobre todo el repo, no solo sobre el código nuevo.
- El checklist manual de `13-pruebas-y-checklist-de-calidad.md` se ha recorrido a mano al menos una vez
  contra la app corriendo de verdad (no basta con que compile).
- Ningún flujo existente (Kanban, Polar, bandeja de recordatorios, campana de notificaciones) cambió de
  comportamiento salvo donde estos documentos dicen explícitamente que debía cambiar.

Empieza ahora por el paso 1.
