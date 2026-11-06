# Correcciones de pestañas y paneles — 9 de septiembre de 2026

## Cierre inesperado al manejar pestañas

Los registros de la instalación del 2 y del 9 de septiembre contienen una excepción no controlada
`ArgumentOutOfRangeException: Invalid drag source (Parameter 'triggerEvent')`, originada en
`Avalonia.X11.Selections.DragDrop.X11DragSource.DoDragDropAsync` y propagada desde
`EditorTabStripView.EditorTab_PointerMoved`.

La barra conservaba un evento de pulsación entre movimientos, sin limpiar el gesto al soltar el botón.
Además, el manejador era `async void` y tenía un `finally`, pero no trataba el rechazo del arrastre
nativo. Una fuente de arrastre invalidada podía terminar el proceso.

La reordenación ahora es local a la barra compartida por las vistas completa y simplificada:

- Captura el puntero únicamente al pulsar con el botón izquierdo sobre una pestaña.
- Conserva el umbral de tres píxeles y el indicador del destino.
- Reordena mediante el comando existente del viewmodel al soltar sobre otra pestaña válida.
- Limpia el gesto al soltar, perder la captura, desmontar la vista o cambiar su contexto.
- Comprueba que el origen y el destino continúan abiertos.
- Excluye el botón de cierre del gesto y conserva el cierre con clic central.

No se inicia ninguna transferencia de arrastre del sistema operativo. Las pruebas de interfaz ejercitan
el gesto real con la plataforma Headless; no ejecutan el backend X11.

## Tareas vinculadas

El editor muestra un botón con icono de enlace y contador en la misma cabecera que el título.
Abre un popover con desplazamiento vertical para listas largas y reutiliza
`EditorViewModel.LinkedCards` y `OpenLinkedCardCommand`: cada entrada sigue abriendo la tarjeta real.
El desplegable se cierra al elegir una tarea, cambiar de pestaña o entrar en modo enfoque.
Se reutilizan las traducciones y los estilos existentes.

## Calendario

Mini-calendario, capas, búsqueda, filtros y elementos sin programar se pueden plegar por separado.
El mini-calendario empieza abierto y los demás bloques plegados. La sección sin programar conserva
su condición de visibilidad cuando no hay elementos.

El estado de estos cinco bloques y el del panel lateral completo se guarda en el JSON habitual de
`SettingsService`, mediante propiedades `Calendar*Expanded` y `CalendarSidebarCollapsed` de
`AppSettings`. Se restaura antes del primer renderizado, sin escrituras adicionales al construir
el viewmodel. Plegar un bloque no cambia sus filtros ni las capas activas.

## Sesiones

Se retiran el botón «Modo enlace» y su texto de instrucciones del gráfico. Se conservan la actualización,
los filtros del gráfico y las relaciones existentes.

## Verificación

Se han añadido pruebas de interfaz para reordenar pestañas, cancelar un gesto cuyo origen se cierra,
cerrar desde el botón, abrir nuevamente, alternar documentos con tablas conservando deshacer,
abrir la tarea desde el desplegable y restaurar el estado del calendario desde disco.

Comandos:

```bash
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
dotnet test enki/tests/enki.UITests/enki.UITests.csproj
```

La suite general tiene dos fallos previos en
`MarkdownLineParserTests.Parse_BulletList_PreservesExtraSpaces`, para `"-  Item"` y `"-   Item"`.
Se han reproducido también en un worktree limpio de la revisión original `8fc5b35`.
Los tests esperan que `MarkerEnd` sea 2, mientras que el parser devuelve 3 y 4, respectivamente:
su código documenta explícitamente que consume los espacios adicionales después de la viñeta.
Estos archivos no se modifican en esta corrección.
