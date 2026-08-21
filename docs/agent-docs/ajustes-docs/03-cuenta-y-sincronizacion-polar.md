# Ajustes › Cuenta — vínculo y sincronización con Polar

> **Alcance**: describe **Ajustes › Cuenta** (`SettingsSection.Account`,
> `SettingsView.axaml:742-875`) tal como está implementado: vincular/desvincular una cuenta Polar
> (Supabase Auth), mostrar tags de tareas Polar en Kanban/Calendario, estado y control manual de la
> sincronización en segundo plano. "Polar" es el nombre del módulo de gestión de tareas sincronizable en
> la nube, compartido con una app Android — ver `docs/supabase-sync/` para el contrato de datos
> completo; este documento se limita a lo que el usuario ve y controla desde Ajustes.
>
> **Fuente de verdad**: verificado contra
> `enki/src/enki/viewmodels/settings/SettingsViewModel.cs` (propiedades `IsPolarLinked`…`SyncNow`),
> `enki/src/enki/services/supabase/PolarAuthService.cs`, `enki/src/enki/services/polar/PolarSyncService.cs`
> y el cableado de `enki/src/enki/viewmodels/shell/MainWindowViewModel.Composition.cs`.

## Requisito previo: Supabase configurado

Todo lo demás en esta página depende de `PolarAuthService.IsConfigured` — verdadero solo si tanto la URL
de Supabase como la clave anónima están presentes (vía `SupabaseConfig`, leído de `local.properties`
empaquetado junto al ejecutable). Si no lo está:

- Se muestra una tarjeta de aviso en rojo ("La sincronización no está disponible porque el backend de
  Polar no está configurado. Contactad con el administrador o consultad la documentación de la
  aplicación.") en vez de ocultar la sección — el usuario siempre ve *que existe* la funcionalidad,
  aunque no pueda usarla en este build/equipo.
- El formulario de vínculo (email/password) queda visible pero deshabilitado
  (`IsEnabled="{Binding IsSupabaseConfigured}"`, `SettingsView.axaml:791`).

## Vincular / desvincular cuenta

Enki **nunca crea cuentas Supabase** — solo inicia sesión con un email/password que ya existe en el
proyecto Supabase compartido (comentario de `LinkPolar`, `SettingsViewModel.cs:988-1012`).

- **`LinkPolarCommand`**: llama a `PolarAuthService.LinkAsync(email, password)`, que hace
  `POST auth/v1/token?grant_type=password` contra Supabase Auth (GoTrue). Errores posibles, todos
  traducidos: sin configuración, credenciales vacías, credenciales inválidas (mensaje del propio
  Supabase si viene informativo), fallo de conexión (con el detalle de la excepción interpolado), o
  timeout (20 s, `PolarAuthService.cs:20`). Mientras está en curso, `IsLinkingPolar` deshabilita el botón
  y cambia su texto a "Vinculando…"; al terminar con éxito se limpian los campos de email/password.
- **Éxito** desbloquea la vista "Gestión de tareas" dentro de Kanban y arranca la sincronización en
  segundo plano — ambos cableados desde `PolarAuthService.LinkStateChanged`, no desde este comando
  directamente (ver más abajo).
- **`UnlinkPolarCommand`**: `PolarAuthService.Unlink()` (`PolarAuthService.cs:106-118`) borra de
  `AppSettings` todo lo relativo a la sesión — `PolarLinked`, `PolarUserId`, `PolarEmail`,
  `PolarAccessToken`, `PolarRefreshToken`, `PolarAccessTokenExpiresAtUtcMs`, `PolarLastSyncAtUtcMs` — y
  guarda. **No borra las tareas/listas locales por sí solo**; eso lo decide el suscriptor de
  `LinkStateChanged` en `MainWindowViewModel` (ver siguiente sección).
- **Cuenta ya vinculada**: la tarjeta muestra el email vinculado (`PolarLinkedEmail`), un badge
  "Vinculado" y el botón "Desvincular"; el formulario de email/password desaparece.

### Efecto colateral al desvincular: limpieza de datos locales de Polar

`MainWindowViewModel.Composition.cs:126-150`, suscrito a `LinkStateChanged`: cuando la cuenta pasa a
"no vinculada", si el store local de `PolarTaskService` tenía algo (`Lists`/`Tasks`/`Subtasks` no
vacíos), se llama `_polarTaskService.ClearAll()` y se empuja una notificación informativa
("tareas eliminadas"). El razonamiento documentado en el propio código: a diferencia de los
Recordatorios, la vista "Gestión de tareas" solo tiene sentido con una cuenta vinculada — no hay ningún
caso de uso "local sin cuenta" que preservar, y dejar esos datos en disco los expondría bajo la
siguiente cuenta distinta que se vincule en el mismo equipo.

También existe **desvinculación forzada silenciosa**: si `EnsureFreshTokenAsync` descubre que el refresh
token fue rechazado por Supabase (sesión expirada/revocada), `PolarAuthService` se desvincula sola y
dispara `SessionExpired` (subconjunto de `LinkStateChanged`) — el compositor lo traduce en una
notificación de aviso ("Notification_PolarSessionExpired_*") porque, a diferencia de una desvinculación
manual desde Ajustes, esta ocurre en segundo plano sin que el usuario la pidiera.

## Sincronización en segundo plano

No hay ningún control en Ajustes para "activar" la sincronización por separado: **está siempre activa
mientras la cuenta esté vinculada**.

- `MainWindowViewModel` arranca un `DispatcherTimer` con intervalo base de 25 s
  (`PolarSyncBaseInterval`, `MainWindowViewModel.cs:53`) que llama
  `PolarSyncService.SyncNowAsync()` en cada tick, más una sincronización inmediata al vincular
  (`LinkStateChanged`, `MainWindowViewModel.Composition.cs:126-130`) y al reconectar el servicio de
  tiempo real. `SyncNowAsync` es no-op si no hay cuenta vinculada.
- Tras cada sincronización completada, el intervalo se ajusta con backoff exponencial mientras el fallo
  sea de red (`LastErrorWasNetwork` → `PolarSyncStatus.Offline`): se duplica el tiempo de espera en cada
  fallo consecutivo hasta un máximo (`PolarSyncMaxInterval`), y vuelve al intervalo base en cuanto una
  sincronización tiene éxito (`MainWindowViewModel.Composition.cs:154+`).
- Al vincular también se conecta `PolarRealtimeService`; al desvincular se desconecta.

`PolarSyncStatus` (`PolarSyncService.cs:26-54`) deriva de tres señales, en este orden de prioridad:
`NotLinked` (sin cuenta) → `Syncing` (en curso) → `Offline` (último error fue de red) → `Error` (último
error no era de red) → `Synced`.

### Lo que el usuario ve en Ajustes › Cuenta

- **"Última sincronización"** (`LastSyncedLabel`, `SettingsViewModel.cs:305-310`): lee
  `PolarSyncService.LastSyncedUtc` (derivado de `AppSettings.PolarLastSyncAtUtcMs`, 0 = nunca) y lo
  formatea igual que `PolarTaskManagementViewModel.RefreshSyncStatus` ("dd MMM, HH:mm" o "Nunca"). Se
  refresca al construir la vista y cada vez que `PolarSyncService.SyncCompleted` se dispara — sin
  necesidad de que el usuario pulse nada.
- **Mensaje de error** (`SyncErrorLabel`): solo visible si `PolarSyncService.LastError` no es nulo;
  compone "No se pudo sincronizar: {error}" fuera del XAML para poder adaptar el orden de palabras por
  idioma. Antes de que existiera esta propiedad, Ajustes › Cuenta no mostraba ningún error de sync — solo
  "Última sincronización", sin forma de distinguir una marca de tiempo desactualizada de una
  sincronización genuinamente rota.
- **"Reintentando en…"** (`NextRetryLabel`): visible únicamente cuando `Status == Offline` **y** hay un
  `NextSyncAttemptUtc` programado — refleja el backoff exponencial descrito arriba.
- **`SyncNowCommand`**: fuerza `PolarSyncService.SyncNowAsync()` fuera del ciclo del timer; el botón
  cambia su texto a "Sincronizando…" y su icono gira mientras `IsSyncingNow` es verdadero. No hace falta
  refrescar `LastSyncedLabel` manualmente al terminar — ya se refresca desde la suscripción a
  `SyncCompleted` hecha en el constructor.

## Gestión de tareas › mostrar etiquetas (`PolarShowTaskTags`)

Toggle independiente del vínculo/sincronización: controla si la fila de chips de filtro por etiqueta se
muestra en la vista Kanban → "Gestión de tareas" (y, según el propio comentario del modelo, también
afecta a Calendario). Por defecto **apagado** — la fila empieza colapsada detrás del ajuste hasta que el
usuario decide activarla (`AppSettings.cs:86-88`). Cambia en caliente vía el callback
`_onPolarShowTaskTagsChanged` cableado en el compositor, sin necesidad de reiniciar.

## Referencias cruzadas

- Redacción de `PolarAccessToken`/`PolarRefreshToken` al exportar ajustes o crear un backup:
  `01-continuidad-y-proteccion-de-datos.md`.
- Contrato de datos completo con Supabase/Android: `docs/supabase-sync/`.
