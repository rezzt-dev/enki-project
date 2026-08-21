# Decisión: almacenamiento de credenciales de sesión Polar/Supabase

Documento de decisión derivado de la Fase 4 del [roadmap](roadmap.md) (análisis §2, §5) —
["Evaluar (no necesariamente implementar en esta fase si el esfuerzo es alto) mover `PolarAccessToken`/
`PolarRefreshToken` fuera de `settings.json` hacia un almacén seguro del SO..."]. Este documento cubre esa
evaluación explícitamente, tal y como pedía ese punto del roadmap.

## Contexto

`AppSettings.PolarAccessToken`/`AppSettings.PolarRefreshToken` (`models/AppSettings.cs`) se persisten en
texto plano en `%AppData%/Enki/settings.json`, junto al resto de ajustes de la app. El `refresh_token` de
Supabase (GoTrue) es de larga duración: cualquier proceso o persona con acceso de lectura a ese fichero
puede reutilizarlo para impersonar la sesión del usuario hasta que se revoque manualmente o se desvincule
desde Ajustes.

Los vectores de exposición del fichero en sí (backup `.zip`, "Exportar ajustes") ya quedaron cerrados en
esta misma fase — ver [roadmap.md, Fase 4](roadmap.md#fase-4). Lo que queda abierto, y es objeto de este
documento, es el propio fichero `settings.json` en disco: cualquiera con acceso de lectura al perfil de
usuario del SO (otro proceso, malware, alguien con acceso físico al equipo, un backup de terceros no
gestionado por Enki) puede leerlo directamente.

## Opciones evaluadas

1. **Mantenerlo en `settings.json` en texto plano** (estado actual tras esta fase, con los vectores de
   fuga por fichero-completo ya cerrados).
2. **Mover los tokens a un almacén seguro nativo del SO** — DPAPI en Windows
   (`ProtectedData.Protect`/`Unprotect`, disponible en .NET sin dependencias nativas adicionales), Secret
   Service/libsecret en Linux (requiere interoperabilidad nativa vía `Avalonia` o un binding P/Invoke a
   `libsecret`, sin soporte de primera clase en .NET), Keychain en macOS (requiere interoperabilidad nativa
   equivalente, tampoco soportado de forma nativa por .NET).
3. **Cifrar los tokens con una clave derivada localmente** (p. ej. `AesGcm` con una clave guardada en otro
   fichero) sin usar el almacén del SO — mitiga la lectura casual del JSON, pero no aporta protección real
   frente a un atacante con el mismo nivel de acceso que necesita para leer la clave, así que se descarta
   por dar una falsa sensación de seguridad sin el coste-beneficio de la opción 2.

## Decisión: opción 1, riesgo aceptado explícitamente por ahora

Enki es una app de escritorio de un solo usuario en su propia máquina, sin superficie de red expuesta más
allá de las llamadas salientes a Supabase ya auditadas (§5 del análisis: TLS forzado, sin interpolación
insegura, RLS del lado del servidor). El modelo de amenaza que la opción 2 mitiga —otro proceso local o una
persona con acceso al perfil de usuario leyendo `settings.json`— es real pero de severidad más baja que los
hallazgos que sí se resuelven directamente en esta fase (aislamiento de cuentas, fuga vía backup/export), y
la opción 2 tiene un coste de implementación real y no trivial:

- **DPAPI (Windows)** es la única de las tres plataformas con soporte nativo directo en .NET
  (`System.Security.Cryptography.ProtectedData`), pero Enki es multiplataforma (Linux es el entorno de
  desarrollo principal, ver `AGENTS.md`) — implementar solo para Windows dejaría el resto de plataformas
  igual de expuestas y crearía una rama de código condicional por SO.
- **libsecret (Linux)** y **Keychain (macOS)** no tienen binding de primera clase en el ecosistema .NET/
  Avalonia; requerirían interoperabilidad nativa (P/Invoke o un paquete de terceros) y quedan fuera del
  patrón "sin dependencias pesadas" que el resto del proyecto sigue deliberadamente (ver Fase 0 del
  roadmap, mismo criterio aplicado al logging).
- Un almacén de credenciales del SO también complica el modelo de "Exportar/Importar ajustes" y el backup
  manual, que hoy asumen que todo el estado de Enki vive en ficheros JSON simples portables entre máquinas.

**Riesgo aceptado**: mientras esta decisión no se revierta, `PolarAccessToken`/`PolarRefreshToken` siguen en
texto plano en `settings.json`. El radio de explosión de una fuga está acotado por RLS de Supabase (un
token robado solo expone los datos de la cuenta afectada, no de otros usuarios — verificado en el análisis
§5) y por que el usuario puede revocar la sesión desvinculando la cuenta desde Ajustes › Cuenta en cualquier
momento, lo que invalida el `refresh_token` del lado del servidor.

## Revisar esta decisión si...

- Enki empieza a distribuirse en un entorno multiusuario (equipos compartidos, terminales de uso común) en
  vez de exclusivamente en la máquina personal de un único usuario.
- Aparece un paquete NuGet/Avalonia con soporte de primera clase para Secret Service/Keychain que reduzca
  el coste de la opción 2 a un nivel comparable al de DPAPI en Windows.
- Un futuro hallazgo de seguridad demuestra que el radio de explosión real es mayor de lo que este
  documento asume (p. ej. si Supabase deja de aplicar RLS correctamente, o si el `refresh_token` deja de
  tener expiración/revocación server-side fiable).
