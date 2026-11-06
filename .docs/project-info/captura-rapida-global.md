# Captura rápida global

## Objetivo

T2 permite soltar una idea sin navegar por Enki ni traer la ventana principal al frente. La misma
mini-ventana se abre desde el icono de bandeja, la paleta de comandos, el atajo local y, cuando el sistema
lo permite, el atajo global remapeable (`Ctrl+Alt+Espacio` por defecto).

Con la ventana principal ya delante hay además una cuarta puerta, sin ventana de por medio: el botón
**bandeja de entrada** (`InboxOutline`) de la barra de título, el último de los cuatro botones de
contenido (buscar · notificaciones · recordatorios · bandeja de entrada). Abre un popup con el mismo
`QuickCaptureViewModel`, así que borrador, destino y columna recordada son los mismos que en la
mini-ventana; `QuickCaptureViewModel.IsOpen`/`ToggleCommand` son lo único propio de esa superficie.
Se oculta con `AppSettings.InboxPanelEnabled` (Ajustes › Notificaciones). Ver
`notificaciones-y-recordatorios.md` para el reparto de los cuatro botones.

No existe un almacén de «capturas» intermedio. Al enviar, el dato se convierte directamente en una de las
entidades reales del producto:

- una nota Markdown bajo `<vault>/Inbox/`; o
- un `KanbanCard` dentro de la columna elegida del tablero del vault.

## Arquitectura

- `QuickCaptureService` contiene la escritura de dominio. Comprueba el vault, crea `Inbox/` cuando falta,
  usa `VaultService.CreateNote` para la escritura atómica y `KanbanService.Save` para persistir y emitir
  `BoardChanged`.
- `QuickCaptureViewModel` conserva el borrador durante toda la vida de la aplicación. Cerrar con `Esc` no
  lo limpia; solo un envío correcto lo hace. También recuerda por vault el último destino y la columna en
  `VaultSettings`. `IsOpen` es el estado del popup de la barra de título; la mini-ventana sigue usando el
  evento `CloseRequested`, y tanto `Submit` como `Cancel` cierran las dos superficies a la vez.
- `QuickCaptureWindow` es una ventana ligera, siempre encima y sin entrada en la barra de tareas. Es una
  ventana real porque debe funcionar con la principal oculta; aplica explícitamente el mismo
  `UiZoomPercent`, tema y sistema de diseño. `Ctrl+Enter` envía y `Esc` cancela.
- `MainWindow` coordina la ventana visual y el comportamiento opcional de cierre a bandeja. El viewmodel no
  conoce ventanas ni APIs nativas.
- `GlobalHotkeyService` elige un backend nativo y devuelve un resultado tipificado. Un fallo nunca impide
  arrancar: deja disponibles la bandeja, la paleta y el atajo dentro de Enki, y genera una notificación solo
  si el fallo es accionable (ver "Soporte del atajo global").

## Formato de las capturas

La primera línea no vacía se usa como título, sin `#` inicial y limitada a 100 caracteres. Para una nota,
el resto se escribe como cuerpo Markdown bajo un encabezado de nivel 1. Para una tarjeta, el resto se usa
como descripción. `CreateNote` resuelve colisiones de nombre con sufijos (`Idea 2.md`, etc.).

`Inbox/` está siempre en la raíz del vault: nunca cae dentro de una sesión cifrada, por lo que una sesión
bloqueada no obliga a leerla ni abre una vía de escritura dentro de ella.

Si la columna Kanban recordada ya no existe, se utiliza la primera columna disponible. Si el tablero no
tiene ninguna, la ventana conserva el borrador y explica el problema.

## Bandeja y cierre

El icono de bandeja existe mientras Enki está en ejecución y ofrece «Captura rápida», «Abrir Enki» y
«Salir». Pulsar directamente el icono abre la captura.

«Mantener Enki en la bandeja al cerrar la ventana principal» está desactivado por defecto. Activarlo hace
que cerrar o pulsar la X oculte la ventana sin ejecutar el apagado. «Salir» desde la bandeja sí vuelca los
autoguardados, bloquea sesiones, libera hotkeys y termina el proceso.

## Soporte del atajo global

| Plataforma | Implementación | Comportamiento |
| --- | --- | --- |
| Windows | `RegisterHotKey` sobre una ventana de mensajes privada | Registro global y detección de conflicto. |
| macOS | `RegisterEventHotKey` de Carbon | Registro global sin permiso de Accesibilidad. |
| Linux X11 | `XGrabKey`, ignorando Caps Lock/Num Lock | Registro global mientras la sesión sea X11. |
| Linux Wayland | Sin captura de teclas | La limitación queda en el log (sin notificación); se usan bandeja/paleta. XWayland no se presenta como soporte global fiable. |

El backend se vuelve a registrar al remapear el atajo desde Ajustes. Teclas que la API nativa no puede
representar, una combinación ocupada o la ausencia de servidor gráfico producen un aviso localizado, no
una excepción de arranque.

Una limitación del escritorio no genera notificación: bajo Wayland (`Unreliable`) o en una plataforma sin
backend (`Unsupported`) el hecho queda en el log y la captura rápida se sigue ofreciendo por bandeja,
paleta y atajo local. No es algo que el usuario pueda arreglar y no cambia entre arranques, así que
avisarlo cada vez solo apila copias idénticas en el panel de notificaciones.

Sí se avisa de lo accionable —combinación ya ocupada (`Conflict`) u otro fallo (`Failed`)— y aun eso una
sola vez por gesto y motivo: la firma del último aviso se guarda en
`AppSettings.QuickCaptureHotkeyWarningSignature`. Cambiar el atajo, o que el motivo pase a ser otro,
vuelve a avisar; cuando el registro funciona la firma se borra, así que un fallo posterior se notifica de
nuevo.

## Accesibilidad y privacidad

- El foco entra directamente en el campo multilínea.
- Destino, columna, entrada y cierre tienen nombres de automatización; todo el flujo funciona con teclado.
- El resultado se anuncia mediante `NotificationService`; los errores permanecen visibles en la ventana.
- No se usa red ni se envía telemetría. Las notas y tarjetas siguen las mismas rutas de persistencia y
  permisos que las creadas desde las vistas principales.

## Pruebas

`QuickCaptureServiceTests` cubre creación de `Inbox/`, colisiones, columna elegida, fallback de columna,
persistencia real y vault ausente. `QuickCaptureViewModelTests` cubre conservación del borrador, envío,
notificación, preferencias por vault y errores no destructivos. `PopupZoomAndThemeAuditTests` verifica que
la segunda ventana sea la única excepción permitida y aplique zoom. El registro nativo y la integración de
bandeja requieren la matriz manual de `.docs/project-info/qa-manual-release-candidate.md`.
