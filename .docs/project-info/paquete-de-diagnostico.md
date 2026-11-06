# PAQUETE DE DIAGNÓSTICO EXPORTABLE

> Seguimiento de **MED-08**. Ajustes › Mantenimiento › Diagnóstico › «Exportar paquete de diagnóstico».

## 1. FLUJO

1. El botón genera el paquete en segundo plano (lee y depura los registros).
2. Se abre un diálogo con la lista de archivos y su contenido, tal y como se guardará.
3. «Guardar ZIP…» pide el destino y escribe exactamente lo mostrado (temporal + mover; un fallo no deja un ZIP
   corrupto). «Cancelar» no escribe nada. No hay telemetría: nada se envía.

## 2. CONTENIDO

| archivo | qué lleva |
| --- | --- |
| `LEEME.txt` | qué contiene el paquete y qué no |
| `resumen.txt` | versión de Enki, sistema, arquitectura, .NET, procesadores, memoria, idioma, desfase horario; en Linux, sesión (`wayland`/`x11`) y escritorio; si hay vault y si el registro de depuración está activo (solo «sí/no») |
| `ajustes.json` | ajustes de la aplicación: solo booleanos, números y textos cortos |
| `registros.txt` | las últimas 2000 líneas / 256 KB de los `enki-*.log`, depurados |

## 3. QUÉ NUNCA ENTRA

Notas, tarjetas, recordatorios; rutas del vault, de la carpeta personal o de cualquier sitio; nombre de usuario
y de equipo; correos, claves, tokens, contraseñas.

- **Ajustes.** Una propiedad pasa solo si su nombre no sugiere datos personales (`path`, `vault`, `token`,
  `key`, `secret`, `password`, `email`, `user`, `url`, `endpoint`, `folder`, `session`, `note`, `file`,
  `account`, `shortcut`, `family`, `name`…, y los `…Id`) **y** su valor tiene forma inofensiva: booleano, número o texto de hasta 40
  caracteres sin `/`, `\`, `@` ni `:`. Listas y diccionarios no se vuelcan, solo su tamaño. Una prueba rellena
  con un valor «personal» todas las propiedades de texto, lista y diccionario de `AppSettings` (también las
  futuras) y comprueba que no sale ninguno.
- **Registros y resumen** (`DiagnosticsPackageService.Scrub`): primero se sustituyen los valores conocidos (ruta
  del vault y de los vaults recientes, carpeta personal, carpeta de datos, usuario, equipo, correo y tokens de
  Polar); lo que sigue a `<vault>`/`<home>`/`<datos>` en la misma ruta se borra (ahí van los nombres de nota);
  después JWT, `Bearer`, `clave=valor` de claves/tokens/contraseñas, claves con prefijo conocido, credenciales
  en URL, cadenas de consulta, correos, rutas absolutas de Windows y Unix, GUID y cadenas largas de aspecto
  secreto. Los nombres de archivo `.cs` de las trazas de pila se conservan (`in VaultService.cs:line 123`), sin
  su carpeta.

## 4. LÍMITES CONOCIDOS

- Un mensaje de registro que contenga el **título** de una nota fuera de una ruta no se puede reconocer: por
  eso el diálogo enseña todo antes de guardar, y el aviso final recuerda revisarlo.
- Una ruta de Unix con espacios que no empiece por el vault ni por la carpeta personal se recorta en el primer
  espacio; la parte posterior (p. ej. `Plan` de `/mnt/Mis Notas/Plan.md`) puede quedar. Las rutas de Windows se
  borran hasta el final de la línea o hasta el siguiente `:`.
- Los registros están al nivel configurado (por defecto, avisos y errores).

Pruebas: `DiagnosticsPackageServiceTests` (12) y `DiagnosticsPreviewViewModelTests`.
