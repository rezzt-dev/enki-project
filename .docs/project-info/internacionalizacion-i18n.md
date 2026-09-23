# Internacionalización de Enki

> Estado revisado el 9 de septiembre de 2026. Este documento describe el código actual y sustituye
> el antiguo plan de extracción de cadenas. Hay **1.584 claves en cada uno de los siete archivos
> `.resx`**: un recurso neutral y seis culturas explícitas. La presencia de una clave no demuestra
> por sí sola que su traducción sea correcta; la cobertura estructural y la revisión lingüística
> son comprobaciones distintas.

## 0. Alcance

| Idioma | Cultura |
|---|---|
| Inglés estadounidense | `en-US` |
| Inglés británico | `en-GB` |
| Alemán | `de-DE` |
| Francés | `fr-FR` |
| Español de España | `es-ES` |
| Español latinoamericano | `es-419` |

Se localizan la interfaz, los mensajes de validación, las notificaciones nuevas, los diálogos propios,
las etiquetas de accesibilidad, los textos iniciales y las instrucciones de las plantillas CSS.
En inglés británico se usa, por ejemplo, `colour`, `organise`, `recognise` y `customisable`.

Los nombres de producto y de temas publicados, las siglas técnicas (`RGB`, `HTML`, etc.), las rutas y
los identificadores de almacenamiento permanecen estables. Los nombres de idioma se muestran en su
forma nativa para que cualquier usuario pueda reconocer su opción.

El contenido editable de notas, sesiones, tarjetas, carpetas y skills no se traduce al cambiar la
interfaz. Esto incluye contenido importado y material externo. Los datos de bienvenida pertenecen al
usuario una vez creados: no se sobrescriben ni se intentan reconocer por su título para retraducirlos.

## 1. Infraestructura

### 1.1. Recursos

`enki/src/enki/localization/Strings.resx` es la fuente neutral en inglés estadounidense.
`Strings.en-US.resx` contiene exactamente los mismos valores. Las demás culturas tienen su archivo
completo, sin depender del fallback para completar funcionalidades.

Las claves tienen prefijos por módulo, por ejemplo `Settings_General_Language_Title`.
`Strings.ResourceManager` resuelve los recursos compilados. No hay almacenes JSON de traducciones,
contenedores de DI ni duplicados de entidades de negocio.

### 1.2. Idioma activo

`LocalizationService.Instance.SetLanguage` normaliza el código mediante `SupportedLanguages.Find`.
Si recibe un código desconocido, utiliza `en-US`, evitando excepciones al arrancar con ajustes
importados o editados a mano. La composición de la shell detecta por separado que la elección
persistida no es válida y muestra el selector inicial.

El servicio actualiza la cultura activa, la cultura del hilo y las culturas predeterminadas de los
nuevos hilos. Notifica `PropertyChanged("Item")`, el nombre que usa el indexador de Avalonia.
Los formateadores que puedan ejecutarse desde un contexto asíncrono antiguo deben usar explícitamente
`LocalizationService.Instance.CurrentCulture`, en vez de confiar en la cultura capturada por ese contexto.

### 1.3. XAML y propiedades calculadas

Las vistas usan `{loc:Tr Clave}`; `TrExtension` crea un binding al singleton. Las propiedades
calculadas se actualizan desde `MainWindowViewModel.RefreshAllLocalizedLabels`, que coordina los
módulos, la navegación, la paleta de comandos y la búsqueda global. No se reconstruye la ventana.

Al añadir una propiedad calculada localizada, incluir su notificación en el refresco del módulo y
verificar que el módulo esté conectado al coordinador. El test histórico de conteo de llamadas es
solo una heurística: no demuestra que cada instancia esté conectada correctamente. Desde entonces se
comprueba además, propiedad a propiedad, que toda etiqueta calculada localizada se vuelva a anunciar,
recorriendo qué métodos alcanza el refresco de cada clase.

Los overlays de edición abiertos —tarjeta Kanban, tarea y lista de Polar, recordatorio— no se
reconstruyen en el `Reload()` de su página, porque hacerlo descartaría lo que el usuario llevara
escrito: se refrescan en el sitio. Sobreviven a ir a Ajustes y volver, así que sí entran en la cadena.

### 1.4. Plurales

`Plural.Get` resuelve claves con sufijos `_one` y `_other`. Francés usa singular para 0 y 1; las otras
culturas admitidas, para 1. Usar frases completas y argumentos, evitando concatenar sufijos o imponer
el orden de las palabras de un idioma a los demás.

### 1.5. Fallback

Las claves inexistentes muestran `[[Clave]]` en Debug. En Release existe un fallback al recurso fuente.
Esta degradación no sustituye la traducción: los tests deben detectar ausencias antes de distribuir.

### 1.6. Persistencia y formatos regionales

JSON, identificadores, nombres ISO de notas diarias y valores CSS siguen siendo invariantes.
`RelativeTimeFormatter` centraliza tiempos relativos y fechas visibles: las fechas absolutas usan el
formato regional `g`, incluidos el orden de día/mes y el reloj de 12 o 24 horas. La variante compacta
abrevia el mes manteniendo su posición regional. Los campos propios de fecha usan la cultura activa.

## 2. Persistencia del idioma y arranque

`AppSettings.Language` contiene el código BCP-47. Un valor ausente o no admitido requiere elegir idioma.
El idioma guardado se aplica antes de construir la ventana.

`LanguageSetupViewModel` guarda la selección antes de continuar. Si falla la escritura, conserva
la configuración anterior, muestra una razón traducida y permite reintentar sin salir del formulario.
Un guardado fallido no cambia el idioma de la aplicación, pero la razón se resuelve en el idioma que el
usuario acaba de pulsar mediante el parámetro de cultura de `LocalizedTextFormatter.Resolve`: en el primer
arranque la cultura activa sigue siendo la de origen, y el aviso en inglés no le serviría a quien acaba de
elegir alemán. El mensaje solo ocupa espacio cuando existe.

El tablero Kanban puede construirse con `deferSeedCreation: true`: si no existe un tablero válido,
espera a que `InitializeShell` llame a `EnsureSeedBoard` con el idioma ya elegido. Un tablero que se
haya cargado de disco no se modifica. Esto corrige la creación anticipada de ejemplos en inglés antes
de que el usuario seleccionara alemán, francés o español.

La nota de bienvenida y la sesión inicial se crean después de elegir idioma, mediante sus recursos.

## 3. Experiencia de cambio en Ajustes

Ajustes › General ofrece un `ComboBox` con los seis nombres nativos. La selección se guarda y se aplica
sin reiniciar. La shell actualiza los textos derivados; notas, rutas, nombres personalizados y fuentes
seleccionadas se conservan.

El valor «Fuente predeterminada» es una etiqueta de presentación: se vuelve a traducir sin guardar
esa etiqueta como un nombre de fuente. La configuración mantiene `null` para la fuente predeterminada.
Las fuentes personalizadas conservan su nombre exacto.

Un fallo al guardar el idioma restaura la elección anterior y genera una notificación en el mecanismo
existente. Si tampoco puede escribirse el historial, el aviso se mantiene en memoria y se registra el
problema en el log para poder informar al usuario aun con un fallo de disco.

## 4. Notificaciones que siguen el idioma

`AppNotification` mantiene sus campos históricos `Title` y `Message` como respaldo, y admite
`LocalizedTitle` y `LocalizedMessage`. No existe un segundo historial.

`LocalizedText` es un modelo POCO con clave, argumentos, contador opcional, separador opcional o texto
literal. `LocalizedTextFormatter` construye y resuelve esos datos. Los argumentos admiten textos
localizados anidados, necesarios para razones de error, listas y plurales.

Ejemplo:

```csharp
_notificationService.PushLocalized(
    LocalizedTextFormatter.Format("Notification_NoteRenameCollision_Title"),
    LocalizedTextFormatter.Format("Notification_NoteRenameCollision_Message", finalTitle),
    NotificationSeverity.Info);
```

`finalTitle` es contenido del usuario: permanece literal aunque coincida con una traducción.
`NotificationItemViewModel` resuelve el texto al mostrarlo. El historial vuelve a traducirse incluso
tras cerrar y abrir la aplicación. La vista de detalle también actualiza su instancia al refrescar.

**Compatibilidad histórica:** las notificaciones de versiones anteriores solo contienen texto ya
renderizado. Se conserva ese texto original: no se deducen claves ni argumentos mediante coincidencias
que podrían alterar nombres del usuario. Por tanto, un historial previo a esta actualización puede
contener avisos en el idioma en que se generaron. Las nuevas notificaciones de la aplicación guardan
metadatos de localización.

## 5. Casos especiales

### 5.1. Errores de plataforma y servicios externos

`Exception.Message` y las respuestas libres del servidor no se muestran como mensajes de interfaz.
Las operaciones de archivos usan `VaultFailureReasons`; autenticación y sincronización presentan
razones propias según el tipo de fallo o el estado HTTP. Los detalles técnicos se conservan en el log.

Las validaciones de skills mantienen el contrato `InvalidOperationException`, transportando la clave
y los argumentos en `Exception.Data` mediante `LocalizedValidationErrors`. Así se conserva el mensaje
específico sin tener que analizar una frase ya traducida.

### 5.2. Plantillas de temas

Las instrucciones exportadas se resuelven en el idioma activo. Los comentarios de categorías también
se traducen, mientras que nombres de variables y colores hexadecimales no cambian. La plantilla debe
seguir pasando `ThemeCssParser.Parse` en todos los idiomas.

### 5.3. Selector de colores

El selector de Avalonia incluye textos internos que no aparecen en los `.axaml` del proyecto.
`LocalizedColorPicker` adapta sus etiquetas de accesibilidad con bindings y muestra códigos
hexadecimales en las muestras de paleta, conservando la edición del control original.

Referencia del control: [documentación de Avalonia](https://docs.avaloniaui.net/controls/input/selectors/colorpicker).
La prueba de interfaz abre el desplegable y verifica las etiquetas y la paleta; hay que mantenerla al
actualizar Avalonia porque la adaptación depende de los nombres de su plantilla.

### 5.4. Diálogos nativos

Enki traduce los títulos y filtros que entrega al selector de archivos. Los botones y textos que
pertenecen al diálogo nativo del sistema operativo siguen la configuración de ese sistema.

## 6. Verificación automatizada

- Paridad de claves y valores no vacíos en las seis culturas.
- Igualdad entre el recurso neutral y `en-US`.
- Conservación de argumentos y sintaxis válida de formatos compuestos.
- Referencias literales de recursos existentes, incluidos XAML y notificaciones.
- Ausencia de textos visibles fijos en XAML, excepto marca, símbolos y atajos explícitos.
- Detección de nuevas copias del inglés en alemán/francés, con excepciones explícitas para palabras
  compartidas, siglas y formatos. Esta prueba es una barrera contra omisiones, no un corrector lingüístico.
- Detección de traducciones caducadas que aún incrustan literalmente la frase inglesa de origen
  (por ejemplo, tras acortarse la cadena fuente) en alemán, francés y las dos variantes de español.
- Cambio de idioma, persistencia, conservación de fuentes y recuperación tras fallos de escritura,
  incluido el idioma en que se explica un fallo de escritura durante el primer arranque.
- Anuncio de cada etiqueta calculada localizada al cambiar de idioma, con la lista de tipos exentos por
  recrearlos su padre documentada junto a quién los recrea.
- Creación diferida de Kanban y conservación de tableros previos.
- Notificaciones tras serialización y reapertura, plurales anidados y preservación de títulos del usuario.
- Fechas regionales y plantillas CSS válidas.
- Controles reales de Ajustes y primer arranque, refresco de bindings y selector de colores, mediante Avalonia Headless.

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
dotnet test enki/tests/enki.UITests/enki.UITests.csproj
```

Los tests que modifican la cultura global se ejecutan sin solaparse con otras colecciones de tests.
Todos los nuevos tests de persistencia usan directorios temporales.

## 7. Revisión antes de publicar

Además de las pruebas automatizadas, realizar un recorrido visual de cada idioma: primer arranque,
Ajustes, navegación, editores y confirmaciones, errores, temas, importación/exportación y notificaciones.
Comprobar el alemán y francés con textos largos, tamaños de ventana pequeños y zoom aumentado.
Validar el idioma del sistema operativo distinto al de Enki y la persistencia tras reiniciar.

La auditoría automatizada no sustituye una revisión lingüística nativa ni una inspección visual en los
sistemas operativos de distribución. No interpretar el número de claves o los tests aprobados como
certificación de perfección lingüística o ausencia absoluta de recortes.

## 8. Añadir o modificar textos

1. Añadir la clave al recurso neutral y traducirla en los seis recursos explícitos en el mismo cambio.
2. Usar `{loc:Tr ...}` en XAML y el servicio de localización en propiedades de presentación.
3. Para notificaciones persistidas, usar `PushLocalized` con claves y argumentos sin renderizar;
   no pasar un mensaje previamente traducido como `Literal`.
4. Usar `_one`/`_other` para cantidades y documentar el significado de argumentos nuevos.
5. Conectar propiedades calculadas al refresco del módulo sin reemplazar datos editables del usuario.
6. Ejecutar los tests relevantes y revisar la pantalla cuando haya riesgo de recortes o bindings obsoletos.
7. No ampliar las excepciones de texto inglés compartido para evitar traducir una cadena pendiente.
