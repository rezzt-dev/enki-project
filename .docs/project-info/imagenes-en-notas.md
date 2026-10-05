# Imágenes en las notas

> **Alcance**: cómo se inserta una imagen en una nota Markdown, dónde acaba en disco, cómo se resuelve su
> ruta y cómo la dibuja el live preview del editor.
>
> **Fuera de alcance**: el resto del live preview (encabezados, listas, código, alineación HTML), que se
> describe en el propio código de `views/editor/Markdown/`, y el módulo Archivos, que gestiona los
> archivos del vault que no son notas.
>
> **Fuente de verdad**: `services/NoteImageAttachments.cs`, `services/NoteImageService.cs`,
> `viewmodels/editor/EditorViewModel.Images.cs`, `views/editor/EditorView.Images.cs`,
> `views/editor/Markdown/MarkdownImageSource.cs`, `views/editor/Markdown/MarkdownImageCache.cs`,
> `views/editor/Markdown/MarkdownElementGenerator.cs` y `views/editor/Markdown/MarkdownLineParser.cs`.

## 1. Las cuatro formas de insertar una imagen

Todas terminan en el mismo sitio —`EditorViewModel.AttachImageBytes` / `AttachImageFile` guardan el
adjunto y devuelven la ruta relativa, y `EditorView.InsertImageReferences` la escribe en el documento—, así
que ninguna puede comportarse distinto de otra:

| Gesto | Qué hace |
|---|---|
| **`Ctrl+V`** con una captura o un archivo de imagen en el portapapeles | Guarda la imagen como adjunto de la nota e inserta el `![](…)`. |
| **Arrastrar** uno o varios archivos de imagen sobre el editor | Igual, e inserta en el punto exacto donde se soltó, no donde estaba el cursor. |
| **Botón de imagen** de la barra de formato | Abre el diálogo de archivos del sistema (selección múltiple). |
| **`/imagen`** | Lo mismo que el botón. |

El pegado se intercepta en `EditorView.OnEditorKeyDown` porque leer el portapapeles es asíncrono y hay que
mirar qué trae antes de decidir. El orden de preferencia está pensado para no romper el pegado de texto de
siempre:

1. **Archivos de imagen primero.** Copiar una imagen en el explorador deja en el portapapeles el archivo
   *y* su ruta como texto; lo que el usuario quiere pegar es la imagen.
2. **Un mapa de bits solo si no viene también texto.** Al copiar de un navegador o de un procesador de
   textos suelen viajar los dos, y ahí lo que se está pegando es texto.
3. **Cualquier otra cosa** cae en `TextEditor.Paste()`, el pegado normal de AvaloniaEdit.

Una imagen insertada se escribe **en su propia línea, sin crear una línea vacía debajo**: reutiliza el
salto existente y solo añade uno detrás si hay texto que separar. Al final de la nota no añade salto.
El cursor justo después del `)` final mantiene la imagen visible; entrar en su sintaxis permite editarla.

**Pegar sustituye la selección**, como cualquier otro pegado: que pegar una imagen fuera la única forma de
pegado que deja intacto lo seleccionado sería una excepción que nadie espera. El borrado y la inserción van
en la misma transacción del documento (`InsertPastedImages`), así que un único `Ctrl+Z` deshace las dos. El
arrastre no borra nada: inserta en el punto donde se soltó, que es lo que el gesto promete.

## 2. Dónde se guardan los adjuntos

Una imagen pegada o arrastrada a `Proyecto.md` se guarda en **`Proyecto.attachments/`**, carpeta hermana
de la nota (`NoteImageAttachments.FolderFor`). La nota y sus imágenes se mueven, se copian y se respaldan
juntas, y el nombre de la carpeta dice a quién pertenecen sin abrir nada.

Las carpetas cuyo nombre termina en `.attachments` (sin distinguir mayúsculas) quedan ocultas en
las vistas de Notas y Archivos, a cualquier profundidad. Sus contenidos tampoco se incluyen en las
búsquedas recursivas ni en los recuentos de elementos visibles. Siguen en el vault y las imágenes
se muestran normalmente dentro de las notas.

- El nombre del archivo es `imagen-AAAAMMDD-HHMMSS.png` para una captura del portapapeles, y el nombre
  original saneado (sin espacios ni caracteres inválidos) para un archivo arrastrado. Una colisión añade
  `-2`, `-3`…
- Una imagen del portapapeles **de la que no se obtiene un solo byte** —un codificador que falla, una
  plataforma que anuncia el formato y luego no lo sirve— no se guarda: `EditorViewModel.AttachImageBytes`
  avisa con la misma notificación que cualquier otro fallo de adjuntado y no inserta nada. Un adjunto de
  cero bytes con un `![](…)` apuntando a él dejaría en la nota una imagen rota indistinguible de una
  borrada por error.
- **Un archivo que ya está dentro del vault se enlaza donde está, no se duplica**: arrastrar a una nota una
  imagen que ya vive en el módulo Archivos debe enlazarla, no dejar dos copias que mantener a la vez.
- La ruta que se escribe en el Markdown es **relativa a la nota**, con `/` como separador y con los
  espacios y paréntesis escapados (`%20`, `%28`, `%29`), porque el `)` de cierre de `![](…)` no admite
  espacios sin ambigüedad.
- Toda escritura pasa por `VaultService.WriteBinaryFile`, así que un adjunto de una nota que vive en una
  **sesión protegida queda cifrado** con la misma clave que la nota.
- **Renombrar o mover una nota se lleva su carpeta de adjuntos** (`VaultService.MoveAttachmentsFolder`), y
  si el movimiento cruza la frontera de una sesión cifrada, cada imagen se re-cifra igual que la nota. Sin
  esto, renombrar una nota dejaría cada `![](Proyecto.attachments/…)` apuntando a una carpeta que ya no se
  llama así.

**Limitación conocida**: borrar una nota **no** borra su carpeta de adjuntos. Es deliberado — la nota se va
a la papelera y puede restaurarse, y una restauración que recuperase el texto pero no las imágenes sería
peor que dejar una carpeta huérfana. Vaciar la papelera no la limpia; hay que borrarla desde el módulo
Archivos.

## 3. Qué sintaxis se reconoce

`MarkdownLineParser` produce un span `MdSpanKind.Image` que **cubre también el `!`** — antes el parser veía
un `Link` con el `!` fuera, y el exportador tenía que mirar el carácter anterior para distinguir una imagen
de un enlace.

- `![texto alternativo](ruta)` — con título opcional (`![alt](foto.png "Título")`), del que la ruta es lo
  que va hasta el primer espacio.
- `![[archivo.png]]` — la incrustación de Obsidian. Solo cuenta como imagen si el destino tiene extensión
  de imagen; `![[Otra nota]]` es una transclusión de nota, que este editor no dibuja, y se degrada a
  wikilink normal para que al menos siga siendo navegable.
- **Tamaño explícito**, en las dos formas: `![alt|300](ruta)`, `![alt|300x200](ruta)`,
  `![[archivo.png|300]]`. Un sufijo que no sea un tamaño válido (`![diagrama|versión 2](…)`) se queda como
  parte del texto alternativo: es más probable que sea contenido del usuario que un tamaño mal escrito.

El texto alternativo no se dibuja junto a la imagen: es el reemplazo de la imagen cuando no hay imagen (y
lo que viaja al `alt` del HTML exportado), no una leyenda.

## 4. Cómo se resuelve la ruta

`MarkdownImageResolver` clasifica el destino en archivo, `data:` o URL remota, y para un archivo busca de
lo más explícito a lo más adivinado:

1. ruta absoluta, tal cual;
2. relativa a la carpeta de la nota;
3. dentro de la carpeta de adjuntos de la nota;
4. por nombre en todo el vault — solo para un destino sin carpeta, que es como se escribe un
   `![[archivo.png]]`.

Si ningún candidato existe se devuelve el primero, para poder decir "falta *este* archivo" con una ruta
concreta. Resolver toca disco, así que solo se hace desde el hilo de carga de la caché, nunca durante el
layout del editor.

**Imágenes remotas** (`http`/`https`): se descargan y se dibujan, pero el interruptor **Ajustes › Editor ›
Imágenes remotas** (`AppSettings.EditorRemoteImages`, por defecto activo) lo desactiva. Con él activo,
abrir una nota implica una petición a un servidor de terceros, que revela la IP del usuario y el momento
exacto en que la lee; las imágenes del propio vault y las `data:` incrustadas no dependen del interruptor y
se dibujan siempre. Al cambiarlo, `MarkdownImageCache.ForgetFailed()` olvida las que no pudieron cargarse
para que el siguiente repintado lo reintente, sin tener que cerrar y reabrir la nota.

### 4.1 Qué puede y qué no puede descargar una imagen remota

Una URL dentro de una nota es texto que puede no haber escrito quien la lee, así que la descarga pasa por
`RemoteImagePolicy` (`services/RemoteImagePolicy.cs`):

- **Destino.** Solo direcciones públicas. Loopback, rangos privados, link-local (incluido el
  `169.254.169.254` de los metadatos de nube), CGNAT, multicast, documentación y reservados se rechazan,
  en IPv4 y en IPv6, y también en las formas en que una IPv4 puede venir disfrazada dentro de una IPv6
  (`::ffff:127.0.0.1`, 6to4, NAT64). La comprobación **no se hace sobre la cadena de la URL** —un dominio
  público puede resolver a `127.0.0.1`, y puede cambiar de respuesta entre la comprobación y la conexión—
  sino en `SocketsHttpHandler.ConnectCallback`: se resuelve el nombre, se descartan las direcciones no
  públicas y se conecta a esas mismas direcciones ya validadas.
- **Redirecciones.** Se siguen a mano, como mucho 3, validando cada salto igual que el primero (cada uno
  abre su propia conexión, así que pasa por el mismo filtro) y rechazando el paso de `https` a `http`.
- **Tamaño.** `MaxRemoteBytes` (16 MB), cortado **leyendo por streaming**: el cuerpo deja de leerse en
  cuanto pasa del tope, aunque el servidor no mande `Content-Length`.
- **Tiempo.** 20 s de presupuesto total para toda la descarga, redirecciones incluidas.

Un destino bloqueado se pinta como imagen rota (estado `Failed`), exactamente igual que una imagen que
falta, y se anota con `LogService.Warning`. Esto **no** protege contra un píxel de seguimiento alojado en
un servidor público: para eso está el interruptor.

## 5. Cómo se dibuja (y por qué es instantáneo)

El generador de elementos de AvaloniaEdit corre **dentro del layout** de cada repintado. Ahí no puede
decodificarse nada: un PNG de 3 MB decodificado en ese punto congelaría el editor en cada pulsación de
tecla. De ahí `MarkdownImageCache`:

- una imagen ya vista se devuelve al instante con una búsqueda en un diccionario — es lo que hace que
  desplazarse por una nota llena de capturas cueste lo mismo que por una de solo texto;
- una imagen nueva se decodifica en un hilo de fondo y, al terminar, pide un redibujado;
- se decodifica **ya reducida al ancho al que se va a dibujar** (`Bitmap.DecodeToWidth` escala dentro de
  Skia, durante el propio decode) y multiplicada por el factor de escala de la pantalla, de modo que una
  foto de 6000 px nunca ocupa en memoria más de lo que ocupa en pantalla, y en una pantalla HiDPI no se ve
  borrosa. El ancho se redondea a saltos de 200 px para que redimensionar la ventana no estrene una entrada
  de caché por píxel.
- El tamaño natural se lee de la **cabecera** del archivo con `SKCodec`, sin decodificar sus píxeles, para
  no ampliar nunca una imagen más pequeña que la columna. Un SVG se rasteriza con `SvgRenderHelper`
  pasándole `allowUpscale: false`, por el mismo motivo: en una miniatura del módulo Archivos ampliar un
  icono pequeño es lo correcto, dentro de una nota no.
- La caché es estática y compartida por todo el proceso (la misma imagen puede estar en varias pestañas), a
  cambio de un presupuesto de memoria explícito por debajo del cual se desalojan las entradas menos usadas.
  Un bitmap desalojado no se libera a mano: una línea visual ya dibujada puede seguir enseñándolo.
- Una imagen ya cargada se comprueba contra su fecha de modificación como mucho una vez cada dos segundos,
  así que editar una captura en un programa externo se refleja en el editor casi al momento.

**Tamaño con el que se dibuja**, por orden: manda el tamaño explícito de la nota; si no lo hay, el tamaño
natural; y en cualquier caso nunca más ancha que la columna de texto, reduciendo el alto en la misma
proporción. Mientras carga —o si no se pudo cargar— queda un hueco del alto de dos líneas con el nombre del
archivo: una imagen que falta tiene que verse como tal, no desaparecer sin dejar rastro de que la nota
pedía algo ahí.

**Alineación y espaciado**: las imágenes que ocupan su propia línea se centran horizontalmente en la
columna, también al redimensionar el editor y dentro de bloques HTML con otra alineación. No llevan
margen vertical adicional; su baseline aloja las métricas del texto dentro del alto de la imagen para
no reservar una franja vacía que el usuario no pueda borrar. Las imágenes integradas en texto o tablas
conservan la disposición del contenido que las rodea.

**Interacción**: el cursor dentro del `![…](…)` devuelve el Markdown en crudo para poder editarlo, igual
que con un enlace o una negrita. **Ctrl+clic** abre la imagen con el visor del sistema (con una copia
temporal descifrada si vive en una sesión protegida) o en el navegador si es una URL.

## 6. Exportación a HTML

`NoteExportRenderer` ya incrustaba las imágenes locales como `data:` URI para que el HTML exportado fuera
autocontenido. Con el span `Image` propio, además: reconoce `![[archivo.png]]`, y el tamaño explícito viaja
como atributos `width`/`height`, de modo que el documento exportado se ve como se veía en el editor.

## 7. Cómo se prueba

- `enki.Tests/Views/Editor/Markdown/MarkdownLineParserTests.cs` — la sintaxis reconocida.
- `enki.Tests/Services/NoteImageAttachmentsTests.cs` — nombres y rutas, sin tocar disco.
- `enki.Tests/Services/NoteImageServiceTests.cs` — dónde acaba el archivo, y que renombrar/mover una nota
  se lleve sus adjuntos.
- `enki.UITests/EditorViewImagesUiTests.cs` — lo puramente visual, contra el `EditorView` real: que
  aparece un `Image` del tamaño calculado, que la línea crece para alojarlo, que el cursor encima devuelve
  el Markdown, y que una imagen que falta deja un hueco visible. Y el gesto de pegar de punta a punta,
  contra el portapapeles de verdad del `TopLevel`: que `Ctrl+V` con archivos de imagen los guarda como
  adjuntos con su nombre saneado e inserta la referencia relativa; que varias entran cada una en su línea;
  que una que ya vive en el vault se enlaza sin duplicarse; que pegar sobre una selección la sustituye y un
  solo `Ctrl+Z` lo deshace; y que pegar texto —o un mapa de bits que además trae texto— sigue pegando texto
  sin crear adjuntos. La plataforma headless de Avalonia
  devuelve bitmaps de 1×1, así que las medidas se comprueban contra el tamaño natural leído de la
  cabecera, no contra los píxeles decodificados.
