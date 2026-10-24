# Sincronizar el almacén de Enki entre equipos

Esta guía también se puede consultar dentro de Enki, en **Ajustes → Vault y archivos → Sincronización privada del almacén**. El texto integrado vive en las claves `Settings_PrivateSync_*` de `enki/src/enki/localization/Strings*.resx`; al cambiar los pasos de esta guía hay que mantenerlos coherentes en todos los idiomas.

Enki guarda las notas como archivos Markdown en una carpeta local llamada **almacén** (o *vault*). No necesitas una cuenta de Enki para llevar esa carpeta a otro equipo: puedes sincronizarla con Syncthing, iCloud Drive o Google Drive y abrir allí **el mismo almacén**. El proveedor mueve archivos; Enki no muestra su progreso ni controla sus conflictos.

Esta guía sirve para las aplicaciones de escritorio de los proveedores. La mera presencia de archivos en iCloud Drive o Google Drive no equivale a cifrado de extremo a extremo: revisa las condiciones y opciones de privacidad del servicio elegido. Syncthing conecta dispositivos que autorizas; si necesitas una copia adicional fuera de ellos, configúrala por separado.

## Antes de empezar

1. En Enki, abre **Ajustes → Vault y archivos → Carpeta del almacén** y anota la ruta exacta. Desde **Ajustes → Vault y archivos → Abrir carpeta del almacén** puedes comprobar qué carpeta es. Normalmente se llama `enki-documents`, pero un almacén antiguo puede tener otro nombre.
2. Haz una **copia de seguridad** desde Ajustes y guárdala *fuera* de la carpeta que vas a sincronizar. La sincronización replica también borrados y errores; no sustituye al respaldo. Comprueba que la copia se creó sin entradas omitidas.
3. Cierra Enki en los equipos afectados antes de mover o copiar un almacén existente. Espera a que el proveedor termine cualquier transferencia pendiente.
4. Sincroniza **la carpeta raíz completa del almacén**, incluidas las carpetas ocultas `.enki`, `.sessions`, `.tasks` y `.trash`. Ahí viven preferencias del almacén, sesiones, tareas, historial y papelera, además de las notas y adjuntos visibles. No sincronices solo los `*.md` si quieres conservar todo lo asociado a ellos.

El archivo global de instalación y credenciales queda fuera del almacén, en el directorio de datos de Enki de cada equipo. Al abrir el almacén en otro equipo quizá tengas que ajustar preferencias locales o volver a vincular Polar. Un build Debug del repositorio usa `src/enki/.devdata/` para esos datos globales; esa carpeta **no** es el almacén que debes sincronizar.

## Configurar el proveedor

Elige **uno** para cada almacén. Evita que dos programas distintos sincronicen a la vez la misma carpeta.

### Syncthing

1. Instala y abre Syncthing en ambos equipos. Intercambia sus identificadores mediante **Añadir dispositivo remoto** y acepta la conexión en los dos lados.
2. En el equipo que tiene el almacén completo, añade **la ruta exacta de su carpeta raíz** como carpeta Syncthing y compártela con el segundo equipo. Para editar desde ambos, usa el tipo **Enviar y recibir**.
3. En el segundo equipo acepta la carpeta compartida y elige una **carpeta local vacía** como destino. No la apuntes a otro almacén con contenido distinto.
4. Espera a que Syncthing indique que la carpeta está actualizada en ambos equipos. Después abre esa carpeta en Enki con **Abrir almacén existente**. Si ya estás en Enki, usa **Ajustes → Vault y archivos → Carpeta del almacén → Cambiar**.

Syncthing puede crear un archivo `*.sync-conflict-*` cuando el mismo archivo cambia por separado en dos equipos. Consérvalo hasta comparar ambas versiones: Syncthing no fusiona el contenido por ti. [Guía de inicio](https://docs.syncthing.net/intro/getting-started) · [Cómo trata los conflictos](https://docs.syncthing.net/users/syncing.html)

### iCloud Drive (macOS o Windows)

1. Activa iCloud Drive en ambos equipos. Con Enki cerrado, mueve o copia el **almacén completo** a una carpeta propia dentro de iCloud Drive; conserva una copia independiente hasta verificar la subida.
2. Espera a que termine la subida en el primer equipo y la descarga en el segundo. Mantén **toda la carpeta del almacén descargada**: en macOS, Finder → clic secundario → **Mantener descargado**; en Windows, Explorador → clic derecho → **Mantener siempre en este dispositivo**. Verifica que las notas y subcarpetas se pueden abrir sin conexión.
3. Abre el almacén descargado desde Enki en cada equipo. Si has movido el original, actualiza la ruta en **Ajustes → Vault y archivos → Carpeta del almacén**.

Una carpeta que solo muestra archivos bajo demanda puede fallar al leer notas, adjuntos o metadatos sin red. [Apple: mantener descargado en Mac](https://support.apple.com/en-euro/guide/mac-help/mchl1a02d711/27/mac/27) · [Apple: mantener descargado en Windows](https://support.apple.com/en-gb/guide/icloud-windows/icw8531ad6b7/icloud)

### Google Drive para ordenadores (Windows o macOS)

1. Instala Drive para ordenadores e inicia sesión en ambos equipos. Con Enki cerrado, coloca el **almacén completo** dentro de **Mi unidad**. Si partes de un almacén existente, conserva una copia independiente durante el traslado.
2. En las preferencias de Drive, elige **Duplicar archivos** para mantener una copia local completa de Mi unidad. Si usas **Transmitir archivos**, marca la carpeta del almacén como **Disponible sin conexión** en ambos equipos y espera a que termine la descarga.
3. Cuando Drive indique que todo está sincronizado, abre esa carpeta local en Enki en cada equipo y verifica una nota y un adjunto. Tras mover el original, actualiza la ruta en **Ajustes → Vault y archivos → Carpeta del almacén**.

No borres la carpeta original ni cambies el modo de Drive mientras haya cambios pendientes de subir. [Google: transmitir o duplicar archivos](https://support.google.com/drive/answer/13401938) · [Google: uso sin conexión](https://support.google.com/drive/answer/2375012)

## Uso diario y conflictos

- Antes de pasar a otro equipo, espera a que Enki termine de guardar, cierra la aplicación y comprueba que el proveedor terminó de subir. En el siguiente equipo, espera a que termine de bajar **antes** de abrir el almacén. Es especialmente importante si cambiaste tareas, sesiones, preferencias o la misma nota en ambos sitios.
- Enki vigila las **notas abiertas** modificadas desde fuera. Si no había cambios locales pendientes, recarga la nota; si los había, pausa el guardado y permite comparar o elegir una versión. El ajuste **Ajustes → Editor → Vigilar cambios externos en el vault** controla el aviso inmediato; la comprobación al guardar sigue activa aunque apagues esa vigilancia. [Detalle de los cambios externos](cambios-externos-en-el-vault.md).
- La protección del editor se centra en el contenido de las notas abiertas. Los JSON de `.tasks/` y `.enki/` no tienen fusión entre dispositivos. Si los modificas simultáneamente, un proveedor puede dejar una copia en conflicto o imponer una versión. Revisa sus avisos y restaura desde una copia de seguridad si falta información.
- Si ves un conflicto de Syncthing, conserva ambos archivos, compara el `*.sync-conflict-*` con la nota normal y une manualmente lo que quieras mantener. Si Enki muestra su diálogo de conflicto, usa **Ver diferencias** antes de elegir. No elimines ninguna copia hasta comprobar el resultado en los dos equipos.
- Si una carpeta desaparece, queda solo en la nube o el proveedor está pausado, vuelve a ponerla disponible y espera a que termine. No crees otro almacén vacío encima del anterior para «arreglar» la ruta.
- Las sesiones protegidas cifran el contenido de sus notas y adjuntos, pero **no** ocultan nombres de archivo, nombres de sesión ni todos los metadatos. Para información sensible, revisa también la política de privacidad y cifrado del proveedor.

## Comprobación rápida

Con todo sincronizado, crea una nota de prueba en el primer equipo, cierra Enki y espera a que el proveedor termine. Ábrela en el segundo equipo, añade una línea, cierra Enki y espera de nuevo. Comprueba el resultado en el primero y prueba a abrir un adjunto. Mantén la copia de seguridad original hasta haber verificado este recorrido.
