# Datos y privacidad de la integración con Polar

La explicación visible para el usuario está en **Ajustes › Acerca de › Privacidad › Polar y datos en la nube**.
El destino concreto de la compilación se muestra allí a partir de `SupabaseConfig.Url`; la app solo acepta
una URL HTTPS para la configuración distribuida.

## Datos que salen del equipo

Sin una cuenta Polar vinculada, no se ejecuta la sincronización. Al vincularla, Enki envía el correo y la
contraseña a Supabase Auth para iniciar sesión. La contraseña no se guarda. Las listas, tareas, subtareas
y recordatorios de Polar se sincronizan con el proyecto Supabase compartido con Polar. Esto incluye títulos,
descripciones, fechas, prioridad, recurrencia, etiquetas, estado, orden y referencias a imágenes de las
tareas, además de los campos de ubicación de los recordatorios si se rellenaron. `PolarWireDtos.cs` contiene
el contrato de campos completo.

El contenido Markdown del vault, los archivos, Kanban local y las sesiones no forman parte del contrato de
sincronización Polar. El usuario puede sincronizar su vault mediante herramientas externas por su cuenta;
esa transmisión queda fuera de esta integración.

## Cifrado, almacenamiento y desvinculación

Auth y REST usan HTTPS y Realtime usa `wss://`. Esto cifra el transporte entre la app y el servidor. Los
datos sincronizados **no tienen cifrado de extremo a extremo**: el servidor los recibe en una forma que
puede procesar. Los tokens de sesión se guardan en el almacén protegido del sistema operativo (DPAPI en
Windows, Keychain en macOS, libsecret en Linux), no junto a las notas. La región física, las copias y el
cifrado en reposo del proyecto Supabase dependen de su operador y no se pueden deducir ni garantizar desde
este repositorio; por eso la interfaz muestra el endpoint configurado y no inventa una ubicación.

Desvincular borra los tokens locales y la caché local de listas, tareas y subtareas Polar; los recordatorios
locales permanecen porque también funcionan sin cuenta. Los registros remotos no se eliminan al
desvincular. El borrado de datos o de la cuenta en el servidor corresponde al operador del proyecto
Polar/Supabase. La exportación de la copia del vault redacta los tokens de Polar.
