# ACCESIBILIDAD Y ALTO CONTRASTE

> Seguimiento de **ALT-03** (`.docs/agent-docs/analysis/new-features/prioridad-alta.md`). Separa lo que está
> hecho y comprobado por pruebas automáticas de lo que solo puede comprobarse a mano con un lector de pantalla.

## 1. QUÉ HAY

| elemento | estado | dónde |
| --- | --- | --- |
| nombres accesibles (`AutomationProperties.*`) | 664 apariciones en 40 vistas `.axaml` (recuento del 2026-11-06) | `enki/src/enki/views/` |
| orden de tabulación | sin `TabIndex`; manda el orden del árbol visual. `Focusable` o `TabIndex` aparecen en 16 vistas | sin auditar |
| tema de alto contraste | `High Contrast Dark` y `High Contrast Light` | `services/BuiltInThemeCatalog.cs` |
| contraste del sistema | si el sistema pide contraste alto y no hay tema elegido, se activa el tema de alto contraste que coincide con la variante clara u oscura guardada | `CustomThemeService.ApplyActiveOnStartup` |
| prueba de contraste | cada color de texto, acento y estado cumple 4,5:1 sobre cada fondo de ambos temas | `enki.Tests/Services/HighContrastThemeTests.cs` |

Los temas de alto contraste aparecen en Ajustes › Apariencia, los primeros de su grupo (oscuros / claros).
Si el usuario elige otro tema o uno personalizado, el contraste del sistema deja de aplicarse; «Usar tema del
sistema» lo recupera en el siguiente arranque.

**Límites conocidos.** La prueba comprueba los colores de los temas, no cada control: un control que dibuje
con un color propio fuera de los 19 huecos de `ThemeCatalog` no queda cubierto. La detección del sistema usa
`PlatformSettings.GetColorValues().ContrastPreference` de Avalonia y no se ha podido probar en Windows ni en
macOS desde el entorno de desarrollo (Linux).

## 2. PRUEBA MANUAL CON LECTOR DE PANTALLA (PENDIENTE)

Herramientas: Orca (Linux), NVDA (Windows), VoiceOver (macOS). Se registra una fila por herramienta y vault
de prueba desechable.

Recorridos que deben completarse **solo con teclado y lector**, sin ratón:

1. **Crear una nota:** ir a Notas, crear nota, escribir título y texto, guardar, volver a la lista.
2. **Mover una tarjeta de Kanban:** abrir el tablero, enfocar una tarjeta, moverla a otra columna, comprobar
   que el lector anuncia columna de origen y de destino.
3. **Crear un recordatorio:** abrir Recordatorios (o el calendario), crear uno con fecha y hora, comprobar
   que aparece en la lista y que su estado se anuncia.

Para cada recorrido anotar: si se completó, controles sin nombre o con nombre engañoso, puntos donde el foco
se pierde o queda atrapado, y anuncios ruidosos o ausentes.

Con el tema de alto contraste activo, repetir el recorrido visual y comprobar a simple vista bordes, foco
visible, texto deshabilitado e iconos sueltos.

### Registro de resultados

| fecha | herramienta | sistema | recorrido 1 | recorrido 2 | recorrido 3 | incidencias |
| --- | --- | --- | --- | --- | --- | --- |
| — | Orca | Linux | sin probar | sin probar | sin probar | — |
| — | NVDA | Windows | sin probar | sin probar | sin probar | — |
| — | VoiceOver | macOS | sin probar | sin probar | sin probar | — |

## 3. AUDITORÍA DE FOCO (PENDIENTE)

Revisar, con Tab y Mayús+Tab, que el foco recorre en un orden lógico y no se pierde, en este orden de
riesgo: Kanban (tarjetas, columnas, editor de tarjeta), Calendario (rejilla, popovers, creación rápida) y
Ajustes (lista de secciones y contenido). Un fallo se corrige con `TabIndex`, `Focusable` o reordenando el
XAML, y se añade aquí como incidencia.

El criterio de cierre de ALT-03 no se da por cumplido hasta rellenar el registro de arriba.
