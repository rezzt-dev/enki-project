# CAPTURA DESDE LA LÍNEA DE COMANDOS Y EN WAYLAND

> Seguimiento de **MED-06**. `enki --capture` entrega una idea a la instancia abierta, para que la invoque un
> atajo del propio escritorio donde Enki no puede registrar uno global (Wayland).

## 1. USO

```bash
enki --capture "Llamar a Marta por el presupuesto"
enki --capture="Comprar pan"
wl-paste | enki --capture -          # lee la entrada estándar
echo "idea" | enki --capture
```

- El texto se guarda en el destino de la captura rápida que el usuario eligió la última vez (nota de
  `Inbox/` o tarjeta de Kanban, con su tablero y columna); si la columna recordada ya no existe, usa el
  tablero y la columna por defecto en lugar de perder la idea. No toca el borrador del popup de captura.
- Si hay una instancia abierta, la entrega y **termina sin abrir ventana**. Código de salida: `0` guardada,
  `1` la instancia no pudo guardarla o no responde, `2` argumentos inválidos (sin texto, más de 100 000
  caracteres).
- Si no hay instancia abierta, arranca Enki con normalidad y guarda la captura en cuanto el vault está listo.

## 2. CÓMO ESTÁ HECHO

`SingleInstanceGuard` decide quién es la primera instancia (como antes). La primera arranca un
`CommandLineServer` antes que la interfaz (`Program.Main`) y la segunda, si lleva `--capture`, se conecta con
`CommandLineChannel.SendCaptureAsync` y sale. El canal es una **tubería con nombre por usuario**
(`enki-capture-<usuario>`; socket de dominio Unix en Linux y macOS) con `PipeOptions.CurrentUserOnly`: nadie más
puede conectarse y no se abre ningún puerto de red.

Protocolo: una línea de JSON `{"capture":"…"}`; respuesta `ok` o `error`. Máximo 512 KiB por mensaje y 5 s para
enviarlo; los mensajes mal formados no llegan al manejador. Los mensajes que llegan antes de que la aplicación
registre su manejador esperan hasta 60 s, de modo que una segunda instancia lanzada mientras la primera arranca
no se pierde. La entrega es de una en una; quien llega durante otra reintenta hasta agotar su tiempo (10 s).

## 3. EJEMPLOS DE ATAJOS

Solo hay que asociar un atajo al comando; no hace falta que Enki registre nada.

- **Hyprland:** `bind = SUPER, N, exec, enki --capture "$(wl-paste)"`
- **Sway:** `bindsym $mod+n exec sh -c 'enki --capture "$(wl-paste)"'`
- **GNOME:** Ajustes › Teclado › Atajos personalizados › comando `sh -c 'wl-paste | enki --capture -'`
- **KDE Plasma:** Preferencias del sistema › Atajos › Añadir comando.

## 4. LÍMITES CONOCIDOS

- El atajo solo envía texto ya existente (portapapeles, un argumento); no abre la ventana de captura. Una
  ventana de captura por línea de comandos sería otro cambio.
- Con la shell sin lista (pantalla de idioma o de vault) una entrega recibida se rechaza con aviso en vez de
  esperar; solo la captura de los argumentos del propio arranque espera al vault.
- Probado con dos extremos del canal en el mismo proceso y con el análisis de argumentos; **no** con una
  sesión Wayland real ni con dos procesos Enki completos (el entorno de desarrollo no tiene pantalla).
