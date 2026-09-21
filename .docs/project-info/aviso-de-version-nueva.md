# Aviso de versión nueva (T9)

Comprobación de si existe una release más reciente en GitHub, al arrancar (opcional) y a mano desde
Ajustes. Corresponde a T9 de `.docs/funcionalidades-pendientes-priorizadas.md` §4.5.

**Solo avisa.** Enki no descarga ni ejecuta nada: lo único que llega a abrirse es la página de la release
en el navegador del sistema. Ni hay auto-actualización ni está previsto que la haya.

## Decisión: desactivado de fábrica

§9 del documento de funcionalidades dejaba abierto el valor por defecto de "Buscar actualizaciones al
iniciar". Se elige **desactivado**, que es lo coherente con el resto del producto: con el ajuste apagado,
Enki no hace ninguna petición de red por su cuenta jamás. El botón "Buscar ahora" de Ajustes › General sigue
disponible siempre, así que nadie se queda sin poder comprobarlo.

Cuando sí está activado, la comprobación automática es **una cada 24 h como mucho**, y consiste en una única
petición GET anónima a la API pública de GitHub: sin identificadores, sin telemetría y sin cookies — la
misma petición que haría abrir la página de releases a mano.

## Piezas

| Archivo | Qué hace |
| --- | --- |
| `services/SemanticVersion.cs` | Versión SemVer comparable: `MAYOR.MENOR.PARCHE` con *prerelease* opcional. |
| `services/UpdateCheckService.cs` | La petición, el límite de frecuencia y las reglas de "¿hay novedad?". |
| `models/UpdateInfo.cs` | La release encontrada: versión, título, enlace, notas y fecha. |
| `viewmodels/settings/SettingsViewModel.Updates.cs` | Ajustes › General: interruptor, "Buscar ahora", "Descargar" y "Omitir esta versión". |
| `MainWindowViewModel.CheckForUpdatesOnStartupAsync` | La comprobación del arranque y el aviso en la campana. |

### Por qué `SemanticVersion` y no `System.Version`

`Assembly.GetName().Version` devuelve un `System.Version`, cuyo orden de comparación no conoce las
*prereleases* — para él `1.0.0-beta` no existe. Y precisamente la regla de "1.0.0-beta es **anterior** a
1.0.0" es la que decide si se avisa o no. `SemanticVersion` implementa el orden de
[semver.org](https://semver.org) §11 (incluido el §11.4 de los identificadores de *prerelease*), acepta la
`v` inicial con la que GitHub etiqueta las releases e ignora los metadatos de compilación (`+build`), como
manda la especificación.

### Qué se descarta, y por qué

`UpdateCheckService.Evaluate` es lógica pura (sin red ni ajustes) para poder recorrer las reglas en los
tests. Descarta:

- **Borradores** (`draft`): no son releases publicadas.
- **Prereleases**, tanto las marcadas como tales en GitHub como las deducidas de la etiqueta
  (`v1.0.0-beta.1`): Enki no tiene canal alfa/beta, y quien quiera una la busca a propósito.
- **La misma versión o una anterior** a la instalada.
- **La versión omitida** y cualquiera anterior a ella; una posterior vuelve a avisar.
- **Cualquier release cuyo enlace no apunte al repositorio oficial** (`https://github.com/...`). Criterio de
  seguridad de T9: "solo abrirá enlaces del repositorio oficial". Si la API respondiera con una URL de otro
  sitio —proxy, respuesta manipulada, error del servidor— el aviso entero se descarta; no se "arregla"
  cayendo a la página genérica de releases, porque eso enmascararía una respuesta manipulada. Al abrirlo,
  `UrlOpener` lo vuelve a filtrar con `ExternalLinkPolicy`.

### Robustez

Timeout corto (8 s) y ni un solo camino que propague excepción: un fallo de red en el arranque devuelve
`Failed` y el arranque sigue como si nada, sin aviso ni toast. **Solo la comprobación manual** convierte ese
fallo en un mensaje visible, que es exactamente lo que pide el criterio EARS. El motivo técnico queda en el
log, igual que en `WebClipService`.

La comprobación del arranque se lanza y se olvida (`_ = CheckForUpdatesOnStartupAsync()`), así que no
retrasa el arranque ni un milisegundo.

## Interfaz

- **Campana:** un aviso con la versión y un botón "Abrir" que lleva a la página de la release.
- **Ajustes › General › Actualizaciones:** interruptor "Buscar al iniciar", versión instalada, "Buscar
  ahora", y —cuando hay novedad— versión encontrada, notas de la release y los botones "Descargar" y
  "Omitir esta versión". Si el aviso saltó al arrancar, Ajustes ya aparece sembrado con esa release, sin
  tener que volver a buscarla.

El documento de la funcionalidad también mencionaba «Recordármelo más tarde». No se ha añadido como botón
porque es exactamente lo que ya pasa si no se pulsa nada: el aviso vuelve en la siguiente comprobación. Un
botón que no hace nada visible es ruido, no una función.

Nota sobre "Omitir": pulsar "Buscar ahora" después de omitir una versión **vuelve a encontrarla**. Omitir
silencia el aviso automático, no una comprobación que el usuario pide a mano.

## Ajustes persistidos (`AppSettings`)

- `CheckUpdatesOnStartup` — `false` de fábrica.
- `LastUpdateCheckUtc` — última comprobación, automática o manual; es el límite de las 24 h.
- `SkippedUpdateVersion` — versión omitida.

## Pruebas

- `tests/enki.Tests/Services/SemanticVersionTests.cs` — análisis (incluidos `v0.10.0`, `1.0.0+build.5` y el
  `0.1.0.0` de cuatro componentes que devuelve el ensamblado) y orden, con el ejemplo del propio documento
  (`0.9.0 < 0.10.0`) y las reglas de *prerelease* de SemVer §11.4.
- `tests/enki.Tests/Services/UpdateCheckServiceTests.cs` — las reglas de decisión sin red, y con red
  simulada (`FakeHttpMessageHandler`) y reloj inyectado: respuesta correcta, fallo de red, 403 por límite de
  la API, ajuste desactivado (ni una petición), límite de 24 h, que la comprobación manual lo ignora, y que
  se envía el `User-Agent` que GitHub exige.
