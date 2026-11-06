# CORRECTOR ORTOGRÁFICO DEL EDITOR

> Seguimiento de **MED-05**. Subrayado ondulado de las palabras que el diccionario local no conoce.

## 1. CÓMO FUNCIONA

- **Motor:** `WeCantSpell.Hunspell` (MIT, código gestionado: sin red, sin proceso externo, sin biblioteca
  nativa). Lee diccionarios Hunspell (`.dic` + `.aff`) del disco.
- **Idioma:** el de la aplicación. `es-ES` busca `es_ES`, luego `es`, luego cualquier `es_*` (así `es-419`
  usa `es_MX` o `es_ES` si no tiene el suyo). Cambiar de idioma cambia de diccionario sin reiniciar.
- **Dónde busca:** `dictionaries/` junto a la aplicación y la carpeta de datos del usuario
  (`~/.config/Enki/dictionaries/`). Ajustes › Editor › Corrector ortográfico dice cuál está en uso o dónde
  copiar uno, y abre la carpeta.
- **Desactivable:** Ajustes › Editor › Corrector ortográfico (`AppSettings.EditorSpellCheckEnabled`, activo de
  fábrica). Sin diccionario para el idioma, no subraya nada aunque esté activo.
- **Diccionario personal:** `personal-dictionary.txt` en la carpeta de diccionarios, una palabra por línea
  (`SpellCheckService.AddWord` la añade; aún no hay entrada de menú para ello).

## 2. QUÉ IGNORA

Frontmatter, bloques y fragmentos de código, wikilinks, enlaces Markdown, direcciones, fórmulas y etiquetas
HTML (el mismo `MaskNonProse` que las menciones sin enlazar), más: `#etiquetas`, `@menciones`, rutas, nombres
de archivo (`archivo.txt`), palabras junto a cifras (`abc123`, `3rd`), siglas de hasta 8 letras y `camelCase`.

## 3. RENDIMIENTO

La revisión no corre en el hilo de interfaz (`SpellCheckController`): tras cada cambio espera 600 ms, copia el
texto, lo revisa en un hilo de fondo y, si el documento no cambió entretanto, sustituye los subrayados. Mientras
tanto los anteriores se desplazan solos con las ediciones (`TextSegmentCollection`). El repintado solo recorre
los subrayados de las líneas visibles. Máximo 3000 palabras marcadas por revisión. Una nota de unos 2 MB se
revisa en menos de 5 s en segundo plano (prueba incluida); una caché de 50 000 palabras evita repetir
consultas.

## 4. DICCIONARIOS: DECISIÓN PENDIENTE

**Enki no distribuye ningún diccionario.** La mayoría de los diccionarios Hunspell abiertos tienen licencias
(GPL, LGPL, MPL, o varias a elegir) que hay que revisar frente a `license.md` antes de incluirlos en el
repositorio o en los instaladores, y esa decisión es del titular. `dictionaries/README.md` documenta el
formato y la carpeta, y `enki.csproj` copia cualquier cosa que se coloque allí junto a la aplicación.
Consecuencia: **hoy el corrector no subraya nada hasta que el usuario (o el titular, al decidir) coloque
diccionarios**, así que el criterio de cierre «funciona sin red» se cumple solo después de ese paso previo.
Una vez colocados, todo es local.

## 5. LÍMITES CONOCIDOS

- Sin sugerencias en la interfaz (`SpellCheckService.Suggest` existe pero no hay menú contextual) ni entrada
  para añadir palabras al diccionario personal.
- El texto se revisa en bruto: el Markdown oculto por la vista previa (`**`, `[[`) no afecta, pero tampoco se
  distingue el idioma de cada párrafo.
- Pruebas: `SpellCheckServiceTests` (diccionarios Hunspell mínimos creados en carpetas temporales) y
  `SpellCheckUiTests` (editor real de AvaloniaEdit en modo headless). No se ha probado con diccionarios reales.
