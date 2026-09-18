# Empaquetado y distribución para usuarios no técnicos

> **Estado**: implementado y verificado en la medida en que este entorno lo permite (ver el detalle de
> qué se ha podido ejecutar de verdad en cada sección). Cubre el punto 1.2 de
> [`roadmap-publicacion-y-mejora-continua.md`](../roadmap-publicacion-y-mejora-continua.md): paquetes
> instalables reales para Windows, Linux y macOS — no solo la documentación de `dotnet publish` — más el
> flujo de release automatizado que los genera sin depender de memoria manual
> (`.github/workflows/release.yml`).

## Por qué hace falta esto

Hoy `README.md` solo documenta `dotnet run`/`dotnet build`, que asume un SDK de .NET instalado — inviable
para repartir Enki a alguien que no es desarrollador. La solución mínima de .NET para esto es un
**publish autocontenido** (`self-contained`): empaqueta el runtime de .NET junto con la app en un único
ejecutable, así que el usuario final no necesita instalar nada.

## Comando base

Desde la raíz del repo:

```bash
dotnet publish enki/src/enki/enki.csproj \
  --configuration Release \
  --runtime <RID> \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  --output publish/<RID>
```

- `--runtime <RID>` — identificador de plataforma (ver tabla siguiente). Sin esto, `dotnet publish`
  produce un build "framework-dependent" que sí exige el runtime de .NET instalado en la máquina
  destino — lo contrario de lo que se busca aquí.
- `--self-contained true` — incluye el runtime de .NET 8 en la salida.
- `-p:PublishSingleFile=true` — empaqueta el `.dll`/`.exe` y sus dependencias managed en un único
  ejecutable. **No** empaqueta las librerías nativas de Avalonia/Skia (ver más abajo) — esas quedan como
  ficheros sueltos junto al ejecutable, no se puede evitar con las opciones actuales del proyecto.
- `-p:PublishTrimmed=false` — **explícito y deliberado**. El *trimming* de IL (`PublishTrimmed=true`)
  reduce el tamaño final pero es agresivo con reflection, y Enki usa serialización JSON con
  `System.Text.Json` sobre varios modelos (`AppSettings`, `KanbanCard`, `Reminder`,
  `PolarTask`/`PolarTaskList`/`PolarSubtask`...) sin `JsonSerializerContext` de generación de código
  (source-gen). Activar trimming sin antes migrar esa serialización a source-gen (o auditar cada tipo con
  `[DynamicallyAccessedMembers]`) arriesga romper la persistencia en runtime de forma silenciosa — un
  `JsonException` o, peor, datos parcialmente deserializados, en la build de release y no en debug. Dejarlo
  fuera de este borrador a propósito; revisar antes de activarlo.

### RIDs (Runtime Identifiers) por plataforma

| Plataforma | RID | Notas |
|---|---|---|
| Windows x64 | `win-x64` | La inmensa mayoría de usuarios de escritorio Windows actuales. |
| Windows ARM64 | `win-arm64` | Copilot+ PCs y similares — opcional, bajo volumen de usuarios hoy. |
| Linux x64 | `linux-x64` | Verificado en esta sesión (ver más abajo). |
| Linux ARM64 | `linux-arm64` | Raspberry Pi / servidores ARM — opcional. |
| macOS x64 (Intel) | `osx-x64` | |
| macOS ARM64 (Apple Silicon) | `osx-arm64` | M1/M2/M3/M4. **No** usar `osx-x64` vía Rosetta como única opción — hay que publicar ambos RIDs de macOS si se quiere dar soporte real a Apple Silicon nativo. |

Enki no fija ningún `<RuntimeIdentifiers>` en `enki.csproj` hoy — no hace falta añadirlo al `.csproj` para
que esto funcione: `--runtime` en la línea de comandos basta para un publish puntual. Solo merece la pena
fijarlo en el `.csproj` si se automatiza esto en CI (ver "Automatizarlo en CI" más abajo).

## Verificación real (linux-x64)

Se ejecutó el comando exacto de arriba contra este repo, en esta sesión, para confirmar que el borrador
no es solo teoría:

```
dotnet publish enki/src/enki/enki.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o /tmp/enki-publish-test
```

Resultado: build correcto, sin warnings ni errores. La carpeta de salida contiene:

- `enki` — ejecutable único autocontenido, ~104 MB (ELF stripped, `x86-64`). Incluye el runtime de .NET 8
  entero, así que el tamaño es esperable para un publish self-contained de una app Avalonia — no es un
  bug ni algo a optimizar sin antes considerar `PublishTrimmed` (ver arriba) o
  `PublishReadyToRun`/compresión adicional si el tamaño se vuelve un problema real para distribución.
- `enki.pdb` — símbolos de depuración (~640 KB). Se puede excluir del paquete final para el usuario con
  `-p:DebugType=none`, a costa de perder stack traces útiles en `LogService` si algo falla en producción;
  probablemente merece la pena conservarlo mientras no haya un sistema de crash-reporting real.
- `default-skills/` — las skills empaquetadas por defecto (`enki.csproj`, `<Content Include=
  "../../../docs/ia-skills/default-skills/*.md">`) — se copian correctamente al publish, confirmando que
  ese `ItemGroup` funciona también fuera de un build de desarrollo.
- `libHarfBuzzSharp.so`, `libSkiaSharp.so`, `libonigwrap.so`, `libpdfium.so` — **librerías nativas que
  quedan como ficheros sueltos junto al ejecutable**, no dentro del single-file. Son dependencias
  transitivas de Avalonia (renderizado, HarfBuzz para shaping de texto), `TextMateSharp.Grammars`
  (resaltado de sintaxis) y `PDFtoImage`/PDFium (visor de PDF de la sección Archivos). **Hay que
  distribuir la carpeta completa, nunca solo el ejecutable suelto** — los tres scripts de empaquetado de
  la sección siguiente lo garantizan copiando siempre el árbol completo del publish, nunca solo el
  ejecutable.

Cross-compilar `osx-arm64` desde este entorno Linux (sin ningún SDK ni herramienta de macOS instalada)
también se ha verificado en esta sesión — es una capacidad estándar del SDK de .NET, no específica de
Enki — y sirvió para probar de extremo a extremo el script de empaquetado de macOS (ver más abajo). `win-x64`
y `osx-x64` no se han podido probar en esta sesión por falta de tiempo de build, pero usan exactamente el
mismo comando con otro `--runtime`.

## Empaquetado real por plataforma

Cada plataforma tiene un script en `enki/packaging/<plataforma>/` que hace el `dotnet publish`
autocontenido de arriba y lo envuelve en el paquete final. Los tres siguen el mismo contrato: aceptan
`--rid`/`-Rid`, `--version`/`-Version` (por defecto, `<Version>` de `enki/Directory.Build.props`) y
`--skip-publish`/`-SkipPublish` (para reempaquetar sin recompilar), y excluyen explícitamente
`local.properties` — las credenciales locales de Supabase de quien compila (ver
`enki.csproj`) — para que nunca viajen dentro de un paquete distribuido.

### Linux — `enki/packaging/linux/build-linux-package.sh`

```bash
enki/packaging/linux/build-linux-package.sh --rid linux-x64 --version 0.2.0
```

Genera en `dist/linux/`:

- **`enki_<version>_<arch>.deb`** — solo si `dpkg-deb` está instalado (Debian/Ubuntu y derivados; siempre
  presente en el runner `ubuntu-latest` de CI). Instala en `/usr/lib/enki/` (el árbol completo del
  publish), con un wrapper en `/usr/bin/enki`, el `.desktop` en `/usr/share/applications/enki.desktop` y
  los iconos en `/usr/share/icons/hicolor/{256x256,512x512}/apps/enki.png` — integración estándar de menú
  de aplicaciones Linux (GNOME, KDE, XFCE...) vía el tema de iconos `hicolor` y la especificación
  freedesktop.org de `.desktop`.
- **`enki-<version>-linux-<arch>.tar.gz`** — se genera siempre, con `install.sh`/`uninstall.sh` que copian
  ese mismo árbol a `/usr` con `sudo`. Es la vía para distros sin `dpkg-deb`, incluida **Arch Linux**, que
  es donde se desarrolla este repo hoy y donde no hay forma de instalar el `.deb` directamente.

Verificado en esta sesión contra este repo: el `.tar.gz` completo (extracción, layout, exclusión de
`local.properties`, `enki.desktop` validado con `desktop-file-validate`). El `.deb` no se ha podido
verificar aquí porque este entorno Arch no tiene `dpkg-deb` instalado (`sudo pacman -S dpkg` lo añadiría);
sí se genera en cada release real porque el job de Linux de la CI corre en `ubuntu-latest`, que lo trae de
serie.

### Windows — `enki/packaging/windows/build-windows-package.ps1`

```powershell
enki\packaging\windows\build-windows-package.ps1 -Rid win-x64 -Version 0.2.0
```

Envuelve el publish con [Inno Setup](https://jrsoftware.org/isinfo.php) (`installer.iss`) y genera
`dist\windows\enki-setup-<version>-win-x64.exe`: instalador con asistente en español/inglés, acceso
directo de menú inicio (y de escritorio, opcional), desinstalador registrado en "Aplicaciones y
características", instalación por usuario sin requerir privilegios de administrador
(`PrivilegesRequired=lowest`) y el icono de `enki.exe` (ya embebido vía `<ApplicationIcon>` en
`enki.csproj`) reutilizado en el instalador y el desinstalador.

`ISCC.exe` (el compilador de línea de comandos de Inno Setup) viene preinstalado en los runners
`windows-latest` de GitHub Actions — no hace falta ningún paso de instalación adicional en CI. Para
compilarlo a mano en un PC de desarrollo con Windows hace falta instalar Inno Setup 6 una vez
(instalador oficial en el enlace de arriba); el script lo busca en el `PATH` y, si no está, en la ruta de
instalación por defecto.

**No verificado en esta sesión** — este entorno es Linux sin acceso a Windows ni a Wine con Inno Setup
instalado. El script y el `.iss` siguen la sintaxis estándar documentada de Inno Setup 6 y las rutas
relativas se han revisado a mano, pero la primera compilación real en CI (o en un PC Windows) es la
verificación pendiente.

### macOS — `enki/packaging/macos/build-macos-package.sh`

```bash
enki/packaging/macos/build-macos-package.sh --rid osx-arm64 --version 0.2.0
```

Arma `Enki.app` (`Contents/MacOS` con el publish completo, `Contents/Resources/enki.icns`,
`Contents/Info.plist` generado desde `Info.plist.template`) siguiendo la
[documentación oficial de Avalonia sobre despliegue en macOS](https://docs.avaloniaui.net/docs/deployment/macOS),
y genera en `dist/macos/`:

- **`Enki-<version>-macos-<arch>.zip`** — siempre; el bundle comprimido con `ditto` (conserva atributos
  extendidos, incluida la firma) cuando está disponible, o `zip` si no.
- **`Enki-<version>-macos-<arch>.dmg`** — solo si `hdiutil` está disponible (macOS real; el job de macOS
  de la CI, en `macos-latest`, lo trae siempre). Incluye un enlace a `/Applications` para el arrastrar-y-soltar
  habitual de instalación en Mac.

El bundle se firma **ad-hoc** (`codesign --sign -`) cuando `codesign` está disponible, para evitar el
aviso "Enki.app está dañado" que Gatekeeper muestra hoy en macOS reciente ante bundles totalmente sin
firmar. Una firma ad-hoc **no** es una firma con certificado de Apple Developer: seguirá apareciendo como
"desarrollador no identificado" al primer intento de abrirlo (el usuario tiene que pasar por
Ajustes → Privacidad y seguridad, o clic derecho → Abrir). Firmar de verdad y notarizar requiere una
cuenta de pago de Apple Developer — fuera de alcance mientras Enki no tenga distribución pública (ver
`README.md`, "Estado del proyecto").

Verificado en esta sesión: el ensamblado completo del bundle y el `.zip`, cross-compilando `osx-arm64`
desde este entorno Linux (sin SDK de macOS) y confirmando con `plistlib` de Python que el `Info.plist`
generado es válido y tiene los valores esperados (`CFBundleVersion`, `CFBundleIdentifier`,
`NSHumanReadableCopyright`...). El `.dmg` y la firma ad-hoc no se han podido verificar porque `hdiutil` y
`codesign` no existen fuera de macOS — sí se ejecutan en cada release real porque el job de macOS de la CI
corre en `macos-latest`.

## Flujo de release automatizado (CI)

`.github/workflows/release.yml` es el pipeline completo — compilar, empaquetar y publicar sin depender de
memoria manual, que es justo lo que pedía este punto del roadmap:

- **`git push` de un tag `vX.Y.Z`** dispara los tres jobs (`linux`, `windows`, `macos` con matriz
  `osx-arm64`/`osx-x64`) en paralelo, cada uno ejecutando el script de empaquetado de su plataforma con
  `--version` resuelto del propio tag (`vX.Y.Z` → `X.Y.Z`, sin el prefijo `v`). Un cuarto job,
  `publish-release`, espera a que los tres terminen y crea la GitHub Release del tag con **todos** los
  paquetes adjuntos (`.deb`, `.tar.gz`, `.exe`, `.zip`, `.dmg`) vía `gh release create`.
- **`workflow_dispatch`** (botón "Run workflow" en la pestaña Actions de GitHub, o
  `gh workflow run release.yml`) ejecuta el mismo pipeline de compilación/empaquetado como prueba, con una
  versión opcional a mano — pero **no** crea ninguna release ni publica nada, porque el job
  `publish-release` solo corre `if: startsWith(github.ref, 'refs/tags/')`. Es la forma de comprobar que el
  empaquetado de las tres plataformas sigue funcionando sin tener que cortar una versión real.

## Flujo mínimo de release

Los pasos manuales para cortar una versión de Enki, en orden — todo lo demás (compilar, empaquetar,
adjuntar los binarios) lo hace `release.yml`:

1. Ejecutar la [checklist de QA manual](qa-manual-release-candidate.md) contra un build de Release.
2. Subir `<Version>` en `enki/Directory.Build.props` a la versión nueva (p. ej. `0.1.0` → `0.2.0`) — es lo
   que ve el usuario en Ajustes → Acerca de (`SettingsViewModel.AppVersion`), así que tiene que coincidir
   con el tag del paso 4.
3. Mover la sección `## [Sin publicar]` de `CHANGELOG.md` a `## [0.2.0] - AAAA-MM-DD` (formato
   [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) que ya sigue el fichero).
4. Confirmar (`git commit`) los dos cambios anteriores, y crear el tag anotado:
   ```bash
   git tag -a v0.2.0 -m "Enki 0.2.0"
   git push origin main --follow-tags
   ```
5. Esperar a que terminen todos los jobs de `release.yml` en la pestaña Actions del repo (unos minutos —
   `linux`, `windows` y las dos ejecuciones en paralelo de `macos` por su matriz `osx-arm64`/`osx-x64`,
   y por último `publish-release`).
6. Revisar la GitHub Release recién creada en el tag: debe tener 5 ficheros adjuntos (`.deb`, `.tar.gz`,
   `.exe`, dos `.zip`+`.dmg` de macOS) y, si se quiere pulir antes de anunciarla, editar sus notas a mano
   (el texto que genera `gh release create` es un placeholder que remite a `CHANGELOG.md`, no un
   changelog en sí).

Para probar el empaquetado sin cortar una versión real, cualquier paso de este flujo se puede reemplazar
por un `workflow_dispatch` de `release.yml` (ver sección anterior) o por ejecutar el script de la
plataforma correspondiente en local.

## Pendiente explícito (fuera de esta fase)

- **Firma de código real**: Authenticode en Windows (evita el aviso de SmartScreen "Windows protegió tu
  PC") y notarización de Apple en macOS (evita el aviso de Gatekeeper más allá de la firma ad-hoc de
  arriba). Ambas requieren certificados/cuentas de pago — tiene sentido revisarlo si Enki pasa a tener
  distribución pública real (ver `README.md`, "Estado del proyecto"), no antes.
- **MSIX / AppImage / `.rpm`**: formatos alternativos mencionados en el roadmap que no se han
  implementado — el `.exe` de Inno Setup y el `.deb`/`.tar.gz` de Linux ya cubren el caso de uso actual
  (instalación de escritorio con desinstalador e icono de menú); añadir más formatos es una extensión
  natural de los mismos scripts si hiciera falta, no un cambio de arquitectura.
- **Verificación real en Windows y macOS**: esta sesión ha verificado todo lo que un entorno Linux sin
  esas plataformas permite verificar (sintaxis, cross-compilación, ensamblado de bundles). La primera
  ejecución real de `release.yml` en CI —o de los scripts en un PC Windows/Mac— es la verificación que
  falta, y es automática en cuanto se empuje el primer tag `v*.*.*`.

## Icono de la aplicación (Fase 10, docs/analisis-proyecto/roadmap.md) ✅ resuelto

Cerrado el hueco que este documento señalaba: `enki/src/enki/assets/icon/` contiene ahora
`enki.ico` (Windows, multi-resolución 16/32/48/64/128/256), `enki.icns` (macOS, familia completa
16–1024 incluidos los `@2x`) y PNGs sueltos de 256/512/1024 que usan hoy el `.desktop`/`.deb`/`.tar.gz` de
Linux (sección "Empaquetado real por plataforma" de arriba) —
monograma "E" (`#EAEAEA`) sobre fondo oscuro (`#121212`) en un cuadrado con esquinas rectas, siguiendo
al pie de la letra "nada de color de marca", "sharp corners" y la paleta monocroma de
[sistema-diseno-web.md §1.3](sistema-diseno-web.md).

Los iconos maestros se toman de `.docs/resources/project-icons/`, que incluye versiones `.png` y `.ico` para
los dos contrastes:

- `assets/icon/enki.ico`, `assets/icon/enki.icns` y `assets/icon/enki-{256,512,1024}.png`: versión
  **oscura** (`E` clara sobre fondo oscuro), adecuada al tema por defecto de Enki y a las barras de
  tareas oscuras más comunes en Windows/Linux.
- `assets/icon/enki-light.ico`, `assets/icon/enki-light.icns` y `assets/icon/enki-light-{256,512,1024}.png`:
  versión **clara** (`E` oscura sobre fondo claro), preparada para el tema claro de la app y para
  escritorios claros del SO. Hoy no hay ningún selector automático de icono por tema: si en el futuro se
  añade, los archivos ya están en el sitio esperado.

- `enki.csproj` fija `<ApplicationIcon>assets/icon/enki.ico</ApplicationIcon>` — el `.exe` publicado en
  Windows deja de mostrar el icono genérico del SO en el Explorador/taskbar.
- `MainWindow.axaml.cs` ajusta `Window.Icon` y el logo del brand header del sidebar en tiempo de ejecución
  según `Application.Current.ActualThemeVariant`: usa `enki-light.ico` / `enki-light-1024.png` en tema
  oscuro y `enki.ico` / `enki-1024.png` en tema claro, para que la E siempre tenga contraste suficiente
  en la barra de título, el taskbar/Dock del SO y el propio sidebar.
- `enki.icns` y `enki-light.icns` se generan en este entorno con Pillow (sin herramientas de macOS
  disponibles — ni `iconutil` ni `png2icns`) construyendo directamente el formato ICNS documentado
  (TOC de entradas `osType`+longitud+PNG: `icp4`/`ic11`/`icp5`/`ic12`/`ic07`/`ic13`/`ic08`/`ic14`/`ic09`/`ic10`),
  verificado con `file`/Pillow. Sigue siendo recomendable regenerarlos con `iconutil -c icns` desde un
  `.iconset` en una máquina macOS real antes de un instalador `.dmg` de producción, como verificación
  cruzada — pero los ficheros actuales ya son válidos y suficientes para publicar.
- Los PNGs de 256/512/1024 son salidas independientes del maestro 1024×1024; `build-linux-package.sh` usa
  hoy los de 256 y 512 para el tema de iconos `hicolor` (ver arriba). El de 1024 queda disponible para
  cualquier otra integración con el SO que pida un PNG más grande.
