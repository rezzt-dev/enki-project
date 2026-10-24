# Editor y biblioteca de habilidades

La sección sigue trabajando directamente con los Markdown de `<vault>/.enki/skills/`.
No añade un almacén de datos ni ejecuta las instrucciones de las skills.

## Interfaz

- **Nueva skill** crea un Markdown con frontmatter y una instrucción inicial en la carpeta activa,
  y abre su editor. Los nombres repetidos reciben un sufijo; no se reemplaza el archivo anterior.
- El árbol selecciona la carpeta activa al pulsar una carpeta. La biblioteca también mantiene el modo
  explorador, la búsqueda global, las etiquetas y la selección para exportar.
- El detalle aprovecha el ancho disponible y mantiene las acciones fuera del área desplazable del
  documento. El inspector desplegable conserva etiquetas, herramientas, permisos y personalidad.
- La lectura reutiliza el renderizador Markdown de las notas, sin permitir editar el archivo.
  **Código Markdown** muestra el documento íntegro, incluido el frontmatter.
- El editor ofrece código o representación Markdown, números de línea, búsqueda con `Ctrl+F`,
  botones de formato (negrita, cursiva, encabezado, lista y código),
  guardado automático y guardado explícito con el botón o `Ctrl+S` (`Cmd+S` en macOS).
- `Ctrl+N` crea una skill; `Ctrl+Shift+N`, una carpeta en la ubicación activa.

## Arrastrar y soltar

Se pueden importar Markdown externos sobre carpetas del árbol, filas y tarjetas de carpeta del
explorador, la raíz y el área de trabajo. Los destinos se resaltan durante el arrastre. El área de
trabajo usa la carpeta activa; el fondo del árbol usa la raíz. También se pueden mover skills de la
biblioteca entre esas ubicaciones.

Los eventos consumidos por una carpeta no se propagan al fondo: una misma entrega no puede acabar
moviendo o importando también a la raíz. Si hay nombres repetidos, el diálogo mantiene el destino
original aunque cambie la carpeta activa mientras se resuelve el conflicto. Soltar el archivo sobre
su propia ubicación no lo sobrescribe ni provoca una excepción.

## Estabilidad y conservación del contenido

- El renombrado sale del estado de edición **antes** de reconstruir las filas. Así, la pérdida de foco
  que provoca la reconstrucción no vuelve a ejecutar el mismo renombrado.
- Las carpetas resuelven colisiones tanto con carpetas como con archivos. `.` y `..` nunca se usan
  literalmente como nombre de una carpeta nueva.
- Mover, renombrar o eliminar contenido con un editor abierto fuerza primero el guardado. Si falla,
  la operación se detiene y el borrador permanece abierto.
- Antes de guardar una skill se compara su contenido en disco con la última versión leída o escrita.
  Un cambio externo impide sobrescribirla, conserva el borrador y muestra el error.
  **Recargar desde disco** permite resolverlo tras confirmar el descarte del borrador. El guardado usa
  archivo temporal y sustitución; los temporales fallidos se limpian.
- Un fallo de guardado no genera reintentos ni notificaciones cada 600 ms. Se puede reintentar al
  editar o guardar explícitamente.
- Cambiar de skill limpia el historial de deshacer de la vista para no mezclar documentos.
- Recargar la biblioteca conserva las marcas de exportación.

## Comprobación

Las regresiones están en `SkillServiceTests`, `SkillsViewModelTests` y `SkillsViewUiTests`.
Las pruebas de interfaz montan el XAML real, incluyendo creación y renombrado con foco activo.
Para una comprobación manual, probar además arrastres desde el gestor de archivos nativo en cada
sistema operativo, carpetas anidadas, conflictos de nombre y un sincronizador modificando una skill
abierta. No basta con probar solo la importación mediante el selector de archivos.
