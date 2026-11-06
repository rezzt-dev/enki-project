# ANALISIS DE PRODUCTO: PRIORIDAD BAJA

> ideas con valor real pero con poca demanda inmediata, alto coste o que dependen de validar antes
> con usuarios. no se empiezan hasta cerrar la prioridad alta y la media. el indice con el avatar y el
> resumen esta en [`indice.md`](./indice.md).

**Fecha del analisis:** 2026-11-06 · rama `beta-v2.0`
**Metodo:** igual que [`prioridad-alta.md`](./prioridad-alta.md); "no aparece" significa "sin
coincidencias en el codigo" y se comprueba antes de empezar cada tarea.

---

## BAJ-01 · BUSQUEDA SEMANTICA LOCAL OPCIONAL

- **tipo:** nuevo
- **usuario afectado:** quien tiene miles de notas y no recuerda las palabras exactas.
- **evidencia:** `NoteSearchService` y `NoteQueryService` buscan por texto, etiquetas y propiedades. ya
  existe un proveedor local (`OllamaService`) que podria generar vectores.
- **propuesta:** indice de vectores en `.enki/` del vault, desactivado de fabrica, calculado en local y
  nunca enviado a un servicio externo.
- **criterio de cierre:** una consulta por concepto devuelve notas sin la palabra literal, sin red.

## BAJ-02 · EXPORTAR E IMPORTAR LA PERSONALIZACION

- **tipo:** nuevo
- **usuario afectado:** quien personaliza mucho (temas, atajos, panel, barra lateral) y usa varios
  equipos o quiere compartir su configuracion.
- **evidencia:** los temas se importan con `CustomThemeService`, pero atajos (`ShortcutRegistry`), orden
  de la barra lateral y disposicion del panel (`DashboardLayoutEngine`) no tienen un paquete comun.
- **propuesta:** un unico fichero de perfil que reuna esas piezas sin datos personales, con vista previa
  antes de aplicarlo.
- **criterio de cierre:** importar el perfil en otro equipo reproduce la interfaz sin tocar el vault.
- **estado:** implementado. Perfil único en `ajustes > apariencia`, vista previa y confirmación, temas
  con colores, atajos, barra lateral y diseño del panel. Excluye datos del usuario y solo escribe
  metadatos de personalización en `.enki/`; las notas y adjuntos no cambian. Reiniciar completa la
  aplicación de las preferencias. Ver [perfil de personalización](../../../project-info/perfil-de-personalizacion.md).

## BAJ-03 · PUBLICAR NOTAS COMO SITIO ESTATICO

- **tipo:** nuevo
- **usuario afectado:** quien quiere compartir apuntes de solo lectura con otras personas.
- **evidencia:** `NoteExportService` y `NoteExportRenderer` exportan una nota a HTML, PDF y PNG, pero no
  un conjunto de notas enlazadas entre si.
- **propuesta:** exportar una carpeta o etiqueta a un directorio de paginas html con sus enlaces,
  adjuntos y sin notas de sesiones protegidas.
- **criterio de cierre:** el resultado se abre sin servidor y respeta `ExternalLinkPolicy`.

## BAJ-04 · REPASO ESPACIADO PARA ESTUDIO

- **tipo:** nuevo
- **usuario afectado:** estudiante que quiere memorizar desde sus propias notas.
- **evidencia:** no aparecen tarjetas de repaso (`Flashcard`) ni calculo de intervalos.
- **propuesta:** marcar pares pregunta-respuesta dentro de una nota y repasarlos con intervalos
  crecientes, guardando el estado en el vault sin tocar el markdown. sin rachas ni puntos.
- **criterio de cierre:** un repaso no modifica el texto de la nota original.

## BAJ-05 · MAS IDIOMAS DE INTERFAZ

- **tipo:** mejora
- **usuario afectado:** usuarios que no leen castellano, ingles, aleman ni frances.
- **evidencia:** hay recursos para `es-ES`, `es-419`, `en-US`, `en-GB`, `de-DE` y `fr-FR`
  (`enki/src/enki/localization/`); `FlowDirection` ya se trata en 8 ficheros, lo que facilita idiomas de
  derecha a izquierda.
- **propuesta:** incorporar portugues y catalan primero, con la comprobacion de paridad que ya usan las
  pruebas de `Localization/`.
- **criterio de cierre:** el nuevo recurso pasa la prueba de paridad sin claves faltantes.

## BAJ-06 · TABLEROS COMPARTIDOS PARA UNA PAREJA O UN EQUIPO PEQUEÑO

- **tipo:** exploracion
- **usuario afectado:** pareja, familia o equipo de dos o tres personas. hoy no es el avatar.
- **evidencia:** polar vincula una cuenta por persona; no aparece el concepto de espacio compartido ni
  de permisos.
- **propuesta:** no implementar todavia. validar primero si existe demanda real, porque obliga a
  repensar identidad, permisos y privacidad, que son el nucleo del producto.
- **criterio de cierre:** decision documentada de seguir o descartar, con usuarios reales consultados.
