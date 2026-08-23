using Avalonia;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using enki.Services;

namespace enki;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());

        // El motor WebView embebido (grafo de sesiones, vía NativeWebView/WebViewHost — las notas solo se
        // exportan a HTML hoy, sin vista de impresión ni WebView propios, ver NoteExportService) usa
        // WPEWebKit en Linux. Su renderer DMA-BUF por defecto produce una
        // superficie en negro/blanco con ciertas combinaciones de driver gráfico y compositor Wayland
        // aunque el motor se reporte "instalado" y la navegación termine con éxito — variable de entorno
        // estándar para forzar el camino de compatibilidad. Debe fijarse antes de que Avalonia inicialice
        // cualquier NativeWebView, así que va aquí, antes de BuildAvaloniaApp.
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") is null)
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", "1");

        // Fase 0 del roadmap de deficiencias (docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2):
        // antes de esto, un fallo no controlado solo se veía en la consola (invisible en un WinExe en
        // Windows) y una tarea en segundo plano que fallaba desaparecía sin dejar rastro. Todo lo de abajo
        // tiene que quedar cableado antes de cualquier otra cosa, para capturar el mayor rango posible de
        // fallos de arranque.
        LogService.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[UNHANDLED] {e.ExceptionObject}");
            LogService.Error(
                $"Excepción no controlada (IsTerminating={e.IsTerminating})",
                e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogService.Error("Excepción de tarea en segundo plano no observada", e.Exception);
            // Marca la excepción como observada para que el runtime no vuelva a hacer nada con ella — ya
            // ha quedado registrada, que es todo lo que Fase 0 pide aquí.
            e.SetObserved();
        };

        // Sweeps any decrypted temp file (enki-dec-*) left behind by a previous run that crashed instead of
        // going through MainWindow's normal Closing handler — that handler sweeps the same directory again
        // at shutdown, so between the two, a leftover plaintext copy of protected-session content never
        // survives more than one launch (docs/version-alfa-3/01-analisis-detallado.md §4.1, Alta).
        VaultService.CleanupStaleDecryptedTempFiles();

        try
        {
            SingleInstanceGuard.Acquire();
        }
        catch (Exception ex)
        {
            // Si ni siquiera se puede crear/abrir el mutex de instancia única (permisos, entorno
            // restringido), antes esto tumbaba el proceso sin ningún rastro. Ahora al menos queda logueado
            // y se avisa por consola antes de salir de forma controlada.
            LogService.Error("No se pudo adquirir el mutex de instancia única", ex);
            Console.Error.WriteLine($"Enki no ha podido iniciar. Revisa el log en {LogService.LogDirectory}.");
            Environment.Exit(1);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Debug);
}
