# POLÍTICA DE FIRMA Y COMPROBACIONES MULTIPLATAFORMA

> Resuelve la parte documental de **ALT-04** (`.docs/agent-docs/analysis/new-features/prioridad-alta.md`).
> Complementa a [`empaquetado-y-distribucion.md`](./empaquetado-y-distribucion.md), que describe cómo se
> construyen los paquetes. Aquí se fija qué se comprueba en cada sistema y cómo se firmará cuando haya
> distribución pública. **Nada de lo que sigue en "Firma real" está activado**: requiere certificados,
> cuentas de pago y un cambio de `release.yml` que deben pedirse y aprobarse por separado.

## 1. COMPROBACIONES EN LA INTEGRACIÓN CONTINUA

| comprobación | ubuntu | windows | macos | dónde |
| --- | --- | --- | --- | --- |
| compilar la aplicación y los dos proyectos de pruebas | sí | sí | sí | `ci.yml` › `build-and-test` |
| pruebas de servicio y de ViewModel (`enki.Tests`) | sí | sí | sí | `ci.yml` › `build-and-test` |
| pruebas de interfaz completas (`enki.UITests`) | sí | no | no | `ci.yml` › `build-and-test` (solo Linux) |
| grafo nativo sin navegador (subconjunto de `enki.UITests`) | sí | sí | sí | `ci.yml` › `graph-compatibility` (Windows y macOS) |
| sintaxis de los scripts de empaquetado | sí | no | no | `ci.yml` › `lint-packaging` |

- La batería completa de interfaz no corre en Windows ni macOS porque no se ha comprobado su estabilidad
  en esos runners. Ampliarla es un cambio aparte: ejecutarla primero con `workflow_dispatch` varias veces.
- `WindowsGlobalHotkey`, `MacGlobalHotkey` y `X11GlobalHotkey` son implementaciones nativas. Las pruebas
  que no necesitan un servidor gráfico real se ejecutan en cada sistema; la captura global con teclado
  físico sigue siendo comprobación manual (`qa-manual-release-candidate.md`).
- Los tres jobs de la matriz se llaman `Compilar y probar (ubuntu-latest)`, `Compilar y probar
  (windows-latest)` y `Compilar y probar (macos-latest)`.

### Impedir fusiones con pruebas en rojo

El criterio de cierre «una PR no se puede fusionar con las pruebas en rojo en los tres sistemas» **no se
puede cumplir solo con el flujo de trabajo**: depende de la protección de ramas del repositorio, que es un
ajuste de GitHub y no un archivo versionado. Pasos para quien administre el repositorio:

1. Ajustes › Ramas › regla de protección para `main` y `beta-version-1.0.0`.
2. Activar «Require status checks to pass before merging».
3. Marcar como obligatorias las tres comprobaciones `Compilar y probar (…)` y `Grafo sin navegador (…)`.

## 2. ESTADO ACTUAL DE LA FIRMA

| sistema | situación hoy | aviso al usuario |
| --- | --- | --- |
| macOS | firma **ad-hoc** (`codesign --sign -`) en `build-macos-package.sh` | «desarrollador no identificado» |
| Windows | instalador de Inno Setup **sin firmar** | SmartScreen «Windows protegió tu PC» |
| Linux | `.deb` y `.tar.gz` sin firmar | ninguno |

La firma ad-hoc evita el aviso «Enki.app está dañado», pero no equivale a un certificado de Apple Developer.

## 3. FIRMA REAL (PROPUESTA, SIN ACTIVAR)

### Cuándo

Se activa cuando Enki tenga distribución pública, no antes: los certificados son de pago y caducan. Hasta
entonces el aviso del sistema es el comportamiento esperado y debe constar en las notas de cada release.

### Qué hace falta

| sistema | requisito | resultado |
| --- | --- | --- |
| macOS | cuenta de pago de Apple Developer y certificado «Developer ID Application» | sin aviso de desarrollador no identificado tras notarizar |
| Windows | certificado de firma de código (Authenticode), preferiblemente en un servicio de firma gestionado | desaparece el aviso de editor desconocido; SmartScreen mejora con reputación acumulada |

### Secretos

Los secretos viven solo en los *secrets* del repositorio o de un *environment* protegido de GitHub; **nunca**
en el repositorio, en los scripts ni en los registros. Nombres previstos (los valores no se escriben aquí):

- macOS: `MACOS_CERTIFICATE`, `MACOS_CERTIFICATE_PASSWORD`, `MACOS_KEYCHAIN_PASSWORD`, `APPLE_ID`,
  `APPLE_TEAM_ID`, `APPLE_APP_SPECIFIC_PASSWORD`.
- Windows: `WINDOWS_CERTIFICATE`, `WINDOWS_CERTIFICATE_PASSWORD` (o las credenciales del servicio de firma).

### Pasos de implementación

1. Crear un *environment* `release` con revisores obligatorios y asociarle los secretos de arriba.
2. En el job de macOS de `release.yml`, importar el certificado a un llavero temporal, sustituir
   `codesign --sign -` por `codesign --sign "Developer ID Application: …" --options runtime --timestamp`,
   enviar el `.dmg` con `xcrun notarytool submit --wait` y grapar el ticket con `xcrun stapler staple`.
3. En el job de Windows, firmar `enki.exe` antes de compilar el instalador y firmar después el instalador
   (`signtool sign /fd SHA256 /tf <servidor de marca de tiempo>`).
4. Hacer que los pasos de firma se omitan, con aviso visible, si faltan los secretos: así
   `workflow_dispatch` y las bifurcaciones siguen generando paquetes sin firmar.
5. Añadir un paso de verificación: `codesign --verify --deep --strict` y `spctl --assess` en macOS;
   `signtool verify /pa` en Windows. El job falla si la firma no valida.

### Criterio de cierre

La descarga de macOS abre sin el aviso de desarrollador no identificado (se comprueba con `spctl --assess
--type execute` sobre una copia descargada, con el atributo de cuarentena puesto) y el instalador de
Windows muestra el nombre del editor en lugar de «Editor desconocido».
