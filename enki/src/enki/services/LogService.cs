using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace enki.Services;

public enum LogLevel
{
    Debug = 0,
    Warning = 1,
    Error = 2
}

/// <summary>Minimal file logger — Fase 0 del roadmap de deficiencias
/// (docs/analisis-proyecto/analisis-deficiencias-proyecto.md §2, "Manejo de errores y logging"): sin este
/// logging, un fallo real (E/S del vault, excepción no controlada, tarea en segundo plano que muere en
/// silencio) no dejaba ningún rastro salvo lo que la ventana de consola mostrara — que en un
/// <c>WinExe</c> como Enki, en Windows, nadie ve. Deliberadamente sin dependencias de terceros (Serilog,
/// Microsoft.Extensions.Logging): un fichero de texto rotado por día en
/// <c>%AppData%/Enki/logs/enki-yyyyMMdd.log</c> es suficiente para un logging mínimo de escritorio de un
/// solo usuario, y evita añadir una dependencia pesada solo para esto. Estático (no inyectado) a
/// propósito: tiene que estar disponible desde <c>Program.Main</c>, antes de que exista cualquier
/// composición de servicios (ver <c>MainWindowViewModel.Composition</c>).</summary>
public static class LogService
{
    private const int RetentionDays = 14;

    private static readonly object WriteLock = new();
    private static readonly string AppVersion = ResolveAppVersion();

    private static string? _logDirectory;
    private static LogLevel _minimumLevel = LogLevel.Warning;

    /// <summary>Null hasta que <see cref="Initialize"/> se haya llamado (o si falló al crear la carpeta) —
    /// cualquier llamada a <see cref="Write"/> mientras tanto es un no-op silencioso en vez de lanzar.</summary>
    public static string? LogDirectory => _logDirectory;

    /// <param name="storageDirectory">Sobrescribe la carpeta por defecto <c>%AppData%/Enki/logs</c> — sin
    /// uso en la app real, pero permite a los tests apuntar el logger a un directorio desechable.</param>
    public static void Initialize(string? storageDirectory = null)
    {
        try
        {
            var appData = storageDirectory is null ? AppDataDirectory.Resolve() : null;
            if (appData is not null) PrivateDataPermissions.HardenExistingTree(appData);
            _logDirectory = storageDirectory ?? Path.Combine(appData!, "logs");
            PrivateDataPermissions.EnsurePrivateDirectory(_logDirectory);
            PurgeOldLogFiles();
        }
        catch (Exception ex)
        {
            // Si ni siquiera se puede crear la carpeta de logs, el logging se degrada a no-op — nunca debe
            // tumbar el arranque de la app por esto (que es justo el tipo de fallo que Fase 0 quiere que se
            // reporte, no que se propague).
            _logDirectory = null;
            Trace.TraceError($"No se pudo inicializar el registro de Enki: {ex}");
        }
    }

    /// <summary>Nivel mínimo por defecto es <see cref="LogLevel.Warning"/> — activar <c>Debug</c> es el
    /// interruptor "Registro de depuración" de Ajustes › General (<c>AppSettings.DebugLoggingEnabled</c>).</summary>
    public static void SetDebugEnabled(bool enabled) => _minimumLevel = enabled ? LogLevel.Debug : LogLevel.Warning;

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private static void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minimumLevel || _logDirectory is null) return;

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] enki ")
            .Append(AppVersion).Append(" | ").Append(message);
        if (exception is not null) line.Append(Environment.NewLine).Append(exception);

        try
        {
            lock (WriteLock)
            {
                var path = Path.Combine(_logDirectory, $"enki-{DateTime.Now:yyyyMMdd}.log");
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Append,
                    Access = FileAccess.Write,
                    Share = FileShare.Read
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateDataPermissions.FileMode;
                using var stream = new FileStream(path, options);
                using var writer = new StreamWriter(stream);
                writer.Write(line.Append(Environment.NewLine).ToString());
                PrivateDataPermissions.EnsurePrivateFile(path);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: un fallo al escribir el propio log (disco lleno, carpeta borrada en caliente) no
            // puede tumbar el código que intenta reportar otro fallo — eso sería justo el "fallo silencioso"
            // que esta fase existe para cerrar.
            Trace.TraceError($"No se pudo escribir el registro de Enki: {ex}");
        }
    }

    private static void PurgeOldLogFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_logDirectory!, "enki-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex)
            {
                // Limpieza best-effort: un fichero antiguo bloqueado/no borrable no debe impedir el arranque.
                Trace.TraceWarning($"No se pudo purgar el log antiguo '{file}': {ex}");
            }
        }
    }

    private static string ResolveAppVersion()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"No se pudo resolver la versión de Enki: {ex}");
            return "unknown";
        }
    }
}
