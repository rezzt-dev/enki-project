# Logo personalizado del sidebar

> **Alcance**: cómo el usuario cambia el icono del brand header de la barra lateral por una imagen suya,
> dónde acaba esa imagen en disco, y cómo se reduce y se recorta al pintarla.
>
> **Fuera de alcance**: el icono de la ventana y del sistema operativo (`assets/icon/`, ver
> `empaquetado-y-distribucion.md`), que sigue siendo el de Enki y no se toca desde Ajustes; y las imágenes
> dentro de notas, que son otra cosa (`docs/imagenes-en-notas.md`).
>
> **Fuente de verdad**: `services/BrandLogoService.cs`, `viewmodels/common/BrandLogoImage.cs`, `models/AppSettings.cs`,
> `viewmodels/settings/SettingsViewModel.Appearance.cs`, `viewmodels/shell/MainWindowViewModel.cs`,
> `views/settings/SettingsView.axaml` y `views/shell/MainWindow.axaml(.cs)`.

## 1. Qué ve el usuario

Ajustes › Apariencia › **Logo de la barra lateral**:

| Control | Qué hace |
|---|---|
| **Imagen que se muestra**: `Por defecto` / `Personalizada` | Elige entre el icono de Enki (adaptativo al tema) y la imagen importada. `Personalizada` está deshabilitada mientras no haya ninguna. |
| **Elegir imagen** | Diálogo del sistema (PNG, JPG, WEBP, BMP, GIF, ICO). Importarla activa `Personalizada` sola: elegir una imagen es querer verla. |
| **Quitar** | Borra el archivo y vuelve al icono por defecto. Solo aparece si hay imagen. |
| **Forma**: `Cuadrada` / `Circular` | Cómo se recorta. Deshabilitado sin imagen personalizada. |

La vista previa de 44 px al lado de los botones usa el mismo recorte y el mismo encaje que tendrá el
sidebar, y debajo se leen las dimensiones y el peso de lo que hay guardado ("1200 × 800 px · 1,4 MB").

**La forma circular es la única excepción admitida al `CornerRadius="0"` del sistema de diseño**
(`sistema-diseno-web.md`), y solo llega a aplicarse a la imagen del usuario: el icono por defecto es una
insignia cuadrada, y recortarla en círculo le comería las esquinas, así que se dibuja siempre cuadrado.

## 2. Dónde vive

| Qué | Dónde |
|---|---|
| La imagen | `%AppData%/Enki/brand-logo.<ext>` (`~/.config/Enki/` en Linux, `.devdata/` en un Debug desde el código) — un solo archivo, con la extensión del formato original. |
| Si está en uso | `AppSettings.UseCustomBrandLogo` |
| La forma | `AppSettings.BrandLogoShape` (`"Square"` / `"Circle"`) |

La imagen **no** va dentro de `settings.json`. Meterla en base64 engordaría un JSON que se lee y reescribe
en cada cambio de cualquier preferencia, y rompería "Exportar ajustes", que es un archivo de texto pensado
para moverse entre máquinas. Que sea un archivo aparte es además lo que permite volver al icono por defecto
sin perder la imagen importada, y recuperarla tal cual al reactivar el modo personalizado.

## 3. Qué se guarda y qué se reduce

**En disco va el archivo elegido, byte a byte.** Ni reescalado ni recodificado. Enki es una aplicación
local: el logo vive en el disco del usuario, no viaja por la red ni se sincroniza, así que ahorrar unos
cientos de kilobytes nunca compensa degradar la única imagen que el usuario ha elegido a mano. Guardar el
original es además lo que deja margen para pintarla bien en cualquier pantalla y a cualquier zoom, en vez
de congelar en disco la resolución que hoy parezca suficiente.

La extensión con la que se guarda sale de lo que Skia reconoce al leer la cabecera, no del nombre del
archivo de origen: un `.png` que en realidad era un JPEG no puede quedarse en `%AppData%` mintiendo sobre lo
que lleva dentro. Un formato que Skia sepa decodificar pero que no esté en el diálogo (HEIF de una foto de
iPhone, AVIF) se guarda con su propio nombre en vez de rechazarse.

Techo de entrada: 64 MB de archivo de origen. Es lo único que acota el tamaño ahora que la imagen se guarda
entera — se relee y se decodifica en cada arranque para pintar un cuadro de 32 px, y un TIFF de 200 MB sería
pagar un pico de RAM absurdo por el mismo resultado.

**La reducción es cosa de quien pinta**, en memoria y al vuelo: `ViewModels.BrandLogoImage` (en
`viewmodels/common/`). Un `Image` de Avalonia escala lo que le den con el modo de interpolación del control,
y el de por defecto no filtra: reducir 2000 px a 32 se convierte en quedarse con uno de cada sesenta
píxeles, que es exactamente el aspecto dentado que tenía el logo antes de esto. `BrandLogoImage.Decode`
reduce una sola vez con el remuestreo de calidad de Skia (`Bitmap.DecodeToWidth`, que sí promedia todos los
píxeles de origen) hasta dejar el lado **menor** en los píxeles de pintado —el menor y no el mayor porque la
forma circular recorta al llenar el cuadro (`UniformToFill`), y es el lado corto el que tiene que dar la
talla—, y lo que llega al control ya está cerca de su tamaño final. Las vistas ponen además
`RenderOptions.BitmapInterpolationMode="HighQuality"` para ese último tramo.

Los tamaños de pintado son el peor caso de cada sitio: `SidebarPixels` = 128 (32 px de cuadro × 2 de HiDPI ×
2 del zoom de interfaz al 200 %) y `PreviewPixels` = 176 para la vista previa de 44 px de Ajustes. Una imagen
que ya es más pequeña se decodifica tal cual: ampliarla solo la emborronaría. El icono de Enki por defecto
(un PNG de 1024 px) pasa por el mismo camino, que es de donde le venía el mismo dentado.

Un fallo al importar (archivo que ya no está, formato que Skia no decodifica, escritura denegada) deja el
logo anterior intacto y se anuncia por `NotificationService`: la copia solo ocurre cuando la imagen ya se ha
reconocido como tal.

## 4. Cómo llega a la pantalla

`BrandLogoService` no conoce Avalonia; solo toca disco y avisa con `LogoChanged`. El reparto es:

- **`MainWindowViewModel`** decide *qué* logo toca. Escucha `SettingsService.SettingsChanged` (encendido y
  forma) y `BrandLogoService.LogoChanged` (la imagen cambió sin que cambiara ningún ajuste), y publica
  `CustomBrandLogoBytes`, `BrandLogoCornerRadius` (0 o 16) y `BrandLogoStretch`. Guarda una firma de lo
  último aplicado para no releer el archivo en cada guardado de una preferencia cualquiera.
- **`MainWindow.axaml.cs`** decide *cómo* se pinta, porque el icono por defecto depende del tema activo de
  Avalonia y ese ya era el único sitio que lo resolvía (junto al icono de la ventana). `UpdateBrandIcon`
  pasa los bytes personalizados por `BrandLogoImage.Decode` si los hay y, si no —o si resultan ilegibles—,
  cae al PNG de `assets/icon/`, decodificado al mismo tamaño y con el mismo filtrado.
- **`MainWindow.axaml`** aplica el recorte: el `Border` de 32×32 del brand header enlaza
  `CornerRadius` a `BrandLogoCornerRadius` con `ClipToBounds="True"`, que en Avalonia recorta a la
  esquina redondeada (`Visual.ClipToBoundsRadius`).

Los bytes se pasan como `byte[]` y no como ruta a propósito: así la vista nunca mantiene abierto el archivo
que Ajustes puede estar a punto de reemplazar.

Un ajuste encendido cuya imagen ya no existe (borrada desde fuera de Enki) cae al icono por defecto en vez
de dejar el hueco vacío, y Ajustes aparece con el segmento en `Por defecto`. La comprobación está en los dos
sitios a propósito, para que ninguno dependa de que el otro corrija el ajuste guardado.

## 5. Pruebas

- `enki.Tests/Services/BrandLogoServiceTests.cs` — que el archivo se guarde intacto, que la extensión salga
  del formato real, un solo archivo por logo, rechazos y el evento `LogoChanged`. Sin plataforma gráfica.
- `enki.Tests/ViewModels/BrandLogoImageTests.cs` — la aritmética del tamaño de decodificación (lado menor,
  sin ampliar) y la lectura de cabecera. El bitmap en sí no se decodifica en pruebas: el host headless de
  `enki.UITests` no decodifica de verdad, así que probarlo allí tampoco diría nada.
- `enki.UITests/BrandLogoSettingsUiTests.cs` — la tarjeta de Ajustes montada sobre la vista real: la vista
  previa, el recorte circular y que el modo personalizado no pueda quedarse encendido sin imagen.
