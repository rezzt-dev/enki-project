# Perfil de personalización (BAJ-02)

Desde **Ajustes > Apariencia > Perfil de personalización** se puede exportar o importar un único
fichero `enki-profile.json`. Es independiente de «Exportar ajustes» y de las copias del vault.

## Uso

1. En el equipo de origen, elegir **Exportar perfil…** y guardar el fichero.
2. En el equipo de destino, abrir el vault que recibirá las preferencias y elegir **Importar perfil…**.
3. Revisar la vista previa: tema activo y temas incluidos, fuentes, zoom, atajos personalizados,
   grupos y secciones ocultas de la barra lateral y disposición de las tarjetas del panel.
4. Elegir **Aplicar perfil**. **Cancelar** cierra la vista previa sin escribir nada.
5. Reiniciar Enki para completar la aplicación de todas las preferencias; algunas vistas conservan
   su modo de presentación hasta volver a cargarse.

La vista previa conserva una instantánea validada: confirmar no relee un fichero que podría haber
cambiado mientras estaba abierta. Cambiar de vault descarta la importación pendiente.

## Qué contiene

- Tema base, tema activo y todos los temas personalizados con sus colores completos. Los temas
  integrados se identifican por su clave; no se copian como temas editables.
- Fuentes de interfaz, editor y código, tamaño y columna del editor, zoom, animaciones y barra de título.
- Atajos personalizados; las acciones sin personalización recuperan su combinación predeterminada.
- Orden de los grupos principal e inferior, nombres personalizados y visibilidad de secciones de la
  barra lateral. La disponibilidad del asistente sigue dependiendo de la configuración local de IA.
- Posición de los paneles laterales de Notas, Archivos, Habilidades y Asistente, y modos y anchuras de
  las barras de navegación de Notas, Archivos y Habilidades.
- Plantilla del panel principal, orden, tarjetas ocultas, filas, columnas y posiciones de la rejilla
  personalizada; barra de desplazamiento del panel y orientación del tablero Kanban.

Los nombres personalizados de temas y secciones y los nombres de fuentes sí viajan en el fichero.
No se empaquetan fuentes: si una no existe en el destino se usa la alternativa disponible.

## Privacidad y persistencia

Una lista explícita de claves permitidas excluye rutas, notas abiertas o recientes, sesiones,
identidad del vault, búsquedas guardadas, plantillas, cuentas Polar, credenciales y configuración de IA.
Añadir una propiedad a `AppSettings` no la añade automáticamente al perfil. No hay acceso a la red.

Importar sustituye las preferencias incluidas y conserva los datos y ajustes ajenos. Los temas
existentes se conservan; los importados reciben identificadores nuevos para evitar colisiones.
Reimportar un mismo perfil añade nuevas copias de sus temas personalizados.

Se escriben exclusivamente los temas en `<vault>/.enki/themes/` y las preferencias en
`<vault>/.enki/settings.json`. No se modifican Markdown, adjuntos ni otros almacenes. «Sin tocar el
vault» se refiere a su contenido: la personalización se guarda en sus metadatos, siguiendo la
persistencia por vault ya existente. Es necesario tener un vault abierto para aplicar un perfil.

El formato lleva `Format: enki-personalization` y `Version: 1`. Se rechazan versiones desconocidas,
claves ajenas, campos incompletos, atajos desconocidos o ilegibles, colores inválidos y posiciones
dañadas. Los tamaños y el zoom se acotan antes de presentar la vista previa. La lectura está limitada a
2 MiB. Los ficheros antiguos de ajustes no son perfiles y conservan su vía de importación anterior.

`PersonalizationProfileService` valida el paquete y prepara los temas, reutilizando
`CustomThemeService.Export`. `SettingsService.ImportPersonalization` confirma las preferencias de
`AppSettings` y el diseño de `VaultSettings` en una única escritura atómica que conserva las claves
ajenas. Si la preparación o la escritura falla, se retiran los temas recién creados. No se accede al
almacén de credenciales.

## Comprobaciones

Las pruebas usan carpetas temporales. Cubren el traslado entre dos instalaciones, conservación de
notas, datos locales y temas existentes, persistencia tras recargar, temas integrados, validación y
límites de tamaño, retirada de temas ante fallo de escritura, vista previa sin cambios, cancelación,
confirmación de la instantánea y controles accesibles desde el buscador de Ajustes.

La licencia existente no cambia: este formato no introduce dependencias, permisos del sistema,
servicios de red ni condiciones de licencia nuevas.
