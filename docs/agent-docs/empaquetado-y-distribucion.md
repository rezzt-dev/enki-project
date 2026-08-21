# Empaquetado y distribución para usuarios no técnicos

> **Estado**: primer borrador, verificado ejecutando los comandos reales contra este repo (no solo
> documentado de memoria). Cubre el mínimo viable — `dotnet publish` autocontenido por plataforma — que
> pedía la Fase 8 de `docs/analisis-proyecto/roadmap.md`. El instalador nativo por SO (MSI/`.deb`/`.dmg`)
> queda fuera de este borrador; ver "Siguiente paso: instaladores nativos" al final.

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
  distribuir la carpeta completa, nunca solo el ejecutable suelto** — hoy no hay ningún mecanismo de
  empaquetado (instalador, `.tar.gz`, `.zip`) que garantice esto; queda como trabajo pendiente explícito
  del "siguiente paso" más abajo.

No se ha podido verificar `win-x64`/`osx-x64`/`osx-arm64` en esta sesión (entorno Linux sin acceso a
Windows/macOS) — la compilación cross-platform con `--runtime` debería funcionar igual (es una capacidad
estándar del SDK de .NET, no específica de este proyecto), pero queda sin confirmar hasta probarse en la
plataforma real o en CI (ver siguiente sección).

## Automatizarlo en CI (opcional, no implementado en esta fase)

`.github/workflows/ci.yml` (Fase 8) hoy solo compila y testea — no publica binarios. Si se quiere que
cada tag de versión genere automáticamente los ejecutables de las tres plataformas, el patrón estándar es
un job de GitHub Actions con matriz `runs-on` (`windows-latest`/`ubuntu-latest`/`macos-latest`) disparado
por `on: push: tags: ['v*']`, cada uno ejecutando el `dotnet publish` de arriba con el RID correspondiente
y subiendo el resultado como `actions/upload-release-asset` o similar. No se implementa en esta fase por
alcance — el roadmap solo pide el "camino" documentado, no el pipeline completo — pero el comando base ya
verificado arriba es exactamente el que iría dentro de ese job.

## Siguiente paso: instaladores nativos

Un ejecutable + carpeta de `.so`/`.dll` sueltos sigue siendo más fricción de la que espera un usuario no
técnico (nada de icono en el menú de aplicaciones, nada de desinstalador, hay que recordar no separar el
ejecutable del resto de ficheros). El camino natural para cada plataforma, en orden de esfuerzo creciente:

- **Windows**: [MSIX](https://learn.microsoft.com/windows/msix/) o un instalador clásico con
  [Inno Setup](https://jrsoftware.org/isinfo.php)/[WiX](https://wixtoolset.org/) — cualquiera de los dos
  envuelve el resultado de `dotnet publish -r win-x64` de arriba sin cambios adicionales en el código.
- **Linux**: paquete `.deb`/`.rpm` (p. ej. con [`dotnet-deb`](https://github.com/quamotion/dotnet-packaging)
  o un `AppImage`, que además evita el problema de dependencias del sistema al ser autocontenido igual
  que el publish de arriba).
- **macOS**: bundle `.app` (estructura `Contents/MacOS/`, `Contents/Info.plist`) + `.dmg` para
  distribución — Avalonia tiene soporte de primera clase para generar el `.app` a partir de un publish
  self-contained, ver la [documentación oficial de Avalonia sobre despliegue en
  macOS](https://docs.avaloniaui.net/docs/deployment/macOS).

Ninguno de los tres requiere cambios en el código de la app en sí — son, en los tres casos, una capa de
empaquetado alrededor del mismo `dotnet publish` ya verificado en este documento. Queda fuera de esta
fase por ser un esfuerzo notablemente mayor (aprender/configurar la herramienta de cada plataforma, firmar
el instalador en Windows/macOS si se quiere evitar el aviso de "editor no reconocido"). El icono de app
real que antes faltaba (ver más abajo) ya no es uno de esos huecos.

## Icono de la aplicación (Fase 10, docs/analisis-proyecto/roadmap.md) ✅ resuelto

Cerrado el hueco que este documento señalaba: `enki/src/enki/assets/icon/` contiene ahora
`enki.ico` (Windows, multi-resolución 16/32/48/64/128/256), `enki.icns` (macOS, familia completa
16–1024 incluidos los `@2x`) y PNGs sueltos de 256/512/1024 para un futuro `.desktop` de Linux —
monograma "E" (`#EAEAEA`) sobre fondo oscuro (`#121212`) en un cuadrado con esquinas rectas, siguiendo
al pie de la letra "nada de color de marca", "sharp corners" y la paleta monocroma de
[docs/sistema-diseno-web.md §1.3](../sistema-diseno-web.md).

Los iconos maestros se toman de `docs/project-icons/`, que incluye versiones `.png` y `.ico` para
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
- Los PNGs de 256/512/1024 son salidas independientes del maestro 1024×1024 para facilitar un futuro
  `.desktop` de Linux o cualquier otra integración con el SO que pida un PNG concreto.
