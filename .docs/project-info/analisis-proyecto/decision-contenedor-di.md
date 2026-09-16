# Decisión: contenedor de inyección de dependencias para la composición del shell

Documento de decisión derivado de la [Fase 10 del roadmap](roadmap.md#fase-10-opcional--deuda-técnica-menor-y-mejoras-de-producto-derivadas-de-la-auditoría)
(análisis §2) — "Evaluar introducir un contenedor de DI ligero y pasar `storageDirectory` de forma explícita
al construir los servicios en `MainWindowViewModel.Composition.cs`, para que el shell completo sea testeable
de forma aislada". El propio punto del roadmap ya apuntaba que es "el mismo punto que el roadmap anterior
dejaba como Fase 7 opcional" — este documento cierra esa evaluación de forma explícita en vez de dejarla
pendiente indefinidamente de sesión en sesión.

## Contexto

`MainWindowViewModel.Composition.cs` (377 líneas) es el único sitio del proyecto donde se instancian
servicios y ViewModels de página: ~32 llamadas a constructores (`new XxxService(...)`/`new XxxViewModel(...)`)
en el constructor de `MainWindowViewModel` y en `InitializeShell`, cableadas a mano en el orden correcto
(cada servicio recibe las dependencias que ya se construyeron antes que él). No hay ningún contenedor de DI
(`Microsoft.Extensions.DependencyInjection` ni ningún otro) en el proyecto — todo el grafo de objetos vive en
código C# plano, explícito y lineal.

## Opciones evaluadas

1. **Mantener la composición manual actual** (estado presente): un único fichero, sin abstracción de
   contenedor, donde el orden de construcción es literalmente el orden de las líneas del fichero.
2. **Introducir `Microsoft.Extensions.DependencyInjection`** (u otro contenedor ligero: `Autofac`,
   `SimpleInjector`) y registrar cada servicio/ViewModel con su ciclo de vida (singleton para los servicios
   del shell, transient para ViewModels de diálogo/editor efímeros como `ReminderEditorViewModel`), resolviendo
   el grafo vía `IServiceProvider` en vez de `new` explícito.
3. **Introducir solo una factoría/`Action<T>` de fábricas** sin contenedor de terceros (un `Dictionary<Type,
   Func<object>>` casero) — evaluada y descartada de entrada: reproduce buena parte de la complejidad de un
   contenedor real (registro, resolución, ciclo de vida) sin ninguna de sus garantías (detección de
   dependencias circulares, validación de registro en tiempo de arranque), así que no aporta nada que la
   opción 2 no dé ya mejor por el mismo coste de introducir una dependencia nueva.

## Decisión: opción 1, mantener la composición manual

La motivación real detrás del punto del roadmap es la **testeabilidad**, no el DI en sí — y esa motivación ya
está resuelta hoy por un camino distinto y más barato: cada servicio de persistencia del proyecto
(`VaultService`, `KanbanService`, `ReminderService`, `SettingsService`, `DismissedReminderService`,
`SessionService`, ...) ya acepta un `storageDirectory` opcional en su constructor —el mismo "seam de testeo"
documentado en el roadmap para la Fase 0/1— y los servicios que dependen de la cuenta Polar vinculada
(`PolarTaskService`, `ReminderService`) ya aceptan un `currentUserIdProvider` inyectable (Fase 4). Gracias a
eso, `enki.Tests` construye cada servicio de forma aislada, apuntado a un directorio temporal desechable, sin
tocar el disco real del usuario ni necesitar el grafo completo del shell — que es exactamente lo que un
contenedor de DI existiría para facilitar. Lo único que un contenedor añadiría de verdad es poder construir
**el `MainWindowViewModel` completo** en un test sin repetir las ~32 líneas de `new` a mano; ese caso de uso
concreto no ha aparecido todavía en la suite de tests (629+ tests, ninguno instancia el shell completo — cada
uno prueba un servicio o un ViewModel de página de forma aislada, que es la unidad de prueba natural aquí).

Además:

- **377 líneas en un único fichero, con dependencias que se leen en orden de arriba a abajo**, siguen siendo
  más fáciles de auditar para una persona (o un agente) nueva en el proyecto que un grafo resuelto
  dinámicamente por reflexión — se puede responder "¿de dónde sale esta instancia de `PolarSyncService`?" con
  `Ctrl+F` en un fichero, sin necesitar entender las reglas de resolución de un contenedor.
- Un contenedor de DI introduce una dependencia nueva (`Microsoft.Extensions.DependencyInjection` u otra) en
  un proyecto que ya sigue, de forma consistente y deliberada, el criterio de "sin dependencias pesadas
  cuando el patrón manual basta" (mismo criterio ya aplicado al logging en la Fase 0 y descartado
  explícitamente para el almacenamiento de credenciales en la
  [decisión de Fase 4](decision-almacenamiento-credenciales-polar.md)).
- Avalonia no tiene una integración de primera clase con `Microsoft.Extensions.DependencyInjection` tan
  madura como, por ejemplo, ASP.NET Core — introducirlo aquí sería configurar el contenedor a mano de todos
  modos para el ciclo de vida de ventana/vista, sin ganar la mayoría de las ventajas que sí tiene en un host
  web.

**Riesgo aceptado**: el propio `MainWindowViewModel` (el shell completo) sigue sin tener un test de
composición end-to-end que construya todo el grafo real y verifique que arranca sin excepciones — hoy esa
garantía la da solo la app arrancando de verdad en local/CI (`dotnet build`+ejecución manual), no un test
automatizado. Si `Composition.cs` empezara a crecer con lógica condicional real (no solo `new X(y, z)`) o el
número de servicios doblara su tamaño actual, el coste de seguir auditándolo a simple vista subiría y esta
decisión debería revisarse.

## Revisar esta decisión si...

- Aparece una necesidad real de test que construya el `MainWindowViewModel` completo (no solo un servicio o
  ViewModel de página aislado) — el caso de uso concreto que un contenedor resolvería mejor que el patrón
  actual.
- `MainWindowViewModel.Composition.cs` crece significativamente más allá de sus ~380 líneas actuales, o el
  grafo de dependencias deja de ser lineal (construcción condicional, dependencias circulares resueltas a
  mano con propiedades nulas post-inicializadas).
- El proyecto adopta Avalonia's propio patrón de hosting (`Microsoft.Extensions.Hosting` vía
  `Avalonia.Extensions.Hosting` o similar) por otro motivo (p. ej. logging estructurado, configuración
  jerárquica) — en ese caso un contenedor de DI vendría "gratis" con esa migración y el cálculo de
  coste-beneficio cambia.
