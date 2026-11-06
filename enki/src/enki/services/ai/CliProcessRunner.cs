using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

/// <summary>Qué lanzar y cómo. Los argumentos van siempre en <see cref="Arguments"/> —uno por elemento,
/// nunca concatenados en una cadena de shell— para que ningún texto de una nota pueda inyectar nada, y el
/// contenido largo viaja por <see cref="StandardInput"/>: no aparece en la lista de procesos ni choca con
/// el límite de longitud de la línea de comandos de Windows.</summary>
internal sealed class CliProcessStartInfo
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? StandardInput { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Tiempo máximo sin recibir ni una línea por stdout ni stderr. Pasado ese tiempo el proceso se
    /// da por colgado y se mata. <see langword="null"/> para no limitarlo.</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Variables de entorno heredadas que no deben llegar al proceso hijo.</summary>
    public Func<string, bool>? ExcludeEnvironmentVariable { get; init; }

    /// <summary>Variables que se fijan en el proceso hijo, por encima de las heredadas.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }

    /// <summary>Actividad que ocurre fuera de stdout/stderr y que también cuenta para
    /// <see cref="IdleTimeout"/>: mientras Enki ejecuta una herramienta que el CLI le pidió, o espera a que
    /// el usuario confirme una, el CLI no escribe nada, pero no está colgado.</summary>
    public CliActivitySignal? Activity { get; init; }
}

/// <summary>Avisa a <see cref="CliProcessRunner"/> de que el proceso sigue vivo aunque no escriba nada. La
/// crea quien lanza el turno y la comparte con quien atiende al CLI por otro canal (el servidor MCP del
/// vault, ver <see cref="EnkiMcpServer"/>).</summary>
public sealed class CliActivitySignal
{
    internal event Action? Touched;

    public void Touch() => Touched?.Invoke();
}

/// <param name="ExitCode">Código de salida; <c>-1</c> si hubo que matar el proceso por inactividad.</param>
/// <param name="StandardError">Salida de error, recortada a <see cref="CliProcessRunner.MaxStandardErrorChars"/>.</param>
/// <param name="TimedOut">Se mató por superar <see cref="CliProcessStartInfo.IdleTimeout"/>.</param>
internal sealed record CliProcessResult(int ExitCode, string StandardError, bool TimedOut);

/// <summary>Lanza un ejecutable de línea de comandos como subproceso, le escribe la entrada por stdin y
/// entrega su salida línea a línea según llega. Común a todo lo que habla con un CLI externo (hoy el de
/// Claude, ver <see cref="ClaudeCliService"/>). Ver <c>.docs/project-info/asistente-ia.md</c>.</summary>
internal static class CliProcessRunner
{
    internal const int MaxStandardErrorChars = 16_000;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Ejecuta el proceso hasta que termina. <paramref name="onStdoutLine"/> se invoca desde un hilo
    /// de fondo con cada línea completa de stdout, en orden. Cancelar <paramref name="cancellationToken"/>
    /// mata el árbol de procesos entero y lanza <see cref="OperationCanceledException"/>; superar el tiempo
    /// de inactividad también lo mata, pero devuelve un resultado con <see cref="CliProcessResult.TimedOut"/>.
    /// Si el ejecutable no se puede lanzar, propaga la excepción de <see cref="Process.Start()"/>
    /// (<see cref="System.ComponentModel.Win32Exception"/>, <see cref="FileNotFoundException"/>…).</summary>
    public static async Task<CliProcessResult> RunAsync(CliProcessStartInfo info, Action<string>? onStdoutLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo(info) };
        process.Start();

        using var idle = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        void Touch()
        {
            try
            {
                if (info.IdleTimeout is { } timeout && !idle.IsCancellationRequested) idle.CancelAfter(timeout);
            }
            catch (ObjectDisposedException)
            {
                // Una señal externa que llega justo cuando el proceso ya terminó: no queda nada que prolongar.
            }
        }
        Touch();
        if (info.Activity is { } activity) activity.Touched += Touch;

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
                {
                    Touch();
                    lock (stderr)
                        if (stderr.Length < MaxStandardErrorChars) stderr.AppendLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }, CancellationToken.None);

        try
        {
            try
            {
                if (info.StandardInput is not null)
                    await process.StandardInput.WriteAsync(info.StandardInput.AsMemory(), linked.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException) when (!linked.IsCancellationRequested)
            {
                // El proceso cerró su entrada antes de leerla (un argumento rechazado, un binario que no es
                // el esperado). No es el error que importa: lo que explique el fallo está en stdout/stderr
                // y en el código de salida, que se leen a continuación.
            }

            while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
            {
                Touch();
                onStdoutLine?.Invoke(line);
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return new CliProcessResult(process.ExitCode, Truncated(stderr), TimedOut: false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || (ex is IOException && linked.IsCancellationRequested))
        {
            Kill(process);
            await stderrTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new CliProcessResult(-1, Truncated(stderr), TimedOut: true);
        }
        catch
        {
            Kill(process);
            throw;
        }
        finally
        {
            // Antes de que se libere el temporizador: una herramienta que termina tarde no debe tocarlo ya.
            if (info.Activity is { } signal) signal.Touched -= Touch;
        }
    }

    /// <summary>Atajo para órdenes cortas (<c>--version</c>, <c>--help</c>, estado de sesión): devuelve la
    /// salida completa de stdout junto al resultado.</summary>
    public static async Task<(CliProcessResult Result, string StandardOutput)> RunToEndAsync(
        CliProcessStartInfo info, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var result = await RunAsync(info, line => output.AppendLine(line), cancellationToken).ConfigureAwait(false);
        return (result, output.ToString());
    }

    private static ProcessStartInfo BuildStartInfo(CliProcessStartInfo info)
    {
        var startInfo = new ProcessStartInfo(info.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        if (!string.IsNullOrEmpty(info.WorkingDirectory)) startInfo.WorkingDirectory = info.WorkingDirectory;
        foreach (var argument in info.Arguments) startInfo.ArgumentList.Add(argument);
        if (info.ExcludeEnvironmentVariable is { } exclude)
            foreach (var key in startInfo.Environment.Keys.Where(exclude).ToList())
                startInfo.Environment.Remove(key);
        if (info.EnvironmentOverrides is { } overrides)
            foreach (var (key, value) in overrides) startInfo.Environment[key] = value;
        return startInfo;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Ya había terminado entre la comprobación y la orden, o el sistema no deja matarlo: no hay
            // nada más que hacer y el llamador ya va a informar del corte.
            LogService.Warning("No se pudo terminar el proceso del CLI", ex);
        }
    }

    private static string Truncated(StringBuilder stderr)
    {
        lock (stderr) return stderr.ToString().Trim();
    }
}
