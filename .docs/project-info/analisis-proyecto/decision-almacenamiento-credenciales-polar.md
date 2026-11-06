# Almacenamiento de credenciales de sesión Polar/Supabase

Decisión de la Fase 4 del [roadmap](roadmap.md): los tokens de sesión se guardan en el almacén seguro del usuario, separados de `settings.json` y del vault.

## Implementación

- **Windows:** DPAPI `CurrentUser` protege una única entrada serializada en `%AppData%/Enki/polar-session.dat`. El archivo contiene bytes cifrados y no se incluye en los backups JSON de Enki.
- **Linux:** Secret Service, mediante `libsecret-1.so.0`, guarda una contraseña en la colección predeterminada. Requiere una sesión de Secret Service funcional y desbloqueada. Las operaciones tienen cancelación a los cinco segundos.
- **macOS:** Keychain guarda una contraseña genérica mediante la API `SecItem` de Security.framework.

La entrada contiene ambos tokens para evitar que una actualización de sesión quede a medias. En Linux y macOS se identifica por un hash de la ruta de datos de la instalación; así una compilación Debug con `.devdata/` no lee la sesión de la instalación Release. La marca `PolarLinked`, el correo, el ID del usuario y la caducidad del access token siguen en el JSON global porque no son secretos de sesión.

## Migración y fallos

Al abrir un `settings.json` antiguo con tokens, `SettingsService` comprueba el almacén seguro. Si no existe una sesión segura, guarda allí los tokens heredados. Solo tras confirmar la escritura elimina ambas claves del JSON. Si el almacén falla, conserva el archivo heredado y muestra una notificación; los guardados posteriores reintentan la migración. Un inicio de sesión nuevo falla con un error visible si no puede proteger sus tokens: nunca se escribe una sesión nueva en texto plano.

La lectura fallida de una sesión ya migrada deja el vínculo inutilizable hasta que vuelva a estar disponible el almacén. Desvincular elimina la entrada segura. Si esa eliminación falla, Enki conserva el estado anterior y comunica el error. Borrar la entrada local no revoca por sí mismo un token ya emitido por Supabase.

Los backups siguen redactando `settings.json` global y el del vault, y «Exportar ajustes» sigue redactando ambos tokens. Esto cubre archivos heredados, migraciones pendientes y copias antiguas; importar ajustes nunca importa una sesión. Las copias de seguridad no restauran el vínculo de Polar en otro equipo: hay que iniciar sesión allí.

## Alcance de la verificación

Las pruebas automatizadas usan un almacén en memoria para comprobar migración, fallo de escritura, redacción, lectura posterior y desvinculación. Se ejecutan en Linux; el flujo nativo de Keychain y DPAPI requiere una comprobación manual en macOS y Windows con una sesión de usuario real.
