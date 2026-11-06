using System;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>«enki --capture» (MED-06): lectura de argumentos y entrega a la instancia abierta por una
/// tubería con nombre propia de cada prueba, así que nunca toca una instancia real de Enki.</summary>
public sealed class CommandLineCaptureTests
{
    private static string[] Args(params string[] args) => args;

    // ── argumentos ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithoutCapture_IsNone()
    {
        var options = CommandLineOptions.Parse(Args("--otra", "cosa"), () => throw new InvalidOperationException("no debe leer"));

        Assert.False(options.CaptureRequested);
        Assert.Null(options.Error);
    }

    [Theory]
    [InlineData("--capture", "Llamar a Marta")]
    [InlineData("--capture=Llamar a Marta")]
    public void Parse_Capture_TakesTheText(params string[] args)
    {
        var options = CommandLineOptions.Parse(args, () => throw new InvalidOperationException("no debe leer"));

        Assert.True(options.CaptureRequested);
        Assert.Equal("Llamar a Marta", options.CaptureText);
        Assert.Null(options.Error);
    }

    [Fact]
    public void Parse_CaptureThenOtherArguments_OnlyTakesTheNextOne()
    {
        var options = CommandLineOptions.Parse(Args("--x", "--capture", "idea", "--y"), () => null);

        Assert.Equal("idea", options.CaptureText);
    }

    [Fact]
    public void Parse_TextThatLooksLikeAnOption_IsStillTheText()
    {
        var options = CommandLineOptions.Parse(Args("--capture", "--lista de cosas"), () => null);

        Assert.Equal("--lista de cosas", options.CaptureText);
    }

    [Theory]
    [InlineData("--capture", "-")]
    [InlineData("--capture")]
    public void Parse_DashOrNoText_ReadsStandardInput(params string[] args)
    {
        var options = CommandLineOptions.Parse(args, () => "texto de la tubería\r\nsegunda línea\n");

        Assert.Equal("texto de la tubería\nsegunda línea", options.CaptureText);
    }

    [Theory]
    [InlineData("--capture", "   ")]
    [InlineData("--capture=")]
    public void Parse_EmptyText_IsAnError(params string[] args)
    {
        var options = CommandLineOptions.Parse(args, () => null);

        Assert.True(options.CaptureRequested);
        Assert.Equal("empty", options.Error);
        Assert.Null(options.CaptureText);
    }

    [Fact]
    public void Parse_NoStandardInputAvailable_IsAnError()
    {
        Assert.Equal("empty", CommandLineOptions.Parse(Args("--capture"), () => null).Error);
    }

    [Fact]
    public void Parse_TooLong_IsAnError()
    {
        var options = CommandLineOptions.Parse(Args("--capture", new string('a', CommandLineOptions.MaxCaptureLength + 1)), () => null);

        Assert.Equal("too-long", options.Error);
    }

    [Fact]
    public void Parse_AcceptsTheEnye()
    {
        Assert.Equal("Diseño del año", CommandLineOptions.Parse(Args("--capture", "Diseño del año"), () => null).CaptureText);
    }

    // ── canal ────────────────────────────────────────────────────────────────────────────

    private static string UniquePipe() => "enki-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SendCapture_ReachesTheRunningInstance_AndTheHandlerAnswersOk()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        string? received = null;
        server.SetHandler(text => { received = text; return Task.FromResult(true); });
        server.Start();

        var delivery = await CommandLineChannel.SendCaptureAsync("Diseño: revisar la contraseña", TimeSpan.FromSeconds(10), pipe);

        Assert.Equal(CommandLineDelivery.Delivered, delivery);
        Assert.Equal("Diseño: revisar la contraseña", received);
    }

    [Fact]
    public async Task SendCapture_WhenTheHandlerCannotSaveIt_IsRejected()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        server.SetHandler(_ => Task.FromResult(false));
        server.Start();

        Assert.Equal(CommandLineDelivery.Rejected, await CommandLineChannel.SendCaptureAsync("x", TimeSpan.FromSeconds(10), pipe));
    }

    [Fact]
    public async Task SendCapture_WhenTheHandlerThrowsAsynchronously_IsStillAnErrorAnswer_AndTheServerKeepsServing()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        var calls = 0;
        server.SetHandler(_ => ++calls == 1 ? Task.FromException<bool>(new InvalidOperationException("fallo")) : Task.FromResult(true));
        server.Start();

        Assert.Equal(CommandLineDelivery.Rejected, await CommandLineChannel.SendCaptureAsync("uno", TimeSpan.FromSeconds(10), pipe));
        Assert.Equal(CommandLineDelivery.Delivered, await CommandLineChannel.SendCaptureAsync("dos", TimeSpan.FromSeconds(10), pipe));
    }

    [Fact]
    public async Task SendCapture_ArrivingBeforeTheAppIsReady_WaitsForTheHandler()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        server.Start();
        string? received = null;

        var sending = CommandLineChannel.SendCaptureAsync("llegó pronto", TimeSpan.FromSeconds(10), pipe);
        await Task.Delay(300);
        Assert.False(sending.IsCompleted);
        server.SetHandler(text => { received = text; return Task.FromResult(true); });

        Assert.Equal(CommandLineDelivery.Delivered, await sending);
        Assert.Equal("llegó pronto", received);
    }

    [Fact]
    public async Task SendCapture_SeveralInARow_AreAllDelivered()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        var count = 0;
        server.SetHandler(_ => { count++; return Task.FromResult(true); });
        server.Start();

        for (var i = 0; i < 5; i++)
            Assert.Equal(CommandLineDelivery.Delivered, await CommandLineChannel.SendCaptureAsync("n" + i, TimeSpan.FromSeconds(10), pipe));

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task SendCapture_WithNoInstance_IsUnreachable()
    {
        var delivery = await CommandLineChannel.SendCaptureAsync("nadie escucha", TimeSpan.FromMilliseconds(400), UniquePipe());

        Assert.Equal(CommandLineDelivery.Unreachable, delivery);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("{\"otra\":\"cosa\"}")]
    [InlineData("{\"capture\":42}")]
    [InlineData("{\"capture\":\"   \"}")]
    [InlineData("[1,2]")]
    public async Task MalformedMessages_AreRefused_WithoutCallingTheHandler(string message)
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        var called = false;
        server.SetHandler(_ => { called = true; return Task.FromResult(true); });
        server.Start();

        var reply = await SendRawAsync(pipe, Encoding.UTF8.GetBytes(message + "\n"));

        Assert.Equal("error", reply);
        Assert.False(called);
    }

    [Fact]
    public async Task OversizedMessages_AreRefused()
    {
        var pipe = UniquePipe();
        using var server = new CommandLineServer(pipe);
        var called = false;
        server.SetHandler(_ => { called = true; return Task.FromResult(true); });
        server.Start();
        var big = new byte[CommandLineServer.MaxMessageBytes + 1024];
        Array.Fill(big, (byte)'a');

        var reply = await SendRawAsync(pipe, big);

        Assert.Equal("error", reply);
        Assert.False(called);
    }

    private static async Task<string> SendRawAsync(string pipe, byte[] payload)
    {
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(10_000);
        try
        {
            await client.WriteAsync(payload);
            await client.FlushAsync();
        }
        catch (System.IO.IOException)
        {
            // El servidor puede cortar antes de que termine de escribirse un mensaje enorme.
        }
        var reply = new byte[16];
        var read = await client.ReadAsync(reply);
        return Encoding.UTF8.GetString(reply, 0, read).Trim();
    }

    [Fact]
    public void TryParse_ReadsTheCaptureField()
    {
        Assert.True(CommandLineServer.TryParse("{\"capture\":\"hola\"}", out var text));
        Assert.Equal("hola", text);
        Assert.False(CommandLineServer.TryParse("", out _));
    }

    [Fact]
    public void DefaultPipeName_IsPerUserAndFileSystemSafe()
    {
        Assert.StartsWith("enki-capture-", CommandLineChannel.DefaultPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', CommandLineChannel.DefaultPipeName);
        Assert.DoesNotContain('\\', CommandLineChannel.DefaultPipeName);
    }
}
