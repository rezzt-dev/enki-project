# Instalación de skills en el sistema

Addon de la sección Habilidades que copia skills de la biblioteca (`<vault>/.enki/skills/`) a los
directorios de usuario de Claude Code, Codex y Gemini CLI. Así esos asistentes descubren y usan las
skills por su cuenta, desde cualquier terminal o proyecto, sin pasar por el asistente de Enki. Enki sigue
siendo el sitio donde se escriben y organizan; el sistema recibe copias gestionadas.

Código: `services/SkillInstallService.cs`, `models/SkillInstallation.cs`,
`viewmodels/skills/SkillsViewModel.Install.cs`, `viewmodels/skills/SkillInstallDialogViewModel.cs` y
`viewmodels/settings/SettingsViewModel.SkillInstall.cs`.

## Activación

- **Ajustes › Habilidades › Instalación en el sistema** activa el addon. Está desactivado por defecto
  porque escribe fuera del vault. La tarjeta muestra las rutas exactas de este equipo antes de activarlo.
- **Mantener sincronizadas las copias instaladas** (activado por defecto) actualiza las copias al guardar,
  mover o convertir una skill instalada.
- Los dos ajustes son claves de instalación (`SettingsService.InstallationKeys`): describen el equipo, no
  el vault, y no viajan con un vault sincronizado a otra máquina.
- Sin el addon activo no aparece ningún botón de instalación y Enki no escribe en esos directorios.

## Destinos

| Asistente | Directorio de usuario | Se considera detectado si existe |
| --- | --- | --- |
| Claude Code | `~/.claude/skills` o `$CLAUDE_CONFIG_DIR/skills` | `~/.claude` o `$CLAUDE_CONFIG_DIR` |
| Codex | `~/.agents/skills` | `~/.codex` o `$CODEX_HOME` |
| Gemini CLI | `~/.gemini/skills` | `~/.gemini` |

- Codex carga las skills de usuario desde `~/.agents/skills`, el directorio del estándar Agent Skills.
  Gemini CLI también lee ese directorio como alias de `~/.gemini/skills` y le da prioridad, así que una
  skill instalada para Codex aparece también en Gemini CLI. Instalar en los dos no causa conflicto: el
  contenido es el mismo.
- Instalar en un asistente no detectado crea su carpeta igualmente; el diálogo lo avisa.
- Claude Code vigila sus carpetas de skills y recoge los cambios en la sesión abierta. Si la carpeta
  `skills` no existía al iniciar la sesión, hay que ejecutar `/reload-skills` una vez. En Gemini CLI,
  `/skills reload` refresca la lista.

## Formato instalado

Cada instalación es una carpeta `<destino>/<nombre>/` con:

- `SKILL.md` generado a partir de la skill:
  - `name` es el nombre de instalación: el id de la skill (nombre de archivo o de paquete) en minúsculas,
    sin diacríticos y con guiones, hasta 64 caracteres (`Revisión de código.md` → `revision-de-codigo`).
    Se deriva del id y no del `name` visible para que cambiar el título no deje copias huérfanas.
  - `description` es la de la skill. Si falta, se usa el nombre; si supera 1024 caracteres, se acorta.
  - El resto del frontmatter se copia línea a línea, sin reserializar, para no alterar campos nativos
    como `allowed-tools`, `disable-model-invocation`, `hooks` o `metadata`.
  - Se quitan los campos propios de Enki: `emoji`, `color`, `vibe`, `tags`, `tools` y `permissions`.
    `tools` y `permissions` no se traducen a `allowed-tools`, que en Claude Code concede permisos reales.
- Los archivos auxiliares del paquete, si la skill es un paquete (sin `.git` ni enlaces simbólicos).
- `.enki-skill.json`, el manifiesto con el origen y la huella SHA-256 de lo instalado.

El diálogo muestra avisos de compatibilidad antes de instalar: sin frontmatter, YAML no válido, sin
descripción, descripción demasiado larga, nombre normalizado y paquete de más de 50 MB (que no se
instala).

## Estados y garantías

| Estado | Significado | Acción |
| --- | --- | --- |
| No instalada | No hay carpeta con ese nombre | Instalar |
| Instalada y al día | La copia coincide con la biblioteca | Desinstalar |
| Desactualizada | La biblioteca cambió o la skill se movió | Actualizar |
| Editada fuera de Enki | La copia ya no coincide con su manifiesto | Sobrescribir, explícito |
| Nombre ocupado | Carpeta ajena o de otra skill de la biblioteca | Ninguna |

- Solo se actualizan o eliminan carpetas con manifiesto de Enki. Una skill escrita a mano en el mismo
  directorio nunca se sustituye ni se borra.
- La sincronización automática solo toca copias desactualizadas; una copia editada fuera de Enki se
  conserva hasta que el usuario elige **Sobrescribir**.
- Si la skill se mueve o se convierte en paquete dentro de la biblioteca y su origen anterior ya no existe,
  la instalación se reconoce como suya y se actualiza el manifiesto.
- Cada instalación se prepara en una carpeta temporal junto al directorio de skills, no dentro, y se
  sustituye al final con copia de seguridad y reversión: el asistente nunca ve una skill a medio escribir.
- Eliminar una skill de la biblioteca no desinstala sus copias. El listado del sistema las marca como
  «su origen ya no está en la biblioteca» y permite importarlas de nuevo o desinstalarlas.

## Skills del sistema

El botón **Sistema** de la barra de la sección (y su icono en el modo árbol) abre un listado de todas las
skills que hay en los tres directorios, las haya instalado Enki o no:

- **Importar** copia la skill, con sus scripts y referencias, a la carpeta activa de la biblioteca,
  usando la importación normal y su diálogo de conflictos. El manifiesto no se copia.
- **Desinstalar** solo aparece en las instaladas por Enki y pide una segunda pulsación, porque si el
  origen ya no existe la copia instalada es la única que queda.
- Los botones de cada asistente abren su directorio en el gestor de archivos.

## Paquetes en la biblioteca

Para desarrollar skills completas, la biblioteca admite el formato nativo `carpeta/SKILL.md`:

- Una carpeta con `SKILL.md` (sin distinguir mayúsculas) es una skill, no una carpeta navegable. Su id es
  el nombre de la carpeta y aparece en la carpeta que la contiene. Sus Markdown auxiliares no se listan
  como skills. Un `SKILL.md` suelto en la raíz de la biblioteca sigue siendo una skill normal.
- **Convertir en paquete** mueve `<id>.md` a `<id>/SKILL.md` sin cambiar su contenido. No está disponible
  para las skills predeterminadas.
- El detalle muestra el distintivo de paquete, la lista de archivos auxiliares y **Abrir carpeta del
  paquete**, que es donde se añaden scripts, referencias o plantillas.
- Importar, exportar, duplicar, mover y eliminar tratan el paquete entero. Importar una carpeta que es un
  paquete conserva su nombre; con conflicto, **Cambiar nombre** crea `nombre 2` y **Sobrescribir**
  sustituye la carpeta completa en vez de mezclar versiones.

## Flujo de desarrollo recomendado

1. Crear la skill en Enki y, si necesita scripts o referencias, convertirla en paquete.
2. Instalarla en los asistentes desde **Instalar en el sistema**.
3. Editarla en Enki: cada guardado actualiza las copias y Claude Code la recarga en la sesión abierta.
4. Para probar cambios hechos directamente en la copia instalada, importarla desde **Sistema** o
   sobrescribirla desde Enki cuando se quiera volver a la versión de la biblioteca.

## Comprobación

Pruebas en `SkillInstallServiceTests`, `SkillPackageTests`, `SkillsViewModelInstallTests` y
`SkillInstallUiTests`. Todas usan una carpeta personal temporal y una función de entorno vacía: ningún
test escribe en el `~/.claude`, `~/.agents` o `~/.gemini` reales. Solo la composición de
`MainWindowViewModel` construye el servicio sobre la carpeta personal del usuario.

Comprobación manual en cada sistema operativo: instalar una skill y un paquete en los tres asistentes,
confirmar que cada CLI la lista, editarla en Enki y ver el cambio en la sesión abierta de Claude Code,
editar a mano la copia instalada y comprobar que Enki la marca y no la pisa, y crear a mano una skill con
el mismo nombre para comprobar que aparece como nombre ocupado.
