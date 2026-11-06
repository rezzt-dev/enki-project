# ANALISIS DE PRODUCTO: PRIORIDAD ALTA

> funcionalidades y cambios que, si faltan, impiden que parte de los usuarios previstos (o de los
> que podrian usar solo una parte del programa) lleguen a usar enki a diario. se abordan antes de
> abrir cualquier modulo nuevo. el indice con el avatar y el resumen esta en [`indice.md`](./indice.md).

**Fecha del analisis:** 2026-11-06 · rama `beta-v2.0`
**Metodo:** lectura de `readme.md`, `changelog.md`, `.docs/project-info/` y busquedas en `enki/src/enki/`.
**No se ha ejecutado la aplicacion**: "no aparece" significa "no hay coincidencias en el codigo" y debe
comprobarse antes de empezar cada tarea. si algo de aqui no coincide con el codigo real, manda el codigo.

---

## ALT-01 · SINCRONIZACION DE NOTAS Y CAPTURA DESDE EL MOVIL

- **tipo:** nuevo
- **usuario afectado:** el avatar cuando se aleja del escritorio; cualquiera que use mas de un equipo.
- **evidencia:** `PolarSyncService` y `PolarTaskService` solo sincronizan tareas y recordatorios; las
  notas dependen de que el usuario configure syncthing, icloud o google drive por su cuenta
  (`.docs/project-info/sincronizacion-privada-notas.md`). la tarea `T12` de
  `funcionalidades-pendientes-priorizadas.md` sigue sin marcar.
- **propuesta:** primer paso barato: bandeja de entrada de ideas capturadas en el movil, enviadas por
  polar y aceptadas en enki como notas de `Inbox/` o tarjetas kanban, sin tocar el vault existente.
  segundo paso, solo tras validar el primero: sincronizar notas con `VaultService.WriteContentChecked`
  y `NoteDiskStamp` para no pisar cambios.
- **criterio de cierre:** una idea escrita en el movil aparece en `Inbox/` sin duplicados tras reconectar,
  y el contenido existente nunca se sobrescribe sin aviso.

## ALT-02 · MODULOS OPCIONALES Y PERFILES DE USO

> **ESTADO (2026-11-06): implementado, pendiente de comprobación manual.** La evidencia de abajo estaba
> desfasada: los interruptores por sección **ya existían** (`*PanelEnabled` en `AppSettings`, guardados por
> vault) y `RebuildNavItems` ya ocultaba la entrada de la barra. Lo que faltaba y se ha añadido:
> perfiles «Solo notas», «Planificador» y «Todo» (`ModuleProfileCatalog`, Ajustes › Navegación y selector al
> crear un vault nuevo), y que un módulo apagado desaparezca también de la paleta de comandos, la búsqueda
> global, el panel (bloques y tarjetas de accesos rápidos), los atajos y la navegación (`Navigate`).
> **No hecho:** los apartados de Ajustes de cada módulo (Kanban, Calendario…) siguen visibles, porque ahí
> está el interruptor para reactivarlos; y Polar se oculta de búsqueda y paleta solo cuando Kanban y
> Calendario están apagados. Cambio de comportamiento: antes ocultar una sección la dejaba accesible desde la
> búsqueda y la paleta (texto de `Settings_General_Sidebar_SearchNote`); ahora no.

- **tipo:** rediseño
- **usuario afectado:** quien solo quiere notas, solo kanban o solo calendario; el estudiante que no usa
  polar; quien llega con una app ajena y quiere probar una parte.
- **evidencia:** `SidebarSectionCatalog` define 14 secciones fijas (`AllKeys`) que se pueden reordenar,
  renombrar y mover a la barra inferior, pero no ocultar ni desactivar. `DashboardHiddenSections` solo
  oculta bloques del panel. la primera ejecucion solo pregunta idioma y vault (`LanguageSetupView`,
  `VaultSetupView`).
- **propuesta:** convertir el catalogo en una lista de modulos activables (guardada por vault en
  `VaultSettings`), con perfiles de partida editables: "solo notas", "planificador", "todo". un modulo
  desactivado desaparece de la barra, la paleta de comandos, la busqueda global, el panel y los atajos, y
  sus datos se conservan intactos en disco.
- **criterio de cierre:** con solo notas activo no queda rastro de kanban, calendario ni polar en la
  interfaz; reactivar un modulo devuelve sus datos sin migracion.

## ALT-03 · ACCESIBILIDAD VERIFICADA Y ALTO CONTRASTE

> **ESTADO (2026-11-06): parcial.** Hecho: temas `High Contrast Dark` y `High Contrast Light` en
> `BuiltInThemeCatalog`, con prueba automática de contraste ≥ 4,5:1 en cada pareja texto/fondo
> (`HighContrastThemeTests`), y activación automática en el arranque si el sistema pide contraste alto y el
> usuario no ha elegido tema. Procedimiento de auditoría en
> [`accesibilidad-y-alto-contraste.md`](../../../project-info/accesibilidad-y-alto-contraste.md).
> **Pendiente:** la auditoría de foco de Kanban, Calendario y Ajustes y la prueba con Orca y NVDA no se han
> hecho (no hay lector de pantalla en el entorno de desarrollo); el criterio de cierre no se da por cumplido.
> Recuento actual: 664 `AutomationProperties.*` en 40 vistas.

- **tipo:** mejora
- **usuario afectado:** usuarios con lector de pantalla, baja vision o que solo usan teclado.
- **evidencia:** `AutomationProperties` aparece en 652 lugares de las vistas, de modo que el hallazgo
  "cero resultados" de `analisis-deficiencias-proyecto.md` esta desfasado. siguen sin aparecer un
  tema de alto contraste (`HighContrast`) ni una auditoria del orden de tabulacion: `Focusable` o
  `TabIndex` solo estan en 14 vistas. la prueba con lector de pantalla sigue siendo manual
  (`qa-manual-release-candidate.md`).
- **propuesta:** auditar el foco de kanban, calendario y ajustes; añadir un tema de alto contraste en
  `BuiltInThemeCatalog` que respete el contraste del sistema; probar con orca y nvda y registrar el
  resultado.
- **criterio de cierre:** se completa crear nota, mover tarjeta y crear recordatorio solo con teclado y
  lector de pantalla; los textos cumplen contraste 4,5:1 en el tema de alto contraste.

## ALT-04 · CALIDAD DE LA ENTREGA EN WINDOWS Y MACOS

> **ESTADO (2026-11-06): parcial.** Hecho: `ci.yml` ejecuta compilación y `enki.Tests` en Ubuntu, Windows y
> macOS (la batería completa de `enki.UITests` sigue solo en Linux). La firma de macOS **no** estaba del todo
> ausente: ya había firma ad-hoc en `build-macos-package.sh`. Política y pasos en
> [`politica-de-firma-y-ci-multiplataforma.md`](../../../project-info/politica-de-firma-y-ci-multiplataforma.md).
> **Pendiente:** la protección de ramas (ajuste de GitHub, no del repositorio) y la firma real con
> certificado y notarización, que necesitan cuentas de pago y una petición explícita. La matriz nueva no se
> ha ejecutado en los runners de Windows y macOS desde este entorno.

- **tipo:** mejora
- **usuario afectado:** cualquiera que no use linux, hoy el entorno mejor cubierto.
- **evidencia:** `ci.yml` ejecuta compilacion y pruebas solo en `ubuntu-latest`; `release.yml` empaqueta
  en windows y macos pero en el flujo no aparecen pasos de firma ni de notarizacion. la captura global
  tiene tres implementaciones propias (`WindowsGlobalHotkey`, `MacGlobalHotkey`, `X11GlobalHotkey`) que
  la integracion continua no ejecuta fuera de linux.
- **propuesta:** añadir a `ci.yml` una matriz con `windows-latest` y `macos-latest` para las pruebas de
  servicio; decidir y documentar la politica de firma (certificado, notarizacion, aviso de smartscreen)
  sin guardar secretos en el repositorio.
- **criterio de cierre:** una pr no se puede fusionar con las pruebas en rojo en los tres sistemas; la
  descarga de macos abre sin el aviso de desarrollador no identificado.

## ALT-05 · DOCUMENTACION DE ANALISIS ALINEADA CON EL CODIGO

> **ESTADO (2026-11-06): hecho para los cuatro análisis previos.** Cada uno lleva una «NOTA DE VIGENCIA»
> con lo que se contrastó con el código y un enlace aquí. Solo se marcó como resuelto lo comprobado; el
> resto de hallazgos de esos documentos no se ha vuelto a verificar y así lo dicen.

- **tipo:** cambio
- **usuario afectado:** quien contribuya o audite el proyecto, y los agentes que lean `.docs/`.
- **evidencia:** `analisis-deficiencias-proyecto.md` afirma que no hay `AutomationProperties` (hoy hay
  652) y `funcionalidades-pendientes-priorizadas.md` cita la rama `alfa-version`. hay tres analisis
  previos en esta carpeta con prioridades que se solapan con estos ficheros.
- **propuesta:** marcar en cada documento previo lo que ya esta resuelto y enlazar aqui las prioridades
  vigentes, sin borrar su historico.
- **criterio de cierre:** ningun documento afirma como pendiente algo que el codigo ya implementa.
