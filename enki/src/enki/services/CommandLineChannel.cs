using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Services;

/// <summary>Resultado de entregar una captura a la instancia abierta.</summary>
public enum CommandLineDelivery
{
    /// <summary>La instancia la guardó.</summary>
    Delivered,
    /// <summary>La instancia respondió, pero no pudo guardarla (sin vault abierto, destino inválido…).</summary>
    Rejected,
    /// <summary>No hay instancia que conteste dentro del tiempo dado.</summary>
    Unreachable
}

/// <summary>Canal local entre un <c>enki --capture</c> y la instancia que ya está abierta (MED-06). Es una
/// tubería con nombre (socket de dominio Unix en Linux y macOS, tubería de Windows en Windows) restringida
/// al usuario actual (<see cref="PipeOptions.CurrentUserOnly"/>): nadie más puede conectarse, y no se abre
/// ningún puerto de red. El mensaje es una línea de JSON, <c>{"capture":"…"}</c>, y la respuesta es
/// <c>ok</c> o <c>error</c>; así el comando devuelve un código de salida que un script puede usar.
///
/// <para>El servidor arranca antes que la interfaz (<c>Program.Main</c>) para que una segunda instancia
/// lanzada mientras la primera aún arranca no se pierda: los mensajes esperan a que la aplicación registre
/// su manejador (<see cref="SetHandler"/>). La entrega se hace de una en una; un cliente que llega mientras
/// se atiende a otro reintenta hasta agotar su tiempo.</para></summary>
public sealed class CommandLineServer : IDisposable
{
    /// <summary>Tope del mensaje completo en bytes. Mayor que el texto máximo para dar margen al JSON.</summary>
    internal const int MaxMessageBytes = 512 * 1024;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandlerReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<Func<string, Task<bool>>> _handler = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    public CommandLineServer(string? pipeName = null) => _pipeName = pipeName ?? CommandLineChannel.DefaultPipeName;

    /// <summary>Empieza a escuchar en segundo plano. Llamar una sola vez.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cancel.Token));

    /// <summary>Registra quién guarda las capturas; las que llegaron antes se entregan ahora. El manejador
    /// devuelve si la captura se guardó. Se ejecuta en un hilo cualquiera: quien lo registra debe pasarlo
    /// al hilo de interfaz si lo necesita.</summary>
    public void SetHandler(Func<string, Task<bool>> handler) => _handler.TrySetResult(handler);

    private async Task RunAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel);
                await ServeAsync(pipe, cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LogService.Warning("El canal de la línea de comandos falló", ex);
                try { await Task.Delay(500, cancel); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        var response = "error";
        try
        {
            var message = await ReadMessageAsync(pipe, cancel);
            if (message is not null && TryParse(message, out var text))
            {
                var ready = await Task.WhenAny(_handler.Task, Task.Delay(HandlerReadyTimeout, cancel));
                if (ready == _handler.Task)
                {
                    var handled = _handler.Task.Result(text);
                    var done = await Task.WhenAny(handled, Task.Delay(HandlerTimeout, cancel));
                    if (done == handled && await handled) response = "ok";
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            // Un cliente que no llegó a enviar nada a tiempo: se le responde error y se sigue.
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            LogService.Warning("No se pudo atender una captura de la línea de comandos", ex);
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(response + "\n");
            await pipe.WriteAsync(bytes, cancel);
            await pipe.FlushAsync(cancel);
        }
        catch (IOException)
        {
            // El cliente ya se fue: no hay a quién avisar.
        }
    }

    /// <summary>Lee hasta el salto de línea con un tope de tamaño y de tiempo. <see langword="null"/> si el
    /// mensaje es demasiado grande o no llega.</summary>
    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ReadTimeout);
        var received = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var take = newline >= 0 ? newline : read;
            if (received.Length + take > MaxMessageBytes) return null;
            received.Write(buffer, 0, take);
            if (newline >= 0) break;
        }
        return received.Length == 0 ? null : Encoding.UTF8.GetString(received.ToArray());
    }

    internal static bool TryParse(string message, out string text)
    {
        text = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("capture", out var capture)
                || capture.ValueKind != JsonValueKind.String) return false;
            text = capture.GetString() ?? string.Empty;
            return text.Trim().Length > 0 && text.Length <= CommandLineOptions.MaxCaptureLength;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }
}

/// <summary>Lado cliente del canal y nombre compartido. Ver <see cref="CommandLineServer"/>.</summary>
public static class CommandLineChannel
{
    /// <summary>Captura que llegó por argumentos a la instancia que se está arrancando (no había otra
    /// abierta): se guarda en cuanto el vault esté listo. La fija <c>Program.Main</c>.</summary>
    public static string? StartupCapture { get; set; }

    /// <summary>El servidor de esta instancia, o <see langword="null"/> si no es la primera.</summary>
    public static CommandLineServer? Server { get; set; }

    /// <summary>Un nombre por usuario: dos usuarios del mismo equipo tienen cada uno su Enki.</summary>
    public static string DefaultPipeName { get; } = "enki-capture-" + Sanitize(Environment.UserName);

    private static string Sanitize(string value)
    {
        var clean = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return clean.Length == 0 ? "user" : clean;
    }

    /// <summary>Entrega <paramref name="text"/> a la instancia abierta. Reintenta la conexión hasta
    /// <paramref name="timeout"/>, porque la primera instancia puede estar aún arrancando.</summary>
    public static async Task<CommandLineDelivery> SendCaptureAsync(string text, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync((int)timeout.TotalMilliseconds);

            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { capture = text }) + "\n");
            await client.WriteAsync(payload);
            await client.FlushAsync();

            using var wait = new CancellationTokenSource(timeout);
            var reply = new byte[16];
            var read = await client.ReadAsync(reply, wait.Token);
            return Encoding.UTF8.GetString(reply, 0, read).Trim() == "ok" ? CommandLineDelivery.Delivered : CommandLineDelivery.Rejected;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return CommandLineDelivery.Unreachable;
        }
    }
}
