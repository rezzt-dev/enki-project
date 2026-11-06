using System;
using System.Collections.Generic;

namespace enki.Services;

/// <summary>Opciones de la línea de comandos de Enki (MED-06). Hoy solo hay una: <c>--capture</c>, que
/// entrega una idea a la instancia abierta (o a la que se está arrancando) para que un atajo del propio
/// escritorio la invoque. Es el camino para Wayland, donde un cliente no puede capturar teclas globales
/// (ver <see cref="GlobalHotkeyService"/>), y para quien automatiza con scripts.
///
/// <para>Formas aceptadas: <c>--capture "texto"</c>, <c>--capture=texto</c> y <c>--capture -</c> (o
/// <c>--capture</c> sin texto), que lee la entrada estándar: <c>wl-paste | enki --capture -</c>. El resto
/// de argumentos se ignora, porque el sistema o el lanzador pueden añadir los suyos.</para></summary>
public sealed record CommandLineOptions(bool CaptureRequested, string? CaptureText, string? Error)
{
    /// <summary>Tope del texto de una captura: una idea, no un volcado de un archivo.</summary>
    public const int MaxCaptureLength = 100_000;

    public static CommandLineOptions None { get; } = new(false, null, null);

    /// <param name="args">Argumentos del proceso, sin el nombre del ejecutable.</param>
    /// <param name="readStandardInput">Cómo leer la entrada estándar cuando la captura la pide; devuelve
    /// <see langword="null"/> si no hay nada que leer (terminal interactiva).</param>
    public static CommandLineOptions Parse(IReadOnlyList<string> args, Func<string?> readStandardInput)
    {
        var requested = false;
        string? text = null;
        var fromStandardInput = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--capture", StringComparison.Ordinal))
            {
                requested = true;
                if (i + 1 < args.Count && args[i + 1] != "-")
                {
                    text = args[++i];
                    fromStandardInput = false;
                }
                else
                {
                    if (i + 1 < args.Count) i++; // el «-» explícito
                    text = null;
                    fromStandardInput = true;
                }
            }
            else if (arg.StartsWith("--capture=", StringComparison.Ordinal))
            {
                requested = true;
                text = arg["--capture=".Length..];
                fromStandardInput = false;
            }
        }

        if (!requested) return None;
        if (fromStandardInput) text = readStandardInput();

        text = text?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrEmpty(text)) return new CommandLineOptions(true, null, "empty");
        if (text.Length > MaxCaptureLength) return new CommandLineOptions(true, null, "too-long");
        return new CommandLineOptions(true, text, null);
    }

    /// <summary>Lee la entrada estándar completa si viene de una tubería o un archivo; con la terminal
    /// interactiva devuelve <see langword="null"/> en vez de quedarse esperando.</summary>
    public static string? ReadStandardInput()
    {
        if (!Console.IsInputRedirected) return null;
        using var input = Console.OpenStandardInput();
        using var reader = new System.IO.StreamReader(input, System.Text.Encoding.UTF8);
        var buffer = new char[MaxCaptureLength + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
