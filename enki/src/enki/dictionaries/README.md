# DICCIONARIOS DEL CORRECTOR ORTOGRAFICO

carpeta de diccionarios [Hunspell](https://hunspell.github.io/) que enki empaqueta junto a la aplicacion.
el corrector del editor (`SpellCheckService`) busca aqui y en la carpeta de datos del usuario
(`ajustes > editor > corrector ortografico > abrir carpeta de diccionarios`).

- cada idioma son dos archivos con el mismo nombre: `es_ES.dic` y `es_ES.aff`; el nombre sigue el codigo
  de idioma de la aplicacion (`es_ES`, `es_419`, `en_US`, `en_GB`, `de_DE`, `fr_FR`)
- si no hay diccionario exacto se usa el del mismo idioma (`es_419` usa `es_MX` o `es_ES`)
- sin diccionario para el idioma activo el corrector no subraya nada
- el diccionario personal es `personal-dictionary.txt` en la carpeta de datos del usuario, una palabra por
  linea

**licencias:** esta carpeta no incluye ningun diccionario de terceros. cada diccionario tiene su propia
licencia (muchos son GPL, LGPL o MPL) y distribuirlo con enki exige revisarla frente a `license.md`; esa
decision no esta tomada. mientras tanto cada usuario puede copiar los diccionarios que prefiera a su carpeta
de datos.
