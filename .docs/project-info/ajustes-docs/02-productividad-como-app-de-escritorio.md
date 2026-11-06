# Ajustes — Enki como app de escritorio: pestañas y atajos

> **Alcance**: este documento cubre las dos funcionalidades del panel de Ajustes citadas hacia él en el
> código — §1 el panel de referencia de atajos de teclado (**Ajustes › Atajos**, de solo lectura) y §2 la
> restauración de las pestañas de notas abiertas al reiniciar Enki. Ambas son "productividad de
> escritorio" en el sentido de que no dependen de ningún backend: funcionan sin vault sincronizado ni
> cuenta vinculada, contra el estado puramente local de la instalación.
>
> **Fuera de alcance**: el resto de Ajustes › General (zona de peligro, idioma, página de inicio) está
> en `01-continuidad-y-proteccion-de-datos.md`; el editor y las notificaciones están en
> `04-editor-y-notificaciones.md`.
>
> **Fuente de verdad**: verificado contra
> `enki/src/enki/viewmodels/notes/NotesViewModel.cs`,
> `enki/src/enki/viewmodels/shell/MainWindowViewModel.Composition.cs`,
> `enki/src/enki/models/AppSettings.cs` y la sección "Atajos de teclado" de
> `enki/src/enki/views/settings/SettingsView.axaml:920-1033`.

## §2 — Restaurar pestañas abiertas al reiniciar

Enki recuerda qué notas estaban abiertas en pestañas (Documentos/Notas) y cuál era la activa, y las
reabre automáticamente la próxima vez que arranca — sin acción del usuario, no es un toggle en Ajustes;
es un comportamiento siempre activo del editor de notas.

- **Persistencia** (`AppSettings.cs:209-221`):
  - `OpenNotePaths: List<string>` — rutas completas de las notas abiertas, en el orden de las pestañas.
  - `ActiveNotePath: string?` — cuál de esas rutas era la pestaña activa.
  - `RecentNotePaths: List<string>` — lista aparte de "abiertas o editadas recientemente" (más reciente
    primero, limitada a 10 entradas por `NotesViewModel`); no participa en la restauración de pestañas,
    es un historial independiente para el selector rápido de notas.
- **Guardado** (`NotesViewModel.PersistOpenTabs`, `NotesViewModel.cs:186-192`): se reescribe
  `OpenNotePaths`/`ActiveNotePath` y se llama `SettingsService.Save()` tras cada cambio estructural de
  pestañas (abrir, cerrar, cerrar todas, reordenar) y tras cada cambio de pestaña activa. Es un no-op si
  el viewmodel se construyó sin `SettingsService` (así lo hacen los tests que no necesitan esta
  persistencia).
- **Restauración** (`NotesViewModel.RestoreOpenTabs`, `NotesViewModel.cs:199-218`): se llama **una sola
  vez**, desde `MainWindowViewModel.InitializeShell` (`MainWindowViewModel.Composition.cs:258-261`),
  justo después de construir `NotesViewModel` pero antes de que la ventana quede lista — así las
  pestañas ya están abiertas la primera vez que el usuario navega a Documentos, sin un parpadeo de
  "vacío → se llenan". Reabre cada ruta guardada en orden vía `VaultService.GetNote`; si `OpenNotePaths`
  está vacío no hace nada. Después reactiva `ActiveNotePath` si su nota sigue entre las reabiertas.
  - **Una ruta que ya no existe** (nota borrada o movida fuera del vault seguido) se ignora en
    silencio — `VaultService.GetNote` devuelve `null` para ella y simplemente no se reabre esa pestaña,
    sin error visible al usuario.
- **El espacio de trabajo elegido también se recuerda** (`AppSettings.NotesSessionId`, escrito por
  `NotesViewModel.PersistSelectedSession` en cada cambio del selector y releído por `ReloadSessions` al
  construir el viewmodel): la app reabre Notas en la sesión en la que se cerró, en vez de volver siempre
  al vault entero. Es la otra mitad de restaurar la sesión de trabajo — reabrir las pestañas de una sesión
  mientras el selector apunta al vault dejaba la pantalla contando dos cosas distintas.
  - Una sesión guardada que ya no existe —borrada, o de otro vault tras cambiar de vault— cae al vault en
    silencio, mismo criterio tolerante que una ruta de pestaña que ya no está.
  - Una sesión **cifrada arranca siempre bloqueada** y aun así se vuelve a elegir: la sección sabe enseñar
    ese estado ("desbloquéala en Sesiones") y devolver al usuario al vault sería perder justo la elección
    que se quería recordar. Sus pestañas, en cambio, no se reabren (ver arriba).
  - **Archivos hace lo mismo con su propio selector** (`AppSettings.FilesSessionId`, escrito por
    `FilesViewModel.PersistSelectedSession` y releído por su `ReloadSessions`, igual que aquí): es el mismo
    selector sobre el mismo árbol de carpetas, y que una sección recordara el espacio de trabajo y la otra
    no era una diferencia sin motivo. Cada una guarda en **su propia clave** a propósito: son dos
    selectores independientes, y compartir una sola haría que cambiar de espacio en una moviera la otra a
    la espalda del usuario.

- No hay ningún toggle en Ajustes para desactivar esta restauración: es comportamiento fijo del editor
  de notas, documentado aquí porque el código lo cita explícitamente como funcionalidad de "Enki como
  app de escritorio" (guardar/recuperar sesión de trabajo entre reinicios, igual que un IDE).

## §1 — Atajos de teclado (Ajustes › Atajos)

Panel puramente informativo (`SettingsSection.Shortcuts`, `SettingsView.axaml:920-1033`): lista los
atajos ya implementados en otras partes de la app, agrupados en cinco tarjetas. No hay ningún control
interactivo en esta sección — no se pueden reasignar ni desactivar atajos desde aquí, solo consultarlos.

| Tarjeta | Atajos listados |
|---|---|
| Navegación | `Ctrl+1`…`Ctrl+8` (Dashboard, Notas, Kanban, Calendario, Archivos, Sesiones, Papelera, Skills), `Ctrl+,` (abrir Ajustes), búsqueda global, nota rápida — las dos últimas usan una tecla localizable (`Settings_Navigation_GlobalSearchKey`/`QuickNoteKey`) en vez de un literal fijo |
| Zoom de interfaz | `Ctrl++`/`Ctrl+-` (subir/bajar), `Ctrl+0` (restablecer), rueda del ratón + tecla modificadora (también localizable) |
| Edición de documentos | `Ctrl+B`/`Ctrl+I` (negrita/cursiva), `Ctrl+K` (insertar enlace), `Ctrl+F` (buscar en la nota), `Ctrl+W` (cerrar pestaña), cambiar de pestaña y cambiar de página (ambas con tecla localizable) |
| Kanban | `Ctrl+F` (buscar en el tablero), `N` (nueva tarjeta), tecla de borrar (localizable) para eliminar tarjeta, `Esc` (cerrar diálogo) |
| Visor de PDF | Navegación entre páginas (tecla localizable) |

Las combinaciones marcadas "tecla localizable" arriba son las únicas filas del panel cuya *tecla*
(no solo la descripción) viene de un recurso de idioma (`Settings_Navigation_GlobalSearchKey`,
`Settings_Navigation_QuickNoteKey`, `Settings_EditDocs_SwitchTabKey`, `Settings_EditDocs_PageTabKey`,
`Settings_KanbanSection_DeleteKey`, `Settings_PdfViewer_NavKey`) — el resto de combinaciones (`Ctrl+1`,
`Ctrl+B`, etc.) están escritas literalmente en el XAML y son iguales en todos los idiomas.

Este panel es un documento vivo del lado del código: si un atajo cambia en su implementación real
(por ejemplo en `KanbanView`/`EditorView`/`MainWindow`) y no se actualiza aquí, el panel queda
desincronizado sin que nada lo detecte automáticamente — no hay ningún test que compare los atajos
reales contra este listado.

## Referencias cruzadas

- Zoom de interfaz (`UiZoomPercent`, persistido por vault) tiene su propia sección en
  Ajustes › Apariencia — no es parte de este documento; ver el comentario de
  `SettingsViewModel.OnUiZoomPercentChanged` y `docs/funcionalidad-zoom-interfaz.md`.
- Zona de peligro / continuidad de datos: `01-continuidad-y-proteccion-de-datos.md`.
- Editor de notas (fuente, tamaño de columna, tabulación, autoguardado): `04-editor-y-notificaciones.md`.
