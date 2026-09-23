# VERSIONADO Y TAGS

> guia corta de como se numeran las versiones de Enki y que tag les corresponde. El detalle de como se
> redactan el titulo y la descripcion de cada release esta en `.docs/pr-merge-release-guidelines.md`,
> seccion 3 — este documento es solo el orden que siguen los numeros y las etiquetas.

## Numero de version

Enki sigue [SemVer](https://semver.org/lang/es/) (`MAJOR.MINOR.PATCH`) tal y como ya dice la cabecera de
`CHANGELOG.md`:

- Mientras el proyecto esta en `0.x`, **cualquier cambio puede romper compatibilidad entre versiones
  menores** — no hay compromiso de estabilidad todavia. El numero que sube es siempre `MINOR` (`0.2.0`,
  `0.3.0`...) salvo que una version solo traiga correcciones puntuales sobre la anterior, en cuyo caso sube
  `PATCH` (`0.2.1`).
- `1.0.0` marca el paso a **estable**: es el momento en que Enki deja de ser beta y empieza a respetar
  compatibilidad entre versiones menores. No hay una fecha objetivo — llega cuando el criterio de
  `.docs/alpha-to-beta.md` ("continuar la validacion antes de publicar") esta satisfecho de forma sostenida
  a lo largo de varias versiones `0.x` reales, con usuarios reales fuera del equipo de desarrollo.

## "Alpha" y "beta" son fases del proyecto, no sufijos de version

Enki no usa tags con sufijo (`-alpha`, `-beta`, `-rc`). El tag es siempre `v<MAJOR.MINOR.PATCH>` a secas,
como ya fija `.docs/pr-merge-release-guidelines.md` seccion 3.3. Lo que cambia entre "alpha" y "beta" es
el **estado del proyecto**, no el numero:

| Fase | Que significa | Cuando termina |
|---|---|---|
| **Alpha** | Desarrollo interno, sin release publica ni paquete instalable para usuarios finales. Es donde ha vivido Enki hasta ahora. | Al publicar la primera release en GitHub con instaladores reales para Windows/Linux/macOS. |
| **Beta** | Hay releases publicas con paquetes instalables (`v0.x.y`), pero el proyecto sigue en `0.x`: puede haber cambios que rompan compatibilidad entre versiones, y la superficie de la app sigue bajo revision activa (ver checklist de `.docs/project-info/qa-manual-release-candidate.md` antes de cada release candidate). | Al llegar a `1.0.0`. |
| **Estable** | `1.0.0` en adelante. Compatibilidad entre versiones menores garantizada. | — |

La primera release publica de Enki (`v0.2.0`) es, por tanto, el tag que abre la fase beta.

## Orden de los tags

- Un tag por version cerrada de `CHANGELOG.md`, nunca uno por commit ni por PR normal.
- Formato fijo: `v<MAJOR.MINOR.PATCH>` (ej. `v0.2.0`), anotado (`git tag -a`), nunca ligero.
- Se crea **despues** de cerrar la seccion `SIN PUBLICAR / UNRELEASED` de `CHANGELOG.md` en
  `## [x.y.z] - aaaa-mm-dd` y de subir `<Version>` en `enki/Directory.Build.props` al mismo numero — los
  dos tienen que coincidir siempre entre si y con el tag.
- Empujar el tag (`git push origin main --follow-tags`) dispara `.github/workflows/release.yml`, que
  compila, empaqueta y publica la GitHub Release automaticamente — ver
  `.docs/project-info/empaquetado-y-distribucion.md`, seccion "Flujo minimo de release", para los pasos
  completos.
