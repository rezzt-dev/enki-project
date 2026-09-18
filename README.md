<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".docs/resources/project-icons/dark-icon/enki-icon-light.png">
    <source media="(prefers-color-scheme: light)" srcset=".docs/resources/project-icons/light-icon/enki-icon-dark.png">
    <img alt="Enki" src=".docs/resources/project-icons/dark-icon/enki-icon-light.png" width="96">
  </picture>

  <h1>Enki</h1>
  <p><em>Tu segundo cerebro, en tu escritorio.</em></p>

  <p>
    <a href="README.en.md">Read in English</a>
  </p>
</div>

---

**Enki** es una aplicación de escritorio pensada para una sola persona: tú. No es una suite colaborativa, ni un SaaS, ni otra herramienta que te obligue a pensar como piensa su equipo de producto. Es un espacio privado donde guardar ideas, tareas, proyectos y recordatorios de forma que todo esté conectado, todo esté a mano y nada se pierda.

Nació de la frustración de tener que abrir cinco aplicaciones distintas para saber qué hacer hoy: las notas en un lado, las tareas en otro, el calendario en otro y los recordatorios en un cuarto. Enki reúne todo eso en una sola ventana, pero sin mezclarlo todo a la fuerza. Cada cosa conserva su lugar, y tú decides cómo conectarlas.

> El objetivo no es que uses Enki todo el día. Es que, cuando lo abras, sepas exactamente dónde está lo que necesitas.

---

## ¿Qué puedes hacer con Enki?

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>📝 Notas</h3>
      <p>Un vault de notas en Markdown que vive en tu disco. Sin bloqueos, sin formatos propietarios. Puedes crear carpetas, arrastrar notas, etiquetar con <code>tags</code> y enlazar unas con otras usando <code>[[wikilinks]]</code>.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🗂️ Kanban</h3>
      <p>Un tablero de tareas local con columnas, prioridades, fechas límite y subtareas. Arrastra tarjetas entre columnas, vincúlalas a una nota y recupera lo borrado desde la papelera.</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>☁️ Polar</h3>
      <p>Tareas sincronizadas en la nube, compartidas con la app Android del mismo nombre. Es optativo: si no lo vinculas, Enki funciona exactamente igual con tus notas y Kanban locales.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🗓️ Calendario</h3>
      <p>Una vista unificada de Kanban, Polar y recordatorios. Arrastra una tarea para cambiar su fecha, crea recordatorios directamente desde un hueco vacío y filtra por lo que realmente importa.</p>
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>🔔 Recordatorios</h3>
      <p>Avisos independientes con fecha y hora. Aparecen en el calendario, alimentan la bandeja de vencimientos y se sincronizan con Polar si lo tienes activo.</p>
    </td>
    <td width="50%" valign="top">
      <h3>🕸️ Sesiones</h3>
      <p>Subespacios de notas con un grafo visual de relaciones. Útil para proyectos aislados: una tesis, un viaje, un producto. Las sesiones limitan lo que ves sin borrar el resto.</p>
    </td>
  </tr>
</table>

Además, Enki incluye un **dashboard** de inicio que resume el día, una **búsqueda global** (`Ctrl+Shift+F`), un **quick switcher** para saltar a notas (`Ctrl+P`), un **centro de notificaciones**, una **papelera transversal** y ajustes granulares para que la interfaz se adapte a ti, no al revés.

---

## Filosofía

- **Tus datos son tuyos.** Las notas son archivos Markdown ordinarios en una carpeta que tú eliges. Puedes abrirlos con cualquier editor, respaldarlos con cualquier herramienta y llevarlos contigo.
- **Conectado, no amontonado.** Cada módulo puede vivir solo, pero gana sentido cuando se cruza con los demás: una nota vinculada a una tarjeta Kanban, una tarea Polar arrastrada en el calendario, un recordatorio que aparece en la bandeja de hoy.
- **Sin distracciones.** Interfaz monocroma, tipografía clara, esquinas rectas y movimiento contenido. No hay gamificación, no hay notificaciones constantes, no hay presión por productividad.
- **Funciona sin internet.** Polar es opcional. El núcleo de Enki —notas, Kanban, recordatorios, calendario— funciona completamente offline.

---

## Estado del proyecto

> 🚧 **En desarrollo activo.** Este repositorio es privado y de uso personal por ahora. No hay un instalador publicado ni soporte para terceros.

Enki ya es funcional para uso diario: notas, Kanban, calendario, recordatorios, búsqueda y papelera están operativos. La sincronización con Polar requiere credenciales de Supabase y, de momento, está pensada solo para el entorno del autor.

Si estás leyendo esto, probablemente eres tú mismo dentro de unos meses. La documentación del proyecto vive en [`.docs/project-info/`](.docs/project-info/), el historial de cambios en [`CHANGELOG.md`](CHANGELOG.md) y las convenciones del código en [`AGENTS.md`](AGENTS.md).

---

## Primeros pasos

Si tienes el entorno de desarrollo configurado:

```bash
# Compilar
dotnet build enki/src/enki/enki.csproj

# Ejecutar
dotnet run --project enki/src/enki/enki.csproj

# Tests
dotnet test enki/tests/enki.Tests/enki.Tests.csproj
```

La primera vez que arranques, Enki te pedirá que elijas idioma y carpeta de vault. A partir de ahí, todo lo que crees se guarda ahí.

Para sincronizar con Polar, necesitas añadir tus credenciales de Supabase en `enki/local.properties` (el archivo está ignorado por git, nunca se sube):

```properties
SUPABASE_URL=<url-del-proyecto>
SUPABASE_ANON_KEY=<clave-anon>
```

Si tienes un `.env` con `SUPABASE_PUBLISHABLE_KEY`, copia ese valor a `SUPABASE_ANON_KEY`.
No copies `SUPABASE_SECRET_KEY`: la aplicación solo necesita la clave publicable.

Sin ese archivo, Enki sigue funcionando perfectamente en modo local.

---

## Documentación

La documentación versionada del proyecto está en [`.docs/project-info/`](.docs/project-info/). Incluye el análisis del producto, el roadmap de mejoras y las especificaciones por módulo. `docs/` está ignorada por Git y no forma parte de este checkout. Algunos puntos de entrada útiles:

- [Análisis del proyecto](.docs/project-info/analisis-proyecto/analisis-deficiencias-proyecto.md) — auditoría del estado actual y prioridades.
- [Roadmap de mejoras](.docs/project-info/analisis-proyecto/roadmap.md) — plan de corrección por fases.
- [Inventario de funcionalidades](.docs/project-info/inventario-funcionalidades-y-mejora-continua.md) — funcionalidades existentes y oportunidades de mejora.
- [Calendario: contexto y visión](.docs/project-info/calendar-feature/01-contexto-y-vision-de-producto.md) — por qué existe el Calendario.
- [Notificaciones y recordatorios](.docs/project-info/notificaciones-y-recordatorios.md) — cómo funcionan.
- [Sistema de diseño](.docs/project-info/sistema-diseno-web.md) — principios visuales.

---

<div align="center">
  <p><sub>Construido con paciencia para uso personal.</sub></p>
  <p><a href="CHANGELOG.md">Changelog</a> · <a href="LICENSE">Licencia</a></p>
</div>
